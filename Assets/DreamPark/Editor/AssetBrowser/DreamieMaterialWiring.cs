#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// Gets an extracted FBX material onto the DreamPark shader and actually
    /// looking right.
    ///
    /// MaterialConverter does the shader swap and the bulk of the property
    /// translation, and it is the right tool for that — it snapshots every
    /// source property before the swap (the swap drops anything unmatched) and
    /// re-applies through its alias tables, preserving the material's GUID so
    /// nothing referencing it breaks.
    ///
    /// What it cannot do is know what a Dreamie asset's textures are FOR. Its
    /// alias tables key off the property names the source shader exposes, and
    /// for a mesh that has been through glTF → FBX those names are lossy and
    /// provider-specific. The files sitting next to the model are named plainly
    /// and are a much better signal, so this pass re-binds from the filenames
    /// and then fixes the scalars that the shader's defaults leave in a state
    /// where a correctly bound map does nothing at all.
    /// </summary>
    public static class DreamieMaterialWiring
    {
        // The DreamPark shader's own property names. `_rougnessMap` is
        // misspelled in the shader; it is reproduced faithfully here because
        // the string has to match.
        private const string PropBaseTex = "_baseTex";
        private const string PropNormalTex = "_nrmTex";
        private const string PropMetallicTex = "_mtlTex";
        private const string PropSmoothnessTex = "_rougnessMap";
        private const string PropOcclusionTex = "_aoTex";
        private const string PropEmissionTex = "_emissionTex";

        private const string PropSmoothness = "_smoothness";
        private const string PropMetallicness = "_metallicness";
        private const string PropNormalStrength = "_nrmStrength";
        private const string PropEmissionStrength = "_emissionStrength";

        /// <summary>
        /// Everything MaterialConverter leaves undone for an AI-generated PBR
        /// set. Every write is guarded by HasProperty, so this is safe to run
        /// against DreamPark-Unlit or anything else that only has some of them.
        /// </summary>
        public static void Fixup(Material mat, DreamieTextureSet set, List<string> warnings = null)
        {
            if (mat == null || set == null) return;

            /* ── 1. re-bind from filenames ─────────────────────────────────
             *
             * Authoritative over whatever the converter inferred. Its fuzzy
             * fallback matches on bidirectional substring over an unordered
             * dictionary, with needles as short as "ao" and "tex" — which is
             * fine for the packs it was written for and is not something to
             * stake a shrimp's normal map on.
             */
            Bind(mat, PropBaseTex, set.Load(DreamieTextureRole.BaseColor));
            Bind(mat, PropNormalTex, set.Load(DreamieTextureRole.Normal));
            Bind(mat, PropMetallicTex, set.Load(DreamieTextureRole.Metallic));
            Bind(mat, PropOcclusionTex, set.Load(DreamieTextureRole.Occlusion));
            Bind(mat, PropEmissionTex, set.Load(DreamieTextureRole.Emissive));

            /* ── 2. smoothness, never raw roughness ────────────────────────
             *
             * `_rougnessMap` feeds SurfaceDescription.Smoothness directly, with
             * no invert in the graph. See RoughnessToSmoothness for the whole
             * story. Binding the source roughness map here is the single most
             * visible way to get a Dreamie asset wrong, so it is done by role
             * and the Roughness role is deliberately never a candidate.
             */
            bool smoothnessHandled = true;
            var smoothness = set.Load(DreamieTextureRole.Smoothness);
            if (smoothness != null)
            {
                Bind(mat, PropSmoothnessTex, smoothness);
            }
            else if (set.Has(DreamieTextureRole.Roughness))
            {
                // The bake failed, and binding the roughness map would render
                // the surface inside out. A flat value is wrong in a boring
                // way; an inverted map is wrong in a way that looks broken.
                //
                // Falls THROUGH rather than returning: an early return here
                // skipped SetDirty (so this very value might never be saved)
                // and skipped the metallic/normal/emission scalars below,
                // which would leave a correctly bound normal map doing
                // nothing — a second, unrelated defect caused by the first.
                if (mat.HasProperty(PropSmoothnessTex)) mat.SetTexture(PropSmoothnessTex, null);
                if (mat.HasProperty(PropSmoothness)) mat.SetFloat(PropSmoothness, 0.5f);
                warnings?.Add($"{mat.name}: could not invert the roughness map, so smoothness is a flat "
                              + "0.5. The roughness texture is still in the Textures folder.");
                smoothnessHandled = false;
            }

            /* ── 3-6. scalars the shader defaults leave at zero ────────────
             *
             * These are MULTIPLIERS on the sampled map, and the graph's
             * defaults are `_metallicness` 0, `_nrmStrength` 0,
             * `_emissionStrength` 0. So a correctly bound metallic map on a
             * default-valued material renders fully dielectric, a correctly
             * bound normal map does nothing whatsoever, and a correctly bound
             * emissive map does not glow. The converter only fills these in
             * when the SOURCE shader exposed a matching scalar, and an FBX
             * material description usually exposes none of them.
             *
             * This is the difference between "the textures are assigned" and
             * "the asset looks right", and it is invisible in the inspector —
             * the map is right there in the slot.
             */
            // HasProperty on the TEXTURE slot before GetTexture, not on the
            // scalar twin. This class promises to be safe against
            // DreamPark-Unlit, which has some of these and not others, and
            // asking an unlit material for `_nrmTex` logs a warning per
            // material per import.
            bool hasSmoothTex = mat.HasProperty(PropSmoothnessTex) && mat.GetTexture(PropSmoothnessTex) != null;
            bool hasMetalTex = mat.HasProperty(PropMetallicTex) && mat.GetTexture(PropMetallicTex) != null;
            bool hasNormalTex = mat.HasProperty(PropNormalTex) && mat.GetTexture(PropNormalTex) != null;
            bool hasEmissionTex = mat.HasProperty(PropEmissionTex) && mat.GetTexture(PropEmissionTex) != null;

            if (smoothnessHandled && mat.HasProperty(PropSmoothness) && hasSmoothTex)
                mat.SetFloat(PropSmoothness, 1f);   // pass the map through unscaled

            if (mat.HasProperty(PropMetallicness) && hasMetalTex)
                mat.SetFloat(PropMetallicness, 1f);

            if (mat.HasProperty(PropNormalStrength)
                && hasNormalTex
                && Mathf.Approximately(mat.GetFloat(PropNormalStrength), 0f))
                mat.SetFloat(PropNormalStrength, 1f);

            if (mat.HasProperty(PropEmissionStrength)
                && hasEmissionTex
                && Mathf.Approximately(mat.GetFloat(PropEmissionStrength), 0f))
                mat.SetFloat(PropEmissionStrength, 1f);

            EditorUtility.SetDirty(mat);
        }

        private static void Bind(Material mat, string property, Texture2D tex)
        {
            if (tex == null || !mat.HasProperty(property)) return;
            mat.SetTexture(property, tex);
        }

        /// <summary>
        /// Textures that belong to ONE material, when an FBX shipped several.
        ///
        /// The folder-wide set is the fallback and is right for the common
        /// case of a single-material prop. When a mesh has two materials it is
        /// wrong for at least one of them, so prefer files whose names carry
        /// the material's own name — which is how the extractors name them.
        /// </summary>
        public static DreamieTextureSet SetForMaterial(string texturesFolder, string materialName, DreamieTextureSet folderSet)
        {
            if (!AssetDatabase.IsValidFolder(texturesFolder) || string.IsNullOrEmpty(materialName))
                return folderSet;

            var scoped = new DreamieTextureSet();
            string needle = materialName.ToLowerInvariant();

            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { texturesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Contains(needle)) continue;
                var role = DreamieTextureSetup.Classify(path);
                if (role != DreamieTextureRole.Unknown && !scoped.paths.ContainsKey(role))
                    scoped.paths[role] = path;
            }

            // Nothing named for this material: a single-material asset whose
            // textures are named after the mesh. The folder set is correct.
            return scoped.paths.Count == 0 ? folderSet : scoped;
        }
    }
}
#endif
