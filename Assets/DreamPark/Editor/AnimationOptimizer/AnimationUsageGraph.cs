#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace DreamPark.EditorTools.AnimationOptimization
{
    /// <summary>
    /// Builds a usage graph for every animation clip under a content folder.
    /// Two clip kinds get surfaced:
    ///
    ///   1. <b>FBX sub-clips</b> — AnimationClips embedded in a .fbx / .ma /
    ///      .mb model file. Optimization is a one-call ModelImporter setting
    ///      flip on the host model.
    ///   2. <b>Standalone .anim files</b> — separate YAML assets. For each
    ///      one we try to locate its source FBX by matching name +
    ///      length + framerate + bone-path Jaccard similarity. When found,
    ///      we round-trip through that FBX with GUID preservation. When not
    ///      found, we mark the row as an orphan and leave it alone in v1.
    ///
    /// To avoid double-counting, FBX sub-clips that ARE the source for a
    /// detected standalone .anim get suppressed in the output — the
    /// standalone row is canonical because that's what prefabs and
    /// AnimatorControllers reference.
    /// </summary>
    public static class AnimationUsageGraph
    {
        public static List<AnimationUsage> Build(string rootAssetFolder, Action<float, string> onProgress = null)
        {
            if (string.IsNullOrEmpty(rootAssetFolder))
                throw new ArgumentNullException(nameof(rootAssetFolder));

            // ── Step 1: enumerate every AnimationClip in the folder ─────
            // FindAssets("t:AnimationClip") returns both standalone .anim
            // assets AND FBX sub-clip clips. We split by host file extension
            // so we can route them down the right execution path.
            var standaloneByGuid = new Dictionary<string, AnimationUsage>();   // .anim guid → usage
            var subClipsByFbx = new Dictionary<string, List<AnimationUsage>>(); // fbx path → list of sub-clip usages

            var guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { rootAssetFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                onProgress?.Invoke(0.02f + 0.18f * i / Mathf.Max(1, guids.Length), "Scanning clips...");
                string guid = guids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith(rootAssetFolder, StringComparison.OrdinalIgnoreCase)) continue;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".anim")
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (clip == null) continue;
                    var usage = BuildStandaloneUsage(path, guid, clip);
                    if (usage != null) standaloneByGuid[guid] = usage;
                }
                else if (IsModelExtension(ext))
                {
                    // FindAssets returns one GUID per asset, and the FBX has
                    // its own GUID. But its sub-clips are returned with the
                    // same GUID. We dedupe by listing all clips inside the
                    // model exactly once when we first see its GUID.
                    if (!subClipsByFbx.ContainsKey(path))
                    {
                        var subs = BuildFbxSubClipUsages(path, guid);
                        if (subs != null && subs.Count > 0)
                            subClipsByFbx[path] = subs;
                    }
                }
            }

            // ── Step 2: source-FBX detection for every standalone ───────
            // Scan FBX files in the folder and try to find a matching
            // sub-clip for each standalone .anim. Strong matches get linked
            // back; orphans stay flagged.
            var standaloneList = standaloneByGuid.Values.ToList();
            int matched = 0;
            for (int i = 0; i < standaloneList.Count; i++)
            {
                onProgress?.Invoke(0.2f + 0.4f * i / Mathf.Max(1, standaloneList.Count), "Locating FBX sources...");
                var u = standaloneList[i];
                var match = FindBestFbxSource(u, subClipsByFbx);
                if (match != null)
                {
                    u.fbxSource = match;
                    u.rowKind = AnimationRowKind.StandaloneWithSource;
                    u.currentCompression = ReadImporterCompression(match.fbxPath);
                    matched++;
                }
                else
                {
                    u.rowKind = AnimationRowKind.StandaloneOrphan;
                }
            }

            // ── Step 3: suppress FBX sub-clip rows that ARE the source ──
            // For each (fbx, subClipName) consumed by a standalone row,
            // remove the duplicate sub-clip from the FBX listing — otherwise
            // the user sees two rows for the same animation.
            var consumedPairs = new HashSet<(string fbx, string clipName)>();
            foreach (var u in standaloneList)
            {
                if (u.fbxSource != null)
                    consumedPairs.Add((u.fbxSource.fbxPath, u.fbxSource.subClipName));
            }
            foreach (var kvp in subClipsByFbx.ToList())
            {
                kvp.Value.RemoveAll(sub => consumedPairs.Contains((kvp.Key, sub.clipName)));
            }

            // ── Step 4: usage attribution — controllers + prefabs ───────
            // Same as before, but we resolve a clip's identity by either its
            // .anim guid (standalone) or its (fbxPath, clipName) pair (sub).
            onProgress?.Invoke(0.62f, "Indexing controllers...");
            IndexControllers(rootAssetFolder, standaloneByGuid, subClipsByFbx);

            onProgress?.Invoke(0.78f, "Computing prefab usage...");
            IndexPrefabs(rootAssetFolder, standaloneByGuid, subClipsByFbx);

            // ── Step 5: classify orphans + finalize kind ────────────────
            onProgress?.Invoke(0.97f, "Finalizing classifications...");
            foreach (var u in standaloneList) FinalizeKind(u);
            foreach (var list in subClipsByFbx.Values)
                foreach (var u in list) FinalizeKind(u);

            // ── Step 6: flatten and sort ────────────────────────────────
            var all = new List<AnimationUsage>();
            all.AddRange(standaloneList);
            foreach (var list in subClipsByFbx.Values) all.AddRange(list);

            onProgress?.Invoke(1f, $"Done. ({standaloneList.Count} standalone, {matched} matched, {all.Count - standaloneList.Count} FBX sub-clips)");
            return all.OrderByDescending(u => u.fileBytes).ToList();
        }

        // ─── Step 1 helpers — usage construction ────────────────────────

        private static readonly HashSet<string> ModelExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".ma", ".mb", ".dae", ".obj", ".blend",
        };

        private static bool IsModelExtension(string ext) => ModelExtensions.Contains(ext);

        private static AnimationUsage BuildStandaloneUsage(string path, string guid, AnimationClip clip)
        {
            long bytes = TryFileLength(path);
            ReadClipMetrics(clip, out var floatCount, out var objCount, out var totalKeys, out var constantCurves);

            return new AnimationUsage
            {
                rowKind = AnimationRowKind.StandaloneOrphan, // upgraded in step 2
                assetPath = path,
                clipName = Path.GetFileNameWithoutExtension(path),
                guid = guid,
                fileBytes = bytes,
                readOnly = IsReadOnlyAsset(path),
                clipKind = ClassifyClipKind(clip),
                length = clip.length,
                frameRate = clip.frameRate,
                floatCurveCount = floatCount,
                objectCurveCount = objCount,
                totalKeyframes = totalKeys,
                constantCurveCount = constantCurves,
                currentCompression = ModelImporterAnimationCompression.Off, // not meaningful for standalone
                kind = AnimationUsageKind.Orphan, // upgraded later
            };
        }

        private static List<AnimationUsage> BuildFbxSubClipUsages(string modelPath, string modelGuid)
        {
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            // Read importer compression once per model — every sub-clip in
            // the same FBX inherits it, so we cache rather than re-reading
            // for every clip.
            var currentCompression = importer != null ? importer.animationCompression : ModelImporterAnimationCompression.Off;
            long modelBytes = TryFileLength(modelPath);
            bool readOnly = IsReadOnlyAsset(modelPath);

            var assets = AssetDatabase.LoadAllAssetsAtPath(modelPath);
            var result = new List<AnimationUsage>();
            foreach (var obj in assets)
            {
                if (!(obj is AnimationClip clip)) continue;
                if (clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;

                ReadClipMetrics(clip, out var floatCount, out var objCount, out var totalKeys, out var constantCurves);

                result.Add(new AnimationUsage
                {
                    rowKind = AnimationRowKind.FbxSubClip,
                    assetPath = modelPath,
                    clipName = clip.name,
                    // Sub-clips share the model's GUID — useful for matching
                    // controllers that reference clip-by-asset, less useful
                    // for unique row identity (we use (path, name) instead).
                    guid = modelGuid,
                    fileBytes = modelBytes,
                    readOnly = readOnly,
                    clipKind = ClassifyClipKind(clip),
                    length = clip.length,
                    frameRate = clip.frameRate,
                    floatCurveCount = floatCount,
                    objectCurveCount = objCount,
                    totalKeyframes = totalKeys,
                    constantCurveCount = constantCurves,
                    currentCompression = currentCompression,
                    kind = AnimationUsageKind.Orphan,
                });
            }
            return result;
        }

        private static void ReadClipMetrics(AnimationClip clip, out int floatCount, out int objCount, out int totalKeys, out int constantCurves)
        {
            floatCount = 0;
            objCount = 0;
            totalKeys = 0;
            constantCurves = 0;
            try
            {
                var floatBindings = AnimationUtility.GetCurveBindings(clip);
                floatCount = floatBindings.Length;
                foreach (var b in floatBindings)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null) continue;
                    totalKeys += curve.length;
                    if (IsConstantCurve(curve)) constantCurves++;
                }
                var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                objCount = objBindings.Length;
                foreach (var b in objBindings)
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys != null) totalKeys += keys.Length;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AnimationOptimizer] Couldn't read curves for {clip.name}: {e.Message}");
            }
        }

        private static bool IsConstantCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length <= 1) return true;
            float first = curve.keys[0].value;
            for (int i = 1; i < curve.length; i++)
                if (Mathf.Abs(curve.keys[i].value - first) > 1e-5f) return false;
            return true;
        }

        private static AnimationClipKind ClassifyClipKind(AnimationClip clip)
        {
            if (clip.legacy) return AnimationClipKind.Legacy;
            if (clip.isHumanMotion) return AnimationClipKind.Humanoid;
            return AnimationClipKind.Generic;
        }

        private static long TryFileLength(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        private static bool IsReadOnlyAsset(string assetPath)
        {
            // Assets under Packages/<immutable-package>/ can't be mutated by
            // a normal optimizer run. We flag them so the UI greys them out.
            return assetPath.StartsWith("Packages/", StringComparison.Ordinal);
        }

        private static ModelImporterAnimationCompression ReadImporterCompression(string fbxPath)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            return importer != null ? importer.animationCompression : ModelImporterAnimationCompression.Off;
        }

        // ─── Step 2 helpers — FBX source detection ──────────────────────

        /// <summary>
        /// Score a standalone .anim against every (fbx, sub-clip) pair we
        /// know about. Strong matches return a populated
        /// <see cref="FbxSourceMatch"/>; weak/no matches return null.
        ///
        /// Scoring weights:
        ///   - Identical sub-clip name (+100)
        ///   - Length matches within 0.01s (+50)
        ///   - Same framerate (+10)
        ///   - Bone-path Jaccard similarity × 1000 (the dominant signal —
        ///     if two clips animate the same set of bone paths, they came
        ///     from the same rig)
        ///
        /// The match must clear a Jaccard floor (≥ 0.5) to be claimed. A
        /// .anim whose bindings barely overlap with the FBX is probably
        /// a different rig that happens to be nearby in the folder tree.
        /// </summary>
        private static FbxSourceMatch FindBestFbxSource(
            AnimationUsage standalone,
            Dictionary<string, List<AnimationUsage>> subClipsByFbx)
        {
            var standaloneClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(standalone.assetPath);
            if (standaloneClip == null) return null;

            var standaloneBindings = AnimationUtility.GetCurveBindings(standaloneClip);
            // Compute Jaccard on path SUFFIXES (last two segments). Asset
            // packs vary on whether an extracted clip's bone paths include
            // a rig-root prefix — e.g. "Spider 1/Armature/Hips" vs just
            // "Armature/Hips". Comparing full paths would Jaccard those
            // to zero and mass-orphan every standalone in the pack. Suffix
            // comparison is robust to prefix differences while still
            // discriminating between unrelated rigs (which have entirely
            // different leaf bone names).
            var standalonePathSet = BuildSuffixSet(standaloneBindings);
            string standaloneName = standalone.clipName;

            // Cache loaded sub-clip lists per FBX path — without this the
            // nested loop calls LoadAllAssetsAtPath N×M times, which is
            // slow on heavy packs (the spider FBX alone is 14 MB).
            var subClipCache = new Dictionary<string, AnimationClip[]>();

            FbxSourceMatch best = null;
            foreach (var kvp in subClipsByFbx)
            {
                string fbxPath = kvp.Key;
                foreach (var sub in kvp.Value)
                {
                    var subClip = LoadSubClipCached(fbxPath, sub.clipName, subClipCache);
                    if (subClip == null) continue;

                    int score = 0;
                    if (string.Equals(sub.clipName, standaloneName, StringComparison.OrdinalIgnoreCase))
                        score += 100;
                    if (Mathf.Abs(sub.length - standalone.length) < 0.01f)
                        score += 50;
                    if (Mathf.Approximately(sub.frameRate, standalone.frameRate))
                        score += 10;

                    var subBindings = AnimationUtility.GetCurveBindings(subClip);
                    var subPathSet = BuildSuffixSet(subBindings);
                    float jaccard = ComputeJaccard(standalonePathSet, subPathSet);
                    score += Mathf.RoundToInt(1000f * jaccard);

                    if (best == null || score > best.matchScore)
                    {
                        bool diverged =
                            jaccard >= 0.7f &&
                            (Mathf.Abs(standaloneBindings.Length - subBindings.Length) > 4 ||
                             Mathf.Abs(sub.length - standalone.length) > 0.05f);

                        best = new FbxSourceMatch
                        {
                            fbxPath = fbxPath,
                            subClipName = sub.clipName,
                            matchScore = score,
                            pathJaccard = jaccard,
                            divergedFromSource = diverged,
                        };
                    }
                }
            }

            // Confidence floor — require Jaccard ≥ 0.5 to claim a source.
            // Name match alone (which only adds 100 score points) is NOT
            // sufficient because two unrelated rigs could share a clip
            // name like "Idle".
            if (best != null && best.pathJaccard >= 0.5f)
                return best;
            return null;
        }

        /// <summary>
        /// Build a set of <em>path suffixes</em> (last two segments) from a
        /// curve binding array. See the call site for why this matters —
        /// it makes Jaccard similarity robust to rig-root prefix
        /// differences between an FBX and its extracted standalones.
        /// </summary>
        private static HashSet<string> BuildSuffixSet(EditorCurveBinding[] bindings)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in bindings) set.Add(LastTwoSegments(b.path));
            return set;
        }

        private static string LastTwoSegments(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? "";
            int last = path.LastIndexOf('/');
            if (last <= 0) return path;
            int prev = path.LastIndexOf('/', last - 1);
            return prev < 0 ? path : path.Substring(prev + 1);
        }

        private static AnimationClip LoadSubClipCached(
            string fbxPath, string clipName, Dictionary<string, AnimationClip[]> cache)
        {
            if (!cache.TryGetValue(fbxPath, out var clips))
            {
                var list = new List<AnimationClip>();
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                    if (obj is AnimationClip c && !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                        list.Add(c);
                clips = list.ToArray();
                cache[fbxPath] = clips;
            }
            foreach (var clip in clips)
                if (clip.name == clipName) return clip;
            return null;
        }

        private static float ComputeJaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 1f;
            int intersect = 0;
            foreach (var x in a) if (b.Contains(x)) intersect++;
            int union = a.Count + b.Count - intersect;
            return union > 0 ? (float)intersect / union : 0f;
        }

        // ─── Step 4 helpers — controller / prefab attribution ───────────

        private static void IndexControllers(
            string rootAssetFolder,
            Dictionary<string, AnimationUsage> standaloneByGuid,
            Dictionary<string, List<AnimationUsage>> subClipsByFbx)
        {
            var controllerGuids = AssetDatabase.FindAssets("t:AnimatorController", new[] { rootAssetFolder });
            foreach (var g in controllerGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                    AttributeClipToController(clip, path, standaloneByGuid, subClipsByFbx);
            }
            var overrideGuids = AssetDatabase.FindAssets("t:AnimatorOverrideController", new[] { rootAssetFolder });
            foreach (var g in overrideGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var ov = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(path);
                if (ov == null) continue;
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                ov.GetOverrides(pairs);
                foreach (var p in pairs)
                {
                    AttributeClipToController(p.Key, path, standaloneByGuid, subClipsByFbx);
                    AttributeClipToController(p.Value, path, standaloneByGuid, subClipsByFbx);
                }
            }
        }

        private static void AttributeClipToController(
            AnimationClip clip,
            string controllerPath,
            Dictionary<string, AnimationUsage> standaloneByGuid,
            Dictionary<string, List<AnimationUsage>> subClipsByFbx)
        {
            if (clip == null) return;
            var clipAssetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(clipAssetPath)) return;

            // Sub-clip case: clipAssetPath is the FBX path; match by clip name.
            if (subClipsByFbx.TryGetValue(clipAssetPath, out var subs))
            {
                foreach (var u in subs)
                {
                    if (u.clipName == clip.name && !u.usingControllers.Contains(controllerPath))
                    {
                        u.usingControllers.Add(controllerPath);
                        if (string.IsNullOrEmpty(u.largestUseExample))
                            u.largestUseExample = controllerPath;
                    }
                }
                return;
            }

            // Standalone case: match by GUID.
            var guid = AssetDatabase.AssetPathToGUID(clipAssetPath);
            if (standaloneByGuid.TryGetValue(guid, out var standalone))
            {
                if (!standalone.usingControllers.Contains(controllerPath))
                    standalone.usingControllers.Add(controllerPath);
                if (string.IsNullOrEmpty(standalone.largestUseExample))
                    standalone.largestUseExample = controllerPath;
            }
        }

        private static void IndexPrefabs(
            string rootAssetFolder,
            Dictionary<string, AnimationUsage> standaloneByGuid,
            Dictionary<string, List<AnimationUsage>> subClipsByFbx)
        {
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { rootAssetFolder });
            foreach (var g in prefabGuids)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(g);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;
                try
                {
                    foreach (var animator in prefab.GetComponentsInChildren<Animator>(true))
                    {
                        if (animator == null) continue;
                        var rt = animator.runtimeAnimatorController;
                        if (rt == null) continue;
                        foreach (var clip in rt.animationClips)
                            AttributePrefabToClip(clip, prefabPath, standaloneByGuid, subClipsByFbx);
                    }
                    foreach (var legacy in prefab.GetComponentsInChildren<Animation>(true))
                    {
                        if (legacy == null) continue;
                        foreach (AnimationState state in legacy)
                        {
                            if (state?.clip == null) continue;
                            AttributePrefabToClip(state.clip, prefabPath, standaloneByGuid, subClipsByFbx);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AnimationOptimizer] Skipped prefab {prefabPath}: {e.Message}");
                }
            }
        }

        private static void AttributePrefabToClip(
            AnimationClip clip,
            string prefabPath,
            Dictionary<string, AnimationUsage> standaloneByGuid,
            Dictionary<string, List<AnimationUsage>> subClipsByFbx)
        {
            if (clip == null) return;
            var clipAssetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(clipAssetPath)) return;

            if (subClipsByFbx.TryGetValue(clipAssetPath, out var subs))
            {
                foreach (var u in subs)
                {
                    if (u.clipName == clip.name && !u.usingPrefabs.Contains(prefabPath))
                        u.usingPrefabs.Add(prefabPath);
                }
                return;
            }

            var guid = AssetDatabase.AssetPathToGUID(clipAssetPath);
            if (standaloneByGuid.TryGetValue(guid, out var standalone)
                && !standalone.usingPrefabs.Contains(prefabPath))
            {
                standalone.usingPrefabs.Add(prefabPath);
            }
        }

        // ─── Step 5 helpers — final kind classification ─────────────────

        private static void FinalizeKind(AnimationUsage u)
        {
            if (u.usingPrefabs.Count > 0)
            {
                u.kind = AnimationUsageKind.Active;
            }
            else if (u.usingControllers.Count > 0)
            {
                u.kind = AnimationUsageKind.UnusedController;
                u.note = $"Referenced by {u.usingControllers.Count} controller(s) but no prefab uses those controllers.";
            }
            else
            {
                u.kind = AnimationUsageKind.Orphan;
                if (u.rowKind != AnimationRowKind.FbxSubClip)
                    u.note = "No controller or prefab references found.";
            }
        }
    }
}
#endif
