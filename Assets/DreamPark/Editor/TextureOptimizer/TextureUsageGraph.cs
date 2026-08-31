#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

namespace DreamPark.EditorTools.TextureOptimization
{
    /// <summary>
    /// Builds a usage graph for every texture under a content folder. For
    /// each texture we record:
    ///
    ///   1. Its on-disk size, format, dimensions, alpha channel, and
    ///      Unity TextureImporter classification (Default / NormalMap /
    ///      Sprite / Lightmap / Cookie / Cubemap).
    ///   2. Which materials reference it (and via which property — Albedo
    ///      vs Normal vs Mask drives the format decision later).
    ///   3. Which prefabs reference those materials.
    ///   4. The largest world-space renderer.bounds.size component across
    ///      every prefab that uses it. This is the heuristic the planner
    ///      uses to pick 256 / 512 / 1024.
    ///
    /// Why bounds-from-prefab, not bounds-from-scene? Scene placements
    /// scale props at runtime (a "small" candle prefab can be placed
    /// 5m tall on a giant cake). Trusting the authored prefab bounds is
    /// the only stable signal we have at edit time. Creators who
    /// up-scale at scene-time should override the row in the review UI.
    /// </summary>
    public static class TextureUsageGraph
    {
        // Image extensions Unity's TextureImporter will handle.
        // Includes the source-bloat offenders (.tga, .tif, .tiff, .psd,
        // .exr) and the formats we'd ideally end up at (.png, .jpg).
        // We scan them all so we can give creators an accurate "before"
        // picture, then the planner only proposes mutations for the ones
        // that have headroom.
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".tga", ".tif", ".tiff", ".psd", ".exr",
            ".png", ".jpg", ".jpeg",
        };

        public static List<TextureUsage> Build(string rootAssetFolder, Action<float, string> onProgress = null)
        {
            if (string.IsNullOrEmpty(rootAssetFolder))
                throw new ArgumentNullException(nameof(rootAssetFolder));

            // ── Step 1: enumerate every image in the folder ─────────────
            // We use AssetDatabase.FindAssets with a t:Texture filter to
            // catch everything Unity has imported as a texture (regardless
            // of file extension), then filter by extension. This catches
            // .psd files imported as sprites, weird vendor extensions like
            // ".tif" vs ".tiff", etc.
            var guids = AssetDatabase.FindAssets("t:Texture", new[] { rootAssetFolder });
            var usagesByGuid = new Dictionary<string, TextureUsage>(guids.Length);

            for (int i = 0; i < guids.Length; i++)
            {
                onProgress?.Invoke(0.1f * i / guids.Length, "Scanning textures...");
                string guid = guids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!ImageExtensions.Contains(ext)) continue;

                var usage = BuildBaseUsage(path, guid, ext);
                if (usage != null) usagesByGuid[guid] = usage;
            }

            // ── Step 2: index every material in the folder ──────────────
            // For each material, figure out which textures it references and
            // in which slot (so we can tag textures as Normal/Mask/etc).
            // A texture's `usingMaterials` ends up as the set of materials
            // that ping it via any slot.
            var matGuids = AssetDatabase.FindAssets("t:Material", new[] { rootAssetFolder });
            for (int i = 0; i < matGuids.Length; i++)
            {
                onProgress?.Invoke(0.1f + 0.3f * i / matGuids.Length, "Indexing materials...");
                string matPath = AssetDatabase.GUIDToAssetPath(matGuids[i]);
                if (string.IsNullOrEmpty(matPath)) continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null || mat.shader == null) continue;

                LinkMaterialToTextures(mat, matPath, usagesByGuid);
            }

            // ── Step 3: index every prefab and compute bounds per texture ─
            // For each prefab we instantiate it (only as an asset-load, not
            // a scene instance) and iterate its renderers. For each
            // renderer whose materials map back to one of our tracked
            // textures, we update that texture's max-renderer-size.
            //
            // This is the slow step — instantiation + Renderer.bounds for
            // a heavy content folder can take 5-20 seconds. We surface
            // progress so the UI doesn't look frozen.
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { rootAssetFolder });
            for (int i = 0; i < prefabGuids.Length; i++)
            {
                onProgress?.Invoke(0.4f + 0.5f * i / prefabGuids.Length, "Computing prefab bounds...");
                string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                if (string.IsNullOrEmpty(prefabPath)) continue;

                LinkPrefabToTextures(prefabPath, usagesByGuid);
            }

            // ── Step 4: classify orphans + finalize kind ────────────────
            onProgress?.Invoke(0.95f, "Finalizing classifications...");
            foreach (var u in usagesByGuid.Values)
                FinalizeKind(u);

            onProgress?.Invoke(1f, "Done.");
            return usagesByGuid.Values
                .OrderByDescending(u => u.fileBytes)
                .ToList();
        }

        // ─── Step 1 helpers ──────────────────────────────────────────────

        private static TextureUsage BuildBaseUsage(string path, string guid, string ext)
        {
            long bytes = 0;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) bytes = fi.Length;
            }
            catch { /* unreadable; keep 0 */ }

            // We pull width/height/alpha info from the TextureImporter, not
            // by loading the Texture2D — the importer reads metadata cheaply
            // and gives us the authoritative source dimensions even when
            // Unity has downscaled the imported texture for the editor.
            int srcW = 0, srcH = 0;
            bool hasAlpha = false;
            bool isReadable = false;
            bool sRGB = true;
            int currentMax = 2048;
            string importerType = "Default";

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                try { importer.GetSourceTextureWidthAndHeight(out srcW, out srcH); }
                catch
                {
                    // Older Unity / unusual import path — fall back to the
                    // imported Texture's dims rather than failing the row.
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
                    if (tex != null) { srcW = tex.width; srcH = tex.height; }
                }

                hasAlpha = importer.DoesSourceTextureHaveAlpha();
                isReadable = importer.isReadable;
                sRGB = importer.sRGBTexture;
                currentMax = importer.maxTextureSize;
                importerType = importer.textureType.ToString();
            }

            return new TextureUsage
            {
                assetPath = path,
                guid = guid,
                extension = ext,
                fileBytes = bytes,
                sourceWidth = srcW,
                sourceHeight = srcH,
                hasAlphaChannel = hasAlpha,
                isReadable = isReadable,
                sRGBTexture = sRGB,
                currentMaxSize = currentMax,
                currentImporterType = importerType,
                kind = TextureUsageKind.Orphan, // upgraded later
                role = TextureRole.Unknown,
            };
        }

        // ─── Step 2 helpers — material → texture linking ────────────────

        /// <summary>
        /// Common texture-property aliases per role. Same idea as
        /// MaterialConverter.cs, scoped here to *classify* a texture's role
        /// (not to copy values across shaders). Order matters: more specific
        /// names first (so "_DetailNormalMap" is detected as Detail, not
        /// generic Normal).
        /// </summary>
        private static readonly (TextureRole role, string[] aliases)[] RoleAliases = new[]
        {
            (TextureRole.Normal,         new[] { "_NormalMap", "_BumpMap", "_NrmTex", "_nrmTex", "_Normal", "_Normals", "_Norm", "_Bump" }),
            (TextureRole.Detail,         new[] { "_DetailMap", "_DetailNormalMap", "_DetailAlbedoMap" }),
            (TextureRole.MaskOrMetallic, new[] { "_MetallicGlossMap", "_MetallicMap", "_MaskMap", "_Mask", "_MetallicTex", "_mtlTex" }),
            (TextureRole.Roughness,      new[] { "_RoughnessMap", "_SmoothnessMap", "_GlossMap" }),
            (TextureRole.Occlusion,      new[] { "_OcclusionMap", "_AOMap" }),
            (TextureRole.Emission,       new[] { "_EmissionMap", "_EmissiveMap", "_EmissionTex" }),
            (TextureRole.Albedo,         new[] { "_BaseMap", "_BaseColorMap", "_MainTex", "_AlbedoMap", "_DiffuseMap", "_ColorMap", "_baseTex" }),
        };

        private static void LinkMaterialToTextures(Material mat, string matPath, Dictionary<string, TextureUsage> byGuid)
        {
            // Shader-property enumeration. ShaderUtil is editor-only and
            // gives us the full property list including custom names from
            // ShaderGraph (which is what DreamPark-UniversalShader is).
            var shader = mat.shader;
            int propCount = ShaderUtil.GetPropertyCount(shader);
            for (int p = 0; p < propCount; p++)
            {
                if (ShaderUtil.GetPropertyType(shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string propName = ShaderUtil.GetPropertyName(shader, p);

                var tex = mat.GetTexture(propName);
                if (tex == null) continue;

                string texPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(texPath)) continue;
                string texGuid = AssetDatabase.AssetPathToGUID(texPath);
                if (!byGuid.TryGetValue(texGuid, out var usage)) continue;

                if (!usage.usingMaterials.Contains(matPath))
                    usage.usingMaterials.Add(matPath);

                // Promote role if we found a more specific one. Roles are
                // listed roughly most-specific first in RoleAliases so the
                // first hit wins — except Albedo, which we only apply if
                // nothing else matched.
                if (usage.role == TextureRole.Unknown)
                    usage.role = ClassifyRole(propName);
            }
        }

        private static TextureRole ClassifyRole(string propName)
        {
            foreach (var (role, aliases) in RoleAliases)
            {
                foreach (var alias in aliases)
                {
                    if (string.Equals(propName, alias, StringComparison.OrdinalIgnoreCase))
                        return role;
                }
            }
            // Fallback substring match — catches vendor packs that use
            // _MainTexture, _NormalsTex, etc.
            string lower = propName.ToLowerInvariant();
            if (lower.Contains("normal") || lower.Contains("bump")) return TextureRole.Normal;
            if (lower.Contains("mask") || lower.Contains("metal")) return TextureRole.MaskOrMetallic;
            if (lower.Contains("rough") || lower.Contains("smooth") || lower.Contains("gloss")) return TextureRole.Roughness;
            if (lower.Contains("emiss")) return TextureRole.Emission;
            if (lower.Contains("occlu") || lower.Contains("ao")) return TextureRole.Occlusion;
            if (lower.Contains("detail")) return TextureRole.Detail;
            return TextureRole.Albedo;
        }

        // ─── Step 3 helpers — prefab → bounds linking ───────────────────

        private static void LinkPrefabToTextures(string prefabPath, Dictionary<string, TextureUsage> byGuid)
        {
            // We load the prefab contents (does NOT instantiate in a scene
            // and does NOT make it dirty). LoadPrefabContents returns an
            // owned GameObject that we MUST unload via UnloadPrefabContents.
            GameObject root = null;
            try
            {
                root = UnityEditor.PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null) return;

                // Track which usages we've already attributed to *this*
                // prefab so we don't double-count its materials.
                var seenInThisPrefab = new HashSet<TextureUsage>();

                // World-space renderers (Mesh, SkinnedMesh).
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    if (renderer == null) continue;

                    // ParticleSystemRenderer has bounds, but they're
                    // dynamic and not meaningful for sizing decisions.
                    // Tag those textures as Particle and skip the bounds
                    // contribution.
                    bool isParticle = renderer is ParticleSystemRenderer;

                    // Compute renderer bounds *in local prefab space*. We
                    // want the authored size, not anything scaled by an
                    // instance transform. Renderer.bounds is world-space
                    // for the *loaded prefab root*, which is what we want
                    // since LoadPrefabContents instantiates at identity.
                    Bounds b = renderer.bounds;
                    float largestAxis = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));

                    foreach (var mat in renderer.sharedMaterials)
                    {
                        if (mat == null) continue;
                        var matPath = AssetDatabase.GetAssetPath(mat);
                        if (string.IsNullOrEmpty(matPath)) continue;

                        // Find every texture this material references and
                        // update the corresponding usage row.
                        ForEachTextureOfMaterial(mat, texPath =>
                        {
                            string guid = AssetDatabase.AssetPathToGUID(texPath);
                            if (!byGuid.TryGetValue(guid, out var usage)) return;

                            if (seenInThisPrefab.Add(usage))
                            {
                                if (!usage.usingPrefabs.Contains(prefabPath))
                                    usage.usingPrefabs.Add(prefabPath);
                            }

                            if (isParticle)
                            {
                                // Particle textures: tag and don't update
                                // bounds. The planner will treat them as
                                // no-bounds and skip auto-resize.
                                if (usage.kind != TextureUsageKind.WorldRenderer)
                                    usage.kind = TextureUsageKind.Particle;
                            }
                            else
                            {
                                usage.kind = TextureUsageKind.WorldRenderer;
                                if (largestAxis > usage.maxRendererSizeMeters)
                                {
                                    usage.maxRendererSizeMeters = largestAxis;
                                    usage.largestUseExample = prefabPath;
                                }
                            }
                        });
                    }
                }

                // UI: Image / RawImage components on a Canvas. These reference
                // sprites/textures by their own slot, not by Renderer.
                foreach (var img in root.GetComponentsInChildren<Image>(true))
                {
                    if (img == null || img.sprite == null) continue;
                    var tex = img.sprite.texture;
                    TagAsUI(tex, prefabPath, byGuid);
                }
                foreach (var raw in root.GetComponentsInChildren<RawImage>(true))
                {
                    if (raw == null || raw.texture == null) continue;
                    TagAsUI(raw.texture, prefabPath, byGuid);
                }
            }
            catch (Exception e)
            {
                // Loading a broken prefab shouldn't blow up the whole scan.
                Debug.LogWarning($"[TextureOptimizer] Skipped prefab {prefabPath}: {e.Message}");
            }
            finally
            {
                if (root != null)
                    UnityEditor.PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void TagAsUI(Texture tex, string prefabPath, Dictionary<string, TextureUsage> byGuid)
        {
            if (tex == null) return;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!byGuid.TryGetValue(guid, out var usage)) return;

            // UI only "wins" the kind slot if nothing else has claimed it.
            // A texture used both as a UI sprite AND on a world renderer
            // gets WorldRenderer (the more constraining case).
            if (usage.kind != TextureUsageKind.WorldRenderer)
                usage.kind = TextureUsageKind.UI;

            if (!usage.usingPrefabs.Contains(prefabPath))
                usage.usingPrefabs.Add(prefabPath);
        }

        /// <summary>
        /// Invokes <paramref name="onEach"/> for every texture-slot-path
        /// on the material. Centralized so prefab walking and material
        /// indexing agree on the property set.
        /// </summary>
        private static void ForEachTextureOfMaterial(Material mat, Action<string> onEach)
        {
            if (mat == null || mat.shader == null) return;
            int propCount = ShaderUtil.GetPropertyCount(mat.shader);
            for (int p = 0; p < propCount; p++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var tex = mat.GetTexture(ShaderUtil.GetPropertyName(mat.shader, p));
                if (tex == null) continue;
                var path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path)) onEach(path);
            }
        }

        // ─── Step 4 helpers — final kind classification ─────────────────

        private static void FinalizeKind(TextureUsage u)
        {
            // Importer-type-based overrides take priority — these are
            // unambiguous from Unity's classification.
            if (u.currentImporterType == "Lightmap")
            {
                u.kind = TextureUsageKind.Lightmap;
                u.note = "Lightmap output — leave alone.";
                return;
            }
            if (u.currentImporterType == "Cubemap" || u.currentImporterType == "SingleChannel")
            {
                u.kind = TextureUsageKind.Skybox;
                u.note = "Cubemap / skybox source — leave alone.";
                return;
            }
            if (u.currentImporterType == "Sprite" && u.kind == TextureUsageKind.Orphan)
            {
                u.kind = TextureUsageKind.UI;
            }

            // If we have materials but no prefabs, the textures are
            // referenced by an unused material chain. Flag for cleanup
            // rather than auto-resize.
            if (u.kind == TextureUsageKind.Orphan && u.usingMaterials.Count > 0 && u.usingPrefabs.Count == 0)
            {
                u.kind = TextureUsageKind.UnusedMaterial;
                u.note = $"Referenced by {u.usingMaterials.Count} material(s) but no prefab uses those materials. Likely stale.";
            }
            else if (u.kind == TextureUsageKind.Orphan && u.usingMaterials.Count == 0)
            {
                u.note = "No material or prefab references found.";
            }
        }
    }
}
#endif
