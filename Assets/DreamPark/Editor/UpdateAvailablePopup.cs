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
            EditorGUILayout.LabelField("Release notes:", EditorStyles.miniBoldLabel);
            notesScroll = EditorGUILayout.BeginScrollView(notesScroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(220));
            EditorGUILayout.LabelField(string.IsNullOrEmpty(releaseNotes) ? "(no release notes)" : releaseNotes, EditorStyles.wordWrappedLabel);
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

            // interactive: true → Unity shows its built-in import dialog.
            // Users can uncheck files to protect any local modifications they've
            // made under Assets/DreamPark/.
            AssetDatabase.ImportPackage(tempPath, true);
            Close();
        }

        private static void OnImportCompleted(string packageName)
        {
            UnsubscribeAll();
            SDKVersion.Reload();
            Debug.Log($"[DreamPark] SDK package '{packageName}' imported. Local version is now {SDKVersion.Current}.");
            // Re-fetch the manifest so the upload-gate / Check for Updates UI
            // reflects the new version immediately.
            SDKUpdateChecker.CheckForUpdate();
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
