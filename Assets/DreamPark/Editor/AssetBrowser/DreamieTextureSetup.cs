#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    public enum DreamieTextureRole
    {
        Unknown,
        BaseColor,
        Normal,
        Metallic,
        Roughness,
        Smoothness,   // generated from Roughness; never shipped by the source
        Occlusion,
        Emissive,
        /// <summary>
        /// A channel-packed mask (glTF's occlusion/roughness/metallic, or an
        /// ORM/ARM variant). Recognised so it is never mistaken for a
        /// single-channel map — deliberately not bound to anything.
        /// </summary>
        Packed,
    }

    /// <summary>Which file plays which role, for one imported asset.</summary>
    public class DreamieTextureSet
    {
        public readonly Dictionary<DreamieTextureRole, string> paths =
            new Dictionary<DreamieTextureRole, string>();

        public string Get(DreamieTextureRole role)
            => paths.TryGetValue(role, out var p) ? p : null;

        public Texture2D Load(DreamieTextureRole role)
        {
            string p = Get(role);
            return string.IsNullOrEmpty(p) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        }

        public bool Has(DreamieTextureRole role) => !string.IsNullOrEmpty(Get(role));
    }

    /// <summary>
    /// Import settings for the textures that came out of an FBX.
    ///
    /// ── THE TWO THINGS THIS EXISTS TO FIX ──────────────────────────────────
    ///
    /// 1. SIZE. Dreamie asks its generator for 4096px textures
    ///    (dreamie/providers/tripo.js: texture_size 4096, and the comment there
    ///    explains that 2048 was quietly halving an image they had just paid to
    ///    upscale). That is the right call for a library master and completely
    ///    wrong for a Quest build: one uncapped 4K albedo is several megabytes
    ///    of over-the-air download for a prop the player sees at arm's length.
    ///    A shrimp does not need more texture than a shrimp.
    ///
    /// 2. COLOUR SPACE. This project renders in Linear (ProjectSettings
    ///    m_ActiveColorSpace: 1), so an sRGB flag set wrong is a visible error,
    ///    not a nicety. Base colour and emissive are colour and must be sRGB;
    ///    metallic, roughness and occlusion are DATA and must be linear, or
    ///    every value in them is silently gamma-shifted. The existing Texture
    ///    Optimizer deliberately never writes sRGBTexture or textureType — it
    ///    only reads them to classify — so nothing in the SDK was setting this
    ///    and an extracted map came in with whatever Unity guessed.
    ///
    /// Sizes are per-role rather than one number, because the roles genuinely
    /// differ: albedo is what the eye reads, and a roughness map is a slowly
    /// varying mask that nobody has ever noticed at half resolution.
    /// </summary>
    public static class DreamieTextureSetup
    {
        public const int MaxSizeColor = 1024;
        public const int MaxSizeData = 512;

        /* ── classification ────────────────────────────────────────────────
         *
         * By FILENAME first. The generators name their maps plainly
         * (basecolor / normal / roughness / metallic), and that is far more
         * reliable than the material-description property name an FBX happens
         * to expose — a glTF-to-FBX conversion flattens the PBR model down to
         * whatever the FBX material spec can carry, so the property names on
         * the other side are lossy and provider-specific in a way the
         * filenames are not.
         * ─────────────────────────────────────────────────────────────── */

        private static readonly (DreamieTextureRole role, string[] needles)[] Patterns =
        {
            (DreamieTextureRole.Normal,    new[] { "normal", "_nrm", "_norm", "bump" }),
            (DreamieTextureRole.Emissive,  new[] { "emissive", "emission", "glow" }),
            (DreamieTextureRole.Occlusion, new[] { "occlusion", "ambientocclusion", "_ao", "ao_" }),
            (DreamieTextureRole.Roughness, new[] { "roughness", "_rough" }),
            (DreamieTextureRole.Smoothness,new[] { "smoothness", "_smooth", "gloss" }),
            (DreamieTextureRole.Metallic,  new[] { "metallic", "metalness", "_metal" }),
            (DreamieTextureRole.BaseColor, new[] { "basecolor", "base_color", "albedo", "diffuse", "_color", "_col", "maintex" }),
        };

        /// <summary>
        /// Channel-packed masks. glTF's own PBR packs occlusion, roughness and
        /// metallic into one RGB image, and these meshes come FROM glTF — so
        /// this is the likely case, not an exotic one.
        ///
        /// Detected in order to be refused. Every name here also contains a
        /// single-channel needle ("metallicRoughness" contains "roughness"), so
        /// without this check a packed map is classified Roughness, inverted
        /// whole-image by the smoothness bake, and bound as smoothness — while
        /// metallic and occlusion get nothing at all. Splitting the channels
        /// properly is worth doing; guessing is not.
        /// </summary>
        private static readonly string[] PackedNeedles =
        {
            "metallicroughness", "metallic_roughness", "roughnessmetallic",
            "occlusionroughnessmetallic", "_orm", "_arm", "_rma", "_mra",
        };

        public static DreamieTextureRole Classify(string texAssetPath)
        {
            string n = Path.GetFileNameWithoutExtension(texAssetPath ?? "").ToLowerInvariant();
            if (n.Length == 0) return DreamieTextureRole.Unknown;

            // Our own bake, named from the asset's stem. Checked first and by
            // suffix, because the stem carries the ASSET'S name — and an asset
            // called "Glow Worm" would otherwise produce
            // "glow-worm__ab12cd34__smoothness.png" and be re-classified as
            // Emissive on the next re-import.
            if (n.EndsWith(RoughnessToSmoothness.SuffixSmoothness.ToLowerInvariant()))
                return DreamieTextureRole.Smoothness;

            foreach (var packed in PackedNeedles)
                if (n.Contains(packed)) return DreamieTextureRole.Packed;

            /* ── LAST match wins, not first ────────────────────────────────
             *
             * Texture files are conventionally named "{subject}_{role}", so the
             * role is at the END and the subject is at the start — and the
             * subject is the asset's own name, which we do not control. First-
             * match-wins reads "glow_worm_basecolor" as Emissive because "glow"
             * appears before "basecolor". Taking the LAST match resolves that
             * the way a human does, while still reading "shrimp_glow" as
             * Emissive.
             */
            var best = DreamieTextureRole.Unknown;
            int bestAt = -1;
            foreach (var (role, needles) in Patterns)
                foreach (var needle in needles)
                {
                    int at = n.LastIndexOf(needle, StringComparison.Ordinal);
                    if (at > bestAt) { bestAt = at; best = role; }
                }

            return best;
        }

        /// <summary>
        /// Configure one texture for its role. Returns true when something
        /// actually changed, so the caller can skip a needless reimport.
        /// </summary>
        public static bool ApplyRoleSettings(string texAssetPath, DreamieTextureRole role)
        {
            var importer = AssetImporter.GetAtPath(texAssetPath) as TextureImporter;
            if (importer == null) return false;

            bool isNormal = role == DreamieTextureRole.Normal;
            // Colour is anything the eye reads directly. Everything else is a
            // number that happens to be stored in an image.
            bool isColour = role == DreamieTextureRole.BaseColor
                            || role == DreamieTextureRole.Emissive
                            || role == DreamieTextureRole.Unknown;
            // A packed mask is three data channels in a trench coat. Linear,
            // like every other data map.
            if (role == DreamieTextureRole.Packed) isColour = false;
            int maxSize = isColour ? MaxSizeColor : MaxSizeData;

            // Must list EVERY property written below. A field missing from
            // this tuple is a setting that silently never gets saved, because
            // the comparison at the end decides whether to reimport at all.
            var before = (importer.textureType, importer.sRGBTexture, importer.maxTextureSize,
                          importer.mipmapEnabled, importer.isReadable, importer.textureCompression,
                          importer.crunchedCompression, importer.compressionQuality,
                          importer.streamingMipmaps);

            importer.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            // A NormalMap-typed importer manages its own encoding; setting
            // sRGB on it is meaningless and Unity ignores it.
            if (!isNormal) importer.sRGBTexture = isColour;

            importer.maxTextureSize = maxSize;
            // Mips are not optional for something viewed at a distance in VR:
            // without them a textured prop shimmers as the head moves, which
            // is far more noticeable than the resolution ever was.
            importer.mipmapEnabled = true;
            importer.isReadable = false;
            importer.textureCompression = TextureImporterCompression.Compressed;
            // Crunch is a lossy DXT/ETC pre-pass. It wrecks normal maps
            // specifically, which is the same carve-out the SDK's Texture
            // Optimizer already makes.
            importer.crunchedCompression = !isNormal;
            if (importer.crunchedCompression) importer.compressionQuality = 50;
            // Texture streaming is off project-wide (QualitySettings
            // streamingMipmapsActive: 0), so flagging these for it would build
            // streaming data nothing reads.
            importer.streamingMipmaps = false;

            var settings = importer.GetDefaultPlatformTextureSettings();
            settings.maxTextureSize = maxSize;
            settings.crunchedCompression = importer.crunchedCompression;
            settings.textureCompression = TextureImporterCompression.Compressed;
            importer.SetPlatformTextureSettings(settings);

            var after = (importer.textureType, importer.sRGBTexture, importer.maxTextureSize,
                         importer.mipmapEnabled, importer.isReadable, importer.textureCompression,
                         importer.crunchedCompression, importer.compressionQuality,
                         importer.streamingMipmaps);

            if (before.Equals(after)) return false;

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return true;
        }

        /// <summary>
        /// Configure every texture in a folder and report what is there.
        /// </summary>
        public static DreamieTextureSet ApplyFolder(string texturesFolder, List<string> warnings = null)
        {
            var set = new DreamieTextureSet();
            if (!AssetDatabase.IsValidFolder(texturesFolder)) return set;

            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { texturesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var role = Classify(path);
                ApplyRoleSettings(path, role);

                if (role == DreamieTextureRole.Packed)
                {
                    warnings?.Add($"{Path.GetFileName(path)} looks like a channel-packed mask "
                                  + "(occlusion/roughness/metallic in one image). DreamPark's shader wants "
                                  + "them separate, so it has been left unbound — split the channels if this "
                                  + "asset needs metal or roughness detail.");
                    continue;
                }

                if (role == DreamieTextureRole.Unknown)
                {
                    // Not an error. A generator can ship a map we have no name
                    // for, and it is still bound by whatever the material
                    // description said. Worth surfacing once so a creator can
                    // look, never worth failing an import over.
                    warnings?.Add($"Unrecognised texture role for {Path.GetFileName(path)} — "
                                  + "imported as colour. Check its assignment if the material looks wrong.");
                    continue;
                }

                // First one wins per role. A second base colour in one asset
                // folder means the FBX had two materials, which is fine — the
                // per-material wiring resolves that; this set is the fallback.
                if (!set.paths.ContainsKey(role)) set.paths[role] = path;
            }

            return set;
        }
    }
}
#endif
