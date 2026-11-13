// ─────────────────────────────────────────────────────────────────────
//  PlaylistAutoTaps.cs — the platform's taps on what titles ALREADY do,
//  for playlist auto-detection (PlaylistAutoSignals, Docs/PLAYLISTS.md § 13).
//  Raised by LuaBehaviour (net_send), SharedState (every applied key change,
//  every request), EasyEnemy (death). Internal on purpose: nothing enters the
//  generated XLua wrappers (no Gen/ drift, no title regen). A listener that
//  throws never breaks the title's call.
// ─────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

namespace DreamPark
{
    internal static class PlaylistAutoTaps
    {
        /// (sending object, kind, payload JSON) — a script's net_send, before the relay (also solo).
        internal static event Action<GameObject, string, string> NetSent;
        /// (state, key, JSON or null) — every applied change of a SharedState key, on every device.
        internal static event Action<SharedState, string, string> StateChanged;
        /// (state, kind, payload JSON) — a SharedState request, on the device that made it.
        internal static event Action<SharedState, string, string> StateRequested;
        /// (enemy object) — an EasyEnemy's hitpoints reached zero.
        internal static event Action<GameObject> EnemyDied;

        internal static void RaiseNetSent(GameObject go, string kind, string payload)
        {
            var h = NetSent; if (h == null) return;
            try { h(go, kind, payload); } catch (Exception e) { Debug.LogWarning("[PlaylistAuto] net tap threw: " + e.Message); }
        }

        internal static void RaiseStateChanged(SharedState s, string key, string json)
        {
            var h = StateChanged; if (h == null) return;
            try { h(s, key, json); } catch (Exception e) { Debug.LogWarning("[PlaylistAuto] state tap threw: " + e.Message); }
        }

        internal static void RaiseStateRequested(SharedState s, string kind, string json)
        {
            var h = StateRequested; if (h == null) return;
            try { h(s, kind, json); } catch (Exception e) { Debug.LogWarning("[PlaylistAuto] request tap threw: " + e.Message); }
        }

        internal static void RaiseEnemyDied(GameObject go)
        {
            var h = EnemyDied; if (h == null) return;
            try { h(go); } catch (Exception e) { Debug.LogWarning("[PlaylistAuto] enemy tap threw: " + e.Message); }
        }
    }
}
