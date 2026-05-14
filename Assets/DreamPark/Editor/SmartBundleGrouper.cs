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
    //      in a "{contentId}-Runtime" bundle so they're still loadable by name.
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
    //     a Unity dep). Will land in Runtime. That's correct semantically; Runtime
    //     becomes a single bundle that updates whenever any of its members
    //     change. Acceptable for audio that genuinely is loaded by name.
    //   - Scripts (.cs): excluded from dep walking — they ship via a separate
    //     MonoScript bundle that Addressables manages.
    //   - Package assets ("Packages/..."): excluded — these aren't shipped as
    //     addressables.
    public static class SmartBundleGrouper
    {
        private const string GroupPrefix = "Bundle";  // gives "{contentId}-Bundle-{root}"
        private const string RuntimeSuffix = "Runtime";
        private const string PreviewsSuffix = "Previews";
        private const string CodeSuffix = "Code";

        // Extensions checked when pairing a preview with its prefab/scene by name.
        // GenerateAllLevelPreviews writes .png today; .jpg / .jpeg are accepted
        // defensively in case anyone hand-drops a different format.
        private static readonly string[] PreviewExtensions = new[] { ".png", ".jpg", ".jpeg" };

        // Suffix used to identify game Lua scripts. Path.GetExtension would
        // return just ".txt", so we match the full compound suffix instead.
        private const string LuaScriptSuffix = ".lua.txt";

        // Public so callers (e.g. the upload-mode filter in ContentUploaderPanel)
        // can identify which built bundles belong to the Code / Previews groups
        // from filename alone — Addressables' AppendHash naming embeds the
        // lowercased group name in the resulting bundle filename.
        public static string CodeGroupName(string gameId) => $"{gameId}-{CodeSuffix}";
        public static string PreviewsGroupName(string gameId) => $"{gameId}-{PreviewsSuffix}";

        public struct Result
        {
            public int rootBundles;
            public int runtimeAssets;
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
            //    pulled in as a dep or lands in Runtime.
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
            // Root ordering matters for shared-dep ownership. Earlier-iterated
            // roots get first claim on a shared dep, and "claimed" sticks for
            // the rest of the pass. So we sort by *root type priority* first,
            // alphabetical as a tiebreak.
            //
            // Priority order: Prop (smallest, most reusable) → Level/Attraction
            // → PlayerRig (largest, most universal). When a texture is used by
            // both an attraction and a prop that lives inside it, the prop wins
            // ownership. The attraction loads the prop's bundle (and the
            // texture along with it) via the runtime dep chain.
            //
            // Why this matters: claiming-by-smaller-unit minimizes the blast
            // radius of asset changes. Edit a wood texture → only the small
            // prop bundle re-uploads. Edit an attraction-unique mesh → only
            // the attraction bundle re-uploads. Add a new attraction that
            // shares props with existing ones → no existing bundles reshuffle,
            // because the props already own those shared deps.
            var rootPaths = contentEntries
                .Select(e => AssetDatabase.GUIDToAssetPath(e.guid))
                .Where(IsUserFacingRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => RootPriority(p))                              // smaller types claim first
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)           // alphabetical tiebreak
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

            // Scope dep ownership to assets within this content title's folder.
            // We deliberately walk deps that aren't yet addressable (the old
            // `allEntryPaths.Contains(d)` filter was too narrow — it meant a
            // material/mesh/texture under Assets/Content/{gameId}/ that wasn't
            // already in an addressable group was invisible to ownership
            // assignment, so Unity's bundle builder would silently pack it
            // into whichever bundle it built first — typically an attraction,
            // alphabetically first — instead of the prop that actually owns
            // it. Result was 4 KB prop bundles with all their texture/material
            // content drained into the host attraction. By promoting any
            // in-content-folder dep to an explicit entry in its owning root's
            // group, we tell Unity exactly which bundle should contain it.
            //
            // The contentFolderPrefix scope keeps us from accidentally
            // promoting SDK-shared assets (Assets/DreamPark/...), Unity
            // built-ins, or package assets into per-content bundles — those
            // stay as implicit deps Unity handles separately.
            string contentFolderPrefix = $"Assets/Content/{gameId}/";

            foreach (var rootPath in rootPaths)
            {
                var deps = CollectExclusiveDeps(rootPath, rootSet, directDepsCache)
                    .Where(d => !ShouldSkipAsDep(d))
                    .Where(d => d.StartsWith(contentFolderPrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(d => !string.Equals(d, rootPath, StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                depGraph[rootPath] = deps;
                foreach (var d in deps)
                    refCount[d] = refCount.TryGetValue(d, out var c) ? c + 1 : 1;
            }

            // 4. Create or reuse the special managed groups:
            //    - Previews: lightweight PNG/JPG screenshots for browser UI
            //    - Code:     game Lua scripts (.lua.txt under Assets/Content/{gameId}/)
            //    - Runtime:     addressables that aren't roots or walked deps
            //  Previews intentionally live outside the root bundles so the
            //  admin/content-manager UI can fetch thumbnails without pulling
            //  the heavy gameplay prefab bundles over the wire.
            //  Code lives outside the root bundles for the same reason —
            //  iterating on Lua should ship a few KB, not the parent prefab
            //  bundle the LuaBehaviour reference happens to point at.
            var previewGroup = GetOrCreateGroup(settings, $"{gameId}-{PreviewsSuffix}", out bool previewCreated);
            ConfigureBundleSchema(settings, previewGroup);
            if (previewCreated) result.groupsCreated++;

            var codeGroup = GetOrCreateGroup(settings, $"{gameId}-{CodeSuffix}", out bool codeCreated);
            ConfigureBundleSchema(settings, codeGroup);
            if (codeCreated) result.groupsCreated++;

            var runtimeGroup = GetOrCreateGroup(settings, $"{gameId}-{RuntimeSuffix}", out bool runtimeCreated);
            ConfigureBundleSchema(settings, runtimeGroup);
            if (runtimeCreated) result.groupsCreated++;

            // 5. Pre-create every root's Logic group AND a parallel "-Content"
            //    group. The Logic group holds the root prefab itself and any
            //    other prefab/.asset deps that carry user MonoBehaviour or
            //    ScriptableObject serialization. The Content group holds the
            //    heavy presentation stuff — materials, meshes, textures, audio,
            //    animations, plus any pure-visual sub-prefabs that don't carry
            //    user scripts.
            //
            //    Why split: a C# script edit that touches serializable shape
            //    rewrites the bytes of every prefab containing a MonoBehaviour
            //    of that class. Without the split, that change drags the whole
            //    100+ MB attraction bundle along with it. With the split, only
            //    the (small) Logic bundle re-uploads; the (heavy) Content
            //    bundle stays byte-identical.
            //
            //    The suffix is "-Content" rather than "-Assets" because Unity's
            //    AppendHash naming style already adds "_assets_all" to bundle
            //    filenames, and "-Assets" would produce ugly
            //    "*-assets_assets_all_<hash>.bundle" paths.
            var rootGroups = new Dictionary<string, AddressableAssetGroup>(StringComparer.OrdinalIgnoreCase);
            var rootAssetsGroups = new Dictionary<string, AddressableAssetGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var rootPath in rootPaths)
            {
                string groupName = rootGroupNames[rootPath];
                var rootGroup = GetOrCreateGroup(settings, groupName, out bool created);
                ConfigureBundleSchema(settings, rootGroup);
                if (created) result.groupsCreated++;
                result.rootBundles++;
                rootGroups[rootPath] = rootGroup;
                MoveEntryToGroup(settings, rootPath, rootGroup);

                // Parallel Content group. Empty for now — populated in step 6
                // with this root's heavy / non-script deps. The empty-group
                // prune at the end of this pass will drop it if no content
                // actually lands here (e.g. a script-only prefab with no
                // material/mesh deps).
                string contentGroupName = $"{groupName}-Content";
                var contentGroup = GetOrCreateGroup(settings, contentGroupName, out bool contentCreated);
                ConfigureBundleSchema(settings, contentGroup);
                if (contentCreated) result.groupsCreated++;
                rootAssetsGroups[rootPath] = contentGroup;
            }

            // 6. Determine ownership of every non-root dep and route it into
            //    either the root's Logic bundle (prefabs with user
            //    MonoBehaviours, or .asset ScriptableObjects — anything whose
            //    bytes shift when scripts change shape) or the root's
            //    -Content bundle (everything else: materials, meshes,
            //    textures, audio, animations, plus pure-visual sub-prefabs).
            //
            //    Ownership rule: the first-iterated root whose exclusiveDeps
            //    contain the dep wins. rootPaths is sorted by priority (props
            //    before attractions before players), so a texture used by both
            //    a prop and the attraction containing it ends up in the prop's
            //    bundle. The attraction loads it via the runtime dep chain.
            //    This is the "smaller unit claims it" rule from the design.
            foreach (var rootPath in rootPaths)
            {
                if (!depGraph.TryGetValue(rootPath, out var deps)) continue;
                var rootGroup = rootGroups[rootPath];
                var assetsGroup = rootAssetsGroups[rootPath];

                foreach (var dep in deps)
                {
                    if (rootSet.Contains(dep)) continue;     // other roots own their own subtree
                    // First-encountered root wins. MoveEntryToGroup is a no-op
                    // when the entry is already in the target group, but we
                    // also want to skip if it's already in *some* Bundle-*
                    // group (assigned by an earlier-priority root's pass —
                    // either its Logic or its Assets bundle).
                    var existing = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(dep));
                    if (existing != null && existing.parentGroup != null
                        && existing.parentGroup.Name.StartsWith($"{gameId}-{GroupPrefix}-", StringComparison.Ordinal))
                    {
                        continue; // already claimed by an earlier root (logic or assets)
                    }
                    // Route via IsLogicAsset: script-bearing prefabs and .asset
                    // ScriptableObjects → Logic bundle. Everything else →
                    // -Content bundle. The small-Logic / heavy-Content split is
                    // what makes script-only edits ship tiny patches.
                    var target = IsLogicAsset(dep) ? rootGroup : assetsGroup;
                    MoveEntryToGroup(settings, dep, target);
                }

            }

            // 6.5. Merge tiny Logic/Content pairs back into a single bundle.
            //
            // The Logic/Content split is only useful when the Content side is
            // heavy enough that isolating it from script-edit churn meaningfully
            // shrinks patches. For a small prop whose total content is under
            // ~10 MB (e.g., a basic decorative prefab with one mesh + one
            // material), splitting produces two near-empty bundles and bloats
            // the bundle count for zero patch-size benefit. Merge them.
            //
            // The merge target is the Logic group (the un-suffixed name), so
            // the consolidated bundle keeps the natural Bundle-{root} name.
            // The Content group is emptied out and gets swept by the empty-
            // group prune in step 9.
            const long kTinyRootMergeBytes = 10L * 1024 * 1024;
            foreach (var rootPath in rootPaths)
            {
                var logicGroup = rootGroups[rootPath];
                var contentGroup = rootAssetsGroups[rootPath];
                long totalBytes = SumGroupEntryBytes(logicGroup) + SumGroupEntryBytes(contentGroup);
                if (totalBytes > kTinyRootMergeBytes) continue;

                // Move every Content entry into the Logic group. The content
                // group will be empty after this loop; step 9's empty-group
                // prune removes it.
                foreach (var entry in contentGroup.entries.ToList())
                {
                    string p = AssetDatabase.GUIDToAssetPath(entry.guid);
                    if (string.IsNullOrEmpty(p)) continue;
                    MoveEntryToGroup(settings, p, logicGroup);
                }
            }

            // 7. Move all preview images into the dedicated preview bundle.
            //    Convention:
            //      Assets/Content/{gameId}/Previews/{rootName}.{png|jpg|jpeg}
            //    Mirrors the preview-generator output and keeps the browser
            //    UX cheap: loading a preview no longer drags in the root
            //    prefab's gameplay bundle.
            string previewFolderPrefix = $"Assets/Content/{gameId}/Previews/";
            foreach (string previewPath in allEntryPaths
                .Where(path => path.StartsWith(previewFolderPrefix, StringComparison.OrdinalIgnoreCase)
                    && PreviewExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                MoveEntryToGroup(settings, previewPath, previewGroup);
            }

            // 7b. Move all game Lua scripts into the dedicated code bundle.
            //     Convention: every *.lua.txt under Assets/Content/{gameId}/
            //     is treated as game code, regardless of subfolder. Lua text
            //     assets are normally pulled into a prefab's bundle as a
            //     dep of LuaBehaviour.luaScript; promoting them to their
            //     own addressable entries in a dedicated Code group lets
            //     code-only patches ship a few-KB Lua bundle without
            //     re-uploading the parent prefab bundles. Prefab references
            //     resolve by GUID so moving the TextAsset doesn't break
            //     LuaBehaviour wiring at runtime.
            //
            //     Scope is intentionally restricted to Assets/Content/{gameId}/
            //     so SDK-shipped Lua under ThirdParty/XLua/Resources/ (engine
            //     scripts, tutorial samples) doesn't get yanked into game
            //     content.
            string contentRoot = $"Assets/Content/{gameId}";
            string[] luaGuids = AssetDatabase.IsValidFolder(contentRoot)
                ? AssetDatabase.FindAssets("t:TextAsset", new[] { contentRoot })
                : Array.Empty<string>();
            foreach (string luaGuid in luaGuids)
            {
                string luaPath = AssetDatabase.GUIDToAssetPath(luaGuid);
                if (string.IsNullOrEmpty(luaPath)) continue;
                if (!luaPath.EndsWith(LuaScriptSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                // CreateOrMoveEntry inside MoveEntryToGroup will both promote
                // a previously-implicit dep to an explicit entry and reassign
                // an explicit entry from its prior folder group — same call
                // covers both cases.
                MoveEntryToGroup(settings, luaPath, codeGroup);
            }

            // 8. Stragglers: any addressable still sitting in a Legacy
            //    folder group (not reached by any root's deps and not yet
            //    moved to a Bundle-* or Runtime) goes to Runtime. These are
            //    typically Lua-loaded audio/textures referenced by name at
            //    runtime — not visible to GetDependencies, so they stay
            //    addressable in Runtime instead of getting orphaned.
            //    Important: skip the chunked siblings (Runtime-2, Previews-2,
            //    Code-2, ...) too. They contain content already correctly
            //    placed by an earlier pass; harvesting them here would silently
            //    reshuffle them through Runtime, defeating chunk stability.
            var stragglers = new List<AddressableAssetEntry>();
            foreach (var group in settings.groups.Where(g => g != null).ToList())
            {
                if (!group.Name.StartsWith(gameId + "-")) continue;
                if (group.Name == $"{gameId}-{RuntimeSuffix}") continue;
                if (group.Name == $"{gameId}-{PreviewsSuffix}") continue;
                if (group.Name == $"{gameId}-{CodeSuffix}") continue;
                if (group.Name.StartsWith($"{gameId}-{GroupPrefix}-")) continue;
                if (group.Name.StartsWith($"{gameId}-{RuntimeSuffix}-", StringComparison.Ordinal)) continue;
                if (group.Name.StartsWith($"{gameId}-{PreviewsSuffix}-", StringComparison.Ordinal)) continue;
                if (group.Name.StartsWith($"{gameId}-{CodeSuffix}-", StringComparison.Ordinal)) continue;
                if (group.Name.EndsWith("-Logos")) continue;

                foreach (var entry in group.entries.ToList())
                    stragglers.Add(entry);
            }
            foreach (var entry in stragglers)
            {
                string p = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(p)) continue;
                MoveEntryToGroup(settings, p, runtimeGroup);
                result.runtimeAssets++;
            }

            // 8.5. Chunk oversized groups so individual asset edits ship
            //      small re-uploads instead of the whole bundle.
            //
            // When a single Bundle-* / Runtime / Content group ends up too large
            // (a texture-heavy Content bundle, a Lua-referenced audio Runtime
            // bundle, etc.), an edit to any one asset inside it requires
            // re-uploading the entire bundle. Splitting into chunks bounds
            // the re-upload cost per asset edit.
            //
            // Strategy: simple bin-packing in path-sorted order. Each chunk
            // fills with assets until adding the next one would exceed the
            // size limit, then spills into the next chunk. Chunk sizes stay
            // ≤ limit (one exception: a single asset bigger than the limit
            // gets its own chunk even though it exceeds the limit).
            const long kChunkInputBytesLimit = 40L * 1024 * 1024;

            // Load the append-only packing order for this content title.
            // The order is shared across the team via git so all devs produce
            // identical chunk composition for the same content. See
            // PackingOrderStore for the rationale and append-only semantics.
            var packingOrder = PackingOrderStore.Load(gameId);
            var packingIndexByGuid = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < packingOrder.Count; i++)
                packingIndexByGuid[packingOrder[i]] = i;

            // Helper that returns a stable index for a guid, appending it to
            // the order list (and the lookup) if it's new. Captures the
            // packingOrder / packingIndexByGuid locals so the chunker doesn't
            // need to know about persistence.
            int GetOrAssignPackingIndex(string guid)
            {
                if (packingIndexByGuid.TryGetValue(guid, out int idx)) return idx;
                int newIdx = packingOrder.Count;
                packingOrder.Add(guid);
                packingIndexByGuid[guid] = newIdx;
                return newIdx;
            }

            // Pre-pass: assign a packing index to EVERY entry across every
            // managed group, in path-sorted order, BEFORE any chunking runs.
            //
            // Why this is critical:
            //   1. First-build determinism. Without this pre-pass, indices are
            //      assigned in `group.entries` iteration order (Unity HashSet),
            //      which is NOT stable across machines. Two devs running the
            //      first build on a fresh content title would produce different
            //      PackingOrder.json files and start fighting over bundle names.
            //   2. Single-entry-group correctness. ChunkOversizedGroupIfNeeded
            //      early-returns when entries.Count <= 1, which means a group
            //      containing a single oversize asset never gets its GUID
            //      indexed. When a sibling joins later, the existing entry's
            //      position is determined by iteration order — non-deterministic.
            //
            // Path-sorted is the canonical deterministic ordering: same asset
            // paths produce the same indices regardless of which dev / OS /
            // Unity session runs the grouper first.
            var allManagedEntries = new List<AddressableAssetEntry>();
            foreach (var rootPath in rootPaths)
            {
                if (rootGroups.TryGetValue(rootPath, out var lg) && lg != null)
                    allManagedEntries.AddRange(lg.entries);
                if (rootAssetsGroups.TryGetValue(rootPath, out var cg) && cg != null)
                    allManagedEntries.AddRange(cg.entries);
            }
            if (runtimeGroup != null) allManagedEntries.AddRange(runtimeGroup.entries);
            if (previewGroup != null) allManagedEntries.AddRange(previewGroup.entries);
            if (codeGroup != null) allManagedEntries.AddRange(codeGroup.entries);

            allManagedEntries.Sort((a, b) =>
            {
                string pa = AssetDatabase.GUIDToAssetPath(a?.guid) ?? "";
                string pb = AssetDatabase.GUIDToAssetPath(b?.guid) ?? "";
                return string.CompareOrdinal(pa, pb);
            });

            foreach (var entry in allManagedEntries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.guid)) continue;
                GetOrAssignPackingIndex(entry.guid);   // no-op for existing; appends for new
            }

            foreach (var rootPath in rootPaths)
            {
                ChunkOversizedGroupIfNeeded(settings, rootGroups[rootPath], kChunkInputBytesLimit, GetOrAssignPackingIndex, ref result);
                ChunkOversizedGroupIfNeeded(settings, rootAssetsGroups[rootPath], kChunkInputBytesLimit, GetOrAssignPackingIndex, ref result);
            }
            ChunkOversizedGroupIfNeeded(settings, runtimeGroup, kChunkInputBytesLimit, GetOrAssignPackingIndex, ref result);
            ChunkOversizedGroupIfNeeded(settings, previewGroup, kChunkInputBytesLimit, GetOrAssignPackingIndex, ref result);
            ChunkOversizedGroupIfNeeded(settings, codeGroup, kChunkInputBytesLimit, GetOrAssignPackingIndex, ref result);

            // Persist the (possibly-extended) packing order so the next build
            // sorts entries by the same indices. Save() short-circuits when
            // the file's on-disk content is already current, so this is a
            // no-op when nothing's been added since last build.
            PackingOrderStore.Save(gameId, packingOrder);

            // 9. Sweep empty Legacy folder groups so the Addressables window
            //    stays tidy. Don't touch groups owned by other contentIds or
            //    by the Default Local Group / Built In Data, which Addressables
            //    requires.
            var toRemove = new List<AddressableAssetGroup>();
            foreach (var group in settings.groups.Where(g => g != null))
            {
                if (!group.Name.StartsWith(gameId + "-")) continue;
                if (group.Name == $"{gameId}-{RuntimeSuffix}") continue;
                if (group.Name == $"{gameId}-{PreviewsSuffix}") continue;
                if (group.Name == $"{gameId}-{CodeSuffix}") continue;
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

            // Also drop empty Smart groups (Bundle-*, Runtime, Previews, Code,
            // and their chunked variants like Runtime-2 / Bundle-X-3) so the
            // next pass starts clean.
            string runtimePrefix = $"{gameId}-{RuntimeSuffix}-";
            string previewsPrefix = $"{gameId}-{PreviewsSuffix}-";
            string codePrefix = $"{gameId}-{CodeSuffix}-";
            var emptyManaged = settings.groups
                .Where(g => g != null && g.Name.StartsWith(gameId + "-")
                            && (g.Name.StartsWith($"{gameId}-{GroupPrefix}-")          // Bundle-* (and Bundle-*-N chunks)
                                || g.Name == $"{gameId}-{RuntimeSuffix}"
                                || g.Name == $"{gameId}-{PreviewsSuffix}"
                                || g.Name == $"{gameId}-{CodeSuffix}"
                                || g.Name.StartsWith(runtimePrefix)                        // Runtime-2, Runtime-3, ...
                                || g.Name.StartsWith(previewsPrefix)
                                || g.Name.StartsWith(codePrefix))
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

        // Sums the input-file bytes of every entry in a group. Used by the
        // tiny-root merge step and as part of the chunking decision. Missing
        // / unreadable files contribute 0.
        private static long SumGroupEntryBytes(AddressableAssetGroup group)
        {
            if (group == null) return 0;
            long total = 0;
            foreach (var entry in group.entries)
            {
                string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) total += info.Length;
                }
                catch { /* unreadable; skip */ }
            }
            return total;
        }

        // If the group's total entry input-bytes exceed maxInputBytes,
        // bin-pack the entries into chunks. Sort order is the team-shared
        // packing index from PackingOrderStore (NOT alphabetical) — new
        // entries get appended to the end of the index list, so they always
        // land in the last chunk (or spill into a new last chunk). Existing
        // entries' indices never change.
        //
        // Properties:
        //   - Chunk sizes stay bounded by maxInputBytes (with one exception:
        //     a single asset bigger than the limit gets its own chunk).
        //   - Stable under appends: adding a new asset → it goes to the end
        //     of the order → only the last chunk grows or a new last chunk
        //     is created. Every other chunk is byte-identical to before.
        //   - Stable under removals: removing an asset → its slot in the
        //     packing order is preserved (append-only); the chunk it was
        //     in just loses that one entry. Every other chunk unchanged.
        //   - Stable under modifications: editing an asset → its index
        //     stays → same chunk. Only that chunk's bytes change.
        //
        // Chunk 0 keeps the original group name. Chunks 1..N-1 are named
        // "{groupName}-2", "{groupName}-3", ... so the un-suffixed group
        // name stays a stable identifier in the catalog.
        private static void ChunkOversizedGroupIfNeeded(
            AddressableAssetSettings settings,
            AddressableAssetGroup group,
            long maxInputBytes,
            Func<string, int> getOrAssignPackingIndex,
            ref Result result)
        {
            if (group == null) return;
            if (getOrAssignPackingIndex == null)
                throw new ArgumentNullException(nameof(getOrAssignPackingIndex));
            var entries = group.entries.ToList();
            if (entries.Count <= 1) return;

            long totalBytes = 0;
            foreach (var entry in entries)
            {
                string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) totalBytes += info.Length;
                }
                catch
                {
                    // Asset path doesn't map to a real file on disk
                    // (could be a folder marker, a stale entry, etc.).
                    // Ignore — those contribute 0 to size.
                }
            }

            if (totalBytes <= maxInputBytes) return;

            // Walk entries in packing-index order and fill each chunk until
            // adding the next entry would push it over the limit, then spill
            // to the next chunk. Each chunk's size stays ≤ maxInputBytes,
            // except for the rare case where a single asset is larger than
            // the limit (it gets its own chunk).
            //
            // Sort key is the packing index (append-only, team-shared via
            // PackingOrderStore). Existing entries keep their index across
            // builds; new entries get the next available index, which puts
            // them at the END of the order — so they only affect the last
            // chunk, never reshuffle existing chunks.
            var entriesWithSize = new List<(AddressableAssetEntry entry, int packIdx, long size)>();
            foreach (var entry in entries)
            {
                string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(path)) continue;
                long size = 0;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) size = info.Length;
                }
                catch { /* unreadable; size 0 */ }
                int packIdx = getOrAssignPackingIndex(entry.guid);
                entriesWithSize.Add((entry, packIdx, size));
            }

            entriesWithSize.Sort((a, b) => a.packIdx.CompareTo(b.packIdx));

            // First pass: figure out chunk count by walking the bin-pack.
            // We do this in a separate pass so we can pre-create exactly
            // the right number of groups instead of creating them lazily.
            var assignments = new List<int>(entriesWithSize.Count);
            int currentChunk = 0;
            long currentChunkBytes = 0;
            foreach (var (_, _, size) in entriesWithSize)
            {
                if (currentChunkBytes > 0 && currentChunkBytes + size > maxInputBytes)
                {
                    currentChunk++;
                    currentChunkBytes = 0;
                }
                assignments.Add(currentChunk);
                currentChunkBytes += size;
            }

            int chunkCount = currentChunk + 1;
            if (chunkCount <= 1) return;

            // Chunk 0 = the original group (unrenamed). Chunks 1..N-1 are
            // freshly-created sibling groups.
            var chunkGroups = new AddressableAssetGroup[chunkCount];
            chunkGroups[0] = group;
            for (int i = 1; i < chunkCount; i++)
            {
                string chunkName = $"{group.Name}-{i + 1}";
                chunkGroups[i] = GetOrCreateGroup(settings, chunkName, out bool created);
                ConfigureBundleSchema(settings, chunkGroups[i]);
                if (created) result.groupsCreated++;
            }

            // Second pass: actually move entries into their assigned chunks.
            // CreateOrMoveEntry is a no-op when the entry's already there.
            for (int i = 0; i < entriesWithSize.Count; i++)
            {
                var target = chunkGroups[assignments[i]];
                if (entriesWithSize[i].entry.parentGroup == target) continue;
                settings.CreateOrMoveEntry(entriesWithSize[i].entry.guid, target, false, false);
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

        // Sort key for the dep-ownership pass. Smaller numbers come first,
        // and earlier-iterated roots win shared-dep ownership. We want the
        // smallest, most reusable unit to claim a shared dep — that way
        // edits to that dep ship the smallest possible patch.
        //
        //   Props (0)       — smallest, most reusable. A wood texture shared
        //                     between an attraction and a prop ends up here.
        //   Levels (1)      — attractions. Owns its unique content but yields
        //                     to props for anything shared.
        //   PlayerRig (2)   — loaded for every park session, owns global
        //                     content like input rig, locomotion. Lowest
        //                     priority because we want least-frequent changes
        //                     to ride along with smaller bundles.
        //
        // Unknown root types (shouldn't happen given IsUserFacingRoot's gate,
        // but defensive) sort last so they don't shadow the canonical types.
        private static int RootPriority(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return 99;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) return 99;
            if (prefab.GetComponent<PropTemplate>() != null)  return 0;
            if (prefab.GetComponent<LevelTemplate>() != null) return 1;
            if (prefab.GetComponent<PlayerRig>() != null)     return 2;
            return 99;
        }

        // Logic vs Content split: which bundle should a non-root dep land in?
        //
        // Logic bundle (returns true): files that carry user MonoBehaviour or
        //   ScriptableObject serialization — their bytes change when user C#
        //   scripts change shape. Isolated so a script edit doesn't drag the
        //   heavy presentation content along with it.
        //
        //     - .prefab   ONLY when the prefab actually contains a
        //                 MonoBehaviour-derived component. A pure-rendering
        //                 prefab — MeshFilter + MeshRenderer + Animator +
        //                 AudioSource, no user scripts — has no MonoScript
        //                 references that change when C# changes, so it's
        //                 effectively presentation content and routes to
        //                 the Content bundle.
        //
        //                 What gets flagged as "logic" by this check:
        //                 - User scripts (any class inheriting `MonoBehaviour`
        //                   that lives in Assembly-CSharp or a content asmdef).
        //                 - SDK runtime components (LuaBehaviour,
        //                   LevelTemplate, PropTemplate, etc.).
        //                 - Unity UI types (Image, Text, Button, Canvas-
        //                   serializing components) — these DO extend
        //                   MonoBehaviour, so any UI-bearing prefab routes
        //                   to Logic. Intentional: UI prefabs carry script
        //                   refs whose serialized shape shifts with their
        //                   source code.
        //                 - Package components that extend MonoBehaviour:
        //                   TextMeshPro (TMP_Text), Cinemachine, NavMesh
        //                   surfaces, PlayableDirector consumers, etc.
        //
        //                 What is NOT flagged (engine-native, returns false):
        //                 - Camera, Light, Animator, AudioSource — extend
        //                   Behaviour, not MonoBehaviour.
        //                 - Transform, MeshFilter, MeshRenderer, Rigidbody,
        //                   Collider — extend Component directly.
        //                 - ParticleSystem, Renderer subclasses.
        //
        //     - .asset    Always counts as logic — ScriptableObjects carry
        //                 MonoScript refs and their serialized data shifts
        //                 when those scripts change. Rare false positives
        //                 (e.g. a TextAsset stored as .asset) are tolerable
        //                 because .asset files are typically tiny.
        //
        // Content bundle (returns false): everything else. Type-agnostic
        //   presentation data whose bytes are independent of C# script shape.
        //     - .mat (materials), .png/.jpg/.tga/.exr (textures), .fbx/.obj/
        //       .mesh (meshes), .wav/.mp3/.ogg (audio), .anim (animations),
        //       .shader, .shadergraph, .cubemap, .terrainlayer, etc.
        //     - .prefab files with no user MonoBehaviours (decorative
        //       sub-prefabs).
        //
        // Note on perf: this loads each prefab via AssetDatabase, which is
        // an in-memory cache so calls are cheap after the first one per asset.
        // Called once per non-root dep per Smart pass — a few thousand calls
        // for a large content set, well under a second total.
        private static bool IsLogicAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string ext = Path.GetExtension(assetPath).ToLowerInvariant();

            if (ext == ".asset") return true;

            if (ext == ".prefab")
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null)
                {
                    // Unloadable / broken — safer to treat as logic so it
                    // doesn't accidentally end up grouped with assets and
                    // confuse a future debugging pass.
                    return true;
                }

                // Any MonoBehaviour-derived component means there's a
                // user MonoScript reference somewhere in the prefab's
                // serialized graph. GetComponentsInChildren<MonoBehaviour>
                // walks the entire hierarchy including inactive children.
                var mbs = prefab.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
                return mbs.Length > 0;
            }

            return false;
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
