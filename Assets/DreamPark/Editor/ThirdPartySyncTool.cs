#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DreamPark.Editor
{
    /// <summary>
    /// Moves used assets from ThirdPartyLocal to ThirdParty folder.
    /// Only moves files that are actually referenced from outside ThirdPartyLocal.
    /// This keeps the repo small while maintaining all required dependencies.
    /// Uses AssetDatabase.MoveAsset to preserve GUIDs and references.
    /// </summary>
    public class ThirdPartySyncTool : EditorWindow
    {
        private const string ContentRootPath = "Assets/Content";
        private const string ThirdPartyLocalFolderName = "ThirdPartyLocal";
        private const string ThirdPartyTargetFolderName = "ThirdParty";
        private const string ContentIdPrefKey = "DreamPark.ThirdPartySyncTool.SelectedContentId";

        // File extensions that can contain GUID references
        private static readonly HashSet<string> UnityFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".mat", ".asset", ".controller", ".anim",
            ".overrideController", ".physicMaterial", ".physicsMaterial2D",
            ".cubemap", ".flare", ".renderTexture", ".mask", ".signal",
            ".playable", ".mixer", ".shadergraph", ".shadersubgraph",
            ".terrainlayer", ".brush", ".preset", ".lighting", ".spriteatlas"
        };

        // Binary files that shouldn't be scanned for GUID references
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".obj", ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff",
            ".gif", ".bmp", ".exr", ".hdr", ".wav", ".mp3", ".ogg", ".aif", ".aiff",
            ".dll", ".so", ".dylib", ".a", ".bundle", ".ttf", ".otf", ".compute"
        };

        private static Vector2 scrollPosition;
        private static List<string> lastMovedFiles = new List<string>();
        private static string lastSyncMessage = "";
        private static bool showDetails = false;
        private static List<string> contentOptions = new List<string>();
        private static int selectedContentIndex = 0;
        private static string selectedContentId = "";

        [MenuItem("DreamPark/Manage Third Party Assets", false, 105)]
        public static void ShowWindow()
        {
            var window = GetWindow<ThirdPartySyncTool>("ThirdParty Sync");
            window.minSize = new Vector2(450, 300);
        }

        // Quick sync menu removed. Method kept so callers elsewhere still work;
        // the window's "Move Now" button covers the same flow.
        public static void QuickSync()
        {
            RunSync(false);
        }

        // Targeted variant invoked by the Content Uploader's Compile & Upload
        // flow. Sets the internal content selection to the contentId being
        // uploaded and runs a real sync (not analyze-only) so any
        // ThirdPartyLocal assets referenced by the content are moved into
        // ThirdParty/ before the Addressables build picks them up. Saves the
        // selection back to EditorPrefs so reopening the ThirdParty Sync
        // window after an upload shows the same content the user just shipped.
        public static void RunSyncForContent(string contentId, bool analyzeOnly = false)
        {
            if (string.IsNullOrEmpty(contentId))
            {
                Debug.LogWarning("[ThirdParty Sync] RunSyncForContent called with empty contentId; skipping.");
                return;
            }

            RefreshContentOptions();
            int idx = contentOptions.IndexOf(contentId);
            if (idx < 0)
            {
                Debug.LogWarning($"[ThirdParty Sync] No content folder found at Assets/Content/{contentId}; skipping.");
                return;
            }

            selectedContentIndex = idx;
            selectedContentId = contentId;
            SaveContentSelection();

            RunSync(analyzeOnly);
        }

        private void OnEnable()
        {
            RefreshContentOptions();
            RestoreContentSelection();
        }

        private void OnFocus()
        {
            RefreshContentOptions();
            RestoreContentSelection();
        }

        private void OnGUI()
        {
            GUILayout.Space(10);

            EditorGUILayout.LabelField("ThirdParty Asset Sync Tool", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This tool scans your project for references to assets in ThirdPartyLocal folders, " +
                "then MOVES the used assets to ThirdParty folders for committing to git.\n\n" +
                "Assets are moved (not copied) to preserve GUIDs and avoid duplicates.",
                MessageType.Info);

            GUILayout.Space(10);

            // Content selector
            EditorGUILayout.LabelField("Content", EditorStyles.boldLabel);
            if (contentOptions.Count > 0 && selectedContentIndex >= contentOptions.Count)
            {
                selectedContentIndex = 0;
            }
            int previousIndex = selectedContentIndex;
            EditorGUI.BeginDisabledGroup(contentOptions.Count == 0);
            selectedContentIndex = EditorGUILayout.Popup(selectedContentIndex, contentOptions.ToArray());
            EditorGUI.EndDisabledGroup();

            if (contentOptions.Count > 0)
            {
                selectedContentId = contentOptions[selectedContentIndex];
            }
            else
            {
                selectedContentId = "";
            }

            if (previousIndex != selectedContentIndex)
            {
                SaveContentSelection();
            }

            if (contentOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("No content folders found under Assets/Content.", MessageType.Warning);
            }

            GUILayout.Space(10);

            // Show configured paths
            EditorGUILayout.LabelField("Configured Paths:", EditorStyles.boldLabel);
            if (TryGetConfiguredPaths(out string localPath, out string targetPath, out _))
            {
                EditorGUILayout.LabelField($"  Source: {localPath}");
                EditorGUILayout.LabelField($"  Target: {targetPath}");
            }
            else
            {
                EditorGUILayout.LabelField("  Source: (none)");
                EditorGUILayout.LabelField("  Target: (none)");
            }

            GUILayout.Space(15);

            // Sync buttons
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Analyze Only", GUILayout.Height(30)))
            {
                RunSync(true);
            }

            if (GUILayout.Button("Move Now", GUILayout.Height(30)))
            {
                RunSync(false);
            }

            EditorGUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Results
            if (!string.IsNullOrEmpty(lastSyncMessage))
            {
                EditorGUILayout.HelpBox(lastSyncMessage, MessageType.None);
            }

            if (lastMovedFiles.Count > 0)
            {
                GUILayout.Space(5);
                showDetails = EditorGUILayout.Foldout(showDetails, $"Moved Files ({lastMovedFiles.Count})");

                if (showDetails)
                {
                    scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.MaxHeight(200));
                    foreach (var file in lastMovedFiles)
                    {
                        EditorGUILayout.LabelField(file, EditorStyles.miniLabel);
                    }
                    EditorGUILayout.EndScrollView();
                }
            }

            GUILayout.FlexibleSpace();

            // Auto-sync option
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Auto-sync on Play:", GUILayout.Width(120));
            bool autoSync = EditorPrefs.GetBool("DreamPark_AutoSyncThirdParty", false);
            bool newAutoSync = EditorGUILayout.Toggle(autoSync);
            if (newAutoSync != autoSync)
            {
                EditorPrefs.SetBool("DreamPark_AutoSyncThirdParty", newAutoSync);
            }
            EditorGUILayout.EndHorizontal();
        }

        public static void RunSync(bool analyzeOnly)
        {
            try
            {
                RefreshContentOptions();
                RestoreContentSelection();

                if (!TryGetConfiguredPaths(out string thirdPartyLocalPath, out string thirdPartyTargetPath, out string configError))
                {
                    lastMovedFiles.Clear();
                    lastSyncMessage = configError;
                    Debug.LogWarning($"[ThirdParty Sync] {configError}");
                    return;
                }

                EditorUtility.DisplayProgressBar("ThirdParty Sync", "Building GUID index...", 0.1f);

                // Step 1: Build GUID -> path mapping for all assets in ThirdPartyLocal only
                var guidToPath = new Dictionary<string, string>();
                var pathToGuid = new Dictionary<string, string>();

                if (Directory.Exists(thirdPartyLocalPath))
                {
                    BuildGuidIndex(thirdPartyLocalPath, guidToPath, pathToGuid);
                }

                EditorUtility.DisplayProgressBar("ThirdParty Sync", "Finding external references...", 0.3f);

                // Step 2: Collect all ThirdPartyLocal paths
                var allLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(thirdPartyLocalPath))
                {
                    allLocalPaths.Add(thirdPartyLocalPath.Replace("\\", "/"));
                }

                // Step 3: Find all GUIDs referenced from OUTSIDE ThirdPartyLocal (and ThirdParty)
                var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    thirdPartyTargetPath.Replace("\\", "/")
                };
                var externalRefs = FindExternalReferences("Assets", allLocalPaths, targetPaths);

                EditorUtility.DisplayProgressBar("ThirdParty Sync", "Resolving dependencies...", 0.5f);

                // Step 4: Resolve recursive dependencies within ThirdPartyLocal
                var usedGuids = ResolveRecursiveDependencies(externalRefs, guidToPath, allLocalPaths);

                EditorUtility.DisplayProgressBar("ThirdParty Sync", "Calculating files to move...", 0.7f);

                // Step 5: Get file paths and their target locations
                var filesToMove = new List<(string source, string target)>();

                foreach (var guid in usedGuids)
                {
                    if (!guidToPath.TryGetValue(guid, out var sourcePath))
                        continue;

                    var normalizedLocalPath = thirdPartyLocalPath.Replace("\\", "/");
                    if (sourcePath.StartsWith(normalizedLocalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var relativePath = sourcePath.Substring(normalizedLocalPath.Length).TrimStart('/');
                        var targetPath = Path.Combine(thirdPartyTargetPath, relativePath).Replace("\\", "/");

                        // Only add if source exists and target doesn't already exist
                        if (File.Exists(sourcePath) && !File.Exists(targetPath))
                        {
                            filesToMove.Add((sourcePath, targetPath));
                        }
                    }
                }

                // Remove duplicates and sort
                filesToMove = filesToMove.Distinct().OrderBy(x => x.source).ToList();

                if (analyzeOnly)
                {
                    lastMovedFiles = filesToMove.Select(x => $"{x.source} → {x.target}").ToList();
                    lastSyncMessage = $"Analysis complete: {filesToMove.Count} assets would be moved.";
                    Debug.Log($"[ThirdParty Sync] {lastSyncMessage}");

                    foreach (var (source, target) in filesToMove)
                    {
                        Debug.Log($"[ThirdParty Sync] Would move: {source} → {target}");
                    }
                }
                else
                {
                    EditorUtility.DisplayProgressBar("ThirdParty Sync", "Moving files...", 0.8f);

                    int movedCount = 0;
                    int failedCount = 0;

                    // Use AssetDatabase.MoveAsset to preserve GUIDs
                    AssetDatabase.StartAssetEditing();
                    try
                    {
                        foreach (var (source, target) in filesToMove)
                        {
                            try
                            {
                                // Create target directory if needed
                                var targetDir = Path.GetDirectoryName(target);
                                if (!Directory.Exists(targetDir))
                                {
                                    Directory.CreateDirectory(targetDir);
                                }

                                // Use Unity's MoveAsset to preserve GUID
                                string error = AssetDatabase.MoveAsset(source, target);
                                if (string.IsNullOrEmpty(error))
                                {
                                    movedCount++;
                                    Debug.Log($"[ThirdParty Sync] Moved: {Path.GetFileName(source)}");
                                }
                                else
                                {
                                    failedCount++;
                                    Debug.LogWarning($"[ThirdParty Sync] Failed to move {source}: {error}");
                                }
                            }
                            catch (Exception ex)
                            {
                                failedCount++;
                                Debug.LogWarning($"[ThirdParty Sync] Failed to move {source}: {ex.Message}");
                            }
                        }
                    }
                    finally
                    {
                        AssetDatabase.StopAssetEditing();
                    }

                    lastMovedFiles = filesToMove.Select(x => x.target).ToList();
                    lastSyncMessage = $"Sync complete: {movedCount} assets moved" +
                        (failedCount > 0 ? $", {failedCount} failed" : "");
                    Debug.Log($"[ThirdParty Sync] {lastSyncMessage}");

                    // Clean up empty directories in ThirdPartyLocal
                    CleanupEmptyDirectories(thirdPartyLocalPath);

                    // Refresh to update the project
                    AssetDatabase.Refresh();
                }
            }
            catch (Exception ex)
            {
                lastSyncMessage = $"Error: {ex.Message}";
                Debug.LogError($"[ThirdParty Sync] {ex}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void BuildGuidIndex(string rootPath, Dictionary<string, string> guidToPath, Dictionary<string, string> pathToGuid)
        {
            var metaFiles = Directory.GetFiles(rootPath, "*.meta", SearchOption.AllDirectories);
            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            foreach (var metaPath in metaFiles)
            {
                try
                {
                    var assetPath = metaPath.Substring(0, metaPath.Length - 5).Replace("\\", "/"); // Remove .meta

                    // Skip if asset doesn't exist (orphaned meta file)
                    if (!File.Exists(assetPath) && !Directory.Exists(assetPath))
                        continue;

                    var content = File.ReadAllText(metaPath);

                    // Only read first 500 chars - GUID is always near the top
                    if (content.Length > 500)
                        content = content.Substring(0, 500);

                    var match = guidRegex.Match(content);
                    if (match.Success)
                    {
                        var guid = match.Groups[1].Value;
                        guidToPath[guid] = assetPath;
                        pathToGuid[assetPath] = guid;
                    }
                }
                catch { }
            }
        }

        private static HashSet<string> FindExternalReferences(string rootPath, HashSet<string> localPaths, HashSet<string> targetPaths)
        {
            var externalRefs = new HashSet<string>();
            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            // Also skip ThirdParty folders (we only want refs from actual game content)
            var skipPaths = new HashSet<string>(localPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var targetPath in targetPaths)
            {
                skipPaths.Add(targetPath.Replace("\\", "/"));
            }

            var allFiles = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories);

            foreach (var filePath in allFiles)
            {
                var normalizedPath = filePath.Replace("\\", "/");

                // Skip files inside ThirdPartyLocal and ThirdParty
                bool shouldSkip = false;
                foreach (var skipPath in skipPaths)
                {
                    if (normalizedPath.StartsWith(skipPath, StringComparison.OrdinalIgnoreCase))
                    {
                        shouldSkip = true;
                        break;
                    }
                }
                if (shouldSkip) continue;

                var ext = Path.GetExtension(filePath).ToLowerInvariant();

                // Skip binary and meta files
                if (BinaryExtensions.Contains(ext) || ext == ".meta")
                    continue;

                // Scan Unity files and scripts
                if (UnityFileExtensions.Contains(ext) || ext == ".cs" || ext == ".shader")
                {
                    try
                    {
                        var content = File.ReadAllText(filePath);
                        var matches = guidRegex.Matches(content);
                        foreach (Match match in matches)
                        {
                            externalRefs.Add(match.Groups[1].Value);
                        }
                    }
                    catch { }
                }
            }

            return externalRefs;
        }

        private static HashSet<string> ResolveRecursiveDependencies(HashSet<string> initialGuids, Dictionary<string, string> guidToPath, HashSet<string> localPaths)
        {
            var usedGuids = new HashSet<string>();
            var toProcess = new Queue<string>(initialGuids);
            var processed = new HashSet<string>();
            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            while (toProcess.Count > 0)
            {
                var guid = toProcess.Dequeue();
                if (processed.Contains(guid))
                    continue;
                processed.Add(guid);

                if (!guidToPath.TryGetValue(guid, out var assetPath))
                    continue;

                // Check if this asset is in ThirdPartyLocal
                bool isInLocal = false;
                foreach (var localPath in localPaths)
                {
                    if (assetPath.StartsWith(localPath, StringComparison.OrdinalIgnoreCase))
                    {
                        isInLocal = true;
                        break;
                    }
                }

                if (!isInLocal)
                    continue;

                usedGuids.Add(guid);

                // Scan this asset for more dependencies
                var ext = Path.GetExtension(assetPath).ToLowerInvariant();
                if (UnityFileExtensions.Contains(ext) && !BinaryExtensions.Contains(ext))
                {
                    try
                    {
                        if (File.Exists(assetPath))
                        {
                            var content = File.ReadAllText(assetPath);
                            var matches = guidRegex.Matches(content);
                            foreach (Match match in matches)
                            {
                                var subGuid = match.Groups[1].Value;
                                if (!processed.Contains(subGuid))
                                    toProcess.Enqueue(subGuid);
                            }
                        }
                    }
                    catch { }
                }
            }

            return usedGuids;
        }

        /// <summary>
        /// Removes empty directories from ThirdPartyLocal after moving files
        /// </summary>
        private static void CleanupEmptyDirectories(string localPath)
        {
            if (!Directory.Exists(localPath))
                return;

            try
            {
                // Get all directories, deepest first
                var directories = Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length)
                    .ToList();

                foreach (var dir in directories)
                {
                    try
                    {
                        // Check if directory is empty (no files, only maybe empty subdirs or .meta files)
                        var files = Directory.GetFiles(dir);
                        var subdirs = Directory.GetDirectories(dir);

                        // Consider empty if only has .meta file for itself
                        bool isEmpty = files.Length == 0 ||
                            (files.Length == 1 && files[0].EndsWith(".meta"));

                        if (isEmpty && subdirs.Length == 0)
                        {
                            // Delete the directory and its .meta file
                            var metaPath = dir + ".meta";
                            if (File.Exists(metaPath))
                                File.Delete(metaPath);

                            // Delete any remaining .meta files inside
                            foreach (var file in files)
                            {
                                File.Delete(file);
                            }

                            Directory.Delete(dir);
                            Debug.Log($"[ThirdParty Sync] Removed empty directory: {dir}");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void RefreshContentOptions()
        {
            contentOptions.Clear();
            if (Directory.Exists(ContentRootPath))
            {
                var dirs = Directory.GetDirectories(ContentRootPath)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                contentOptions.AddRange(dirs);
            }
        }

        private static void RestoreContentSelection()
        {
            if (contentOptions.Count == 0)
            {
                selectedContentIndex = 0;
                selectedContentId = "";
                return;
            }

            string savedContentId = EditorPrefs.GetString(ContentIdPrefKey, "");
            if (!string.IsNullOrEmpty(savedContentId))
            {
                int savedIndex = contentOptions.IndexOf(savedContentId);
                if (savedIndex >= 0)
                {
                    selectedContentIndex = savedIndex;
                    selectedContentId = contentOptions[selectedContentIndex];
                    return;
                }
            }

            selectedContentIndex = Mathf.Clamp(selectedContentIndex, 0, contentOptions.Count - 1);
            selectedContentId = contentOptions[selectedContentIndex];
            SaveContentSelection();
        }

        private static void SaveContentSelection()
        {
            if (string.IsNullOrEmpty(selectedContentId))
                return;

            EditorPrefs.SetString(ContentIdPrefKey, selectedContentId);
        }

        private static bool TryGetConfiguredPaths(out string localPath, out string targetPath, out string error)
        {
            localPath = "";
            targetPath = "";

            if (contentOptions.Count == 0 || string.IsNullOrEmpty(selectedContentId))
            {
                error = "No content folder is selected. Create/select a folder under Assets/Content.";
                return false;
            }

            localPath = Path.Combine(ContentRootPath, selectedContentId, ThirdPartyLocalFolderName).Replace("\\", "/");
            targetPath = Path.Combine(ContentRootPath, selectedContentId, ThirdPartyTargetFolderName).Replace("\\", "/");

            if (!Directory.Exists(localPath))
            {
                error = $"Source folder not found: {localPath}";
                return false;
            }

            error = "";
            return true;
        }

        // Auto-sync on play mode
        [InitializeOnLoadMethod]
        private static void RegisterPlayModeCallback()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                if (EditorPrefs.GetBool("DreamPark_AutoSyncThirdParty", false))
                {
                    Debug.Log("[ThirdParty Sync] Auto-syncing before play mode...");
                    RunSync(false);
                }
            }
        }
    }
}
#endif
