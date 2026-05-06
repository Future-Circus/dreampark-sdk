#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Caches whether the current user is allowed to publish SDK versions.
    // Source of truth is the backend (GET /api/sdk/canPublish); this class is
    // just an in-memory cache so the Publish menu's validate function returns
    // synchronously.
    //
    // Lifecycle:
    //   - On editor load (delayCall, after AuthAPI is restored from EditorPrefs):
    //     if logged in, probe.
    //   - On AuthAPI.LoginStateChanged: clear (and re-probe if newly logged in).
    //   - Until the probe completes, IsAdmin is null and the menu stays disabled.
    [InitializeOnLoad]
    internal static class AdminState
    {
        // null = unknown (haven't probed, or probe in flight, or transient failure).
        // true / false = known answer from the backend.
        public static bool? IsAdmin { get; private set; }

        // Fired after each probe settles so MenuItem validate functions can
        // re-evaluate. Unity rebuilds menus on its own ticks, but explicitly
        // forcing a repaint makes the disable/enable feel snappier.
        public static event Action AdminStateChanged;

        static AdminState()
        {
            AuthAPI.LoginStateChanged -= OnLoginStateChanged;
            AuthAPI.LoginStateChanged += OnLoginStateChanged;

            // Re-probe when the editor regains focus IFF we don't yet have a
            // known answer. This self-heals the case where the first probe
            // hit a 404 (backend not deployed yet, transient outage) — without
            // it, the menu stays disabled until the user logs out and back in.
            EditorApplication.focusChanged -= OnFocusChanged;
            EditorApplication.focusChanged += OnFocusChanged;

            // Defer the initial probe until after the editor finishes loading
            // so AuthAPI's static state is restored from EditorPrefs.
            EditorApplication.delayCall += MaybeProbe;
        }

        private static void OnLoginStateChanged(bool isLoggedIn)
        {
            // Clear stale state immediately on logout so the menu disables
            // without waiting for a probe.
            IsAdmin = null;
            AdminStateChanged?.Invoke();
            if (isLoggedIn) MaybeProbe();
        }

        private static void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus) return;
            // Only retry if we DON'T currently have a known answer. Once IsAdmin
            // is true/false we trust it for the session — a stale "true" would
            // be caught on the actual publish (the backend re-checks), and a
            // stale "false" is harmless. This keeps the focus path cheap.
            if (IsAdmin != null) return;
            MaybeProbe();
        }

        public static void MaybeProbe()
        {
            if (!AuthAPI.isLoggedIn)
            {
                IsAdmin = null;
                AdminStateChanged?.Invoke();
                return;
            }

            SDKAPI.CheckCanPublish((success, response) =>
            {
                if (success && response?.json != null && response.json.HasField("canPublish"))
                {
                    IsAdmin = response.json.GetField("canPublish").boolValue;
                }
                else
                {
                    // Failed (network blip, 5xx) — fail closed: leave as null/false
                    // so the menu stays disabled until the next probe succeeds.
                    IsAdmin = null;
                }
                AdminStateChanged?.Invoke();
            });
        }
    }
}
#endif
