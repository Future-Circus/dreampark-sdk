using System;
using System.Collections.Generic;
using UnityEngine;
using DreamPark; // SessionContext + GameArea live in the root DreamPark namespace

namespace DreamPark.API
{
    /// <summary>
    /// Session-based playtime reporter. A "session" = one consumer-session
    /// at a specific park (locationId). Lifecycle:
    ///
    ///   1. SessionContext.OnSessionPaired fires after the headset checks
    ///      into a consumer_session → start a fresh session here. parkId
    ///      is read from SessionConfig.locationId. This happens whether or
    ///      not a user is logged in — heartbeats wait for ProfileAPI.IsBound
    ///      but the clock is running.
    ///   2. As the player moves between GameAreas, accumulate per-contentId
    ///      time locally. No network traffic yet.
    ///   3. Every HEARTBEAT_INTERVAL_SEC (default 60s), if ProfileAPI.IsBound,
    ///      POST absolute totals to /app/profile/session/heartbeat. The server
    ///      diffs against what it's already credited and applies the delta —
    ///      so the very first heartbeat after a late login credits ALL time
    ///      elapsed since pairing (including the anonymous portion before
    ///      the user paired their account).
    ///   4. SessionContext.OnSessionUnpaired (return, timeout, unpair) →
    ///      send a final /session/end and clear local state.
    ///   5. On app pause / focus loss / quit, send a final /session/end so
    ///      a killed headset still records when play actually stopped.
    ///
    /// Anchoring to SessionContext (not Unity boot) means:
    ///   - Lobby / menu time before pairing is NOT credited to any park
    ///   - Moving from park A → park B in one app run = two separate sessions
    ///   - The parkId on the session is always the actual park-of-record
    ///
    /// Robustness:
    ///   - Dropped heartbeats are recovered on the next successful one.
    ///   - Duplicate heartbeats are no-ops (server delta = 0).
    ///   - Out-of-order heartbeats are clamped server-side (per-key max).
    ///   - Power-off loses at most one heartbeat-interval of time.
    ///   - Pre-pair / late-pair players still get full credit — the SDK has
    ///     been recording locally the whole time.
    /// </summary>
    public static class SessionReporter
    {
        public const float HEARTBEAT_INTERVAL_SEC = 60f;

        // Debounce: pause + focus loss often fire back-to-back when the
        // Quest is doffed. Suppress the second one when the gap is tiny.
        const float END_BEAT_DEBOUNCE_SEC = 5f;

        // ── Session state ───────────────────────────────────────────────
        static string s_sessionId;
        static string s_parkId;
        static DateTime s_sessionStartedAtUtc;
        static float s_sessionStartedRealtimeSec;

        // Per-content time accumulator. Updated on zone change + read on every
        // heartbeat (current-zone in-progress time is added on read).
        static readonly Dictionary<string, float> s_contentTimes = new();
        static string s_currentContentId;
        static float s_currentContentEnterRealtimeSec;

        // Heartbeat scheduling.
        static float s_lastHeartbeatRealtimeSec;
        static float s_lastBeatTotalDuration = -1f;
        static float s_lastEndBeatRealtimeSec = -999f;
        static bool s_enabled = true;
        static bool s_hooked = false;

        public static string SessionId => s_sessionId;
        public static string ParkId => s_parkId;
        public static DateTime SessionStartedAtUtc => s_sessionStartedAtUtc;
        public static bool HasActiveSession => !string.IsNullOrEmpty(s_sessionId);
        public static bool IsAutoReportEnabled => s_enabled;

        public static float CurrentSessionDurationSeconds
            => HasActiveSession ? Time.realtimeSinceStartup - s_sessionStartedRealtimeSec : 0f;

        /// <summary>
        /// Disable to take manual control of session reporting. The session
        /// state is preserved — just no automatic heartbeats fire.
        /// </summary>
        public static void SetAutoReportEnabled(bool enabled) => s_enabled = enabled;

        // ── Bootstrap ───────────────────────────────────────────────────
        // Runs once at Unity startup. Doesn't start a session — only wires
        // up the subscriptions so SessionContext lifecycle events drive the
        // reporter's session boundaries.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Bootstrap()
        {
            if (s_hooked) return;
            s_hooked = true;

            SessionContext.OnSessionPaired += OnSessionPaired;
            SessionContext.OnSessionUnpaired += OnSessionUnpaired;

            GameArea.OnContentZoneChanged += OnZoneChanged;
            Application.quitting += () => SendBeat(ended: true);

            var ticker = new GameObject("[DreamPark] SessionHeartbeat");
            UnityEngine.Object.DontDestroyOnLoad(ticker);
            ticker.hideFlags = HideFlags.HideAndDontSave;
            ticker.AddComponent<HeartbeatDriver>();
        }

        // ── Session lifecycle ───────────────────────────────────────────
        static void OnSessionPaired(SessionConfig config)
        {
            // If we somehow had a prior session still open (shouldn't
            // happen since Clear()→OnSessionUnpaired ends it, but defensive),
            // close it before starting the new one.
            if (HasActiveSession)
            {
                SendBeat(ended: true);
            }

            s_sessionId = Guid.NewGuid().ToString("N");
            s_parkId = config?.locationId;
            s_sessionStartedAtUtc = DateTime.UtcNow;
            s_sessionStartedRealtimeSec = Time.realtimeSinceStartup;
            s_lastHeartbeatRealtimeSec = s_sessionStartedRealtimeSec;
            s_lastBeatTotalDuration = -1f;

            s_contentTimes.Clear();

            // If the player is already inside a GameArea when pairing
            // completes (e.g. pre-pair zone enter), start the in-progress
            // clock from the pair moment — earlier time isn't attributable
            // to a session that didn't yet exist.
            s_currentContentId = !string.IsNullOrEmpty(GameArea.currentGameArea?.gameId)
                ? GameArea.currentGameArea.gameId : null;
            s_currentContentEnterRealtimeSec = s_currentContentId != null ? Time.realtimeSinceStartup : 0f;
        }

        static void OnSessionUnpaired()
        {
            // Flush whatever's pending, then clear local state. The final
            // beat ships absolute totals so any time since the last
            // heartbeat is credited.
            SendBeat(ended: true);

            s_sessionId = null;
            s_parkId = null;
            s_contentTimes.Clear();
            s_currentContentId = null;
            s_currentContentEnterRealtimeSec = 0f;
        }

        // ── Zone tracking ───────────────────────────────────────────────
        static void OnZoneChanged(GameArea previous, GameArea next)
        {
            // Only track when a session is active — pre-pair zone visits
            // don't belong to any park record.
            if (!HasActiveSession) return;

            // Bank time on the zone the player is leaving (if any).
            if (s_currentContentId != null)
            {
                float elapsed = Time.realtimeSinceStartup - s_currentContentEnterRealtimeSec;
                if (elapsed > 0f)
                {
                    s_contentTimes[s_currentContentId] =
                        (s_contentTimes.TryGetValue(s_currentContentId, out var prev) ? prev : 0f) + elapsed;
                }
            }

            s_currentContentId = !string.IsNullOrEmpty(next?.gameId) ? next.gameId : null;
            s_currentContentEnterRealtimeSec = s_currentContentId != null ? Time.realtimeSinceStartup : 0f;
        }

        // ── Heartbeat dispatch ──────────────────────────────────────────
        // Snapshots the local state — accumulated content time PLUS the
        // in-progress chunk for the current zone — and ships absolute totals.
        // Server diffs to compute deltas, so we don't reset locals after a
        // successful send.
        static void SendBeat(bool ended)
        {
            if (!s_enabled) return;
            if (!HasActiveSession) return;
            if (!ProfileAPI.IsBound) return; // wait for pair; server replays full duration on first beat post-pair

            // Debounce end-beats so OnApplicationPause + OnApplicationFocus
            // firing back-to-back doesn't ship two identical requests.
            if (ended && Time.realtimeSinceStartup - s_lastEndBeatRealtimeSec < END_BEAT_DEBOUNCE_SEC) return;
            if (ended) s_lastEndBeatRealtimeSec = Time.realtimeSinceStartup;

            float totalDuration = Time.realtimeSinceStartup - s_sessionStartedRealtimeSec;
            // Skip if nothing changed since last beat — saves a no-op network
            // round-trip. (Server would also no-op, but this is cheaper.)
            if (!ended && totalDuration - s_lastBeatTotalDuration < 0.5f) return;

            // Build the per-content snapshot, including in-progress zone time.
            var snapshot = new Dictionary<string, float>(s_contentTimes);
            if (s_currentContentId != null)
            {
                float ongoing = Time.realtimeSinceStartup - s_currentContentEnterRealtimeSec;
                if (ongoing > 0f)
                {
                    snapshot[s_currentContentId] =
                        (snapshot.TryGetValue(s_currentContentId, out var prev) ? prev : 0f) + ongoing;
                }
            }

            ProfileAPI.SendSessionHeartbeat(
                sessionId: s_sessionId,
                startedAtUtc: s_sessionStartedAtUtc,
                durationSeconds: totalDuration,
                contentTimes: snapshot,
                parkId: s_parkId,
                ended: ended
            );

            s_lastHeartbeatRealtimeSec = Time.realtimeSinceStartup;
            s_lastBeatTotalDuration = totalDuration;
        }

        // MonoBehaviour driver — Update() polls the interval. Coroutines
        // were rejected because pause flushes need to be synchronous with
        // the lifecycle callbacks (a yield-based loop can't observe
        // OnApplicationPause cleanly without extra plumbing).
        class HeartbeatDriver : MonoBehaviour
        {
            void Update()
            {
                if (!s_enabled || !HasActiveSession) return;
                if (Time.realtimeSinceStartup - s_lastHeartbeatRealtimeSec < HEARTBEAT_INTERVAL_SEC) return;
                SendBeat(ended: false);
            }

            void OnApplicationPause(bool paused)
            {
                if (paused) SendBeat(ended: true);
            }

            void OnApplicationFocus(bool focused)
            {
                if (!focused) SendBeat(ended: true);
            }
        }
    }
}
