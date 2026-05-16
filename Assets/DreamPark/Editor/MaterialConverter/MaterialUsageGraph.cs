#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.MaterialConversion
{
    /// <summary>
    /// Builds a usage graph for every material under a content folder.
    ///
    /// "In use" = referenced (directly or transitively) by any prefab or scene
    /// that lives under the same content folder. Materials that aren't reached
    /// from any root are flagged as Orphan so the user can delete or ignore.
    ///
    /// Two material classes get special treatment:
    ///
    ///   - Embedded materials (a sub-asset of an FBX / glTF / .blend import)
    ///     are marked isEmbeddedInModel = true. They're read-only until the
    ///     user uses "Extract Materials" in the model importer, so the
    ///     planner won't propose mutating them in place.
    ///
    ///   - Materials whose parent folder lives inside /ThirdPartyLocal/ are
    ///     excluded entirely — they're staging area, not shipped content.
    ///     Mirrors the exclusion ContentProcessor and SmartBundleGrouper
    ///     already apply.
    /// </summary>
    public static class MaterialUsageGraph
    {
        public static List<MaterialUsage> Build(string rootAssetFolder, Action<float, string> onProgress = null)
        {
            if (string.IsNullOrEmpty(rootAssetFolder))
                throw new ArgumentNullException(nameof(rootAssetFolder));
            if (!AssetDatabase.IsValidFolder(rootAssetFolder))
                return new List<MaterialUsage>();

            // ── Step 1: enumerate every material under the root folder ──
            //
            // FindAssets("t:Material") catches both standalone .mat assets
            // AND materials that are sub-assets of an FBX / glTF. For the
            // sub-assets, AssetDatabase reports the parent asset path
            // (e.g. ".../Model.fbx"), so we have to disambiguate.
            onProgress?.Invoke(0f, "Finding materials...");
            var guids = AssetDatabase.FindAssets("t:Material", new[] { rootAssetFolder });
            var usages = new List<MaterialUsage>(guids.Length);
            var byGuid = new Dictionary<string, MaterialUsage>(guids.Length, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < guids.Length; i++)
            {
                if ((i & 63) == 0)
                    onProgress?.Invoke(0.1f * i / Mathf.Max(1, guids.Length), "Loading materials...");
                string guid = guids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                bool embedded = !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;

                var usage = new MaterialUsage
                {
                    assetPath = path,
                    guid = guid,
                    shaderName = mat.shader != null ? mat.shader.name : "(none)",
                    isEmbeddedInModel = embedded,
                };
                usages.Add(usage);
                // For embedded materials many sub-assets share the same parent
                // asset path; only the first one wins the guid lookup. That's
                // fine — the usage graph keys by *path*, not by sub-asset
                // instance ID, because we can't address sub-assets by GUID alone.
                if (!byGuid.ContainsKey(guid)) byGuid[guid] = usage;
            }

            // Group usages by asset path so embedded sub-materials sharing an
            // FBX collapse into one entry per parent path. We track them as a
            // dictionary keyed by path for the reverse-walk in step 2.
            var byPath = new Dictionary<string, MaterialUsage>(usages.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var u in usages)
            {
                if (!byPath.ContainsKey(u.assetPath)) byPath[u.assetPath] = u;
            }

            // ── Step 2: walk every prefab + scene under the root and record
            //    which materials each one drags in via its dep closure.
            //    AssetDatabase.GetDependencies(path, recursive: true) is the
            //    most reliable signal — it follows Renderer.sharedMaterials,
            //    UI Image.material, ParticleSystemRenderer materials, etc.
            //    without us having to know every component type.
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { rootAssetFolder });
            var sceneGuids  = AssetDatabase.FindAssets("t:Scene",  new[] { rootAssetFolder });

            int totalRoots = prefabGuids.Length + sceneGuids.Length;
            int processed = 0;

            foreach (var pg in prefabGuids)
            {
                processed++;
                if ((processed & 31) == 0)
                    onProgress?.Invoke(0.2f + 0.7f * processed / Mathf.Max(1, totalRoots), "Walking prefab dependencies...");
                string ppath = AssetDatabase.GUIDToAssetPath(pg);
                if (string.IsNullOrEmpty(ppath)) continue;
                if (ppath.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                RecordMaterialUses(ppath, byPath, isScene: false);
            }
            foreach (var sg in sceneGuids)
            {
                processed++;
                if ((processed & 31) == 0)
                    onProgress?.Invoke(0.2f + 0.7f * processed / Mathf.Max(1, totalRoots), "Walking scene dependencies...");
                string spath = AssetDatabase.GUIDToAssetPath(sg);
                if (string.IsNullOrEmpty(spath)) continue;
                if (spath.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                RecordMaterialUses(spath, byPath, isScene: true);
            }

            onProgress?.Invoke(1f, "Done");
            return usages;
        }

        private static void RecordMaterialUses(string rootPath, Dictionary<string, MaterialUsage> byPath, bool isScene)
        {
            // GetDependencies returns every asset path the root walks through,
            // including transitive deps. We don't need a hand-rolled BFS.
            var deps = AssetDatabase.GetDependencies(rootPath, recursive: true);
            foreach (var d in deps)
            {
                if (!byPath.TryGetValue(d, out var usage)) continue;
                if (isScene) usage.usingScenes.Add(rootPath);
                else         usage.usingPrefabs.Add(rootPath);
            }

            // ── ParticleSystemRenderer tag pass ──────────────────────────
            // Load the prefab once and walk its ParticleSystemRenderer
            // components. Any material attached to one gets tagged as
            // "used by a particle renderer" — independent of the source
            // shader's naming. This is what catches vendor shaders like
            // Hovl Studio's HS_Explosion that don't have "particle" in
            // their name but are unambiguously authored for particles.
            //
            // Scenes are skipped to save the LoadAssetAtPath cost; particle
            // systems in scenes typically use materials that are also
            // referenced from prefabs, so the prefab pass catches them.
            // If a project ever has scene-only particle materials, we
            // can extend this — for now prefab-only is the right tradeoff.
            if (isScene) return;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(rootPath);
            if (prefab == null) return;
            var renderers = prefab.GetComponentsInChildren<ParticleSystemRenderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                var mats = r.sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                {
                    var m = mats[j];
                    if (m == null) continue;
                    string mp = AssetDatabase.GetAssetPath(m);
                    if (string.IsNullOrEmpty(mp)) continue;
                    if (byPath.TryGetValue(mp, out var usage))
                        usage.isUsedByParticleRenderer = true;
                }
                // Trail material — ParticleSystemRenderer.trailMaterial is
                // a separate slot for the Trails module. Check it too.
                var trail = r.trailMaterial;
                if (trail != null)
                {
                    string tp = AssetDatabase.GetAssetPath(trail);
                    if (!string.IsNullOrEmpty(tp) && byPath.TryGetValue(tp, out var trailUsage))
                        trailUsage.isUsedByParticleRenderer = true;
                }
            }
        }
    }
}
#endif
