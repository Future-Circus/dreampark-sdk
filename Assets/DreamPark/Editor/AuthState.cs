#if UNITY_EDITOR
using System;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Editor-only auth lifecycle helper. Hooks EditorApplication.focusChanged so
    // when the user returns to the Unity editor we silently probe /auth/refresh.
    // If the server reports the session is dead (401), AuthAPI clears the local
    // session and fires LoginStateChanged — panels gated on isLoggedIn then
    // re-render and prompt the user to log in.
    //
    // Throttled: at most one probe every MIN_INTERVAL_SECONDS to avoid hammering
    // the backend if the user rapidly tabs away and back.
    [InitializeOnLoad]
    internal static class AuthState
    {
        private const double MIN_INTERVAL_SECONDS = 60;
        // Tracked in EditorPrefs (not just a static field) so we don't re-probe on
        // every domain reload (script recompile / play mode toggle).
        private const string LastCheckedPrefKey = "DreamPark.AuthState.LastCheckedAt";

        // How much runway is left before we say something. /auth/refresh VALIDATES a
        // session but never re-mints it (routes/auth.routes.js POST /refresh verifies the
        // cookie, returns the user, and stops there), so the ~14-day deadline set at login
        // is final and nothing the editor does postpones it. Warning at 48h means the
        // creator hears about it while signing in again is a ten-second interruption,
        // rather than when a long upload 401s at the commit step with the build spent.
        private const double ExpiryWarningHours = 48;

        // The expiry value we last warned about, so the notice appears once per SESSION
        // rather than once per focus probe. Keyed on the deadline itself: a fresh login
        // produces a new deadline and re-arms the warning, while the current session stays
        // quiet however many times the user tabs away and back. In EditorPrefs for the
        // same reason as LastCheckedPrefKey — a static field would re-arm on every
        // script recompile.
        private const string ExpiryWarnedPrefKey = "DreamPark.AuthState.ExpiryWarnedFor";

        static AuthState()
        {
            // Subscribe once. focusChanged passes true on focus gained, false on lost.
            EditorApplication.focusChanged -= OnFocusChanged;
            EditorApplication.focusChanged += OnFocusChanged;

            // Also probe shortly after editor load (e.g. fresh editor start).
            EditorApplication.delayCall += () => MaybeRefresh("startup");
        }

        private static void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus) return;
            MaybeRefresh("focus");
        }

        private static void MaybeRefresh(string reason)
        {
            if (!AuthAPI.isLoggedIn) return;

            double now = EditorApplication.timeSinceStartup;
            // EditorPrefs stores strings — parse defensively.
            double last = 0;
            string raw = EditorPrefs.GetString(LastCheckedPrefKey, "0");
            double.TryParse(raw, out last);

            // timeSinceStartup resets every editor restart, so "last" can be larger
            // than "now" on a fresh launch. Treat that as "never checked" and probe.
            bool freshSession = last > now;
            if (!freshSession && (now - last) < MIN_INTERVAL_SECONDS) return;

            EditorPrefs.SetString(LastCheckedPrefKey, now.ToString("F2"));

            AuthAPI.Refresh(stillValid =>
            {
                if (!stillValid)
                {
                    // AuthAPI.Refresh already cleared the session on 401 and fired
                    // LoginStateChanged. We log a hint so devs know why panels
                    // suddenly demand re-login.
                    Debug.Log($"[DreamPark] Session expired (probed via {reason}). Please log in again.");
                    return;
                }
                WarnIfSessionEndingSoon();
            });
        }

        private static void WarnIfSessionEndingSoon()
        {
            double hoursLeft = AuthAPI.sessionExpiresInHours;
            // -1 means the expiry is unknown — a session stored before the SDK recorded
            // expiresAt, or one injected by the native handoff. Unknown is NOT "expiring
            // soon"; staying silent is the only correct reading.
            if (hoursLeft < 0 || hoursLeft > ExpiryWarningHours) return;

            string stamp = AuthAPI.sessionExpiresAt.ToString();
            if (EditorPrefs.GetString(ExpiryWarnedPrefKey, "") == stamp) return;
            EditorPrefs.SetString(ExpiryWarnedPrefKey, stamp);

            Debug.LogWarning(
                $"[DreamPark] Your DreamPark session ends in about {Mathf.CeilToInt((float)hoursLeft)}h. "
                + "Sign in again (DreamPark → Sign In) before starting a long upload.");
        }
    }
}
#endif
