#if !DREAMPARKCORE
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;
using System.Linq;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using System.Collections.Generic;
using System;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using System.Text;
using System.Text.RegularExpressions;

namespace DreamPark {
    [InitializeOnLoad]
    public static class ContentProcessor
    {
        private const string kLastFingerprintKey = "DreamPark.ContentProcessor.lastFingerprint";
        private static int sProcessingDepth;

        static ContentProcessor()
        {
            // Subscribe to file change events from the Watchdog
            ContentFolderWatchdog.OnContentFilesChanged += OnContentFilesChanged;
        }

      [InitializeOnLoadMethod]
        private static void RunOnStartup()
        {
            // Only run once per Unity session
            if (SessionState.GetBool("DreamPark_RanOnStartup", false))
                return;

            SessionState.SetBool("DreamPark_RanOnStartup", true);

            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
                {
                    Debug.Log("🪄 Auto-running AssignAllGameIds on Editor startup (first time this session)...");
                    ExecuteWithWatchdogPaused(() =>
                    {
                        ForceUpdateAllContentInternal();
                        EnforceContentNamespaces();
                    });
                }
            };
        }

        private static bool IsProcessing => sProcessingDepth > 0;

        private static void ExecuteWithWatchdogPaused(Action action)
        {
            if (action == null)
                return;

            sProcessingDepth++;
            if (sProcessingDepth == 1)
                ContentFolderWatchdog.Pause();

            try
            {
                action();
            }
            finally
            {
                sProcessingDepth = Mathf.Max(0, sProcessingDepth - 1);
                if (sProcessingDepth == 0)
                    ContentFolderWatchdog.Resume();
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static string GetGameFolderName()
        {
            string[] possibleContentPaths = Directory.GetDirectories("Assets", "Content", SearchOption.AllDirectories);
            foreach (string contentPath in possibleContentPaths)
            {
                var subdirs = Directory.GetDirectories(contentPath);
                if (subdirs.Length > 0)
                {
                    string folderName = Path.GetFileName(subdirs[0]);
                    if (!string.IsNullOrEmpty(folderName))
                        return folderName;
                }
            }
            return "YOUR_GAME_HERE";
        }

        public static string GetGamePrefix()
        {
            return Sanitize(GetGameFolderName());
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "");
            return name.Replace("[", "").Replace("]", "").Trim();
        }

        // True if `groupName` is a group that SmartBundleGrouper manages —
        // i.e., a per-root bundle group ({gameId}-Bundle-*) or the misc
        // bundle ({gameId}-Misc). Used to detect when an incremental edit
        // shouldn't disturb a Smart-organized addressable layout. Note: the
        // {gameId}-Shared bundle was removed in favor of consolidating
        // shared assets into the first-alphabetical root's bundle, so we
        // no longer treat it as a managed group.
        private static bool IsSmartManagedGroupName(string gameId, string groupName)
        {
            if (string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(gameId)) return false;
            string prefix = gameId + "-";
            if (!groupName.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return groupName.StartsWith(prefix + "Bundle-", StringComparison.Ordinal)
                || groupName == prefix + "Misc";
        }

        private static void EnsureGlobalLabel(AddressableAssetSettings settings, string gameId)
        {
            settings?.AddLabel(gameId);
        }

        [MenuItem("DreamPark/Troubleshooting/Force Update All Content", false, 203)]
        public static void ForceUpdateAllContent()
        {
            ExecuteWithWatchdogPaused(() => {
                ForceUpdateAllContentInternal();
                CleanupAddressableSettings();
            });
        }

        private static void ForceUpdateAllContentInternal()
        {
            string[] contentIds = Directory.GetDirectories("Assets/Content")
                .Select(path => Path.GetFileName(path))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToArray();

            foreach (string contentId in contentIds) {
                ForceUpdateContentInternal(contentId);
            }
        }

        // Two-purpose janitor for the Addressables settings:
        //
        //   1. Drops entries whose GUID no longer resolves to a real asset on
        //      disk — Unity's addressables window leaves these as "Missing
        //      Reference" rows after assets get deleted/moved without going
        //      through AssetDatabase. They serve no purpose at runtime and
        //      pollute the inspector.
        //
        //   2. Drops empty groups whose contentId prefix (the segment before
        //      the first '-') doesn't match any folder under Assets/Content/.
        //      Catches YOUR_GAME_HERE-* groups left over from the SDK template
        //      after the new-park.sh rename, plus any group from a contentId
        //      that's been deleted entirely. Doesn't touch groups whose
        //      prefix DOES match a current folder — those belong to the
        //      Smart pass / Legacy logic to manage.
        //
        // Safe to run repeatedly. Always preserves Default Local Group and
        // Built In Data (Addressables requires them).
        [MenuItem("DreamPark/Troubleshooting/Cleanup Addressables", false, 206)]
        public static void CleanupAddressables()
        {
            ExecuteWithWatchdogPaused(CleanupAddressableSettings);
        }

        public static void CleanupAddressableSettings()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            // Snapshot of which content folders currently exist on disk —
            // anything else is fair game for empty-group removal.
            var contentFolderPrefixes = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists("Assets/Content"))
            {
                foreach (var dir in Directory.GetDirectories("Assets/Content"))
                {
                    string name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name)) contentFolderPrefixes.Add(name);
                }
            }

            int removedEntries = 0;
            int removedGroups = 0;

            // Pass 1 — drop missing-reference entries from every group.
            foreach (var group in settings.groups.Where(g => g != null).ToList())
            {
                var entriesToRemove = new List<string>();
                foreach (var entry in group.entries.ToList())
                {
                    string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        entriesToRemove.Add(entry.guid);
                    }
                }
                foreach (var guid in entriesToRemove)
                {
                    settings.RemoveAssetEntry(guid);
                    removedEntries++;
                }
            }

            // Pass 2 — drop empty groups for stale content prefixes.
            var groupsToRemove = new List<AddressableAssetGroup>();
            foreach (var group in settings.groups.Where(g => g != null))
            {
                if (group.entries.Count > 0) continue;
                if (group.Default) continue;                    // protect Default Local Group
                if (group.ReadOnly) continue;                   // protect Built In Data and similar
                if (string.IsNullOrEmpty(group.Name)) continue;

                int dashIdx = group.Name.IndexOf('-');
                if (dashIdx <= 0) continue;                     // no recognizable contentId prefix

                string prefix = group.Name.Substring(0, dashIdx);
                if (contentFolderPrefixes.Contains(prefix)) continue; // belongs to a live content
                groupsToRemove.Add(group);
            }
            foreach (var g in groupsToRemove)
            {
                settings.RemoveGroup(g);
                removedGroups++;
            }

            if (removedEntries > 0 || removedGroups > 0)
            {
                Debug.Log($"🧹 Cleanup: removed {removedEntries} missing-reference entry/entries and {removedGroups} stale empty group(s).");
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
        }


        [MenuItem("DreamPark/Troubleshooting/Regenerate Level Previews", false, 204)]
        public static void GenerateAllPreviewsMenu()
        {
            ExecuteWithWatchdogPaused(() =>
            {
                string contentRoot = "Assets/Content";
                if (!Directory.Exists(contentRoot))
                {
                    Debug.LogWarning("⚠️ No Assets/Content folder found.");
                    return;
                }

                string[] contentIds = Directory.GetDirectories(contentRoot)
                    .Select(path => Path.GetFileName(path))
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToArray();

                if (contentIds.Length == 0)
                {
                    Debug.LogWarning("⚠️ No content folders found under Assets/Content.");
                    return;
                }

                foreach (string contentId in contentIds)
                {
                    Debug.Log($"🖼️ Generating previews for content: {contentId}");
                    GenerateAllLevelPreviews(contentId);
                }

                Debug.Log($"✅ Manual preview generation finished for {contentIds.Length} content folder(s).");
            });
        }

        public static void ForceUpdateContent(string contentId)
        {
            ExecuteWithWatchdogPaused(() => ForceUpdateContentInternal(contentId));
        }

        private static void ForceUpdateContentInternal(string contentId)
        {
            Debug.Log("🔄 Assigning contentId " + contentId + " to files in Assets/Content/" + contentId + "..");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            string contentRoot = $"Assets/Content/{contentId}";
            if (!AssetDatabase.IsValidFolder(contentRoot))
            {
                Debug.LogWarning($"⚠️ No folder found at {contentRoot}");
                return;
            }

            Debug.Log($"🔄 Force updating all prefabs and addressables for {contentId}...");

            // Process all prefabs under this content folder, EXCLUDING
            // ThirdPartyLocal — those prefabs never ship and frequently
            // contain demo/example content with missing-script references
            // (e.g. CFXR Dynamic Text Example, RunemarkStudio FPSController
            // demo) that would crash EditPrefabContentsScope's auto-save
            // when UpdateSpecificPrefabs touches them.
            string[] allPrefabs = AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Where(p => p.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0)
                .ToArray();

            UpdateSpecificPrefabs(allPrefabs.ToList(), contentId);
            GenerateAllLevelPreviews(contentId);
            ApplyGameIdLabelToContentEntries(AddressableAssetSettingsDefaultObject.Settings, contentId);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("✅ Finished manual full content update.");
        }

        // ---------------------------------------------------------------------
        // Incremental change handler
        // ---------------------------------------------------------------------
        private static void OnContentFilesChanged(List<string> changedFiles)
        {
            if (changedFiles == null || changedFiles.Count == 0)
                return;

            if (IsProcessing)
                return;

            ExecuteWithWatchdogPaused(() => ProcessContentFilesChanged(changedFiles));
        }

        private static void ProcessContentFilesChanged(List<string> changedFiles)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return;

            // Convert absolute → Unity asset paths
            static string ToAssetPath(string absolutePath)
            {
                if (string.IsNullOrEmpty(absolutePath) || !absolutePath.StartsWith(Application.dataPath))
                    return null;

                string rel = Path.GetRelativePath(Application.dataPath, absolutePath).Replace("\\", "/");
                return "Assets/" + rel;
            }

            var assetPaths = changedFiles
                .Select(ToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p.Replace("\\", "/"))
                .Where(p => p.StartsWith("Assets/Content/", StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            if (assetPaths.Count == 0)
                return;

            string GetGameIdFromPath(string p)
            {
                string rest = p.Substring("Assets/Content/".Length);
                int slash = rest.IndexOf('/');
                return slash >= 0 ? rest.Substring(0, slash) : rest;
            }

            var groups = assetPaths.GroupBy(GetGameIdFromPath).ToList();
            int totalPrefabs = 0, totalOther = 0, totalScripts = 0;

            foreach (var group in groups)
            {
                string gameId = group.Key;
                if (string.IsNullOrEmpty(gameId))
                    continue;

                string contentRoot = $"Assets/Content/{gameId}";
                if (!AssetDatabase.IsValidFolder(contentRoot))
                    continue;

                var groupPaths = group.ToList();

                var prefabPaths = groupPaths
                    .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var scriptPaths = groupPaths
                    .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var otherAssets = groupPaths
                    .Except(prefabPaths)
                    .Except(scriptPaths)
                    .Where(p => !AssetDatabase.IsValidFolder(p))
                    .ToList();

                if (prefabPaths.Count > 0)
                {
                    UpdateSpecificPrefabs(prefabPaths, gameId);
                    ApplyGameIdLabelToContentEntries(settings, gameId, prefabPaths);
                }

                if (otherAssets.Count > 0)
                {
                    ApplyGameIdLabelToContentEntries(settings, gameId, otherAssets);
                }

                if (scriptPaths.Count > 0)
                {
                    EnforceContentNamespaces(scriptPaths);
                }

                // Mark every addressable group that received a touched file
                // as "dirty" since the last successful upload. The patch
                // estimator reads this set to answer "what would change if
                // I uploaded right now?" without having to actually run a
                // build. Group lookup happens AFTER ApplyGameIdLabelToContentEntries
                // has assigned the changed entries to their current groups.
                var dirtyGroups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in groupPaths)
                {
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid)) continue;
                    var entry = settings.FindAssetEntry(guid);
                    if (entry?.parentGroup != null && !string.IsNullOrEmpty(entry.parentGroup.Name))
                    {
                        dirtyGroups.Add(entry.parentGroup.Name);
                    }
                }
                if (dirtyGroups.Count > 0)
                {
                    DirtyGroupsStore.AddDirty(gameId, dirtyGroups);
                }

                totalPrefabs += prefabPaths.Count;
                totalOther += otherAssets.Count;
                totalScripts += scriptPaths.Count;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"🔄 Processed {totalPrefabs} prefabs, {totalOther} assets, {totalScripts} scripts across {groups.Count} content folder(s).");
        }

        // ---------------------------------------------------------------------
        // Prefab Updates (incremental)
        // ---------------------------------------------------------------------
        private static void UpdateSpecificPrefabs(List<string> prefabPaths, string gameId)
        {
            if (prefabPaths == null || prefabPaths.Count == 0) return;

            int modified = 0;
            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (var path in prefabPaths)
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        continue;

                    // Defensive: never edit ThirdPartyLocal prefabs.
                    // Callers should already filter these out, but a missing-
                    // script prefab in ThirdPartyLocal would crash the
                    // EditPrefabContentsScope auto-save and abort the rest of
                    // the pass. ThirdPartyLocal never ships, so injecting
                    // gameId there is pointless anyway.
                    if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
                    {
                        var root = scope.prefabContentsRoot;
                        bool any = false;

                        foreach (var comp in root.GetComponentsInChildren<Component>(true))
                        {
                            // Skip missing scripts (important!)
                            if (comp == null) continue;

                            var type = comp.GetType();
                            var field = type.GetField("gameId",
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic |
                                System.Reflection.BindingFlags.Instance);

                            if (field != null && field.FieldType == typeof(string))
                            {
                                var current = (string)field.GetValue(comp);
                                if (!string.Equals(current, gameId, StringComparison.Ordinal))
                                {
                                    field.SetValue(comp, gameId);
                                    EditorUtility.SetDirty(comp);
                                    any = true;
                                }
                            }
                        }

                        if (any)
                        {
                            // Save *only if* all components resolved
                            PrefabUtility.SaveAsPrefabAsset(scope.prefabContentsRoot, path);
                            modified++;
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (modified > 0)
                Debug.Log($"🧩 Updated {modified} prefab(s) for gameId={gameId} (safe mode).");
        }

        private static bool ShouldSkipAsset(string assetPath)
        {
            assetPath = assetPath.Replace("\\", "/");

            if (disallowedExtensionsList.Any(ext => assetPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.LogWarning($"⏭️ Skipped disallowed extension asset: {assetPath}");
                return true;
            }

            if (string.IsNullOrEmpty(assetPath)) {
                Debug.LogWarning($"⏭️ Skipped empty asset: {assetPath}");
                return true;
            }

            // Skip Editor folders
            if (assetPath.Contains("/Editor/"))
            {
                return true;
            }

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                Debug.LogWarning($"⏭️ Skipped folder asset: {assetPath}");
                return true;
            }

            var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (main == null)
            {
                Debug.LogWarning($"⏭️ Skipped null main asset: {assetPath}");
                return true;
            }

             // Skip if the main asset itself is flagged
            if ((main.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset)) != 0)
            {
                Debug.LogWarning($"⏭️ Skipped (HideFlags) asset: {assetPath} with flag: {main.hideFlags}");
                return true;
            }

            //we don't want to assume skipping ThirdParty assets (but skip ThirdPartyLocal - those are untracked)
            if (assetPath.Contains("/ThirdParty/") && !assetPath.Contains("/ThirdPartyLocal/")) {
                return false;
            }

            // Skip ThirdPartyLocal entirely - these are local-only asset store packages
            if (assetPath.Contains("/ThirdPartyLocal/")) {
                return true;
            }

            // // For prefabs, check all serialized references
            // if (main is GameObject go)
            // {
            //     foreach (var c in go.GetComponentsInChildren<Component>(true))
            //     {
            //         if (c == null) continue;
            //         if ((c.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset)) != 0)
            //         {
            //             Debug.LogWarning($"⏭️ Skipped prefab with DontSave component: {assetPath}");
            //             return true;
            //         }

            //         // NEW: scan serialized fields for hidden refs
            //         using (var so = new SerializedObject(c))
            //         {
            //             var prop = so.GetIterator();
            //             while (prop.NextVisible(true))
            //             {
            //                 if (prop.propertyType == SerializedPropertyType.ObjectReference)
            //                 {
            //                     var obj = prop.objectReferenceValue;
            //                     if (obj != null && (obj.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave)) != 0)
            //                     {
            //                         Debug.LogWarning($"⏭️ Skipped prefab with hidden DontSave reference ({obj.name}) in {assetPath}");
            //                         return true;
            //                     }
            //                 }
            //             }
            //         }
            //     }
            // }

            return false;
        }

        // Lightweight version of ShouldSkipAsset used by the self-heal dep
        // sweep. The full ShouldSkipAsset rejects when AssetDatabase.LoadMainAssetAtPath
        // returns null, which can be transient for assets mid-import — using
        // it during the sweep would re-create the same blind spot we're
        // trying to plug. Here we filter purely on path / extension /
        // ThirdPartyLocal exclusion, trusting that anything reachable through
        // a tracked asset's dep graph is real even if Unity's import state
        // is stale. ShouldSkipAsset still gates the *initial* FindAssets scan
        // and the per-asset registration loop, so a genuinely broken asset
        // would still get caught there.
        private static bool ShouldSkipAssetForSweep(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return true;
            assetPath = assetPath.Replace("\\", "/");
            if (disallowedExtensionsList.Any(ext => assetPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) return true;
            if (assetPath.Contains("/Editor/")) return true;
            if (assetPath.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (AssetDatabase.IsValidFolder(assetPath)) return true;
            return false;
        }

        private static List<string> disallowedExtensionsList = new List<string> {
            ".meta",
            ".cs",
            ".unity",
            ".blend",
            ".blend1",
            ".js",
            ".boo",
            ".asmdef",
            ".asmref",
            ".dll",
            ".pdb",
            ".mdb",
            ".sln",
            ".csproj",
            ".buildreport",
            ".assetstore",
            ".log",
            ".tmp",
            ".max",
            ".ma",
            ".mb",  
            ".c4d",
            ".psd",
            ".ai",
            ".svg",
            ".unitypackage",
            ".zip",
            ".7z",
            ".gz",
            ".rar",
            ".tar",
            ".hdr",
            ".so",
            ".pdf",
            ".exe",
            ".app",
            ".apk",
            ".aab",
            ".ipa",
            ".so",
            ".bundle",
            ".framework",
            ".dylib",
            ".html",
            ".txt",
            ".css"
        };

        // ---------------------------------------------------------------------
        // Addressable Updates (targeted)
        // ---------------------------------------------------------------------
        private static void ApplyGameIdLabelToContentEntries(AddressableAssetSettings settings, string gameId, List<string> specificPaths = null)
        {
            if (settings == null) return;

            EnsureGlobalLabel(settings, gameId);

            // Determine which asset paths to operate on
            IEnumerable<string> assetPaths;
            if (specificPaths != null && specificPaths.Count > 0)
            {
                assetPaths = specificPaths;
            }
            else
            {
                string contentRoot = $"Assets/Content/{gameId}/";
                // FindAssets accepts paths with or without trailing slash, but
                // we strip it defensively because some Unity versions handle
                // the trailing slash inconsistently and silently return fewer
                // results.
                string contentRootForFind = contentRoot.TrimEnd('/');
                Debug.Log($"🔍 Searching for assets in {contentRoot}");

                RestoreAssetSaveability(gameId);

                // Build the initial list from AssetDatabase.FindAssets.
                var foundAssets = new HashSet<string>(
                    AssetDatabase.FindAssets("", new[] { contentRootForFind })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Where(p => !ShouldSkipAsset(p)),
                    StringComparer.OrdinalIgnoreCase);

                // ── Self-heal: walk transitive deps of every found asset and
                // pull in anything reachable under Content/{gameId}/. This
                // is conceptually the same job ThirdPartySyncTool does to
                // decide what to move from ThirdPartyLocal — closure-walk a
                // dep graph — but applied to addressable registration so a
                // freshly-arrived asset like ThirdParty/.../Albedo.png that
                // AssetDatabase.FindAssets missed (stale index, import
                // timing) still ends up addressable as long as it's reachable
                // from something that IS tracked.
                //
                // Critically, the sweep uses ShouldSkipAssetForSweep —
                // a stripped-down version of ShouldSkipAsset that doesn't
                // call AssetDatabase.LoadMainAssetAtPath. The full
                // ShouldSkipAsset rejects assets when LoadMainAssetAtPath
                // returns null, which can happen transiently for assets
                // mid-import; relying on it here would re-create the same
                // silent blind spot we're trying to fix.
                int addedViaSweep = 0;
                {
                    var queue = new Queue<string>(foundAssets);
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        string[] deps;
                        try { deps = AssetDatabase.GetDependencies(current, recursive: false); }
                        catch { continue; }
                        foreach (var dep in deps)
                        {
                            if (string.IsNullOrEmpty(dep)) continue;
                            if (string.Equals(dep, current, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!dep.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase)) continue;
                            if (ShouldSkipAssetForSweep(dep)) continue;
                            if (!foundAssets.Add(dep)) continue;
                            queue.Enqueue(dep);
                            addedViaSweep++;
                        }
                    }
                }
                if (addedViaSweep > 0)
                {
                    Debug.Log($"🩹 Self-heal: pulled {addedViaSweep} dep-reachable asset(s) into the addressable scan that AssetDatabase.FindAssets initially missed.");
                }

                assetPaths = foundAssets;

                try
                {
                    var allowedAssetSet = foundAssets;

                    // Gather all (group, entry) pairs to remove first to prevent collection modification during enumeration
                    var entriesToRemove = new List<(AddressableAssetGroup, AddressableAssetEntry)>();
                    foreach (var group in settings.groups.Where(g => g != null))
                    {
                        foreach (var e in group.entries)
                        {
                            string path = AssetDatabase.GUIDToAssetPath(e.guid);
                            if (!allowedAssetSet.Contains(path))
                            {
                                entriesToRemove.Add((group, e));
                            }
                        }
                    }

                    // Now, actually remove them
                    foreach (var (group, e) in entriesToRemove)
                    {
                        settings.RemoveAssetEntry(e.guid);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error removing invalid assets from content entries: {e.Message}");
                }
            }

            Debug.Log($"🔍 Found {assetPaths.Count()} assets to process for gameId={gameId}.");

            int labeled = 0, moved = 0;
            foreach (var assetPath in assetPaths)
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid)) continue;

                var rel = assetPath.Replace("\\", "/");
                int idx = rel.IndexOf($"{gameId}/");
                if (idx < 0) continue;

                var subPath = rel.Substring(idx + gameId.Length + 1);
                var firstSlash = subPath.IndexOf('/');
                string folderName = firstSlash > 0 ? subPath.Substring(0, firstSlash) : "Root";
                string groupName = $"{Sanitize(gameId)}-{folderName}";

                var group = settings.groups.FirstOrDefault(g => g != null && g.Name == groupName)
                    ?? settings.CreateGroup(groupName, false, false, true,
                        new List<AddressableAssetGroupSchema> {
                            (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(BundledAssetGroupSchema)),
                            (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(ContentUpdateGroupSchema))
                        });

                var bag = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
                bag.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
                bag.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
                bag.UseAssetBundleCache = true;
                bag.UseAssetBundleCrc = true;
                bag.UseAssetBundleCrcForCachedBundles = false;
                bool useSeparate = folderName == "Models" || folderName == "Textures" || folderName == "Audio";
                bag.BundleMode = useSeparate
                    ? BundledAssetGroupSchema.BundlePackingMode.PackSeparately
                    : BundledAssetGroupSchema.BundlePackingMode.PackTogether;
                bag.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;

                var entry = settings.FindAssetEntry(guid);

                // In Smart mode, an entry is typically already in a
                // Smart-managed group ({gameId}-Bundle-*, {gameId}-Shared,
                // {gameId}-Misc) from the last full pass. Incremental edits
                // must NOT move it back to the Legacy folder group computed
                // above — doing so silently undoes Smart bundling for that
                // asset (the next build would produce a Legacy-shaped bundle
                // mismatched with the rest of the Smart layout) AND breaks
                // dirty-group tracking because the post-move group name no
                // longer matches the Smart bundle filenames in the baseline.
                // Smart's full pass runs at upload time (gated to
                // specificPaths == null), so this is the right place to
                // protect Smart membership.
                bool inSmartGroup = entry?.parentGroup != null
                    && IsSmartManagedGroupName(gameId, entry.parentGroup.Name);
                bool isIncremental = specificPaths != null && specificPaths.Count > 0;
                bool preserveSmart = isIncremental
                    && BundlingStrategyPrefs.Current == BundlingStrategy.Smart
                    && inSmartGroup;

                if (!preserveSmart && (entry == null || entry.parentGroup != group))
                {
                    entry = settings.CreateOrMoveEntry(guid, group, false, false);
                    moved++;
                }

                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                string extension = Path.GetExtension(assetPath).ToLowerInvariant();
                string typeFolder = null;

                // ✅ Handle special cases first
                if (extension == ".fbx" || extension == ".obj" || extension == ".dae") {
                    typeFolder = "Models";
                } else if (extension == ".asset") {
                    typeFolder = "Assets";
                } else if (assetType == typeof(AudioClip))
                    typeFolder = "Audio";
                else if (assetType == typeof(Texture) || assetType == typeof(Texture2D) || assetType == typeof(RenderTexture))
                    typeFolder = "Textures";
                else if (assetType == typeof(Material))
                    typeFolder = "Materials";
                else if (assetType == typeof(Shader) || extension == ".shadergraph")
                    typeFolder = "Shaders";
                else if (assetType == typeof(AnimationClip))
                    typeFolder = "Animations";
                else if (extension == ".json" || assetType == typeof(TextAsset))
                    typeFolder = "Data";

                string desiredAddress;
                if (typeFolder == null && assetType == typeof(GameObject)) {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null) continue;
                    var levelTemplate = prefab.GetComponent<LevelTemplate>();
                    var propTemplate = prefab.GetComponent<PropTemplate>();
                    if (levelTemplate != null) {
                        desiredAddress = $"{gameId}/Levels/{levelTemplate.size.ToString()}/{Path.GetFileNameWithoutExtension(assetPath)}";
                        string filePreview = $"Assets/Content/{gameId}/Previews/{Path.GetFileNameWithoutExtension(assetPath)}.png";
                        // if (!File.Exists(filePreview)) {
                        //     GeneratePreview(prefab).ContinueWith(t => SavePreview(t.Result, filePreview));
                        // }
                    } else if (propTemplate != null) {
                        desiredAddress = $"{gameId}/Props/{propTemplate.category.ToString()}/{Path.GetFileNameWithoutExtension(assetPath)}";
                    } else {
                        desiredAddress = $"{gameId}/{Path.GetFileNameWithoutExtension(assetPath)}";
                    }
                } else if (typeFolder == "Textures" && assetPath.Contains("Previews")) {
                    desiredAddress =  $"{gameId}/Previews/{Path.GetFileNameWithoutExtension(assetPath)}";
                } else if (!string.IsNullOrEmpty(typeFolder)) {
                    desiredAddress = $"{gameId}/{typeFolder}/{Path.GetFileNameWithoutExtension(assetPath)}";
                } else {
                    desiredAddress = assetPath;
                }

                if (entry.address != desiredAddress) {
                    Debug.Log("🔍 Address changed for: " + assetPath);
                    Debug.Log("desiredAddress: " + desiredAddress);
                    Debug.Log("entry.address: " + entry.address);
                    entry.address = desiredAddress;
                }

                if (!entry.labels.Contains(gameId))
                {
                    Debug.Log("🔍 Label changed for: " + assetPath);
                    Debug.Log("gameId: " + gameId);
                    Debug.Log("entry.labels: " + string.Join(", ", entry.labels));
                    entry.SetLabel(gameId, true, true);
                    labeled++;
                }
            }

            if (labeled > 0 || moved > 0)
                Debug.Log($"🏷 Addressables: {moved} moved/created, {labeled} labeled for '{gameId}'.");

            // ── Bundling strategy ────────────────────────────────────────
            // After the Legacy folder-based grouping has finished assigning
            // every content asset to a "{gameId}-{folder}" group, optionally
            // re-partition into dependency-aware bundles. Legacy is the
            // default and runs alone; Smart is an opt-in pass that re-slices
            // those groups so a one-asset edit invalidates one small bundle
            // instead of a folder-level one. See BundlingStrategy.cs.
            //
            // Only run the Smart pass on full updates (specificPaths == null),
            // not on incremental file-change passes — Smart needs the full
            // addressable set to compute correct shared-vs-unique refCounts,
            // and the upload path always triggers a full ForceUpdateContent
            // before building so a dropped Smart pass during incremental
            // editing is recovered by the time we build.
            if (specificPaths == null && BundlingStrategyPrefs.Current == BundlingStrategy.Smart)
            {
                var result = SmartBundleGrouper.ApplyDependencyAwareGrouping(settings, gameId);
                Debug.Log($"📦 Smart bundling [experimental] for '{gameId}': " +
                          $"{result.rootBundles} root bundles, {result.miscAssets} misc assets, " +
                          $"+{result.groupsCreated}/-{result.groupsRemoved} groups.");
            }
        }

        public static Texture2D CreateAlphaMask(Texture2D original, Color bg, float threshold = 0.1f)
        {
            Texture2D masked = new Texture2D(original.width, original.height, TextureFormat.RGBA32, false);

            for (int y = 0; y < original.height; y++)
            {
                for (int x = 0; x < original.width; x++)
                {
                    Color c = original.GetPixel(x, y);

                    // color distance
                    float diff = Vector3.Distance(
                        new Vector3(c.r, c.g, c.b),
                        new Vector3(bg.r, bg.g, bg.b)
                    );

                    // if close to background → invisible
                    float a = diff < threshold ? 0f : 1f;

                    // optional: fade edges slightly
                    a = Mathf.Clamp01((diff - (threshold * 0.5f)) * 4f);

                    masked.SetPixel(x, y, new Color(c.r, c.g, c.b, a));
                }
            }

            masked.Apply();
            return masked;
        }
        public static void GenerateAllLevelPreviews(string contentId)
        {
            string contentRoot = $"Assets/Content/{contentId}";
            if (!AssetDatabase.IsValidFolder(contentRoot))
            {
                Debug.LogWarning($"⚠️ No folder found at {contentRoot}");
                return;
            }

            // Find all prefabs with LevelTemplate or PropTemplate component
            string[] allPrefabs = AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToArray();

            int generated = 0;

            foreach (string prefabPath in allPrefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;

                // Same root-prefab predicate the SmartBundleGrouper and the
                // Park Assets preview grid use: LevelTemplate (catches
                // AttractionTemplate via inheritance), PropTemplate, or
                // PlayerRig. Keeping the three callsites in lockstep means
                // every prefab that shows up in the panel grid also gets a
                // PNG generated.
                var levelTemplate = prefab.GetComponent<LevelTemplate>();
                var propTemplate = prefab.GetComponent<PropTemplate>();
                var playerRig = prefab.GetComponent<PlayerRig>();
                if (levelTemplate == null && propTemplate == null && playerRig == null) continue;

                string previewPath = $"Assets/Content/{contentId}/Previews/{Path.GetFileNameWithoutExtension(prefabPath)}.png";

                // Ensure Previews directory exists
                string previewDir = Path.GetDirectoryName(previewPath);
                if (!Directory.Exists(previewDir))
                {
                    Directory.CreateDirectory(previewDir);
                }

                // Render high-quality preview with true transparency
                Texture2D preview = PrefabPreviewRenderer.RenderPreview(prefab);
                if (preview == null)
                {
                    Debug.LogWarning($"⚠️ Could not generate preview for {prefabPath}");
                    continue;
                }

                byte[] png = preview.EncodeToPNG();
                File.WriteAllBytes(previewPath, png);

                // Import and configure the texture
                AssetDatabase.ImportAsset(previewPath, ImportAssetOptions.ForceSynchronousImport);

                var importer = AssetImporter.GetAtPath(previewPath) as TextureImporter;
                if (importer != null)
                {
                    importer.alphaIsTransparency = true;
                    importer.isReadable = true;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.sRGBTexture = true;
                    importer.SaveAndReimport();
                }

                generated++;
                Debug.Log($"🖼️ Generated preview: {previewPath}");
            }

            AssetDatabase.Refresh();
            Debug.Log($"✅ Preview generation complete. Generated {generated} preview(s).");
        }

        [MenuItem("DreamPark/Troubleshooting/Remove Broken Addressables", false, 200)]
        public static void FixAddressables()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("No AddressableAssetSettings found.");
                return;
            }

            int before = settings.groups.Count;
            settings.groups.RemoveAll(g => g == null);
            int after = settings.groups.Count;
            Debug.Log($"🧹 Removed {before - after} null groups.");

            AssetDatabase.SaveAssets();
            Debug.Log("✅ Addressables settings cleaned.");
        }
            
        [MenuItem("DreamPark/Troubleshooting/Fix Script Namespaces", false, 202)]
        public static void EnforceContentNamespaces() {
            string[] contentIds = Directory.GetDirectories("Assets/Content")
                .Select(path => Path.GetFileName(path))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToArray();
        
            foreach (string contentId in contentIds) {
                EnforceContentNamespaces(contentId);
            }
        }

        public static void EnforceContentNamespaces(string contentId)
        {
            string root = $"Assets/Content/{contentId}";
            if (!Directory.Exists(root))
            {
                Debug.LogError("❌ No Assets/Content/{contentId} folder found.");
                return;
            }

            string[] csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(f => f.Replace('\\', '/'))
                .Where(f => !f.Contains("/ThirdParty/") && !f.Contains("/ThirdPartyLocal/"))
                .ToArray();

            EnforceContentNamespaces(csFiles.ToList());
        }
        public static void EnforceContentNamespaces(List<string> specificPaths = null)
        {
            string root = Path.Combine(Application.dataPath, "Content");
            if (!Directory.Exists(root))
            {
                Debug.LogError("❌ No Assets/Content folder found.");
                return;
            }

            string[] csFiles = (specificPaths != null && specificPaths.Count > 0 ? specificPaths.ToArray() : Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                .Select(f => f.Replace('\\', '/'))
                .Where(f => !f.Contains("/ThirdParty/") && !f.Contains("/ThirdPartyLocal/"))
                .ToArray();

            int modified = 0, skipped = 0;

            foreach (string sysPath in csFiles)
            {
                string path = sysPath.Replace("\\", "/");

                if (path.Contains("/Editor/") || path.Contains("/Generated/"))
                {
                    skipped++;
                    continue;
                }

                Debug.Log($"[EnforceContentNamespaces] 🔍 Processing file: {path}");

                string[] parts = path.Split('/');
                if (parts.Length < 4) { skipped++; continue; }

                int contentIdx = parts.ToList().IndexOf("Content");
                string gameId = parts[contentIdx + 1];
                string expectedNamespace = SanitizeNamespace(gameId);

                // Build compound namespace for game subfolders (e.g. ArcadeLand/Gauntlet3D → ArcadeLand.Gauntlet3D)
                // Detect subfolders that have their own Scripts/ directory as game modules.
                if (contentIdx + 2 < parts.Length)
                {
                    string subFolder = parts[contentIdx + 2];
                    string subFolderFullPath = Path.Combine("Assets/Content", gameId, subFolder).Replace('\\', '/');
                    string subScriptsPath = Path.Combine(subFolderFullPath, "Scripts").Replace('\\', '/');
                    if (Directory.Exists(subScriptsPath))
                    {
                        string sanitizedSub = SanitizeNamespace(subFolder);
                        // Only use compound namespace if the sanitized sub-name is a valid C# identifier start
                        if (sanitizedSub.Length > 0 && char.IsLetter(sanitizedSub[0]))
                        {
                            expectedNamespace = expectedNamespace + "." + sanitizedSub;
                        }
                    }
                }

                // read file text with normalized newlines
                string text = File.ReadAllText(path, Encoding.UTF8)
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n");
                    
                var nsPatternCheck = new Regex(@"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Multiline);

                var match = nsPatternCheck.Match(text);
                if (match.Success)
                {
                    string existingNamespace = match.Groups[1].Value.Trim();
                    if (existingNamespace == expectedNamespace)
                    {
                        // ✅ Namespace already correct — skip rewriting
                        skipped++;
                        continue;
                    }
                }

                // strip existing namespace wrapper if present
                var nsPattern = new System.Text.RegularExpressions.Regex(
                    @"^\s*namespace\s+[A-Za-z0-9_.]+\s*\{([\s\S]*)\}\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                if (nsPattern.IsMatch(text))
                {
                    string existingNamespace = nsPattern.Match(text).Groups[1].Value.Trim();
                    if (existingNamespace == expectedNamespace)
                    {
                        // ✅ Already correct, skip rewriting
                        skipped++;
                        continue;
                    }
                    string inner = nsPattern.Match(text).Groups[1].Value;
                    var innerLines = inner.Split('\n')
                        .Select(l => l.StartsWith("    ") ? l.Substring(4) : l)
                        .ToList();

                    while (innerLines.Count > 0 && string.IsNullOrWhiteSpace(innerLines[0])) innerLines.RemoveAt(0);
                    while (innerLines.Count > 0 && string.IsNullOrWhiteSpace(innerLines[^1])) innerLines.RemoveAt(innerLines.Count - 1);
                    text = string.Join("\n", innerLines);
                }

                // wrap with clean namespace
                var sb = new StringBuilder();
                sb.AppendLine($"namespace {expectedNamespace}");
                sb.AppendLine("{");
                string[] rawLines = text.Split('\n');
                foreach (var line in rawLines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        sb.AppendLine();
                    else
                        sb.AppendLine("    " + line.TrimEnd());
                }
                sb.AppendLine("}");

                string final = Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");

                if (final.Contains("class CoreExtensionsInterface"))
                {
                    string pattern = @"(\[.*?\]\s*)?(public\s*)?(static\s+string\s+gameId\s*=\s*)(?:""[^""]*""|string\.Empty|null|[^;\n]*)";
                    string replacement = $"${{1}}${{2}}${{3}}\"{gameId}\"";
                    string updated = Regex.Replace(final, pattern, replacement);

                    final = updated;
                }

                File.WriteAllText(path, final.TrimEnd() + "\n", Encoding.UTF8);

                Debug.Log($"🧩 Ensured single namespace '{expectedNamespace}' in {path}");
                modified++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"✅ Namespace enforcement complete. Modified {modified}, skipped {skipped}.");
        }

        private static string SanitizeNamespace(string ns)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
                ns = ns.Replace(c.ToString(), "");
            ns = ns.Replace(" ", "").Replace("__", "_").Replace("..", ".");
            return ns;
        }

        [MenuItem("DreamPark/Troubleshooting/Find Unused Scripts", false, 205)]
        public static void Scan()
        {
            string[] scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/Content" });
            var scriptPaths = scriptGuids.Select(AssetDatabase.GUIDToAssetPath).ToArray();
            var used = new HashSet<string>();

            string[] assetGuids = AssetDatabase.FindAssets("t:Object");
            int checkedCount = 0;

            foreach (var guid in assetGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".cs") || AssetDatabase.IsValidFolder(path)) continue;

                var deps = AssetDatabase.GetDependencies(path, recursive: true);
                foreach (var dep in deps)
                {
                    if (dep.EndsWith(".cs"))
                        used.Add(dep);
                }

                checkedCount++;
            }

            Debug.Log($"🔍 Scanned {checkedCount} assets for script dependencies.");

            // --- Log all unused scripts individually ---
            int unusedCount = 0;
            foreach (string scriptPath in scriptPaths)
            {
                if (!used.Contains(scriptPath))
                {
                    Debug.LogWarning($"🗑 Unused script detected: {scriptPath}");
                    unusedCount++;
                }
            }

            Debug.Log($"✅ Script usage scan complete. Found {unusedCount} unused script(s).");
        }

        public static void SelectBuildGroups(string contentId) {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("❌ Addressable settings not found.");
                return;
            }

            foreach (var group in settings.groups)
            {
                bool shouldInclude = group.Name.StartsWith(contentId);
            
                // Disable build output for unrelated groups
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema != null)
                {
                    schema.IncludeInBuild = shouldInclude;
                }
                Debug.Log($"{(shouldInclude ? "✅ Including" : "🚫 Skipping")} group: {group.Name}");
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        public static bool BuildUnityPackage(string contentId) {
            Debug.Log($"Building unity package for {contentId}");
            try {
            string sourceFolder = "Assets/Content/" + contentId;
                string[] guids = AssetDatabase.FindAssets("t:Script", new[] { sourceFolder })
                .Where(g => !g.Contains("/Editor/")).ToArray();

                string[] assetPaths = guids
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .ToArray();

                if (assetPaths.Length == 0)
                {
                    Debug.LogError("No scripts found in folder: " + sourceFolder);
                    return true;
                }

                // Export to a temporary location
                string tempPath = Path.Combine(Application.dataPath, $"../{contentId}.unitypackage");
                AssetDatabase.ExportPackage(assetPaths, tempPath, ExportPackageOptions.Default);

                // Ensure the "ServerData" and "Unity" directories exist
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string serverDataPath = Path.Combine(projectRoot, "ServerData");
                string unityPath = Path.Combine(serverDataPath, "Unity");

                if (!Directory.Exists(serverDataPath))
                {
                    Directory.CreateDirectory(serverDataPath);
                    Debug.Log("Created directory: " + serverDataPath);
                }

                if (!Directory.Exists(unityPath))
                {
                    Directory.CreateDirectory(unityPath);
                    Debug.Log("Created directory: " + unityPath);
                }

                // Move/copy to persistent storage
                string destPath = Path.Combine(unityPath, $"{contentId}.unitypackage");
                File.Copy(tempPath, destPath, overwrite: true);

                Debug.Log("Scripts exported to: " + destPath);
                return true;
            } catch (Exception e) {
                Debug.LogError("❌ Content upload failed: " + e);
                return false;
            }
        }

        [MenuItem("DreamPark/Troubleshooting/Restore Prefab Saveability", false, 201)]
        public static void RestoreAssetSaveability()
        {
            // Previously hardcoded to "SuperAdventureLand". Now detects the
            // current game folder(s) under Assets/Content and applies to the
            // right one (prompting if there are several).
            const string contentRoot = "Assets/Content";
            if (!Directory.Exists(contentRoot))
            {
                EditorUtility.DisplayDialog("Restore Prefab Saveability",
                    "No Assets/Content folder found.", "OK");
                return;
            }

            string[] gameIds = Directory.GetDirectories(contentRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();

            if (gameIds.Length == 0)
            {
                EditorUtility.DisplayDialog("Restore Prefab Saveability",
                    "No game folders found under Assets/Content.", "OK");
                return;
            }

            string gameId;
            if (gameIds.Length == 1)
            {
                gameId = gameIds[0];
                if (!EditorUtility.DisplayDialog("Restore Prefab Saveability",
                        $"Clear HideFlags.DontSave / HideAndDontSave on every asset under\n" +
                        $"Assets/Content/{gameId}/ ?\n\n" +
                        "This is safe to run; it only affects assets that were stuck in an unsaveable state.",
                        "Restore", "Cancel"))
                    return;
            }
            else
            {
                string picked = EditorUtility.OpenFolderPanel(
                    "Select game folder under Assets/Content", contentRoot, "");
                if (string.IsNullOrEmpty(picked)) return;

                gameId = Path.GetFileName(picked);
                if (!gameIds.Contains(gameId))
                {
                    EditorUtility.DisplayDialog("Restore Prefab Saveability",
                        $"\"{gameId}\" is not a direct subfolder of Assets/Content.", "OK");
                    return;
                }
            }

            RestoreAssetSaveability(gameId);
        }

        public static void RestoreAssetSaveability(string gameId)
        {
            int restored = 0;
            string contentRoot = $"Assets/Content/{gameId}/";
            if (!Directory.Exists(contentRoot))
            {
                Debug.LogError("❌ No Assets/Content/{contentId} folder found.");
                return;
            }

            //Set all content assets to be able to save
            string[] assetPaths = AssetDatabase.FindAssets("", new[] { contentRoot })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(f => f.Replace('\\', '/'))
            .Where(f => !f.Contains("/ThirdParty/") && !f.Contains("/ThirdPartyLocal/"))
            .Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            .Where(f => !AssetDatabase.IsValidFolder(f))
            .ToArray();

            Debug.Log($"🔍 Found {assetPaths.Length} assets to restore saveability for {gameId}.");
            
            foreach (var assetPath in assetPaths)
            {
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (mainAsset != null && (mainAsset.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset)) != 0)
                {
                    mainAsset.hideFlags = HideFlags.None;
                    EditorUtility.SetDirty(mainAsset);
                    restored++;
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"✅ Restored {restored} assets saveability for {gameId}.");
        }

        public static void RemoveUnsavedAssets(string gameId)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            var entriesToRemove = new List<(AddressableAssetGroup group, AddressableAssetEntry entry)>();

            foreach (var group in settings.groups.Where(g => g != null))
            {
                foreach (var entry in group.entries.ToList())
                {
                    string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                    var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
                    bool cantSave = false;

                    if (mainAsset == null)
                    {
                        cantSave = true;
                    }
                    else
                    {
                        if ((mainAsset.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset)) != 0)
                            cantSave = true;
                    }

                    if (!cantSave) {
                         // For prefabs, check all serialized references
                        if (mainAsset is GameObject go)
                        {
                            foreach (var c in go.GetComponentsInChildren<Component>(true))
                            {
                                if (c == null) continue;
                                if ((c.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset)) != 0)
                                {
                                    cantSave = true;
                                    break;
                                }

                                // NEW: scan serialized fields for hidden refs
                                using (var so = new SerializedObject(c))
                                {
                                    var prop = so.GetIterator();
                                    while (prop.NextVisible(true))
                                    {
                                        if (prop.propertyType == SerializedPropertyType.ObjectReference)
                                        {
                                            var obj = prop.objectReferenceValue;
                                            if (obj != null && (obj.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave)) != 0)
                                            {
                                                cantSave = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (cantSave)
                        entriesToRemove.Add((group, entry));
                }
            }

            int removed = 0;
            foreach (var r in entriesToRemove)
            {
                settings.RemoveAssetEntry(r.entry.guid);
                removed++;
            }

            if (removed > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"🧹 Removed {removed} unsaveable addressable asset(s) for {gameId}.");
            }
            else
            {
                Debug.Log($"ℹ️ No unsaveable addressable assets found for {gameId}.");
            }
        }  
    }
}
#endif // !DREAMPARKCORE
