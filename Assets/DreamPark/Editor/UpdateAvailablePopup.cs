#if UNITY_EDITOR
using System.Collections;
using System.IO;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark
{
    // Modal "SDK update available" popup. Shown by SDKUpdateChecker after a
    // successful manifest fetch when local < latest and the user hasn't already
    // dismissed this exact version.
    //
    // [Update Now]: downloads the .unitypackage to Temp/ and calls
    // AssetDatabase.ImportPackage(path, true). Interactive=true is critical —
    // it shows Unity's standard import dialog so the user can review/uncheck
    // any locally-modified files instead of getting silently overwritten.
    public class UpdateAvailablePopup : EditorWindow
    {
        private string currentVersion;
        private string latestVersion;
        private string releaseNotes;
        private string downloadUrl;
        private bool isDownloading = false;
        private float downloadProgress = 0f;
        private string statusMessage = null;
        private Vector2 notesScroll;
        private GUIStyle notesStyle; // Lazy-built in OnGUI — GUI styles can't be created outside OnGUI.

        public static void Show(string currentVersion, string latestVersion, string releaseNotes, string downloadUrl)
        {
            var existing = Resources.FindObjectsOfTypeAll<UpdateAvailablePopup>();
            if (existing != null && existing.Length > 0)
            {
                existing[0].Focus();
                return;
            }

            var win = CreateInstance<UpdateAvailablePopup>();
            win.titleContent = new GUIContent("DreamPark SDK Update Available");
            win.currentVersion = currentVersion;
            win.latestVersion = latestVersion;
            win.releaseNotes = releaseNotes ?? "";
            win.downloadUrl = downloadUrl;
            win.minSize = new Vector2(420, 300);
            win.maxSize = new Vector2(500, 500);
            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(
                main.x + (main.width - 440) / 2f,
                main.y + (main.height - 320) / 2f,
                440, 320);
            win.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("DreamPark SDK Update Available", EditorStyles.boldLabel);
            GUILayout.Space(4);
            EditorGUILayout.LabelField($"Installed: v{currentVersion}");
            EditorGUILayout.LabelField($"Latest: v{latestVersion}", EditorStyles.boldLabel);

            GUILayout.Space(8);
            // releaseNotes is the COMBINED history since the installed version
            // (built by SDKUpdateChecker.BuildReleaseNotesSince) — devs often
            // skip several releases, so they see every update they're picking
            // up, not just the latest. Rendered as a disabled text area:
            // read-only, but scrolls and shows all versions verbatim.
            EditorGUILayout.LabelField($"Release notes since v{currentVersion}:", EditorStyles.miniBoldLabel);
            if (notesStyle == null)
            {
                notesStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            }
            notesScroll = EditorGUILayout.BeginScrollView(notesScroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(220));
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextArea(
                    string.IsNullOrEmpty(releaseNotes) ? "(no release notes)" : releaseNotes,
                    notesStyle, GUILayout.ExpandHeight(true));
            }
            EditorGUILayout.EndScrollView();

            if (isDownloading)
            {
                GUILayout.Space(6);
                Rect r = GUILayoutUtility.GetRect(18, 18, "TextField");
                EditorGUI.ProgressBar(r, downloadProgress, $"Downloading {(downloadProgress * 100f):0}%");
            }
            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
            }

            GUILayout.FlexibleSpace();

            GUI.enabled = !isDownloading;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Skip This Version"))
            {
                SDKUpdateChecker.MarkSkipped(latestVersion);
                Close();
            }
            if (GUILayout.Button("Remind Me Later"))
            {
                SDKUpdateChecker.RemindLater();
                Close();
            }
            if (GUILayout.Button("Update Now", EditorStyles.miniButtonRight))
            {
                StartDownload();
            }
            GUILayout.EndHorizontal();
            GUI.enabled = true;
        }

        private void StartDownload()
        {
            if (string.IsNullOrEmpty(downloadUrl))
            {
                statusMessage = "Missing download URL — try reopening the editor.";
                return;
            }
            isDownloading = true;
            downloadProgress = 0f;
            statusMessage = null;
            EditorCoroutineUtility.StartCoroutineOwnerless(DownloadAndImport());
        }

        private IEnumerator DownloadAndImport()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"dreampark-sdk-update-{latestVersion}.unitypackage");
            using (var req = UnityWebRequest.Get(downloadUrl))
            {
                req.downloadHandler = new DownloadHandlerFile(tempPath);
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    downloadProgress = req.downloadProgress;
                    Repaint();
                    yield return null;
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    isDownloading = false;
                    statusMessage = "Download failed: " + req.error;
                    Repaint();
                    yield break;
                }
            }

            isDownloading = false;
            downloadProgress = 1f;
            Repaint();

            // Hook completion BEFORE calling ImportPackage so we don't miss
            // the event. After Unity finishes applying the import (which may
            // include the user accepting the interactive dialog), we force
            // SDKVersion to re-read its JSON. Otherwise the static cache holds
            // the old version forever — Unity only triggers a domain reload
            // (which would naturally reset the cache) if .cs files changed,
            // and a JSON-only update slips through.
            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            AssetDatabase.importPackageCancelled += OnImportCancelled;
            AssetDatabase.importPackageFailed -= OnImportFailed;
            AssetDatabase.importPackageFailed += OnImportFailed;

            // Record the intent BEFORE importing. The import rewrites every SDK .cs
            // file, which forces a recompile and a domain reload — and that reload
            // destroys the in-flight importPackageCompleted callback we just
            // subscribed. (The subscription itself is fine; SDKUpdateChecker's
            // [InitializeOnLoad] ctor re-runs. It is specifically the ONE callback for
            // the import that caused the reload that is lost — the one that mattered.)
            //
            // SessionState survives domain reload and dies on editor restart, so this
            // marker outlives the reload without becoming permanent litter.
            // SDKUpdateChecker.ReconcileAfterInstall picks it up on the far side and
            // verifies the version actually changed.
            SessionState.SetString(SDKUpdateChecker.PendingVersionKey, latestVersion);

            // interactive: true → Unity shows its built-in import dialog.
            // Users can uncheck files to protect any local modifications they've
            // made under Assets/DreamPark/.
            AssetDatabase.ImportPackage(tempPath, true);
            Close();
        }

        private static void OnImportCompleted(string packageName)
        {
            UnsubscribeAll();

            // Defer a tick so Unity's own import bookkeeping settles before we force
            // the version file's reimport — otherwise Reload() can re-latch the
            // PRE-import value and then log it as a success, which is worse than not
            // reloading at all.
            EditorApplication.delayCall += () =>
            {
                SDKVersion.Reload();
                Debug.Log($"[DreamPark] SDK package '{packageName}' imported. Local version is now {SDKVersion.Current}.");

                // Verifies the install landed and clears the pending marker. Safe to
                // call when nothing is pending — it no-ops.
                SDKUpdateChecker.ReconcileAfterInstall();

                // Re-fetch the manifest so the upload-gate / Check for Updates UI
                // reflects the new version immediately. (Editor coroutines don't
                // survive domain reload either, so this can still be lost — the
                // reconcile above is what makes the outcome visible regardless.)
                SDKUpdateChecker.CheckForUpdate();
            };
        }

        private static void OnImportCancelled(string packageName)
        {
            UnsubscribeAll();
            Debug.Log($"[DreamPark] SDK update import cancelled for '{packageName}'. Local version unchanged.");
        }

        private static void OnImportFailed(string packageName, string errorMessage)
        {
            UnsubscribeAll();
            Debug.LogWarning($"[DreamPark] SDK update import failed for '{packageName}': {errorMessage}");
        }

        private static void UnsubscribeAll()
        {
            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            AssetDatabase.importPackageFailed -= OnImportFailed;
        }
    }
}
#endif
