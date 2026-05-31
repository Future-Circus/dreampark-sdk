using System;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Static context storing the current paired session info from the backend checkin.
    /// Populated by HeadsetCheckin after successful temporal pairing.
    /// </summary>
    public static class SessionContext
    {
        public static string SessionId;
        public static string DreamboxId;
        // LocationId is the canonical "park" identifier on the consumer-session.
        // Persisted statically so other SDK systems (e.g. SessionReporter) can
        // read it without re-querying the backend.
        public static string LocationId;
        public static string RelayHost;
        public static int? RelayPort;
        public static string[] SelectedContentIds;
        public static int? SessionLengthMinutes;
        public static bool IsPaired;

        public static event Action<SessionConfig> OnSessionPaired;
        // Fires when the headset's consumer-session ends (return, timeout,
        // unpair). Subscribers should flush any per-session state — e.g.
        // SessionReporter sends a final /session/end so the playtime log
        // records when play actually stopped.
        public static event Action OnSessionUnpaired;

        public static void SetPaired(SessionConfig config)
        {
            SessionId = config.sessionId;
            DreamboxId = config.dreamboxId;
            LocationId = config.locationId;
            RelayHost = config.relayHost;
            RelayPort = config.relayPort;
            SelectedContentIds = config.selectedContentIds;
            SessionLengthMinutes = config.sessionLengthMinutes;
            IsPaired = true;

            if (!string.IsNullOrEmpty(config.dreamboxId))
            {
                UnityEngine.PlayerPrefs.SetString("lastDreamboxId", config.dreamboxId);
                UnityEngine.PlayerPrefs.Save();
            }

            OnSessionPaired?.Invoke(config);
        }

        public static void Clear()
        {
            var wasPaired = IsPaired;
            SessionId = null;
            DreamboxId = null;
            LocationId = null;
            RelayHost = null;
            RelayPort = null;
            SelectedContentIds = null;
            SessionLengthMinutes = null;
            IsPaired = false;

            if (wasPaired) OnSessionUnpaired?.Invoke();
        }
    }

    [Serializable]
    public class SessionConfig
    {
        public string sessionId;
        public string locationId;
        public string dreamboxId;
        public int headsetCount;
        public int? sessionLengthMinutes;
        public string contentId;
        public string[] selectedContentIds;
        public string relayHost;
        public int? relayPort;
        public string[] addOns;
        public string marketTier;
        public int? perMinuteRateCents;
        public string startedAt;
    }
}
