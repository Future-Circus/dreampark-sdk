#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Defective.JSON;
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

        // Newest-first release-notes history from the manifest's `history`
        // field. Lets the update popup show notes for EVERY version the dev
        // skipped, not just the latest — devs often go several releases
        // between updates. Older backends without `history` leave this empty
        // and we fall back to LatestReleaseNotes.
        public struct ReleaseEntry
        {
            public string version;
            public string notes;
        }
        public static List<ReleaseEntry> ReleaseHistory { get; } = new List<ReleaseEntry>();

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

            ReleaseHistory.Clear();
            if (ManifestFetchSucceeded)
            {
                LatestVersion = response.json.GetField("latest").stringValue;
                LatestReleaseNotes = response.json.HasField("releaseNotes")
                    ? response.json.GetField("releaseNotes").stringValue
                    : "";
                LatestDownloadUrl = response.json.HasField("downloadUrl")
                    ? response.json.GetField("downloadUrl").stringValue
                    : null;

                // Optional `history` array (newest-first). Absent on older
                // backends — everything below degrades to latest-only notes.
                var history = response.json.HasField("history") ? response.json.GetField("history") : null;
                if (history != null && history.type == JSONObject.Type.Array && history.list != null)
                {
                    for (int i = 0; i < history.list.Count; i++)
                    {
                        var entry = history.list[i];
                        if (entry == null || !entry.HasField("version")) continue;
                        ReleaseHistory.Add(new ReleaseEntry
                        {
                            version = entry.GetField("version").stringValue,
                            notes = entry.HasField("releaseNotes") ? entry.GetField("releaseNotes").stringValue : ""
                        });
                    }
                }
            }
            else
            {
                LatestVersion = null;
                LatestReleaseNotes = null;
                LatestDownloadUrl = null;
            }
        }

        // Combined release notes for every published version NEWER than
        // `installedVersion`, newest first — what the update popup renders in
        // its read-only notes box so a dev who skipped several releases sees
        // everything they're about to pick up. Falls back to the latest
        // version's notes when the backend didn't send `history`.
        public static string BuildReleaseNotesSince(string installedVersion)
        {
            if (ReleaseHistory.Count == 0) return LatestReleaseNotes ?? "";

            var sb = new StringBuilder();
            foreach (var entry in ReleaseHistory)
            {
                if (SDKVersion.Compare(entry.version, installedVersion) <= 0) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("v").Append(entry.version).Append('\n');
                sb.Append(string.IsNullOrEmpty(entry.notes) ? "(no release notes)" : entry.notes.Trim());
            }
            return sb.Length > 0 ? sb.ToString() : (LatestReleaseNotes ?? "");
        }

        // Click-time upload gate. The passive gate in ContentUploaderPanel
        // reads the once-per-session manifest cache, which goes stale if the
        // editor stays open across a release — so Compile & Upload runs a
        // FRESH manifest check here before the upload popup is allowed to
        // open. Out of date → route to UpdateAvailablePopup instead (bypassing
        // skip/remind state: those silence the nag, they don't unlock
        // uploads). Fetch failure fails open, matching the passive gate — a
        // backend blip shouldn't lock everyone out, and the upload itself
        // will surface real connectivity problems.
        public static void EnsureUpToDateThen(Action onUpToDate)
        {
            if (!AuthAPI.isLoggedIn)
            {
                onUpToDate?.Invoke(); // Upload flow enforces login itself.
                return;
            }

            EditorUtility.DisplayProgressBar("DreamPark", "Verifying SDK version...", 0.5f);
            SDKAPI.GetManifest((success, response) =>
            {
                EditorUtility.ClearProgressBar();
                UpdateCacheFromManifest(success, response);
                ManifestUpdated?.Invoke();

                if (!ManifestFetchSucceeded)
                {
                    Debug.LogWarning("[DreamPark] SDK version check failed before upload — continuing (fail-open). " +
                                     SDKAPI.ExtractError(response, "Could not reach the update server."));
                    onUpToDate?.Invoke();
                    return;
                }

                string current = SDKVersion.Current;
                if (SDKVersion.Compare(current, LatestVersion) >= 0)
                {
                    onUpToDate?.Invoke();
                    return;
                }

                EditorUtility.DisplayDialog(
                    "SDK update required",
                    $"Your DreamPark SDK is out of date (installed v{current}, latest v{LatestVersion}).\n\n" +
                    "Update the SDK before uploading content to avoid version drift between creators.",
                    "OK");
                UpdateAvailablePopup.Show(current, LatestVersion, BuildReleaseNotesSince(current), LatestDownloadUrl);
            });
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
                UpdateAvailablePopup.Show(current, LatestVersion, BuildReleaseNotesSince(current), LatestDownloadUrl);
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

            UpdateAvailablePopup.Show(current, LatestVersion, BuildReleaseNotesSince(current), LatestDownloadUrl);
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
