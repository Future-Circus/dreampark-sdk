#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    public class ImportPassResult
    {
        public string fbxAssetPath;
        public ModelKind kind;
        public bool ok;
        public string error;

        public int texturesExtracted;
        public int materialsExtracted;
        public int materialsWired;
        public bool smoothnessBaked;
        public Bounds bounds;

        public List<string> warnings = new List<string>();
    }

    /// <summary>
    /// Everything that happens to an FBX between landing in Assets/ and being
    /// ready to drag into a scene.
    ///
    /// ── WHY THIS IS FIVE PASSES AND NOT ONE ────────────────────────────────
    ///
    /// Materials do not exist as assets until after the model has imported,
    /// and textures embedded in the FBX do not exist as files until they are
    /// extracted — which itself changes what the material descriptions point
    /// at, requiring another reimport. The multi-pass shape is not a design
    /// choice, it is the asset pipeline's shape. Doing it as explicit
    /// imperative code (rather than across three AssetPostprocessor callbacks)
    /// means the ordering is readable, failures stop where they happen, and
    /// there are no re-entrancy guards to get wrong — writing .mat and .png
    /// files re-triggers OnPostprocessAllAssets, which is the classic route to
    /// a project that reimports forever.
    /// </summary>
    public static class DreamieImportPass
    {
        /// <summary>
        /// A prop smaller than this or larger than this is almost certainly a
        /// unit-conversion error rather than an artistic choice. Warned about,
        /// never silently corrected — a tool that quietly rescales art is
        /// impossible to debug when it guesses wrong.
        /// </summary>
        private const float MinSaneMetres = 0.01f;
        private const float MaxSaneMetres = 20f;

        public static ImportPassResult Run(
            string fbxAssetPath,
            DreamieProvenanceRecord provenance,
            string contentId,
            ModelKind? kindOverride = null)
        {
            var result = new ImportPassResult { fbxAssetPath = fbxAssetPath };

            var importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
            if (importer == null)
            {
                result.error = "Unity did not import this as a model. The file may be corrupt or not an FBX.";
                return result;
            }

            try
            {
                /* ── B: classify, then apply the profile ──────────────────
                 * Classification reads what actually arrived — a skinned
                 * renderer or an animation clip — rather than the asset's
                 * tags, because a mesh tagged "character" that came in without
                 * a skeleton is a static prop, and importing it as Generic
                 * would build an avatar out of nothing.
                 */
                result.kind = kindOverride ?? ModelImportProfile.Detect(fbxAssetPath);
                ModelImportProfile.For(result.kind).ApplyTo(importer);
                importer.SaveAndReimport();

                // SaveAndReimport destroys and recreates the native importer,
                // so every managed reference to it is potentially dead from
                // here on. Re-fetch after each one rather than carrying the
                // original through the whole method.
                importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
                if (importer == null)
                {
                    result.error = "The model importer went away after applying settings.";
                    return result;
                }

                string assetFolder = Path.GetDirectoryName(fbxAssetPath).Replace('\\', '/');
                string texturesFolder = assetFolder + "/Textures";
                string materialsFolder = assetFolder + "/Materials";

                /* ── C: pull textures and materials out of the FBX ──────── */

                DreamieFolders.EnsureFolder(texturesFolder);
                if (importer.ExtractTextures(texturesFolder))
                {
                    // Targeted, not AssetDatabase.Refresh(). An explicit
                    // Refresh is NOT suppressed by the caller's
                    // DisallowAutoRefresh, so one per asset would give back
                    // most of the batching the phase split was designed to buy
                    // — thirty full project scans for a thirty-model download.
                    AssetDatabase.ImportAsset(texturesFolder, ImportAssetOptions.ImportRecursive);

                    // The material descriptions still point INSIDE the FBX;
                    // reimporting is what rebinds them to the files we just
                    // wrote out.
                    importer.SaveAndReimport();
                    importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
                    if (importer == null)
                    {
                        result.error = "The model importer went away after extracting textures.";
                        return result;
                    }
                }
                result.texturesExtracted = CountAssets(texturesFolder, "t:Texture2D");

                // Worth saying out loud. The downloader fetches only the FBX,
                // so everything downstream depends on the textures being
                // EMBEDDED in it. If a generator ever ships them as sibling
                // files instead, the model arrives untextured and every other
                // stage quietly succeeds at doing nothing.
                if (result.texturesExtracted == 0)
                    result.warnings.Add(
                        "No textures were embedded in this FBX, so the material has no maps. "
                        + "If the asset looks untextured, its images may be published as separate "
                        + "files in the library rather than inside the mesh.");

                DreamieFolders.EnsureFolder(materialsFolder);
                result.materialsExtracted = ExtractMaterials(fbxAssetPath, materialsFolder, result.warnings);

                /* ── D: texture roles, and the smoothness bake ──────────── */

                var set = DreamieTextureSetup.ApplyFolder(texturesFolder, result.warnings);

                string roughness = set.Get(DreamieTextureRole.Roughness);
                if (roughness != null && !set.Has(DreamieTextureRole.Smoothness))
                {
                    string stem = Path.GetFileNameWithoutExtension(fbxAssetPath);
                    string outPath = $"{texturesFolder}/{stem}{RoughnessToSmoothness.SuffixSmoothness}.png";
                    string baked = RoughnessToSmoothness.Bake(roughness, outPath);
                    if (baked != null)
                    {
                        set.paths[DreamieTextureRole.Smoothness] = baked;
                        result.smoothnessBaked = true;
                    }
                }

                /* ── E: onto the DreamPark shader ───────────────────────── */

                result.materialsWired = WireMaterials(materialsFolder, texturesFolder, set, result.warnings);

                /* ── F: remember where this came from ───────────────────── */

                if (provenance != null)
                {
                    provenance.importProfile = result.kind.ToString();
                    DreamieProvenance.Write(fbxAssetPath, provenance);
                }

                CheckScale(fbxAssetPath, result);

                result.ok = true;
                return result;
            }
            catch (Exception e)
            {
                result.error = e.Message;
                Debug.LogError($"[Dreamie] Import pass failed for {fbxAssetPath}: {e}");
                return result;
            }
        }

        /// <summary>
        /// Re-run the settings, texture and material passes on an FBX already
        /// in the project — the right-click menu item and the browser's
        /// "Re-apply" both land here. Provenance is preserved, not rewritten.
        /// </summary>
        public static ImportPassResult Reapply(string fbxAssetPath, ModelKind? kindOverride = null)
        {
            DreamieProvenance.TryRead(fbxAssetPath, out var existing);
            string contentId = ContentFolders.FolderOfAsset(fbxAssetPath);
            return Run(fbxAssetPath, existing, contentId, kindOverride);
        }

        /* ── helpers ───────────────────────────────────────────────────── */

        /// <summary>The part of an extracted material's name after the stem prefix.</summary>
        private static string LeafName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int at = name.LastIndexOf("__", StringComparison.Ordinal);
            return at >= 0 && at + 2 < name.Length ? name.Substring(at + 2) : name;
        }

        private static int CountAssets(string folder, string filter)
            => AssetDatabase.IsValidFolder(folder) ? AssetDatabase.FindAssets(filter, new[] { folder }).Length : 0;

        /// <summary>
        /// AssetDatabase.ExtractAsset per embedded material — the same call
        /// Unity's own "Extract Materials..." button makes. Chosen over
        /// materialLocation = External because it takes the destination path,
        /// and every file belonging to this asset has to carry the unique stem
        /// (ContentProcessor addresses models by bare filename and discards
        /// folders, so two "shrimp" materials in one content package would
        /// otherwise collide).
        ///
        /// Must not run inside AssetDatabase.StartAssetEditing — extraction
        /// needs the database to actually process between calls.
        /// </summary>
        private static int ExtractMaterials(string fbxAssetPath, string materialsFolder, List<string> warnings)
        {
            string stem = Path.GetFileNameWithoutExtension(fbxAssetPath);
            var embedded = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath)
                .OfType<Material>()
                .ToList();

            int n = 0;
            foreach (var mat in embedded)
            {
                string target = $"{materialsFolder}/{stem}__{DreamieFolders.Slug(mat.name)}.mat";
                if (File.Exists(target)) { n++; continue; }   // a re-import; keep the existing GUID

                string err = AssetDatabase.ExtractAsset(mat, target);
                if (string.IsNullOrEmpty(err)) n++;
                else warnings?.Add($"Could not extract material '{mat.name}': {err}");
            }

            if (n > 0)
            {
                // Extraction writes an external-object remap onto the importer;
                // it does not take effect until the settings are flushed and
                // the model re-imported against them.
                AssetDatabase.WriteImportSettingsIfDirty(fbxAssetPath);
                AssetDatabase.ImportAsset(fbxAssetPath, ImportAssetOptions.ForceUpdate);
            }
            return n;
        }

        private static int WireMaterials(string materialsFolder, string texturesFolder, DreamieTextureSet folderSet, List<string> warnings)
        {
            if (!AssetDatabase.IsValidFolder(materialsFolder)) return 0;

            var guids = AssetDatabase.FindAssets("t:Material", new[] { materialsFolder });
            int n = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;

                MaterialConverter.ConvertMaterial(mat);

                // One material: the whole folder's textures are its textures.
                // Several: prefer the ones named after this material, because
                // a folder-wide set is guaranteed wrong for all but one of them.
                // mat.name is the EXTRACTED file's name by now ("{stem}__{slug}"),
                // because Unity syncs a native asset's main-object name to its
                // filename on import. Match on the part after the last "__" or
                // nothing in the Textures folder can ever contain it.
                var set = guids.Length == 1
                    ? folderSet
                    : DreamieMaterialWiring.SetForMaterial(texturesFolder, LeafName(mat.name), folderSet);

                DreamieMaterialWiring.Fixup(mat, set, warnings);
                n++;
            }

            if (n > 0) AssetDatabase.SaveAssets();
            return n;
        }

        /// <summary>
        /// Report, do not repair.
        ///
        /// Dreamie asks its generator for real-world scale (auto_size), so this
        /// should normally pass — which is exactly why a failure is worth
        /// saying out loud rather than absorbing. A 100x or 0.01x unit error is
        /// unmistakable at these thresholds, and a Z-up source arrives lying on
        /// its side, which shows up here as a bounding box of the wrong shape
        /// even when the size is fine.
        /// </summary>
        private static void CheckScale(string fbxAssetPath, ImportPassResult result)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (go == null) return;

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                result.warnings.Add("No renderers — this FBX contains no visible geometry.");
                return;
            }

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            result.bounds = bounds;

            float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (largest < MinSaneMetres || largest > MaxSaneMetres)
            {
                result.warnings.Add(
                    $"This model's largest dimension is {largest:0.###} m, which is almost certainly a unit "
                    + "mismatch rather than intent. Check Scale Factor on the model importer, and "
                    + "Bake Axis Conversion if it is also lying on its side.");
            }
        }
    }
}
#endif
