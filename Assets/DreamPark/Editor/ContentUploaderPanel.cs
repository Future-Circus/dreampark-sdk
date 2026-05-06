#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEditor;
using UnityEngine;
using System.IO;
using DreamPark.API;
using System;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using Defective.JSON;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine.AddressableAssets;
using System.Collections.Generic;

namespace DreamPark {
    public class ContentUploaderPanel : EditorWindow
    {
        private string contentId = "";
        private string contentName = "";
        private string contentDescription = "";
        private string releaseNotes = "";
        private Texture2D logoTexture = null;
        private bool isUploading = false;
        private bool isLoadingMetadata = false;
        private int? lastSchemaVersion = null;
        private Vector2 uploadProgressScroll;

        private List<string> contentIdOptions = new List<string>();
        private int contentIdIndex = 0;

        // Team / collaborators state. Loaded from /api/content/:contentId/users
        private class TeamMember
        {
            public string userId;
            public string email;
        }
        private List<TeamMember> teamMembers = new List<TeamMember>();
        private string teamPrimaryOwnerId = null;
        private bool isLoadingTeam = false;
        private string teamErrorMessage = null;

        // Access state from the most recent FetchContentMetadata call.
        // null = unknown / not yet checked, true = we own (200 or 404 — fresh content),
        // false = 403 (someone else owns this contentId). Used to gate the panel
        // without leaking owner identity.
        private bool? isContentAccessibleByMe = null;
        private const string LogoPrefKeyPrefix = "DreamPark.ContentUploader.LogoPath.";
        private const string ContentIdPrefKey = "DreamPark.ContentUploader.LastContentId";
        private const string BuildAndroidPrefKey = "DreamPark.ContentUploader.Build.Android";
        private const string BuildIosPrefKey = "DreamPark.ContentUploader.Build.iOS";
        private const string BuildOsxPrefKey = "DreamPark.ContentUploader.Build.StandaloneOSX";
        private const string BuildWindowsPrefKey = "DreamPark.ContentUploader.Build.StandaloneWindows";
        private const string CleanBeforeEachTargetPrefKey = "DreamPark.ContentUploader.Build.CleanBeforeEachTarget";
        private static readonly HashSet<string> BuiltInUnityTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "Untagged",
            "Respawn",
            "Finish",
            "EditorOnly",
            "MainCamera",
            "Player",
            "GameController"
        };
        private bool buildAndroid = true;
        private bool buildIos = true;
        private bool buildOsx = true;
        private bool buildWindows = true;
        private bool cleanBeforeEachTarget = false;

        // priority 0 pins Content Uploader to the top of the DreamPark menu;
        // the big priority gap to the next item (Multiplayer at 100) creates
        // a separator so it sits in its own section.
        [MenuItem("DreamPark/Content Uploader", false, 0)]
        public static void ShowWindow()
        {
            GetWindow<ContentUploaderPanel>("Content Uploader");
        }

        // Listen for folder changes in Assets/Content and update content options

        private static FileSystemWatcher contentFolderWatcher;

        [InitializeOnLoadMethod]
        private static void InitContentFolderWatcher()
        {
            string path = Path.Combine(Application.dataPath, "Content");
            if (!Directory.Exists(path)) return;

            if (contentFolderWatcher != null)
            {
                contentFolderWatcher.EnableRaisingEvents = false;
                contentFolderWatcher.Dispose();
            }

            contentFolderWatcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            contentFolderWatcher.Created += (s, e) =>
            {
                if (Directory.Exists(e.FullPath))
                {
                    // Folder created
                    EditorApplication.delayCall += () =>
                    {
                        // Find all open ContentUploaderPanels and refresh their content options
                        foreach (ContentUploaderPanel win in Resources.FindObjectsOfTypeAll<ContentUploaderPanel>())
                        {
                            win.RefreshContentIdOptions();
                            win.Repaint();
                        }
                    };
                }
            };

            contentFolderWatcher.Deleted += (s, e) =>
            {
                EditorApplication.delayCall += () =>
                {
                    // Find all open ContentUploaderPanels and refresh their content options
                    foreach (ContentUploaderPanel win in Resources.FindObjectsOfTypeAll<ContentUploaderPanel>())
                    {
                        win.RefreshContentIdOptions();
                        win.Repaint();
                    }
                };
            };
        }

        private void OnEnable()
        {
            RefreshContentIdOptions();
            ContentAPI.UploadProgressChanged += OnUploadProgressChanged;
            AuthAPI.LoginStateChanged += OnLoginStateChanged;
            SDKUpdateChecker.ManifestUpdated += OnManifestUpdated;

            RestoreContentIdSelection();
            LoadBuildTargetSelection();

            LoadLogoSelection();
            FetchContentMetadata();
            FetchContentUsers();
        }

        private void OnDisable()
        {
            ContentAPI.UploadProgressChanged -= OnUploadProgressChanged;
            AuthAPI.LoginStateChanged -= OnLoginStateChanged;
            SDKUpdateChecker.ManifestUpdated -= OnManifestUpdated;
        }

        private void OnManifestUpdated() => Repaint();

        // Called by ContentIdSetupPopup after a successful rename. Refreshes the
        // dropdown options and selects the newly-named folder so the panel
        // immediately moves past the rename gate.
        private void OnContentFolderRenamed(string newFolderName)
        {
            RefreshContentIdOptions();
            int idx = contentIdOptions.IndexOf(newFolderName);
            if (idx >= 0)
            {
                contentIdIndex = idx;
                contentId = newFolderName;
                SaveContentIdSelection();
            }
            // Force fresh metadata + team list for the new id.
            releaseNotes = "";
            LoadLogoSelection();
            FetchContentMetadata();
            FetchContentUsers();
            Repaint();
        }

        // Repaint and re-fetch content lists when login state changes — e.g. after
        // AuthState detects an expired session, or after the user logs in via the popup.
        private void OnLoginStateChanged(bool isLoggedIn)
        {
            if (isLoggedIn)
            {
                FetchContentMetadata();
                FetchContentUsers();
            }
            else
            {
                // Wipe per-content state so we don't show a stale team list to the
                // next user who logs in.
                teamMembers.Clear();
                teamPrimaryOwnerId = null;
                teamErrorMessage = null;
            }
            Repaint();
        }

        private void OnUploadProgressChanged()
        {
            Repaint();
        }

        private void RefreshContentIdOptions()
        {
            contentIdOptions.Clear();
            string contentPath = Path.Combine(Application.dataPath, "Content");
            if (Directory.Exists(contentPath))
            {
                var dirs = Directory.GetDirectories(contentPath)
                    .Select(d => Path.GetFileName(d))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .OrderBy(d => d)
                    .ToList();
                contentIdOptions.AddRange(dirs);
            }
            else
            {
                Debug.LogWarning("No Assets/Content folder exists in this project.");
            }

            // Sanity - no invalid selection
            if (contentIdOptions.Count == 0)
            {
                contentId = "";
                contentIdIndex = 0;
            }
        }

        private void OnGUI()
        {
            // Auth gate: if logged out, the rest of the panel is hidden behind a
            // single Login CTA. Authentication itself happens in AuthPopup.
            if (!AuthAPI.isLoggedIn)
            {
                DrawLoginGate("Log in to use the Content Uploader.");
                return;
            }

            // Compact logged-in header — full email + Logout
            GUILayout.BeginHorizontal();
            string displayEmail = !string.IsNullOrEmpty(AuthAPI.email) ? AuthAPI.email : ("uid: " + AuthAPI.userId);
            EditorGUILayout.LabelField("Signed in as " + displayEmail, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Log out", GUILayout.Width(70)))
            {
                Logout();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("Upload New Content", EditorStyles.boldLabel);

            // ContentId dropdown
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Content ID", GUILayout.Width(EditorGUIUtility.labelWidth));
            int prevIndex = contentIdIndex;

            EditorGUI.BeginDisabledGroup(contentIdOptions.Count == 0);

            contentIdIndex = EditorGUILayout.Popup(contentIdIndex, contentIdOptions.ToArray());
            if (contentIdOptions.Count > 0)
            {
                // Only set contentId if a valid selection is available
                contentId = contentIdOptions[contentIdIndex];
            }
            else
            {
                contentId = "";
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();
            if (prevIndex != contentIdIndex)
            {
                releaseNotes = "";
                SaveContentIdSelection();
                LoadLogoSelection();
                FetchContentMetadata();
                FetchContentUsers();
            }

            if (contentIdOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("No content folders found under Assets/Content. Please create at least one game/content folder.", MessageType.Warning);
            }

            // ── Gate 1: placeholder folder name (SDK template default).
            //    User must rename YOUR_GAME_HERE to a real ID before doing anything.
            if (contentId == ContentIdSetupPopup.PlaceholderName)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "Please give your game an ID (ex: SuperAdventureLand). Your game folder is still the SDK template.",
                    MessageType.Warning);
                if (GUILayout.Button("Set Content ID", GUILayout.Height(28)))
                {
                    ContentIdSetupPopup.Show(contentId, OnContentFolderRenamed);
                }
                return;
            }

            // ── Gate 2: existing folder has unsafe characters (dashes, spaces,
            //    punctuation). Anything that fails the same regex the popup uses
            //    breaks Addressables / upload paths, so we refuse to proceed.
            if (!string.IsNullOrEmpty(contentId) && !ContentIdSetupPopup.IsValid(contentId))
            {
                GUILayout.Space(8);
                string why = ContentIdSetupPopup.ExplainInvalid(contentId) ?? "Invalid folder name.";
                EditorGUILayout.HelpBox(
                    $"Your content folder '{contentId}' has an invalid name: {why} Rename it to use only letters and digits.",
                    MessageType.Error);
                if (GUILayout.Button("Fix Folder Name", GUILayout.Height(28)))
                {
                    ContentIdSetupPopup.Show(contentId, OnContentFolderRenamed);
                }
                return;
            }

            // ── Gate 3: backend says we don't have access to this contentId.
            //    Some other user owns it. Show a generic message — NO emails, no
            //    owner identity, nothing that confirms anything about the other
            //    user. The fix is to rename the local folder to something else.
            if (isContentAccessibleByMe == false)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "You do not have access to this project. Rename your folder to a different ID to upload.",
                    MessageType.Error);
                if (GUILayout.Button("Rename Folder", GUILayout.Height(28)))
                {
                    ContentIdSetupPopup.Show(contentId, OnContentFolderRenamed);
                }
                return;
            }

            GUILayout.Space(5);
            contentName = EditorGUILayout.TextField("Name", contentName);
            EditorGUILayout.LabelField("Description");
            contentDescription = EditorGUILayout.TextArea(contentDescription, GUILayout.MinHeight(52));
            logoTexture = (Texture2D)EditorGUILayout.ObjectField("Logo", logoTexture, typeof(Texture2D), false);
            if (GUILayout.Button("Use Default Logo (Assets/Resources/Logos/<ContentId>.png)"))
            {
                string defaultLogoPath = $"Assets/Resources/Logos/{contentId}.png";
                logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(defaultLogoPath);
                SaveLogoSelection();
            }

            GUILayout.Space(5);
            EditorGUILayout.LabelField("Release Notes");
            releaseNotes = EditorGUILayout.TextArea(releaseNotes, GUILayout.MinHeight(70));
            if (isLoadingMetadata)
            {
                EditorGUILayout.HelpBox("Loading content metadata from backend...", MessageType.Info);
            }

            GUILayout.Space(10);
            DrawTeamSection();

            GUILayout.Space(10);
            DrawBuildTargetSelection();
            GUILayout.Space(6);

            // Upload gate: if the manifest fetch succeeded AND the local SDK
            // version is older than the published latest, block uploads. We
            // fail open on manifest errors (offline, 500, etc.) so a transient
            // backend issue doesn't lock everyone out.
            bool sdkOutOfDate = SDKUpdateChecker.ManifestFetchSucceeded
                                && !string.IsNullOrEmpty(SDKUpdateChecker.LatestVersion)
                                && SDKVersion.Compare(SDKVersion.Current, SDKUpdateChecker.LatestVersion) < 0;
            if (sdkOutOfDate)
            {
                EditorGUILayout.HelpBox(
                    $"Your DreamPark SDK is out of date (installed v{SDKVersion.Current}, latest v{SDKUpdateChecker.LatestVersion}). " +
                    "Update before uploading content to avoid version drift between creators.",
                    MessageType.Error);
                if (GUILayout.Button("Update SDK Now", GUILayout.Height(28)))
                {
                    UpdateAvailablePopup.Show(
                        SDKVersion.Current,
                        SDKUpdateChecker.LatestVersion,
                        SDKUpdateChecker.LatestReleaseNotes,
                        SDKUpdateChecker.LatestDownloadUrl);
                }
                GUILayout.Space(6);
            }

            GUI.enabled = !isUploading && !string.IsNullOrEmpty(contentId) && !string.IsNullOrEmpty(contentName) && !sdkOutOfDate;
            if (GUILayout.Button("Compile & Upload", GUILayout.Height(32)))
            {
                if (!SaveModifiedScenesBeforeCompile())
                {
                    EditorUtility.DisplayDialog("Compile Cancelled", "Save all modified scenes before compiling.", "OK");
                    return;
                }
                SaveLogoSelection();
                UploadContent(true);
            }
            if (GUILayout.Button("Try Reupload", GUILayout.Height(32)))
            {
                SaveLogoSelection();
                UploadContent(false);
            }
            if (GUILayout.Button("Preflight Tag Check (No Upload)", GUILayout.Height(24)))
            {
                RunPreflightTagCheck();
            }
            if (GUILayout.Button("Fix Preflight Tag Mismatches", GUILayout.Height(24)))
            {
                RunPreflightTagCheck(autoFixMismatches: true);
            }
            GUI.enabled = true;

            if (isUploading)
            {
                GUILayout.Space(10);
                GUILayout.Label("Uploading...", EditorStyles.miniLabel);
            }

            DrawUploadProgressArea();
        }

        private void DrawUploadProgressArea()
        {
            var progressEntries = ContentAPI.GetUploadProgressSnapshot();
            if (!isUploading && (progressEntries == null || progressEntries.Count == 0))
            {
                return;
            }

            GUILayout.Space(12);
            GUILayout.Label("Upload Progress", EditorStyles.boldLabel);

            if (progressEntries == null || progressEntries.Count == 0)
            {
                EditorGUILayout.HelpBox("Collecting files and initializing upload...", MessageType.Info);
                return;
            }

            float overall = progressEntries.Average(e => e.progress);
            EditorGUILayout.LabelField($"Overall: {(overall * 100f):0.0}% ({progressEntries.Count} files)");
            Rect overallRect = GUILayoutUtility.GetRect(18, 18, "TextField");
            EditorGUI.ProgressBar(overallRect, overall, $"{overall * 100f:0.0}%");
            GUILayout.Space(6);

            uploadProgressScroll = EditorGUILayout.BeginScrollView(uploadProgressScroll, GUILayout.MinHeight(140), GUILayout.MaxHeight(220));
            foreach (var entry in progressEntries)
            {
                string status = entry.failed ? "Failed" : (entry.completed ? "Done" : "Uploading");
                string header = $"{entry.platform} / {entry.fileName}";
                string sizeText = $"{FormatBytes(entry.uploadedBytes)} / {FormatBytes(entry.totalBytes)}";

                EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField($"{status}  -  {sizeText}  -  {(entry.progress * 100f):0.0}%", EditorStyles.miniLabel);
                Rect rowRect = GUILayoutUtility.GetRect(18, 18, "TextField");
                EditorGUI.ProgressBar(rowRect, Mathf.Clamp01(entry.progress), $"{entry.progress * 100f:0.0}%");
                GUILayout.Space(4);
            }
            EditorGUILayout.EndScrollView();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024d && unit < units.Length - 1)
            {
                value /= 1024d;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }

        private void DrawBuildTargetSelection()
        {
            EditorGUILayout.LabelField("Build Targets", EditorStyles.boldLabel);

            bool changed = false;

            bool newBuildAndroid = EditorGUILayout.ToggleLeft("Android", buildAndroid);
            if (newBuildAndroid != buildAndroid) { buildAndroid = newBuildAndroid; changed = true; }

            bool newBuildIos = EditorGUILayout.ToggleLeft("iOS", buildIos);
            if (newBuildIos != buildIos) { buildIos = newBuildIos; changed = true; }

            bool newBuildOsx = EditorGUILayout.ToggleLeft("StandaloneOSX", buildOsx);
            if (newBuildOsx != buildOsx) { buildOsx = newBuildOsx; changed = true; }

            bool newBuildWindows = EditorGUILayout.ToggleLeft("StandaloneWindows", buildWindows);
            if (newBuildWindows != buildWindows) { buildWindows = newBuildWindows; changed = true; }

            bool newCleanBeforeEachTarget = EditorGUILayout.ToggleLeft("Clean Addressables Before Each Target", cleanBeforeEachTarget);
            if (newCleanBeforeEachTarget != cleanBeforeEachTarget) { cleanBeforeEachTarget = newCleanBeforeEachTarget; changed = true; }

            if (!buildAndroid && !buildIos && !buildOsx && !buildWindows)
            {
                EditorGUILayout.HelpBox("Select at least one target for Compile & Upload.", MessageType.Warning);
            }

            if (changed)
            {
                SaveBuildTargetSelection();
            }
        }

        private void LoadLogoSelection()
        {
            if (string.IsNullOrEmpty(contentId))
            {
                logoTexture = null;
                return;
            }

            string savedPath = EditorPrefs.GetString(LogoPrefKeyPrefix + contentId, "");
            if (!string.IsNullOrEmpty(savedPath))
            {
                logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(savedPath);
                if (logoTexture != null)
                {
                    return;
                }
            }

            string defaultLogoPath = $"Assets/Resources/Logos/{contentId}.png";
            logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(defaultLogoPath);
        }

        private void SaveLogoSelection()
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return;
            }

            if (logoTexture == null)
            {
                EditorPrefs.DeleteKey(LogoPrefKeyPrefix + contentId);
                return;
            }

            string logoPath = AssetDatabase.GetAssetPath(logoTexture);
            if (!string.IsNullOrEmpty(logoPath))
            {
                EditorPrefs.SetString(LogoPrefKeyPrefix + contentId, logoPath);
            }
        }

        private void RestoreContentIdSelection()
        {
            if (contentIdOptions.Count == 0)
            {
                contentId = "";
                contentIdIndex = 0;
                return;
            }

            string savedContentId = EditorPrefs.GetString(ContentIdPrefKey, "");
            if (!string.IsNullOrEmpty(savedContentId))
            {
                int savedIndex = contentIdOptions.IndexOf(savedContentId);
                if (savedIndex >= 0)
                {
                    contentIdIndex = savedIndex;
                    contentId = contentIdOptions[contentIdIndex];
                    return;
                }
            }

            // Fall back to project prefix if no saved selection exists.
            var defaultPrefix = ContentProcessor.GetGamePrefix();
            if (!string.IsNullOrEmpty(defaultPrefix))
            {
                int idx = contentIdOptions.IndexOf(defaultPrefix);
                if (idx >= 0)
                {
                    contentIdIndex = idx;
                    contentId = contentIdOptions[contentIdIndex];
                    SaveContentIdSelection();
                    return;
                }
            }

            contentIdIndex = 0;
            contentId = contentIdOptions[0];
            SaveContentIdSelection();
        }

        private void SaveContentIdSelection()
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return;
            }

            EditorPrefs.SetString(ContentIdPrefKey, contentId);
        }

        private void LoadBuildTargetSelection()
        {
            buildAndroid = EditorPrefs.GetBool(BuildAndroidPrefKey, true);
            buildIos = EditorPrefs.GetBool(BuildIosPrefKey, true);
            buildOsx = EditorPrefs.GetBool(BuildOsxPrefKey, true);
            buildWindows = EditorPrefs.GetBool(BuildWindowsPrefKey, true);
            cleanBeforeEachTarget = EditorPrefs.GetBool(CleanBeforeEachTargetPrefKey, false);
        }

        private void SaveBuildTargetSelection()
        {
            EditorPrefs.SetBool(BuildAndroidPrefKey, buildAndroid);
            EditorPrefs.SetBool(BuildIosPrefKey, buildIos);
            EditorPrefs.SetBool(BuildOsxPrefKey, buildOsx);
            EditorPrefs.SetBool(BuildWindowsPrefKey, buildWindows);
            EditorPrefs.SetBool(CleanBeforeEachTargetPrefKey, cleanBeforeEachTarget);
        }

        private static bool SaveModifiedScenesBeforeCompile()
        {
            if (!EditorSceneManager.SaveOpenScenes())
            {
                return false;
            }

            AssetDatabase.SaveAssets();
            return true;
        }

        private void RunPreflightTagCheck(bool autoFixMismatches = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(contentId))
                {
                    EditorUtility.DisplayDialog("Preflight Tag Check", "Select a Content ID first.", "OK");
                    return;
                }

                var refresh = TagLayerSchemaSyncUtility.ForceRefreshContentPrefabs(contentId);
                var snapshot = TagLayerSchemaSyncUtility.ReadLocalTagManager();

                string contentRoot = Path.Combine("Assets", "Content", contentId);
                if (!Directory.Exists(contentRoot))
                {
                    EditorUtility.DisplayDialog("Preflight Tag Check", $"Content path not found: {contentRoot}", "OK");
                    return;
                }

                string[] prefabPaths = Directory.GetFiles(contentRoot, "*.prefab", SearchOption.AllDirectories);
                int checkedCount = 0;
                int mismatchCount = 0;
                int unknownTagCount = 0;
                int fixedCount = 0;

                foreach (var rawPath in prefabPaths)
                {
                    string assetPath = rawPath.Replace("\\", "/");
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null)
                    {
                        continue;
                    }

                    checkedCount++;
                    string resolvedTag = prefab.tag ?? "";
                    string serializedRootTag = ExtractFirstSerializedTag(assetPath);

                    if (!string.IsNullOrEmpty(resolvedTag)
                        && !snapshot.tags.Contains(resolvedTag)
                        && !BuiltInUnityTags.Contains(resolvedTag))
                    {
                        unknownTagCount++;
                        Debug.LogWarning($"[TagPreflight] Unknown resolved tag '{resolvedTag}' in {assetPath}");
                    }

                    if (!string.IsNullOrEmpty(serializedRootTag)
                        && !string.Equals(serializedRootTag, resolvedTag, StringComparison.Ordinal))
                    {
                        mismatchCount++;
                        Debug.LogWarning($"[TagPreflight] Mismatch in {assetPath} serialized='{serializedRootTag}' resolved='{resolvedTag}'");

                        if (autoFixMismatches && IsKnownTag(serializedRootTag, snapshot.tags))
                        {
                            if (TrySetPrefabRootTag(assetPath, serializedRootTag))
                            {
                                fixedCount++;
                            }
                        }
                    }
                }

                if (autoFixMismatches && fixedCount > 0)
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    // Re-read once after fixes to report current truth.
                    RunPreflightTagCheck(autoFixMismatches: false);
                    Debug.Log($"[TagPreflight] Auto-fix updated {fixedCount} prefabs to match serialized root tags.");
                    return;
                }

                string message =
                    $"Prefabs refreshed: {refresh.prefabsReserialized}/{refresh.prefabsProcessed}\n" +
                    $"Prefabs checked: {checkedCount}\n" +
                    $"Serialized/Resolved mismatches: {mismatchCount}\n" +
                    $"Resolved tags missing from TagManager (non-built-in): {unknownTagCount}" +
                    (autoFixMismatches ? $"\nMismatches auto-fixed: {fixedCount}" : "");

                if (mismatchCount > 0 || unknownTagCount > 0)
                {
                    Debug.LogWarning("[TagPreflight] " + message);
                    EditorUtility.DisplayDialog("Preflight Tag Check", message + "\n\nSee Console warnings for details.", "OK");
                }
                else
                {
                    Debug.Log("[TagPreflight] " + message);
                    EditorUtility.DisplayDialog("Preflight Tag Check", message, "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TagPreflight] Failed: " + ex);
                EditorUtility.DisplayDialog("Preflight Tag Check Failed", ex.Message, "OK");
            }
        }

        private static bool IsKnownTag(string tag, List<string> tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            if (BuiltInUnityTags.Contains(tag))
            {
                return true;
            }

            return tags != null && tags.Contains(tag);
        }

        private static bool TrySetPrefabRootTag(string assetPath, string tag)
        {
            try
            {
                var root = PrefabUtility.LoadPrefabContents(assetPath);
                if (root == null)
                {
                    return false;
                }

                root.tag = tag;
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                PrefabUtility.UnloadPrefabContents(root);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TagPreflight] Failed to auto-fix {assetPath}: {e.Message}");
                return false;
            }
        }

        private static string ExtractFirstSerializedTag(string assetPath)
        {
            string text = File.ReadAllText(assetPath);
            var match = Regex.Match(text, @"^\s*m_TagString:\s*(.+)$", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private string GetLogoAddress()
        {
            if (logoTexture == null || string.IsNullOrEmpty(contentId))
            {
                return null;
            }

            string logoPath = AssetDatabase.GetAssetPath(logoTexture);
            if (string.IsNullOrEmpty(logoPath))
            {
                return null;
            }

            return $"{contentId}/Logos/{Path.GetFileNameWithoutExtension(logoPath)}";
        }

        private void FetchContentMetadata()
        {
            // Reset access state for the new contentId so the gate UI doesn't
            // briefly show stale "no access" between selections.
            isContentAccessibleByMe = null;

            if (string.IsNullOrEmpty(contentId) || !AuthAPI.isLoggedIn)
            {
                return;
            }

            isLoadingMetadata = true;
            ContentAPI.GetContent(contentId, (success, response) =>
            {
                isLoadingMetadata = false;

                // Translate response → access state.
                //   200 → we own it (success=true), accessible.
                //   404 → content doesn't exist yet (we'd auto-create on upload), accessible.
                //   403 → someone else owns this contentId, NOT accessible.
                //   anything else → leave unknown so we don't punish transient failures.
                if (success)
                {
                    isContentAccessibleByMe = true;
                }
                else if (response != null && response.statusCode == 404)
                {
                    isContentAccessibleByMe = true;
                }
                else if (response != null && response.statusCode == 403)
                {
                    isContentAccessibleByMe = false;
                }

                if (!success || response?.json == null || !response.json.HasField("content"))
                {
                    Repaint();
                    return;
                }

                JSONObject content = response.json.GetField("content");
                if (content.HasField("contentName"))
                {
                    contentName = content.GetField("contentName").stringValue ?? contentName;
                }
                if (content.HasField("contentDescription"))
                {
                    contentDescription = content.GetField("contentDescription").stringValue ?? contentDescription;
                }
                Repaint();
            });
        }

        private void FetchContentUsers()
        {
            // Reset state for the new contentId
            teamMembers.Clear();
            teamPrimaryOwnerId = null;
            teamErrorMessage = null;

            if (string.IsNullOrEmpty(contentId) || !AuthAPI.isLoggedIn)
            {
                isLoadingTeam = false;
                Repaint();
                return;
            }

            isLoadingTeam = true;
            ContentAPI.ListContentUsers(contentId, (success, response) =>
            {
                isLoadingTeam = false;

                if (!success || response?.json == null)
                {
                    // 403 / 404 / network — show a friendly message instead of raw error
                    if (response != null && response.statusCode == 403)
                    {
                        teamErrorMessage = "You are not an owner of this content — team list is hidden.";
                    }
                    else if (response != null && response.statusCode == 404)
                    {
                        teamErrorMessage = "This content has not been uploaded yet. Compile & upload first to manage collaborators.";
                    }
                    else
                    {
                        teamErrorMessage = "Could not load team: " + (response?.error ?? "unknown error");
                    }
                    Repaint();
                    return;
                }

                if (response.json.HasField("contentOwner"))
                {
                    teamPrimaryOwnerId = response.json.GetField("contentOwner").stringValue;
                }
                if (response.json.HasField("owners"))
                {
                    var ownersField = response.json.GetField("owners");
                    if (ownersField != null && ownersField.list != null)
                    {
                        foreach (var entry in ownersField.list)
                        {
                            string userId = entry.HasField("userId") ? entry.GetField("userId").stringValue : null;
                            string emailValue = entry.HasField("email") ? entry.GetField("email").stringValue : null;
                            if (!string.IsNullOrEmpty(userId))
                            {
                                teamMembers.Add(new TeamMember { userId = userId, email = emailValue });
                            }
                        }
                    }
                }
                Repaint();
            });
        }

        // Reusable "you must log in" gate for any editor panel. Other DreamPark
        // panels (e.g. SDKPublishPanel) draw the same lockout when the user is
        // signed out.
        public static void DrawLoginGate(string reason)
        {
            EditorGUILayout.HelpBox(reason, MessageType.Info);
            GUILayout.Space(6);
            if (GUILayout.Button("Log in", GUILayout.Height(28)))
            {
                AuthPopup.Show();
            }
        }

        private void DrawTeamSection()
        {
            GUILayout.Label("Team", EditorStyles.boldLabel);

            if (string.IsNullOrEmpty(contentId))
            {
                EditorGUILayout.HelpBox("Select a content folder to manage its team.", MessageType.None);
                return;
            }
            if (!AuthAPI.isLoggedIn)
            {
                EditorGUILayout.HelpBox("Log in to manage collaborators.", MessageType.None);
                return;
            }
            if (isLoadingTeam)
            {
                EditorGUILayout.HelpBox("Loading team...", MessageType.Info);
                return;
            }
            if (!string.IsNullOrEmpty(teamErrorMessage))
            {
                EditorGUILayout.HelpBox(teamErrorMessage, MessageType.Info);
                return;
            }

            if (teamMembers.Count == 0)
            {
                EditorGUILayout.HelpBox("No collaborators yet.", MessageType.None);
            }
            else
            {
                foreach (var member in teamMembers.ToArray()) // ToArray so we can mutate during iteration
                {
                    GUILayout.BeginHorizontal();
                    string label = !string.IsNullOrEmpty(member.email) ? member.email : "(unknown email — uid: " + member.userId + ")";
                    bool isPrimary = !string.IsNullOrEmpty(teamPrimaryOwnerId) && teamPrimaryOwnerId == member.userId;
                    bool isSelf = !string.IsNullOrEmpty(AuthAPI.userId) && member.userId == AuthAPI.userId;
                    if (isPrimary) label += "  (primary)";
                    if (isSelf) label += "  (you)";
                    EditorGUILayout.LabelField(label);

                    // Disable the trash button for both the primary owner AND
                    // yourself. Removing the primary always 400s server-side;
                    // removing yourself is also blocked here as a safety net so
                    // you can't accidentally orphan your own access — if you
                    // really want to leave a project, ask another owner to
                    // remove you (separate "Leave project" flow can be added
                    // later if needed).
                    bool canRemove = !isPrimary && !isSelf;
                    GUI.enabled = canRemove;
                    string tooltip = isPrimary ? "Primary owner cannot be removed."
                                   : isSelf ? "You cannot remove yourself."
                                   : "Remove collaborator";
                    // Use Unity's built-in trash icon (auto-themes light/dark)
                    // instead of a unicode glyph — renders consistently across
                    // platforms and matches the look of other editor buttons.
                    var trashIcon = EditorGUIUtility.IconContent("TreeEditor.Trash");
                    if (GUILayout.Button(new GUIContent(trashIcon.image, tooltip), GUILayout.Width(30)))
                    {
                        if (EditorUtility.DisplayDialog("Remove collaborator?",
                                $"Remove {label} from {contentId}?",
                                "Remove", "Cancel"))
                        {
                            RemoveTeamMember(member.userId);
                        }
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(4);
            if (GUILayout.Button("+ Add team member"))
            {
                AddTeamMemberPopup.Show(contentId, OnAddTeamMemberSubmitted);
            }
        }

        private void OnAddTeamMemberSubmitted(string emailToAdd)
        {
            if (string.IsNullOrEmpty(emailToAdd) || string.IsNullOrEmpty(contentId)) return;

            ContentAPI.AddContentUser(contentId, emailToAdd, (success, response) =>
            {
                if (success)
                {
                    Debug.Log($"✅ Added '{emailToAdd}' to {contentId}");
                    FetchContentUsers();
                }
                else
                {
                    string err = ExtractServerErrorMessage(response);
                    Debug.LogError($"[DreamPark] Add collaborator failed (status={response?.statusCode}, raw={response?.rawText}): {err}");
                    EditorUtility.DisplayDialog("Could not add collaborator", err, "OK");
                }
            });
        }

        private void RemoveTeamMember(string userId)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(contentId)) return;

            ContentAPI.RemoveContentUser(contentId, userId, (success, response) =>
            {
                if (success)
                {
                    Debug.Log($"✅ Removed user {userId} from {contentId}");
                    FetchContentUsers();
                }
                else
                {
                    string err = ExtractServerErrorMessage(response);
                    Debug.LogError($"[DreamPark] Remove collaborator failed (status={response?.statusCode}, raw={response?.rawText}): {err}");
                    EditorUtility.DisplayDialog("Could not remove collaborator", err, "OK");
                }
            });
        }

        // Defensive error-string extraction. The previous one-liner sometimes
        // surfaced the raw "HTTP/1.1 400 Bad Request" string instead of the
        // server's clean { error: "..." } message — that happens when the
        // pre-parsed `response.json` is null even though the body is valid JSON
        // (e.g. JSON parser hiccup on certain bodies). This re-parses rawText
        // as a fallback so we surface the server's intent whenever possible.
        private static string ExtractServerErrorMessage(DreamPark.API.DreamParkAPI.APIResponse response)
        {
            if (response == null) return "Unknown error.";

            // Path 1: pre-parsed JSON has an `error` field.
            if (response.json != null && response.json.HasField("error"))
            {
                var msg = response.json.GetField("error").stringValue;
                if (!string.IsNullOrEmpty(msg)) return msg;
            }

            // Path 2: rawText is JSON but APIResponse failed to parse it. Try once more.
            if (!string.IsNullOrEmpty(response.rawText)
                && (response.rawText.TrimStart().StartsWith("{") || response.rawText.TrimStart().StartsWith("[")))
            {
                try
                {
                    var parsed = new Defective.JSON.JSONObject(response.rawText);
                    if (parsed != null && parsed.HasField("error"))
                    {
                        var msg = parsed.GetField("error").stringValue;
                        if (!string.IsNullOrEmpty(msg)) return msg;
                    }
                }
                catch
                {
                    // Fall through to next path.
                }
            }

            // Path 3: transport-level error string from UnityWebRequest.
            return string.IsNullOrEmpty(response.error) ? "Unknown error." : response.error;
        }

        public void Login(string email, string password)
        {
            AuthAPI.Login(email, password, (success, response) =>
            {
                if (success)
                {
                    Debug.Log("Login successful: " + response.json.Print());
                }
                else
                {
                    Debug.LogError("Failed to login: " + response.error);
                }
            });
        }

        public void Logout()
        {
            AuthAPI.Logout((success, response) =>
            {
                if (success)
                {
                    Debug.Log("Logout successful: " + response.json.Print());
                }
                else
                {
                    Debug.LogError("Failed to logout: " + response.error);
                }
            });
        }

        private void UploadContent(bool build = false)
        {
            try
            {
                if (string.IsNullOrEmpty(contentId))
                {
                    Debug.LogError("❌ Game ID not detected. Make sure your content folder exists under Assets/Content.");
                    return;
                }

                isUploading = true;

                Debug.Log($"🚀 Uploading content for {contentId}...");

                ContentAPI.GetContent(contentId, (exists, response) =>
                {
                    Action uploadBuiltContent = () =>
                    {
                        var contentDirectory = response != null ? response.json : null;
                        var versionNumber = contentDirectory != null && contentDirectory.HasField("content") && contentDirectory.GetField("content").HasField("versions")
                            ? contentDirectory.GetField("content").GetField("versions").list.Count + 1
                            : 1;
                        var targetUrl = $"{DreamParkAPI.devBaseUrl}/app/content/addressables/{contentId}/{versionNumber}";

                        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                        string serverDataPath = Path.Combine(projectRoot, "ServerData");

                        bool buildSuccess = true;
                        try
                        {
                            if (build)
                            {
                                if (!buildAndroid && !buildIos && !buildOsx && !buildWindows)
                                {
                                    throw new Exception("Select at least one build target.");
                                }

                                if (Directory.Exists(serverDataPath))
                                {
                                    Directory.Delete(serverDataPath, true);
                                    Debug.Log("ServerData folder deleted successfully.");
                                }
                                else
                                {
                                    Debug.LogWarning("ServerData folder does not exist.");
                                }
                                Caching.ClearCache();
                                Addressables.ClearResourceLocators();
                                AssetDatabase.Refresh();
                                Debug.Log("✅ Cache purge complete.");

                                var settings = AddressableAssetSettingsDefaultObject.Settings;
                                settings.MonoScriptBundleNaming = MonoScriptBundleNaming.Custom;
                                settings.MonoScriptBundleCustomNaming = contentId + "_";
                                // Pin catalog filename to the contentId so every build produces
                                // "catalog_{contentId}.json" instead of using the app bundleVersion.
                                Debug.Log($"📛 [BEFORE] OverridePlayerVersion = '{settings.OverridePlayerVersion}', PlayerBuildVersion = '{settings.PlayerBuildVersion}'");
                                settings.OverridePlayerVersion = contentId;
                                Debug.Log($"📛 [AFTER]  OverridePlayerVersion = '{settings.OverridePlayerVersion}', PlayerBuildVersion = '{settings.PlayerBuildVersion}'");
                                EditorUtility.SetDirty(settings);
                                AssetDatabase.SaveAssets();

                                //refresh database with new addressables
                                ContentProcessor.ForceUpdateContent(contentId);
                                SyncLogoAddressableEntry();
                                ContentProcessor.EnforceContentNamespaces(contentId);
                                buildSuccess &= ContentProcessor.BuildUnityPackage(contentId);

                                if (!buildSuccess) throw new Exception("Unity package build failed");
                                if (buildAndroid)
                                {
                                    buildSuccess &= BuildForTarget(BuildTarget.Android, BuildTargetGroup.Android, $"{targetUrl}/Android", contentId);
                                    if (!buildSuccess) throw new Exception("Android build failed");
                                }
                                if (buildIos)
                                {
                                    buildSuccess &= BuildForTarget(BuildTarget.iOS, BuildTargetGroup.iOS, $"{targetUrl}/iOS", contentId);
                                    if (!buildSuccess) throw new Exception("iOS build failed");
                                }
                                if (buildOsx)
                                {
                                    buildSuccess &= BuildForTarget(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone, $"{targetUrl}/StandaloneOSX", contentId);
                                    if (!buildSuccess) throw new Exception("OSX build failed");
                                }
                                if (buildWindows)
                                {
                                    buildSuccess &= BuildForTarget(BuildTarget.StandaloneWindows, BuildTargetGroup.Standalone, $"{targetUrl}/StandaloneWindows", contentId);
                                    if (!buildSuccess) throw new Exception("Windows build failed");
                                }
                            }
                            ContentAPI.UploadContent(contentId, releaseNotes, lastSchemaVersion, (success, apiResponse) =>
                            {
                                if (success)
                                {
                                    Debug.Log("✅ Content uploaded successfully");
                                    EditorUtility.DisplayDialog("Success", $"'{contentName}' uploaded successfully!", "OK");
                                }
                                else
                                {
                                    Debug.LogError($"❌ Content uploaded failed: {apiResponse.error}");
                                    EditorUtility.DisplayDialog("Error", $"Upload failed: {apiResponse.error}", "OK");
                                }
                                isUploading = false;
                            });
                        }
                        catch (Exception e)
                        {
                            Debug.LogError("❌ Addressable build failed: " + e);
                            EditorUtility.DisplayDialog("Error", $"Error: {e.Message}", "OK");
                            isUploading = false;
                        }
                    };

                    Action continueAfterSchemaSync = () =>
                    {
                        SyncTagLayerSchema((syncSuccess, syncError) =>
                        {
                            if (!syncSuccess)
                            {
                                Debug.LogError($"❌ Schema sync failed: {syncError}");
                                EditorUtility.DisplayDialog("Schema Sync Failed", syncError, "OK");
                                isUploading = false;
                                return;
                            }
                            uploadBuiltContent();
                        });
                    };

                    if (exists)
                    {
                        Debug.Log("Content found: " + response.json.Print());
                        JSONObject metadataUpdate = new JSONObject();
                        metadataUpdate.AddField("contentName", contentName);
                        metadataUpdate.AddField("contentDescription", contentDescription);
                        string logoAddress = GetLogoAddress();
                        if (!string.IsNullOrEmpty(logoAddress))
                        {
                            metadataUpdate.AddField("logoAddress", logoAddress);
                        }

                        ContentAPI.UpdateContent(contentId, metadataUpdate, (updateSuccess, updateResponse) =>
                        {
                            if (!updateSuccess)
                            {
                                Debug.LogError($"❌ Failed to update content metadata: {updateResponse.error}");
                                EditorUtility.DisplayDialog("Error", $"Metadata update failed: {updateResponse.error}", "OK");
                                isUploading = false;
                                return;
                            }
                            continueAfterSchemaSync();
                        });
                        return;
                    }
                    else if (response.statusCode == 403)
                    {
                        Debug.LogError($"❌ Content '{contentId}' is owned by another user.");
                        EditorUtility.DisplayDialog("Access Denied",
                            $"Content '{contentId}' is owned by another user. Choose a different folder name in Assets/Content/ or ask the content owner to add you as a collaborator.",
                            "OK");
                        isUploading = false;
                        return;
                    }
                    else if (response.statusCode == 404)
                    {
                        // Content doesn't exist yet — create it
                        ContentAPI.AddContent(contentId, contentName, contentDescription, GetLogoAddress(), (success, response) =>
                        {
                            if (success)
                            {
                                Debug.Log($"✅ Content '{contentName}' uploaded successfully!");
                                UploadContent(build);
                            }
                            else
                            {
                                Debug.LogError($"❌ Failed to create new content: {response.error}");
                                EditorUtility.DisplayDialog("Error", $"Failed to create new content: {response.error}", "OK");
                                isUploading = false;
                            }
                        });
                    }
                    else
                    {
                        // Other errors (401, 500, network issues)
                        Debug.LogError($"❌ Failed to check content: {response.error}. Make sure you are logged in.");
                        EditorUtility.DisplayDialog("Error",
                            $"Failed to check content: {response.error}\n\nMake sure you are logged in with a valid session.",
                            "OK");
                        isUploading = false;
                    }
                });
            }
            catch (Exception e)
            {
                Debug.LogError("❌ Upload failed: " + e);
                EditorUtility.DisplayDialog("Error", $"Error: {e.Message}", "OK");
                isUploading = false;
            }
        }

        private void SyncTagLayerSchema(Action<bool, string> callback)
        {
            try
            {
                var local = TagLayerSchemaSyncUtility.ReadLocalTagManager();
                ContentAPI.SyncTagLayerSchema(contentId, 0, local.tags, local.layers, (syncSuccess, syncResponse) =>
                {
                    // If endpoint is unavailable, allow legacy backend and continue.
                    if (!syncSuccess && syncResponse != null && (syncResponse.statusCode == 404 || syncResponse.statusCode == 405))
                    {
                        lastSchemaVersion = null;
                        Debug.LogWarning("[TagLayerSchema] Schema endpoint missing on backend; continuing without schema enforcement.");
                        callback?.Invoke(true, null);
                        return;
                    }

                    if (!syncSuccess)
                    {
                        string backendError = syncResponse?.error;
                        if (syncResponse?.json != null && syncResponse.json.HasField("error"))
                        {
                            backendError = syncResponse.json.GetField("error").stringValue ?? backendError;
                        }
                        callback?.Invoke(false, backendError ?? "Failed to sync schema");
                        return;
                    }

                    var syncResult = ContentAPI.ParseTagLayerSchemaSyncResult(syncResponse);
                    if (syncResult.layerConflicts != null && syncResult.layerConflicts.Count > 0)
                    {
                        string conflictMessage = "Layer index conflicts detected:\n" + string.Join("\n", syncResult.layerConflicts);
                        callback?.Invoke(false, conflictMessage);
                        return;
                    }

                    // Strict sync before build:
                    // remap prefab tags by index changes, then apply canonical schema exactly.
                    var targetTags = TagLayerSchemaSyncUtility.BuildTargetTagOrder(syncResult.schema.tags, local.tags, preserveLocalExtras: true);
                    var remap = TagLayerSchemaSyncUtility.BuildTagRemapByIndex(local.tags, targetTags);
                    var remapResult = TagLayerSchemaSyncUtility.RemapContentPrefabsByTagName(contentId, remap);
                    if (remapResult.replacements > 0)
                    {
                        Debug.Log($"[TagLayerSchema] Remapped prefab tags for {contentId}: {remapResult.replacements} replacements across {remapResult.filesChanged} prefabs.");
                    }

                    var applyResult = TagLayerSchemaSyncUtility.ApplyCanonicalSchema(syncResult.schema.tags, syncResult.schema.layers, preserveLocalExtras: true);
                    if (!string.IsNullOrEmpty(applyResult.error))
                    {
                        callback?.Invoke(false, applyResult.error);
                        return;
                    }

                    bool schemaChangedLocally = remapResult.replacements > 0 || applyResult.changed;
                    if (schemaChangedLocally)
                    {
                        var refreshResult = TagLayerSchemaSyncUtility.ForceRefreshContentPrefabs(contentId);
                        if (refreshResult.prefabsProcessed > 0)
                        {
                            Debug.Log($"[TagLayerSchema] Force refreshed prefab imports for {contentId}: {refreshResult.prefabsReserialized}/{refreshResult.prefabsProcessed}");
                        }
                    }

                    lastSchemaVersion = syncResult.schemaVersion > 0 ? syncResult.schemaVersion : (int?)null;
                    if (syncResult.updated)
                    {
                        Debug.Log($"[TagLayerSchema] Schema updated to v{syncResult.schemaVersion}. Added tags: {syncResult.addedTags.Count}, layers: {syncResult.addedLayers.Count}");
                    }
                    else
                    {
                        Debug.Log($"[TagLayerSchema] Schema already aligned at v{syncResult.schemaVersion}.");
                    }
                    if (syncResult.proposalPending)
                    {
                        Debug.LogWarning($"[TagLayerSchema] Content '{syncResult.proposalContentId ?? contentId}' has pending schema additions awaiting acceptance by core.");
                    }
                    callback?.Invoke(true, null);
                });
            }
            catch (Exception e)
            {
                callback?.Invoke(false, e.Message);
            }
        }

        private void SyncLogoAddressableEntry()
        {
            if (logoTexture == null)
            {
                return;
            }

            string logoPath = AssetDatabase.GetAssetPath(logoTexture);
            if (string.IsNullOrEmpty(logoPath))
            {
                Debug.LogWarning("No asset path found for selected logo texture.");
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(logoPath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning("No GUID found for selected logo texture.");
                return;
            }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("Addressable settings not found; skipping logo addressable entry.");
                return;
            }

            string groupName = $"{contentId}-Logos";
            var group = settings.groups.FirstOrDefault(g => g != null && g.Name == groupName)
                ?? settings.CreateGroup(groupName, false, false, true, new List<AddressableAssetGroupSchema>
                {
                    (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(BundledAssetGroupSchema)),
                    (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(ContentUpdateGroupSchema))
                });

            var bag = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
            bag.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
            bag.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
            bag.UseAssetBundleCache = true;
            bag.UseAssetBundleCrc = true;
            bag.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bag.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;

            var entry = settings.CreateOrMoveEntry(guid, group, false, false);
            entry.address = GetLogoAddress();
            entry.SetLabel(contentId, true, true);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"✅ Synced logo addressable: {entry.address}");
        }
        public static bool BuildForTarget(BuildTarget target, BuildTargetGroup group, string targetUrl, string contentId = null)
        {
            try
            {

                if (!BuildPipeline.IsBuildTargetSupported(group, target))
                {
                    throw new System.Exception($"❌ Build target {target} is not supported. Please ensure the necessary build support is installed.");
                }

                var settings = AddressableAssetSettingsDefaultObject.Settings;

                Debug.Log($"Switching build target to {target} in {group}");

                // 🔹 Switch build target (Addressables only respects the *active* target)
                if (EditorUserBuildSettings.activeBuildTarget != target)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);

                Debug.Log($"Setting RemoteLoadPath to {targetUrl}");
                string profileId = settings.activeProfileId;
                string existingValue = settings.profileSettings.GetValueByName(profileId, "RemoteLoadPath");
                if (string.IsNullOrEmpty(existingValue))
                {
                    settings.profileSettings.CreateValue("RemoteLoadPath", targetUrl);
                    Debug.Log("Created RemoteLoadPath profile variable");
                }
                else
                {
                    settings.profileSettings.SetValue(profileId, "RemoteLoadPath", targetUrl);
                    Debug.Log($"🌐 RemoteLoadPath set to {targetUrl}");
                }

                // Re-apply OverridePlayerVersion right before build in case a
                // Refresh() between the initial set and now reloaded the .asset
                if (!string.IsNullOrEmpty(contentId))
                {
                    settings.OverridePlayerVersion = contentId;
                    Debug.Log($"📛 [BuildForTarget] OverridePlayerVersion = '{settings.OverridePlayerVersion}', PlayerBuildVersion = '{settings.PlayerBuildVersion}'");
                }

                if (EditorPrefs.GetBool(CleanBeforeEachTargetPrefKey, false))
                {
                    // Addressables cache/build artifacts are target-specific.
                    // Enable this when stale bundles are suspected.
                    CleanAddressablesPlayerContentCache();
                }

                // 🔹 Kick off the Addressables build
                AddressablesPlayerBuildResult result;
                AddressableAssetSettings.BuildPlayerContent(out result);

                if (!string.IsNullOrEmpty(result.Error))
                {
                    throw new System.Exception($"❌ Addressables build failed for {target}: {result.Error}");
                }
                Debug.Log($"✅ Addressables build complete for {target}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"❌ Addressables build failed for {target}: {e}");
                return false;
            }
        }

        private static void CleanAddressablesPlayerContentCache()
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    Debug.LogWarning("Addressables settings not found; skipping clean step.");
                    return;
                }

                var cleanMethods = typeof(AddressableAssetSettings)
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Where(m => m.Name == "CleanPlayerContent")
                    .ToList();

                bool cleaned = false;
                foreach (var method in cleanMethods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        method.Invoke(null, null);
                        cleaned = true;
                        break;
                    }

                    if (parameters.Length == 1)
                    {
                        method.Invoke(null, new object[] { settings.ActivePlayerDataBuilder });
                        cleaned = true;
                        break;
                    }
                }

                if (cleaned)
                {
                    Debug.Log("🧹 Addressables player content cache cleaned once before platform builds.");
                }
                else
                {
                    Debug.LogWarning("Addressables clean method not found; continuing with normal build.");
                }
            }
            catch (Exception cleanEx)
            {
                Debug.LogWarning($"Addressables clean step failed, continuing build: {cleanEx.Message}");
            }
        }
    }

    public class AddTeamMemberPopup : EditorWindow
    {
        private string contentId = "";
        private string emailInput = "";
        private Action<string> onSubmit;
        private bool focusOnce = false;

        public static void Show(string contentId, Action<string> onSubmit)
        {
            var win = CreateInstance<AddTeamMemberPopup>();
            win.titleContent = new GUIContent("Add Team Member");
            win.contentId = contentId;
            win.onSubmit = onSubmit;
            win.minSize = new Vector2(360, 110);
            win.maxSize = new Vector2(360, 110);
            // Center on screen using current main window's resolution
            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(main.x + (main.width - 360) / 2f, main.y + (main.height - 110) / 2f, 360, 110);
            win.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField($"Add a collaborator to '{contentId}' by email:", EditorStyles.wordWrappedLabel);
            GUILayout.Space(6);

            GUI.SetNextControlName("AddTeamMemberEmail");
            emailInput = EditorGUILayout.TextField("Email", emailInput);
            if (!focusOnce)
            {
                EditorGUI.FocusTextInControl("AddTeamMemberEmail");
                focusOnce = true;
            }

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel"))
            {
                Close();
            }
            GUI.enabled = !string.IsNullOrEmpty(emailInput) && emailInput.Contains("@");
            if (GUILayout.Button("Add") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.enabled))
            {
                var trimmed = emailInput.Trim();
                onSubmit?.Invoke(trimmed);
                Close();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
    }
}
#endif
