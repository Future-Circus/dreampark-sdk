#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Tracks which addressable groups have been touched since the last
    // *successful* upload, so the patch estimator can answer "what would
    // change if I uploaded right now?" without having to actually run a
    // build.
    //
    // The watchdog (ContentFolderWatchdog) already fires whenever an asset
    // under Assets/Content/ changes. ContentProcessor.ProcessContentFilesChanged
    // calls AddDirty(...) with the parent groups of the changed files, so
    // the dirty set stays current as the developer works. On successful
    // upload, ContentUploaderPanel calls Clear(...) — at that point the
    // groups on the server are in sync with the local state, so nothing's
    // dirty.
    //
    // Stored at <ProjectRoot>/Library/DreamParkBuildManifests/{contentId}-dirty.json
    // alongside the BuildManifestStore baselines. Library/ is git-ignored
    // and per-machine; matches the baseline's lifecycle. A future enhancement
    // could sync this to the backend so teammates share dirty state.

    [Serializable]
    internal class DirtyGroupsData
    {
        public List<string> dirtyGroupNames = new List<string>();
    }

    public static class DirtyGroupsStore
    {
        public static string PathForContent(string contentId)
        {
            return Path.Combine(BuildManifestStore.ManifestRoot, $"{contentId}-dirty.json");
        }

        public static HashSet<string> Load(string contentId)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(contentId)) return set;

            string path = PathForContent(contentId);
            if (!File.Exists(path)) return set;

            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<DirtyGroupsData>(json);
                if (data?.dirtyGroupNames != null)
                {
                    foreach (var name in data.dirtyGroupNames)
                    {
                        if (!string.IsNullOrEmpty(name)) set.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DirtyGroupsStore] Failed to read {path}: {e.Message}");
            }
            return set;
        }

        public static void Save(string contentId, HashSet<string> groupNames)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            try
            {
                Directory.CreateDirectory(BuildManifestStore.ManifestRoot);
                var data = new DirtyGroupsData
                {
                    dirtyGroupNames = groupNames != null
                        ? groupNames.OrderBy(n => n, StringComparer.Ordinal).ToList()
                        : new List<string>(),
                };
                string json = JsonUtility.ToJson(data, prettyPrint: true);
                File.WriteAllText(PathForContent(contentId), json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DirtyGroupsStore] Failed to save: {e.Message}");
            }
        }

        // Adds the given group names to the dirty set. No-op if all names
        // are already present (avoids spurious file writes from rapid
        // editor saves).
        public static void AddDirty(string contentId, IEnumerable<string> groupNames)
        {
            if (string.IsNullOrEmpty(contentId) || groupNames == null) return;

            var current = Load(contentId);
            bool changed = false;
            foreach (var name in groupNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (current.Add(name)) changed = true;
            }
            if (changed) Save(contentId, current);
        }

        // Clears every dirty entry for the contentId. Called on successful
        // upload — the server is now in sync with the local state.
        public static void Clear(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            try
            {
                string path = PathForContent(contentId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DirtyGroupsStore] Failed to clear: {e.Message}");
            }
        }
    }

    // Result of estimating a patch from a dirty-groups set against a saved
    // baseline manifest. estimatedBytes is approximate — see notes on the
    // estimator method for accuracy bounds.
    public class DirtyGroupsEstimate
    {
        public long estimatedBytes;
        public int matchedBundles;
        public int matchedGroups;
        // Group names that we couldn't find any matching bundle for in the
        // baseline. Usually means: the group was created after the last
        // upload, or Smart bundling re-partitioned and the old group name
        // doesn't exist any more. The UI should signal that the estimate
        // is incomplete in this case.
        public List<string> unmatchedGroupNames = new List<string>();
        public bool isIncomplete => unmatchedGroupNames.Count > 0;
    }

    public static class DirtyGroupsEstimator
    {
        // Matches a dirty group name against bundle filenames in the
        // baseline manifest by prefix, summing the sizes of bundles that
        // belong to that group. Bundle filenames produced by Addressables
        // (AppendHash naming) follow the pattern
        // "{lowercase(groupName)}_{kind}_{...}_{hash}.bundle", so a
        // case-insensitive prefix match on "{groupName}_" reliably picks
        // out a group's bundles.
        //
        // Accuracy bounds:
        //   - Accurate when the dirty group still exists in the baseline
        //     (Legacy mode, or Smart-mode groups that haven't been
        //     re-partitioned since the baseline was saved).
        //   - Over-estimates when the user touched a file but the resulting
        //     bundle ends up byte-identical (rare). The actual upload's
        //     skip-set will catch this and ship fewer bytes than estimated.
        //   - Incomplete when a dirty group has no matching bundles in the
        //     baseline (new group, renamed group). Surfaces via
        //     unmatchedGroupNames; the UI should label the estimate
        //     accordingly.
        public static DirtyGroupsEstimate Estimate(BuildManifest baseline, IEnumerable<string> dirtyGroupNames)
        {
            var result = new DirtyGroupsEstimate();
            if (baseline == null || dirtyGroupNames == null) return result;

            var dirty = dirtyGroupNames
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (dirty.Count == 0) return result;

            foreach (var groupName in dirty)
            {
                string prefix = groupName.ToLowerInvariant() + "_";
                bool matchedAny = false;

                foreach (var platform in baseline.platforms)
                {
                    foreach (var f in platform.files)
                    {
                        if (string.IsNullOrEmpty(f.fileName)) continue;
                        if (!f.fileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) continue;
                        if (f.fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            result.estimatedBytes += f.sizeBytes;
                            result.matchedBundles++;
                            matchedAny = true;
                        }
                    }
                }

                if (matchedAny) result.matchedGroups++;
                else result.unmatchedGroupNames.Add(groupName);
            }

            return result;
        }
    }
}
#endif
