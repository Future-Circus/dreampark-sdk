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
        //
        // deferCompiledCode (Oct 13 2026): the build/publish flows pass true. Moving a
        // .cs/.asmdef/.asmref/.dll/.rsp recompiles the Editor and schedules a domain
        // reload, and that happened INSIDE the publish: CrashCourse's beta job died in
        // a reload after CFXR's Editor scripts moved, and SuperAdventureLand's beta
        // shipped 0 beta bundles because ForceUpdateContent saw "compiling" and skipped
        // the stamp. Where a script sits (ThirdPartyLocal vs ThirdParty, both under the
        // content folder) changes nothing in the build — same assembly, same scripts
        // package — so those moves are held back and made by
        // FlushDeferredCompiledCodeMoves() once the job is over. Every other asset still
        // moves now, before the Addressables build gathers it.
        public static void RunSyncForContent(string contentId, bool analyzeOnly = false, bool deferCompiledCode = false)
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

            RunSync(analyzeOnly, deferCompiledCode);
        }

        // Files whose move makes Unity recompile scripts (and then reload the domain).
        internal static bool IsCompiledCodePath(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".cs" || ext == ".asmdef" || ext == ".asmref" || ext == ".dll" || ext == ".rsp";
        }

        // Script moves a build/publish sync held back (see RunSyncForContent). Static, so a
        // domain reload drops them; nothing is lost then, the files simply stay in
        // ThirdPartyLocal until the next sync without deferral moves them.
        private static readonly List<(string source, string target)> deferredCodeMoves = new List<(string source, string target)>();
        private static string deferredCodeLocalPath;
        internal static int DeferredCompiledCodeMoveCount => deferredCodeMoves.Count;

        /// <summary>
        /// Makes the script moves a build/publish sync deferred. Call only once the job is
        /// over: the moves recompile the Editor and reload the domain. No-op when nothing
        /// is pending.
        /// </summary>
        public static void FlushDeferredCompiledCodeMoves()
        {
            if (deferredCodeMoves.Count == 0) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // Retry once play mode is over rather than recompiling into it.
                EditorApplication.delayCall += FlushDeferredCompiledCodeMoves;
                return;
            }
            var moves = deferredCodeMoves.Where(m => File.Exists(m.source) && !File.Exists(m.target)).ToList();
            string localPath = deferredCodeLocalPath;
            deferredCodeMoves.Clear();
            deferredCodeLocalPath = null;
            if (moves.Count == 0) return;

            Debug.Log($"[ThirdParty Sync] Moving {moves.Count} script file(s) deferred during the build (Unity recompiles now).");
            int moved = MoveFiles(moves, out int failed);
            Debug.Log($"[ThirdParty Sync] Deferred script move complete: {moved} moved" + (failed > 0 ? $", {failed} failed" : ""));
            if (!string.IsNullOrEmpty(localPath)) CleanupEmptyDirectories(localPath);
            AssetDatabase.Refresh();
        }

        // AssetDatabase.MoveAsset keeps GUIDs (and every reference) intact.
        private static int MoveFiles(List<(string source, string target)> moves, out int failedCount)
        {
            int movedCount = 0;
            failedCount = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (source, target) in moves)
                {
                    try
                    {
                        var targetDir = Path.GetDirectoryName(target);
                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

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
            return movedCount;
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
            RunSync(analyzeOnly, deferCompiledCode: false);
        }

        private static void RunSync(bool analyzeOnly, bool deferCompiledCode)
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

                string contentRoot = Path.Combine(ContentRootPath, selectedContentId).Replace("\\", "/");
                SyncPlan plan = PlanSync(contentRoot, thirdPartyLocalPath, thirdPartyTargetPath, showProgress: true);
                var filesToMove = plan.moves;
                ReportScriptReferences(plan.referencedScripts, analyzeOnly);

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

                    var movesNow = filesToMove;
                    int deferredCount = 0;
                    if (deferCompiledCode)
                    {
                        var codeMoves = filesToMove.Where(m => IsCompiledCodePath(m.source)).ToList();
                        if (codeMoves.Count > 0)
                        {
                            movesNow = filesToMove.Where(m => !IsCompiledCodePath(m.source)).ToList();
                            foreach (var move in codeMoves)
                                if (!deferredCodeMoves.Contains(move)) deferredCodeMoves.Add(move);
                            deferredCodeLocalPath = thirdPartyLocalPath;
                            deferredCount = codeMoves.Count;
                            Debug.Log($"[ThirdParty Sync] Deferring {codeMoves.Count} script file(s) until the build is over "
                                + "(moving them now would recompile and reload the Editor mid-build):\n"
                                + string.Join("\n", codeMoves.Select(m => m.source)));
                        }
                    }

                    int movedCount = MoveFiles(movesNow, out int failedCount);

                    lastMovedFiles = movesNow.Select(x => x.target).ToList();
                    lastSyncMessage = $"Sync complete: {movedCount} assets moved" +
                        (failedCount > 0 ? $", {failedCount} failed" : "") +
                        (deferredCount > 0 ? $", {deferredCount} script file(s) deferred until the build is over" : "");
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

        internal sealed class SyncPlan
        {
            public List<(string source, string target)> moves = new List<(string source, string target)>();
            /// <summary>Scripts under ThirdPartyLocal that tracked assets reference.</summary>
            public List<string> referencedScripts = new List<string>();
        }

        /// <summary>
        /// Which ThirdPartyLocal files the content uses (and where each goes in ThirdParty),
        /// without moving anything. contentRoot is Assets/Content/{id}.
        /// </summary>
        internal static SyncPlan PlanSync(string contentRoot, string thirdPartyLocalPath, string thirdPartyTargetPath,
            bool showProgress = false)
        {
            if (showProgress) EditorUtility.DisplayProgressBar("ThirdParty Sync", "Building GUID index...", 0.1f);

            // Step 1: Build GUID -> path mapping for all assets in ThirdPartyLocal only
            var guidToPath = new Dictionary<string, string>();
            var pathToGuid = new Dictionary<string, string>();

            if (Directory.Exists(thirdPartyLocalPath))
            {
                BuildGuidIndex(thirdPartyLocalPath, guidToPath, pathToGuid);
            }

            if (showProgress) EditorUtility.DisplayProgressBar("ThirdParty Sync", "Finding external references...", 0.3f);

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

            // Step 3b: the GUID text scan cannot see references inside binary-serialized
            // assets (Modular 3D Text's .asset fonts → .obj meshes, ArcadeLand Oct 2026),
            // and it does not follow references out of tracked assets that live in
            // ThirdParty/. Ask the AssetDatabase instead: everything the content folder's
            // own assets depend on, recursively, that lives in ThirdPartyLocal is used.
            if (showProgress) EditorUtility.DisplayProgressBar("ThirdParty Sync", "Collecting asset dependencies...", 0.4f);
            List<string> dependedLocal = LocalDependenciesOf(contentRoot, thirdPartyLocalPath);
            foreach (var dependedPath in dependedLocal)
            {
                string dependedGuid = AssetDatabase.AssetPathToGUID(dependedPath);
                if (!string.IsNullOrEmpty(dependedGuid)) externalRefs.Add(dependedGuid);
            }

            // Scripts in ThirdPartyLocal that tracked assets reference: a clean checkout
            // without them does not compile, so they are reported as well as moved.
            var referencedScripts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependedPath in dependedLocal)
                if (dependedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) referencedScripts.Add(dependedPath);
            foreach (var referencedGuid in externalRefs)
                if (guidToPath.TryGetValue(referencedGuid, out var referencedPath)
                    && referencedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    referencedScripts.Add(referencedPath);

            if (showProgress) EditorUtility.DisplayProgressBar("ThirdParty Sync", "Resolving dependencies...", 0.5f);

            // Step 4: Resolve recursive dependencies within ThirdPartyLocal
            var usedGuids = ResolveRecursiveDependencies(externalRefs, guidToPath, pathToGuid, allLocalPaths);

            if (showProgress) EditorUtility.DisplayProgressBar("ThirdParty Sync", "Calculating files to move...", 0.7f);

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

            return new SyncPlan { moves = filesToMove, referencedScripts = referencedScripts.ToList() };
        }

        /// <summary>
        /// ThirdPartyLocal paths that the content folder's own assets (everything under
        /// contentRoot outside ThirdPartyLocal, ThirdParty included) depend on, recursively.
        /// </summary>
        internal static List<string> LocalDependenciesOf(string contentRoot, string thirdPartyLocalPath)
        {
            if (string.IsNullOrEmpty(contentRoot) || !AssetDatabase.IsValidFolder(contentRoot))
                return new List<string>();
            var roots = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("", new[] { contentRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                if (IsUnderFolder(path, thirdPartyLocalPath)) continue;
                roots.Add(path);
            }
            if (roots.Count == 0) return new List<string>();
            return ClassifyLocal(AssetDatabase.GetDependencies(roots.Distinct().ToArray(), true), thirdPartyLocalPath);
        }

        /// <summary>The paths under thirdPartyLocalPath, de-duplicated and sorted.</summary>
        internal static List<string> ClassifyLocal(IEnumerable<string> dependencyPaths, string thirdPartyLocalPath)
        {
            var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (dependencyPaths == null) return result.ToList();
            foreach (string path in dependencyPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string normalized = path.Replace("\\", "/");
                if (IsUnderFolder(normalized, thirdPartyLocalPath)) result.Add(normalized);
            }
            return result.ToList();
        }

        private static bool IsUnderFolder(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            string prefix = folder.Replace("\\", "/").TrimEnd('/') + "/";
            return path.Replace("\\", "/").StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> lastReferencedScripts = new List<string>();
        internal static IReadOnlyList<string> LastReferencedScripts => lastReferencedScripts;

        private static void ReportScriptReferences(List<string> scripts, bool analyzeOnly)
        {
            lastReferencedScripts = scripts ?? new List<string>();
            if (lastReferencedScripts.Count == 0) return;
            Debug.LogWarning("[ThirdParty Sync] Tracked assets reference " + lastReferencedScripts.Count
                + " script(s) under ThirdPartyLocal. A clean checkout without them won't compile, so they "
                + (analyzeOnly ? "would be moved" : "are moved")
                + " into ThirdParty/ with the rest; commit them:\n" + string.Join("\n", lastReferencedScripts));
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

        private static HashSet<string> ResolveRecursiveDependencies(HashSet<string> initialGuids, Dictionary<string, string> guidToPath, Dictionary<string, string> pathToGuid, HashSet<string> localPaths)
        {
            var usedGuids = new HashSet<string>();
            var toProcess = new Queue<string>(initialGuids);
            var processed = new HashSet<string>();
            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            // Tracks assembly roots we've already bulk-pulled so we only walk each tree once.
            var pulledAssemblyRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                string matchingLocalRoot = null;
                foreach (var localPath in localPaths)
                {
                    if (assetPath.StartsWith(localPath, StringComparison.OrdinalIgnoreCase))
                    {
                        isInLocal = true;
                        matchingLocalRoot = localPath;
                        break;
                    }
                }

                if (!isInLocal)
                    continue;

                usedGuids.Add(guid);

                var ext = Path.GetExtension(assetPath).ToLowerInvariant();

                // ── Script bulk-pull ────────────────────────────────────────
                // C# type-name references (e.g., script A uses an enum/class
                // defined in script B) are invisible to GUID scanning — .cs
                // files don't contain `guid:` tokens for the types they
                // reference, the compiler resolves those by namespace/name.
                // Conservatively pull in every .cs/.asmdef/.asmref within
                // the same assembly boundary so the moved code still
                // compiles.
                if (ext == ".cs" || ext == ".asmdef" || ext == ".asmref")
                {
                    var assemblyRoot = FindAssemblyRoot(assetPath, matchingLocalRoot);
                    if (!string.IsNullOrEmpty(assemblyRoot) && pulledAssemblyRoots.Add(assemblyRoot))
                    {
                        int siblingCount = 0;
                        foreach (var siblingPath in EnumerateAssemblyScripts(assemblyRoot))
                        {
                            if (pathToGuid.TryGetValue(siblingPath, out var siblingGuid) && !processed.Contains(siblingGuid))
                            {
                                toProcess.Enqueue(siblingGuid);
                                siblingCount++;
                            }
                        }
                        if (siblingCount > 0)
                        {
                            Debug.Log($"[ThirdParty Sync] Pulling in {siblingCount} script file(s) from assembly: {assemblyRoot}");
                        }
                    }
                }

                // Scan this asset for more dependencies (Unity asset GUID refs)
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
        /// Finds the assembly boundary for a script under ThirdPartyLocal.
        /// Returns the nearest ancestor folder containing an .asmdef, or
        /// failing that, the top-level package folder directly under
        /// ThirdPartyLocal (since packages almost always ship as one
        /// cohesive top-level subfolder). Returns null for files that live
        /// directly at the ThirdPartyLocal root (no clear package scope).
        /// </summary>
        private static string FindAssemblyRoot(string assetPath, string localRoot)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(localRoot))
                return null;

            var normalizedAsset = assetPath.Replace("\\", "/");
            var normalizedRoot = localRoot.Replace("\\", "/").TrimEnd('/');

            if (!normalizedAsset.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            // Walk up looking for an .asmdef; stop before reaching the
            // ThirdPartyLocal root itself (we never want to treat the entire
            // ThirdPartyLocal as one assembly).
            var dir = Path.GetDirectoryName(normalizedAsset)?.Replace("\\", "/");
            while (!string.IsNullOrEmpty(dir) && dir.Length > normalizedRoot.Length)
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.asmdef", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        return dir;
                    }
                }
                catch { }

                var parent = Path.GetDirectoryName(dir)?.Replace("\\", "/");
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }

            // No .asmdef found — fall back to the top-level subfolder under
            // ThirdPartyLocal. e.g. ThirdPartyLocal/PackageA/Scripts/Foo.cs
            // → ThirdPartyLocal/PackageA
            var relative = normalizedAsset.Substring(normalizedRoot.Length).TrimStart('/');
            var firstSlash = relative.IndexOf('/');
            if (firstSlash < 0) return null; // file is at ThirdPartyLocal root — no package scope
            var topLevel = relative.Substring(0, firstSlash);
            return $"{normalizedRoot}/{topLevel}";
        }

        /// <summary>
        /// Enumerates every .cs/.asmdef/.asmref under an assembly root,
        /// skipping files that live below a nested .asmdef (those belong
        /// to a different assembly and shouldn't be bulk-pulled).
        /// </summary>
        private static IEnumerable<string> EnumerateAssemblyScripts(string assemblyRoot)
        {
            if (!Directory.Exists(assemblyRoot))
                yield break;

            string[] allFiles;
            try
            {
                allFiles = Directory.GetFiles(assemblyRoot, "*.*", SearchOption.AllDirectories);
            }
            catch
            {
                yield break;
            }

            foreach (var path in allFiles)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".cs" && ext != ".asmdef" && ext != ".asmref")
                    continue;

                if (IsBelowDifferentAsmdef(path, assemblyRoot))
                    continue;

                yield return path.Replace("\\", "/");
            }
        }

        /// <summary>
        /// Returns true if there's a nested .asmdef between assemblyRoot
        /// and filePath — i.e., the file belongs to a sub-assembly.
        /// </summary>
        private static bool IsBelowDifferentAsmdef(string filePath, string assemblyRoot)
        {
            var normRoot = assemblyRoot.Replace("\\", "/").TrimEnd('/');
            var dir = Path.GetDirectoryName(filePath)?.Replace("\\", "/");

            while (!string.IsNullOrEmpty(dir) && dir.Length > normRoot.Length)
            {
                try
                {
                    if (Directory.GetFiles(dir, "*.asmdef", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        return true;
                    }
                }
                catch { }

                var parent = Path.GetDirectoryName(dir)?.Replace("\\", "/");
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
            return false;
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
