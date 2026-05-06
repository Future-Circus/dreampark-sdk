#if UNITY_EDITOR
using System;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // [InitializeOnLoadMethod] runs on every editor load + every domain reload
    // (script recompile). We don't want to spam /api/sdk/manifest, so the check
    // is throttled to once per editor session via a delayCall + a static guard.
    //
    // After the manifest fetch completes:
    //   - If a newer version exists AND the user hasn't dismissed it for this
    //     specific version, show UpdateAvailablePopup.
    //   - Cache the result in a static field that ContentUploaderPanel reads
    //     to gate uploads (no duplicate request from the panel).
    [InitializeOnLoad]
    internal static class SDKUpdateChecker
    {
        private const string SkipPrefKeyPrefix = "DreamPark.SDKUpdate.Skipped.";
        private const string RemindPrefKey = "DreamPark.SDKUpdate.RemindAfter";
        private static bool checkScheduled;

        // Manifest result cache. Other panels read these to decide whether to
        // gate behavior (e.g. "block uploads if local < latest"). All three are
        // written together, so reading any of them between writes is safe.
        public static string LatestVersion { get; private set; }
        public static string LatestReleaseNotes { get; private set; }
        public static string LatestDownloadUrl { get; private set; }
        public static bool ManifestFetchSucceeded { get; private set; }
        public static bool ManifestFetchAttempted { get; private set; }

        // Fired after the manifest fetch settles (success or failure). Panels
        // subscribe to refresh their gating UI.
        public static event Action ManifestUpdated;

        static SDKUpdateChecker()
        {
            // EditorApplication.delayCall fires once after the next editor tick —
            // by which point AuthAPI's static state has been restored from EditorPrefs.
            EditorApplication.delayCall += ScheduleCheck;
        }

        private static void ScheduleCheck()
        {
            if (checkScheduled) return;
            checkScheduled = true;

            // Defer to next tick so we don't block editor startup.
            EditorApplication.delayCall += CheckForUpdate;
        }

        public static void CheckForUpdate()
        {
            if (!AuthAPI.isLoggedIn) return; // The /api/sdk/manifest endpoint requires auth.

            SDKAPI.GetManifest((success, response) =>
            {
                UpdateCacheFromManifest(success, response);
                ManifestUpdated?.Invoke();
                MaybeShowPopup();
            });
        }

        // Shared cache-update step used by both the auto check (CheckForUpdate)
        // and the manual menu-item check (CheckForUpdateManual). The fully
        // qualified type name uses `global::` because we're inside namespace
        // DreamPark — without it the compiler tries to resolve the leading
        // `DreamPark` against the current namespace and fails (CS0426).
        private static void UpdateCacheFromManifest(bool success, global::DreamPark.API.DreamParkAPI.APIResponse response)
        {
            ManifestFetchAttempted = true;
            ManifestFetchSucceeded = success && response?.json != null && response.json.HasField("latest");

            if (ManifestFetchSucceeded)
            {
                LatestVersion = response.json.GetField("latest").stringValue;
                LatestReleaseNotes = response.json.HasField("releaseNotes")
                    ? response.json.GetField("releaseNotes").stringValue
                    : "";
                LatestDownloadUrl = response.json.HasField("downloadUrl")
                    ? response.json.GetField("downloadUrl").stringValue
                    : null;
            }
            else
            {
                LatestVersion = null;
                LatestReleaseNotes = null;
                LatestDownloadUrl = null;
            }
        }

        // User-initiated check via DreamPark menu. Differs from the silent
        // CheckForUpdate in three ways: (1) shows a progress bar so the click
        // feels responsive, (2) shows a result dialog regardless of outcome
        // (a manual click that produces no feedback feels broken), (3) bypasses
        // the skip / remind-me-later state — if the user explicitly asked for
        // an update check, they want to see the popup even for versions they
        // previously skipped.
        [MenuItem("DreamPark/Check for SDK Updates", false, 1)]
        public static void CheckForUpdateManual()
        {
            if (!AuthAPI.isLoggedIn)
            {
                // Skip the "go open another panel" dialog — just show the login
                // popup directly. The user can re-click "Check for SDK Updates"
                // after logging in for an immediate check. Not auto-chaining
                // here because subscribing to LoginStateChanged for one-shot
                // retry is fiddly to clean up if the user cancels login.
                AuthPopup.Show();
                return;
            }

            EditorUtility.DisplayProgressBar("DreamPark", "Checking for SDK updates...", 0.5f);
            SDKAPI.GetManifest((success, response) =>
            {
                EditorUtility.ClearProgressBar();
                UpdateCacheFromManifest(success, response);
                ManifestUpdated?.Invoke();

                if (!ManifestFetchSucceeded)
                {
                    string err = SDKAPI.ExtractError(response, "Could not reach the update server.");
                    EditorUtility.DisplayDialog("Update check failed", err, "OK");
                    return;
                }

                string current = SDKVersion.Current;
                if (SDKVersion.Compare(current, LatestVersion) >= 0)
                {
                    EditorUtility.DisplayDialog(
                        "You're up to date",
                        $"DreamPark SDK v{current} is the latest version.",
                        "OK");
                    return;
                }

                // Manually triggered — bypass skip / remind-me-later state.
                UpdateAvailablePopup.Show(current, LatestVersion, LatestReleaseNotes, LatestDownloadUrl);
            });
        }

        private static void MaybeShowPopup()
        {
            if (!ManifestFetchSucceeded) return;
            string current = SDKVersion.Current;
            if (SDKVersion.Compare(current, LatestVersion) >= 0) return;

            // Skipped this exact version? Stay quiet.
            if (EditorPrefs.GetBool(SkipPrefKeyPrefix + LatestVersion, false)) return;

            // "Remind me later" sets a timestamp — we re-show after 24h.
            string remindRaw = EditorPrefs.GetString(RemindPrefKey, "");
            if (!string.IsNullOrEmpty(remindRaw) && double.TryParse(remindRaw, out double remindAfter))
            {
                double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                if (nowMs < remindAfter) return;
            }

            UpdateAvailablePopup.Show(current, LatestVersion, LatestReleaseNotes, LatestDownloadUrl);
        }

        // Used by UpdateAvailablePopup callbacks.
        public static void MarkSkipped(string version)
        {
            EditorPrefs.SetBool(SkipPrefKeyPrefix + version, true);
        }

        public static void RemindLater()
        {
            double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            double in24h = nowMs + (24 * 60 * 60 * 1000);
            EditorPrefs.SetString(RemindPrefKey, in24h.ToString("F0"));
        }
    }
}
#endif
