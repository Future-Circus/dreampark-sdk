#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace DreamPark
{
    // Dependency-aware bundle grouper. Replaces the Legacy "one bundle per
    // top-level folder" assignment with one bundle per user-facing root —
    // a prefab carrying LevelTemplate, PropTemplate, or PlayerRig (see
    // IsUserFacingRoot for the rationale). Each root's bundle also contains
    // the deps it uniquely owns; deps shared by multiple roots land in a
    // Shared bundle. The DreamPark SDK doesn't ship scenes — content is
    // exclusively prefab-based.
    //
    // Why this matters for patch size:
    //   With Legacy bundling, editing one texture in Models/Textures invalidates
    //   the entire Textures bundle (often hundreds of MB). With Smart bundling,
    //   it invalidates only the bundles of the prefab(s) that actually use that
    //   texture (typically one bundle, a few MB).
    //
    // What the algorithm does:
    //   1. Enumerate all addressable entries the Legacy pass produced and pick
    //      "user-facing roots" — Level/Attraction prefabs, Prop prefabs, and
    //      the Player rig prefab (the things players load by address).
    //   2. For each root, walk AssetDatabase.GetDependencies(path, recursive: true)
    //      and build a reverse-ref map: dep -> set of roots that reference it.
    //   3. For each root, create a group named "{contentId}-Bundle-{root}".
    //      Move the root + every dep with refCount == 1 into that group.
    //   4. For deps with refCount > 1, move them to "{contentId}-Shared".
    //   5. Addressable entries that aren't roots and aren't picked up as deps
    //      (e.g. AudioClips referenced only by Lua via address strings) stay
    //      in a "{contentId}-Misc" bundle so they're still loadable by name.
    //   6. Bundle naming uses a stable group name; Addressables' AppendHash
    //      contributes the content hash. Two builds with identical content
    //      produce identical bundle filenames.
    //
    // Status: EXPERIMENTAL. Run behind the BundlingStrategy.Smart toggle. The
    // first build after switching to Smart will look like a full re-upload
    // because every asset moves to a new group.
    //
    // Open edge cases worth validating before flipping default:
    //   - Materials shared by many props: should land in Shared (typically a
    //     few hundred KB total). Verify by running and inspecting groups.
    //   - Shaders: will be heavily shared, will land in Shared. Same.
    //   - Lua-referenced audio: not picked up by GetDependencies (Lua isn't
    //     a Unity dep). Will land in Misc. That's correct semantically; Misc
    //     becomes a single bundle that updates whenever any of its members
    //     change. Acceptable for audio that genuinely is loaded by name.
    //   - Scripts (.cs): excluded from dep walking — they ship via a separate
    //     MonoScript bundle that Addressables manages.
    //   - Package assets ("Packages/..."): excluded — these aren't shipped as
    //     addressables.
    public static class SmartBundleGrouper
    {
        private const string GroupPrefix = "Bundle";  // gives "{contentId}-Bundle-{root}"
        private const string MiscSuffix = "Misc";

        // Extensions checked when pairing a preview with its prefab/scene by name.
        // GenerateAllLevelPreviews writes .png today; .jpg / .jpeg are accepted
        // defensively in case anyone hand-drops a different format.
        private static readonly string[] PreviewExtensions = new[] { ".png", ".jpg", ".jpeg" };

        public struct Result
        {
            public int rootBundles;
            public int miscAssets;
            public int groupsCreated;
            public int groupsRemoved;
        }

        public static Result ApplyDependencyAwareGrouping(
            AddressableAssetSettings settings,
            string gameId)
        {
            var result = new Result();
            if (settings == null || string.IsNullOrEmpty(gameId)) return result;

            // 1. Collect addressable entries that belong to this contentId.
            //    The Legacy pass put them in "{gameId}-{folder}" groups; we
            //    re-partition them.
            var contentEntries = new List<AddressableAssetEntry>();
            foreach (var group in settings.groups.Where(g => g != null))
            {
                if (!group.Name.StartsWith(gameId + "-")) continue;
                // Skip the special "{gameId}-Logos" group — logos are app-level
                // assets, not gameplay content; let them ride the Logos bundle.
                if (group.Name.EndsWith("-Logos")) continue;
                foreach (var entry in group.entries.ToList())
                {
                    contentEntries.Add(entry);
                }
            }

            if (contentEntries.Count == 0) return result;

            // 2. Pick roots. A "root" is an addressable that players load by
            //    address: prefabs and scenes. Everything else either gets
            //    pulled in as a dep or lands in Misc.
            var allEntryPaths = contentEntries
                .Select(e => AssetDatabase.GUIDToAssetPath(e.guid))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The full root set is just the top-level roots — prefabs with
            // LevelTemplate, PropTemplate, or PlayerRig. We previously also
            // promoted "shared sub-prefabs" (non-template prefabs referenced
            // by 2+ TLRs) to secondary roots so they got their own bundles.
            // In practice that produced lots of thin "wrapper" bundles
            // (e.g. SkeletonPBRDefault.prefab with one material inside) that
            // bloat the bundle count without giving meaningful update
            // granularity. Dropped that promotion — those wrapper prefabs
            // now pack as deps with the first-alphabetical TLR that
            // references them, consolidating the skeleton/coin/etc. content
            // into a single themed hub bundle. If a shared sub-prefab ever
            // genuinely deserves its own bundle (large independent unit),
            // we can re-add a smarter promotion rule (e.g. "promote only if
            // the prefab carries N+ unique non-prefab deps").
            var rootPaths = contentEntries
                .Select(e => AssetDatabase.GUIDToAssetPath(e.guid))
                .Where(IsUserFacingRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Roots get their own bundles unconditionally. We track them as
            // a HashSet so the dep loops below can skip moving any path
            // that's a root in its own right — without this guard a root
            // referenced by another root (e.g. a sub-prefab) would get
            // pulled into the parent's bundle, lose its own granularity,
            // and have its own (now empty) group cleaned up.
            var rootSet = new HashSet<string>(rootPaths, StringComparer.OrdinalIgnoreCase);

            // Pre-compute group names for each root and disambiguate any
            // name collisions (two prefabs across different folders sharing
            // a filename would otherwise both claim "{gameId}-Bundle-{name}"
            // and silently clobber each other). The disambiguator is a
            // short, deterministic hash of the asset path so it stays
            // stable across builds — important for the BuildCache to keep
            // unchanged bundles cached.
            var rootGroupNames = BuildRootGroupNames(gameId, rootPaths);

            // 3. Build dep closures + reverse-ref counts using *exclusive*
            //    deps — we BFS each root's direct deps but don't traverse
            //    through other roots. This way, a root's exclusive subtree
            //    only contains assets in its own ownership (not the deps
            //    of secondary roots it happens to reference). Without this,
            //    a TLR that references CoinPrefab would have CoinTexture
            //    and CoinModel in its dep list (because GetDependencies is
            //    transitive), making them refCount==2 from CoinPrefab's
            //    perspective and stranding them in Shared instead of
            //    packing them with CoinPrefab.
            var depGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var refCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var directDepsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var rootPath in rootPaths)
            {
                var deps = CollectExclusiveDeps(rootPath, rootSet, directDepsCache)
                    .Where(d => !ShouldSkipAsDep(d))
                    .Where(d => allEntryPaths.Contains(d))
                    .Where(d => !string.Equals(d, rootPath, StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                depGraph[rootPath] = deps;
                foreach (var d in deps)
                    refCount[d] = refCount.TryGetValue(d, out var c) ? c + 1 : 1;
            }

            // 4. Create or reuse the Misc group. Note: there's no Shared
            //    group anymore — when an asset is referenced by multiple
            //    roots, we pack it with the first-alphabetical root that
            //    uses it instead of stranding it in a generic Shared bundle.
            //    That root's bundle becomes the "hub" for that family of
            //    assets, and Addressables auto-loads it whenever a peer
            //    root needs the shared content.
            var miscGroup = GetOrCreateGroup(settings, $"{gameId}-{MiscSuffix}", out bool miscCreated);
            ConfigureBundleSchema(settings, miscGroup);
            if (miscCreated) result.groupsCreated++;

            // 5. Pre-create every root's bundle group and move just the root
            //    asset into it. Deps come in step 6 below.
            //    rootPaths is already sorted alphabetically, so iteration order
            //    is deterministic and stable across builds.
            var rootGroups = new Dictionary<string, AddressableAssetGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var rootPath in rootPaths)
            {
                string groupName = rootGroupNames[rootPath];
                var rootGroup = GetOrCreateGroup(settings, groupName, out bool created);
                ConfigureBundleSchema(settings, rootGroup);
                if (created) result.groupsCreated++;
                result.rootBundles++;
                rootGroups[rootPath] = rootGroup;
                MoveEntryToGroup(settings, rootPath, rootGroup);
            }

            // 6. Determine ownership of every non-root dep and move it into
            //    that root's bundle. Rule: each non-root dep is owned by the
            //    first-alphabetical root whose exclusiveDeps contain it.
            //    (rootPaths is already alphabetically sorted, so iterating
            //    in order means the first encounter wins.)
            //    This consolidates skeleton-themed content into a single
            //    "skeleton hub" bundle (e.g. Bundle-P_Skeleton), instead of
            //    splintering it into Shared + per-TLR thin bundles. Players
            //    loading any peer that references that content auto-pull
            //    the hub bundle through Addressables' dep chain.
            foreach (var rootPath in rootPaths)
            {
                if (!depGraph.TryGetValue(rootPath, out var deps)) continue;
                var rootGroup = rootGroups[rootPath];

                foreach (var dep in deps)
                {
                    if (rootSet.Contains(dep)) continue;     // other roots own their own subtree
                    // First-encountered root wins. MoveEntryToGroup is a no-op
                    // when the entry is already in the target group, but we
                    // also want to skip if it's already in *some* root group
                    // (assigned by an earlier-alphabetical root's pass).
                    var existing = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(dep));
                    if (existing != null && existing.parentGroup != null
                        && existing.parentGroup.Name.StartsWith($"{gameId}-{GroupPrefix}-", StringComparison.Ordinal))
                    {
                        continue; // already claimed by an earlier root
                    }
                    MoveEntryToGroup(settings, dep, rootGroup);
                }

                // Co-pack the matching preview, if any. Convention:
                //   Assets/Content/{gameId}/Previews/{rootName}.{png|jpg|jpeg}
                // belongs with the prefab named {rootName}.prefab. Previews
                // aren't picked up by GetDependencies — the prefab doesn't
                // reference its own preview — so without this rule they'd
                // all land in Misc and a single prop edit would invalidate
                // every preview's bundle. Pairing by name mirrors how
                // ContentProcessor / GenerateAllLevelPreviews already pair
                // previews to prefabs at write time.
                string rootBaseName = Path.GetFileNameWithoutExtension(rootPath);
                string previewFolder = $"Assets/Content/{gameId}/Previews/";
                foreach (var previewExt in PreviewExtensions)
                {
                    string previewPath = previewFolder + rootBaseName + previewExt;
                    if (allEntryPaths.Contains(previewPath))
                    {
                        MoveEntryToGroup(settings, previewPath, rootGroup);
                    }
                }
            }

            // 7. Stragglers: any addressable still sitting in a Legacy
            //    folder group (not reached by any root's deps and not yet
            //    moved to a Bundle-* or Misc) goes to Misc. These are
            //    typically Lua-loaded audio/textures referenced by name at
            //    runtime — not visible to GetDependencies, so they stay
            //    addressable in Misc instead of getting orphaned.
            var stragglers = new List<AddressableAssetEntry>();
            foreach (var group in settings.groups.Where(g => g != null).ToList())
            {
                if (!group.Name.StartsWith(gameId + "-")) continue;
                if (group.Name == $"{gameId}-{MiscSuffix}") continue;
                if (group.Name.StartsWith($"{gameId}-{GroupPrefix}-")) continue;
                if (group.Name.EndsWith("-Logos")) continue;

                foreach (var entry in group.entries.ToList())
                    stragglers.Add(entry);
            }
            foreach (var entry in stragglers)
            {
                string p = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(p)) continue;
                MoveEntryToGroup(settings, p, miscGroup);
                result.miscAssets++;
            }

            // 8. Sweep empty Legacy folder groups so the Addressables window
            //    stays tidy. Don't touch groups owned by other contentIds or
            //    by the Default Local Group / Built In Data, which Addressables
            //    requires.
            var toRemove = new List<AddressableAssetGroup>();
            foreach (var group in settings.groups.Where(g => g != null))
            {
                if (!group.Name.StartsWith(gameId + "-")) continue;
                if (group.Name == $"{gameId}-{MiscSuffix}") continue;
                if (group.Name.StartsWith($"{gameId}-{GroupPrefix}-")) continue;
                if (group.Name.EndsWith("-Logos")) continue;
                if (group.entries.Count == 0)
                    toRemove.Add(group);
            }
            foreach (var g in toRemove)
            {
                settings.RemoveGroup(g);
                result.groupsRemoved++;
            }

            // Also drop empty Smart groups (Bundle-* or Misc when nothing
            // qualified) so the next pass starts clean.
            var emptyManaged = settings.groups
                .Where(g => g != null && g.Name.StartsWith(gameId + "-")
                            && (g.Name.StartsWith($"{gameId}-{GroupPrefix}-")
                                || g.Name == $"{gameId}-{MiscSuffix}")
                            && g.entries.Count == 0)
                .ToList();
            foreach (var g in emptyManaged)
            {
                settings.RemoveGroup(g);
                result.groupsRemoved++;
            }

            EditorUtility.SetDirty(settings);
            return result;
        }

        // --- helpers ---------------------------------------------------------

        // Computes the bundle-group name for each root, deterministically
        // disambiguating any name collisions. The common case is "two prefabs
        // never share a filename" — those get clean names like
        // "{gameId}-Bundle-Treasure". When a collision exists (e.g. two
        // different folders both contain Decoration.prefab), every collider
        // gets a stable 6-char suffix derived from its full asset path so
        // the names round-trip identically build over build. We disambiguate
        // *all* members of a colliding name (not just the second-onwards) so
        // group identity doesn't shift when adding/removing siblings.
        private static Dictionary<string, string> BuildRootGroupNames(string gameId, List<string> rootPaths)
        {
            var nameToPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in rootPaths)
            {
                string sanitized = Sanitize(Path.GetFileNameWithoutExtension(p));
                if (!nameToPaths.TryGetValue(sanitized, out var list))
                {
                    list = new List<string>();
                    nameToPaths[sanitized] = list;
                }
                list.Add(p);
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in nameToPaths)
            {
                string baseName = kv.Key;
                if (kv.Value.Count == 1)
                {
                    result[kv.Value[0]] = $"{gameId}-{GroupPrefix}-{baseName}";
                }
                else
                {
                    foreach (var path in kv.Value)
                        result[path] = $"{gameId}-{GroupPrefix}-{baseName}-{ShortPathHash(path)}";
                }
            }
            return result;
        }

        // Stable 6-char hex hash of a string. Deterministic across processes
        // and Unity versions — important so unchanged content produces the
        // same group names build over build, which is what lets the
        // BuildCache + content-hash bundle filenames stay valid.
        private static string ShortPathHash(string s)
        {
            unchecked
            {
                uint hash = 2166136261u;                                 // FNV-1a 32-bit
                for (int i = 0; i < s.Length; i++)
                {
                    hash ^= s[i];
                    hash *= 16777619u;
                }
                return hash.ToString("x8").Substring(0, 6);
            }
        }

        // BFS the dep graph from `startRoot`, collecting every asset reachable
        // via *direct* references — but stop at any other root we encounter.
        // A root's transitive subtree belongs to that root, not the caller.
        // Caching direct-deps per path inside a single Smart pass amortizes
        // the cost when the same asset is reached by multiple roots.
        private static HashSet<string> CollectExclusiveDeps(
            string startRoot, HashSet<string> rootSet,
            Dictionary<string, string[]> directDepsCache)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startRoot };
            queue.Enqueue(startRoot);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!directDepsCache.TryGetValue(current, out var directDeps))
                {
                    directDeps = AssetDatabase.GetDependencies(current, recursive: false);
                    directDepsCache[current] = directDeps;
                }
                foreach (var dep in directDeps)
                {
                    if (string.Equals(dep, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!visited.Add(dep)) continue;
                    result.Add(dep);

                    // Other roots own their own subtree — record the reference
                    // (so the caller's refCount is correct) but don't recurse
                    // through them.
                    if (!rootSet.Contains(dep))
                        queue.Enqueue(dep);
                }
            }
            return result;
        }

        // A "user-facing root" is something players load by address as a
        // top-level unit, and therefore deserves its own bundle for granular
        // updates. The DreamPark SDK has exactly three classes of root
        // prefab — every other asset is either a dep of one of these or an
        // object instantiated at runtime:
        //
        //   - LevelTemplate (catches AttractionTemplate via inheritance) —
        //     the loadable level/attraction units players walk into.
        //   - PropTemplate — individual props placed in levels.
        //   - PlayerRig — the persistent Player.prefab that hosts global
        //     systems (audio, score, park state) across attractions.
        //
        // Treating every .prefab as a root would give material-library
        // prefabs, sub-component prefabs, and prefab variants their own
        // bundles, defeating the point of co-packing refCount==1 deps
        // with their consumer.
        //
        // Unity scenes are deliberately not classified as roots — DreamPark
        // content ships as Addressable prefabs, not scenes (per the SDK
        // CLAUDE.md: "Scenes are for testing only").
        private static bool IsUserFacingRoot(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (ext != ".prefab") return false;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) return false;

            return prefab.GetComponent<LevelTemplate>() != null
                || prefab.GetComponent<PropTemplate>() != null
                || prefab.GetComponent<PlayerRig>() != null;
        }

        private static bool ShouldSkipAsDep(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return true;
            // ThirdPartyLocal is the staging area for un-tracked / un-shipped
            // third-party content. ContentProcessor already excludes it from
            // addressable groups, but we double-up here as defensive guard so
            // the Smart pass never accidentally tries to bundle anything from
            // that folder even if a stale entry slipped through.
            if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            // Scripts / asmdefs ship via the MonoScript bundle, not as
            // addressable assets. Excluding them avoids false sharing.
            if (ext == ".cs" || ext == ".asmdef" || ext == ".asmref") return true;
            if (ext == ".dll" || ext == ".meta") return true;
            return false;
        }

        private static AddressableAssetGroup GetOrCreateGroup(
            AddressableAssetSettings settings, string groupName, out bool created)
        {
            var existing = settings.groups.FirstOrDefault(g => g != null && g.Name == groupName);
            if (existing != null) { created = false; return existing; }
            created = true;
            return settings.CreateGroup(groupName, false, false, true,
                new List<AddressableAssetGroupSchema>
                {
                    (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(BundledAssetGroupSchema)),
                    (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(ContentUpdateGroupSchema)),
                });
        }

        private static void ConfigureBundleSchema(
            AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            var bag = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
            bag.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
            bag.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
            bag.UseAssetBundleCache = true;
            bag.UseAssetBundleCrc = true;
            bag.UseAssetBundleCrcForCachedBundles = false;
            // PackTogether *within* a group — the granularity comes from how
            // we slice the groups, not from PackSeparately. Avoids the nested-
            // directory bundle layout problem that PackSeparately causes.
            bag.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bag.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
        }

        private static void MoveEntryToGroup(
            AddressableAssetSettings settings, string assetPath, AddressableAssetGroup target)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return;
            var entry = settings.FindAssetEntry(guid);
            if (entry != null && entry.parentGroup == target) return;
            settings.CreateOrMoveEntry(guid, target, false, false);
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "");
            return name.Replace("[", "").Replace("]", "").Replace(" ", "_").Trim();
        }
    }
}
#endif
