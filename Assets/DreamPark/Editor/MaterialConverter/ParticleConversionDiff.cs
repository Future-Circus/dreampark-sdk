#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using DreamPark.EditorTools;  // for MaterialConverter

namespace DreamPark.EditorTools.MaterialConversion
{
    // ─────────────────────────────────────────────────────────────────────
    // Particle-conversion pre-flight analyzer.
    //
    // The risk with particle conversion isn't that the converter fails
    // outright — it's that the converted material looks "right" in the
    // Inspector but silently loses visual data the vendor shader was
    // doing. Examples we've seen in the wild:
    //
    //   - Custom 3-channel mask textures (RGBChannelMask, ChannelGradient)
    //     drive separate effects on R/G/B that DreamPark/Particles ignores.
    //   - Per-axis UV scrolling (_ScrollX/_ScrollY) on the base map — the
    //     converter handles distortion-map scroll but not base scroll.
    //   - Rim/Fresnel parameters (_RimColor, _RimPower) on stylized shaders
    //     that fake lighting on unlit particles. No target on DreamPark/Particles.
    //   - HSV shift, color ramp LUTs, gradient maps for stylized FX packs.
    //
    // This analyzer walks every property + keyword on the source material's
    // shader, cross-references the converter's mapping table, and reports
    // anything that won't survive the round-trip. The Material Converter
    // window surfaces this as a per-row diff so the user can decide whether
    // to convert or leave the vendor shader alone.
    //
    // IMPORTANT: the mapping table below must stay in sync with
    // MaterialConverter.ConvertParticleMaterial. If you add a new alias to
    // the converter, add it here too — otherwise the diff will incorrectly
    // claim that property is lost.
    // ─────────────────────────────────────────────────────────────────────

    public enum DiffSeverity
    {
        /// <summary>Known engine prop or known-ignored vendor flag. Hidden by default in the UI.</summary>
        Ignored,
        /// <summary>Default-valued prop with no target. No visual impact.</summary>
        Low,
        /// <summary>Vendor lighting / shading feature replaced by DreamPark's simpler
        /// pseudo-lighting pipeline. The material WILL render lit — just with a
        /// less faithful version of the vendor's feature (e.g. linear falloff
        /// instead of cel-shading ramp, ambient floor instead of tinted shadows).
        /// Shown in its own section in the popup with positive language. Counts
        /// toward "Will look different" readiness but with a clearer explanation
        /// than generic data-loss notes.</summary>
        Approximated,
        /// <summary>Non-default scalar / float, no target. Subtle visual difference likely.</summary>
        Medium,
        /// <summary>Non-default color, no target. Visible color shift.</summary>
        High,
        /// <summary>Non-null texture or known-broken keyword. Breaks the effect.</summary>
        Critical,
    }

    public class DiffEntry
    {
        public string propertyName;        // e.g. "_RimColor", "_NoiseTex"
        public ShaderPropertyType type;    // Texture / Color / Float / Range / Vector / Int
        public string currentValue;        // human-readable representation
        public bool isDefault;             // true if value matches shader default
        public DiffSeverity severity;
        public string note;                // why the analyzer flagged this
    }

    /// <summary>
    /// User-facing readiness for converting this material. This is what the
    /// row badge in the window communicates — three buckets that map to a
    /// concrete next action, not the four-level severity scale we use
    /// internally for sorting issues inside the diff.
    /// </summary>
    public enum ConversionReadiness
    {
        /// <summary>Convert it. No functional data loss expected.</summary>
        Ready,

        /// <summary>Convert it — the material's lighting features will be
        /// translated to DreamPark's pseudo-lighting (simpler model than
        /// the vendor's lit pipeline but a real lit look). Only lighting
        /// approximations are present; no actual data loss.</summary>
        ReadyWithApproximations,

        /// <summary>Convert if you're okay with visible visual differences.
        /// The material has properties we can't carry over (rim, lit PBR
        /// data, scrolling base maps, etc.) but the converted result will
        /// render — just not identically to the vendor version.</summary>
        WillLookDifferent,

        /// <summary>Don't convert without manual prep. Source uses textures
        /// or features that the target shader can't replicate at all, and
        /// the converted material will be visibly broken or missing
        /// content (flipbook blending pops, refraction goes opaque, etc.).</summary>
        Blocked,
    }

    public class ParticleDiffReport
    {
        public string sourceShaderName;
        public string targetShaderName;

        // Properties whose values will carry over to the target (source → target alias hits).
        public List<DiffEntry> mapped = new List<DiffEntry>();

        // Properties on the source shader that the converter doesn't know about.
        // These are the interesting ones — anything non-default here is data loss.
        public List<DiffEntry> unmapped = new List<DiffEntry>();

        // Enabled keywords that don't get translated to a DreamPark/Particles equivalent.
        public List<DiffEntry> unmappedKeywords = new List<DiffEntry>();

        public int CriticalCount     => Count(DiffSeverity.Critical);
        public int HighCount         => Count(DiffSeverity.High);
        public int MediumCount       => Count(DiffSeverity.Medium);
        public int ApproximatedCount => Count(DiffSeverity.Approximated);
        public int LowCount          => Count(DiffSeverity.Low);
        public int IgnoredCount      => Count(DiffSeverity.Ignored);

        public bool HasIssues => CriticalCount > 0 || HighCount > 0 || MediumCount > 0 || ApproximatedCount > 0;
        public DiffSeverity TopSeverity =>
            CriticalCount     > 0 ? DiffSeverity.Critical :
            HighCount         > 0 ? DiffSeverity.High         :
            MediumCount       > 0 ? DiffSeverity.Medium       :
            ApproximatedCount > 0 ? DiffSeverity.Approximated  :
            LowCount          > 0 ? DiffSeverity.Low          : DiffSeverity.Ignored;

        /// <summary>
        /// Three-way readiness derived from the underlying severity counts.
        /// This is the right thing to bind UI to — "will it convert?" is
        /// the question the user is actually asking.
        /// </summary>
        public ConversionReadiness Readiness =>
            CriticalCount > 0                    ? ConversionReadiness.Blocked :
            (HighCount + MediumCount) > 0        ? ConversionReadiness.WillLookDifferent :
            ApproximatedCount > 0                ? ConversionReadiness.ReadyWithApproximations :
                                                   ConversionReadiness.Ready;

        /// <summary>
        /// Total count of issues that would affect the converted look
        /// (everything that isn't Low / Ignored). The window badge uses
        /// this to say e.g. "3 differences" instead of the granular
        /// "3 critical, 8 med" breakdown. Approximations count as
        /// issues for accounting purposes — the user wants to see them.
        /// </summary>
        public int IssueCount => CriticalCount + HighCount + MediumCount + ApproximatedCount;

        /// <summary>
        /// One-line headline for the badge. Action-oriented, not engineer-speak.
        /// </summary>
        public string Headline
        {
            get
            {
                switch (Readiness)
                {
                    case ConversionReadiness.Blocked:
                        return $"⛔ {CriticalCount} blocker{(CriticalCount == 1 ? "" : "s")}";
                    case ConversionReadiness.WillLookDifferent:
                        return $"⚠ {IssueCount} difference{(IssueCount == 1 ? "" : "s")}";
                    case ConversionReadiness.ReadyWithApproximations:
                        return $"✓ Ready ({ApproximatedCount} approximated)";
                    default:
                        return "✓ Ready to convert";
                }
            }
        }

        /// <summary>
        /// Sub-line under the badge. Explains what kind of action to take.
        /// </summary>
        public string SubHeadline
        {
            get
            {
                switch (Readiness)
                {
                    case ConversionReadiness.Blocked:
                        return "review before converting";
                    case ConversionReadiness.WillLookDifferent:
                        return "will look slightly different";
                    case ConversionReadiness.ReadyWithApproximations:
                        return "lighting features → DreamPark pseudo-lighting";
                    default:
                        return $"all {mapped.Count} props map cleanly";
                }
            }
        }

        private int Count(DiffSeverity s)
        {
            int n = 0;
            foreach (var e in unmapped) if (e.severity == s) n++;
            foreach (var e in unmappedKeywords) if (e.severity == s) n++;
            return n;
        }
    }

    public static class ParticleConversionDiff
    {
        // Every source-shader property alias the converter looks at, lifted from
        // MaterialConverter.ConvertParticleMaterial. Keep in sync.
        //
        // The convention is: each entry is one TARGET DreamPark/Particles property
        // along with the aliases the converter searches in the source. The diff
        // analyzer marks any source property whose name appears here as "mapped".
        private static readonly Dictionary<string, string[]> ParticleAliases = new Dictionary<string, string[]>
        {
            // Textures
            ["_BaseMap"]       = new[] {
                "_BaseMap", "_MainTex", "_BaseTexture", "_TexBase", "_MainTexture",
                // Standard / vendor renames — these are the same texture, just different names.
                "_Albedo", "_AlbedoMap", "_AlbedoTex",
                "_Diffuse", "_DiffuseMap", "_DiffuseTex",
                "_BaseColorMap", "_ColorMap", "_Tex",
            },
            ["_EmissionMap"]   = new[] { "_EmissionMap", "_EmissiveMap", "_EmissionTex", "_EmissiveTex", "_GlowMap", "_GlowTex", "_SelfIllumMap", "_emissive" },
            ["_DissolveMap"]   = new[] { "_DissolveTex", "_DissolveMap", "_NoiseTex", "_DissolveNoise", "_DissolveTexture" },
            ["_DistortionMap"] = new[] { "_DistortionTex", "_DistortionMap", "_DisplacementMap", "_DistortTex",
                                          // Lightning / energy effects (dual-layer) — layer 1.
                                          "_DistortTex1",
                                          // Hovl Studio uses "_Flow" as the distortion map slot. Their
                                          // flow texture stores per-pixel directional offsets — same
                                          // role as a distortion map even though the math is slightly
                                          // different (true flow mapping does two time-staggered samples;
                                          // we do one warp). Visually close-enough for "renders correctly."
                                          "_Flow", "_FlowMap", "_FlowTex" },
            ["_DistortionMap2"] = new[] { "_DistortTex2", "_DistortionMap2", "_DistortionTex2", "_DistortTex_2" },
            ["_DistortionStrength2"] = new[] { "_DistortionStrength2", "_DistortStrength2", "_Distortion2" },

            // Colors
            ["_BaseColor"]          = new[] {
                "_Color", "_BaseColor", "_TintColor", "_MainColor", "_AlbedoColor", "_DiffuseColor",
                // Underscore-separated variants used by some vendor packs (Hovl Studio,
                // a few stylized FX packs). Same meaning as _BaseColor.
                "_Albedo_Color", "_Tint", "_BaseColor_Color",
            },
            ["_EmissionColor"]      = new[] {
                "_EmissionColor", "_EmissiveColor", "_HdrColor",
                // Lowercase / underscore variants. Hovl Studio uses _Emission_color (note
                // the lowercase 'c'); other packs use _Glow, _GlowColor, _SelfIllumColor.
                "_Emission_color", "_Emission_Color", "_Glow", "_GlowColor", "_SelfIllumColor",
            },
            ["_DissolveEdgeColor"]  = new[] { "_DissolveEdgeColor", "_EdgeColor", "_DissolveColor" },

            // Floats
            ["_Cutoff"]                 = new[] { "_Cutoff", "_Cutout", "_AlphaCutoff", "_AlphaThreshold" },
            ["_EmissionStrength"]       = new[] {
                "_EmissionStrength", "_EmissionIntensity", "_EmissionScale",
                "_EmissionMultiplier", "_EmissionValue", "_EmissionPower",
                "_GlowIntensity", "_GlowStrength", "_GlowPower",
                "_SelfIllumStrength", "_SelfIlluminationStrength",
            },
            ["_HdrBoostMultiplier"]     = new[] { "_HdrBoost", "_HdrMultiply" },
            ["_SoftParticlesNear"]      = new[] { "_SoftParticlesFadeDistanceNear", "_SoftParticlesNearFadeDistance", "_SoftFadeNear", "_SoftParticleNear" },
            ["_SoftParticlesFar"]       = new[] { "_SoftParticlesFadeDistanceFar",  "_SoftParticlesFarFadeDistance",  "_SoftFadeFar",  "_SoftParticleFar"  },
            ["_CameraNearFadeDistance"] = new[] { "_CameraNearFadeDistance", "_CameraFadeNear", "_NearFadeDistance" },
            ["_CameraFarFadeDistance"]  = new[] { "_CameraFarFadeDistance",  "_CameraFadeFar",  "_FarFadeDistance"  },
            ["_Cull"]                   = new[] { "_Cull" },
            ["_ZWrite"]                 = new[] { "_ZWrite" },
            ["_SrcBlend"]               = new[] { "_SrcBlend" },   // derived through blend-mode inference
            ["_DstBlend"]               = new[] { "_DstBlend" },
            ["_BlendMode"]              = new[] { "_BlendMode" },
            ["_ColorMode"]              = new[] { "_ColorMode" },  // vertex color mode (translated to _VC keyword)
            ["_DissolveAmount"]         = new[] { "_DissolveAmount", "_Dissolve", "_DissolveProgress", "_DissolveValue" },
            ["_DissolveEdgeWidth"]      = new[] { "_DissolveEdgeWidth", "_EdgeWidth", "_DissolveEdge", "_DissolveSmooth" },
            ["_DistortionStrength"]     = new[] { "_DistortionStrength", "_DistortStrength", "_DistortAmount", "_DistortionAmount", "_Distort", "_Distortion" },
            ["_DistortionScrollX"]      = new[] { "_DistortionScrollX", "_DistortSpeedX", "_DistortionSpeedX" },
            ["_DistortionScrollY"]      = new[] { "_DistortionScrollY", "_DistortSpeedY", "_DistortionSpeedY" },

            // Overlay (v3) — second-texture layer support
            ["_OverlayTex"]             = new[] { "_OverlayTex", "_OverlayMap", "_OverlayTexture", "_NoiseOverlay" },
            ["_OverlayStrength"]        = new[] { "_OverlayStrength", "_OverlayIntensity", "_OverlayAmount" },
            ["_OverlayBlendMode"]       = new[] { "_CFXR_OVERLAYBLEND", "_OverlayBlendMode", "_OverlayBlend" },

            // Radial UV (v4) — polar UV transformation for rings/halos
            ["_RadialUVInnerRadius"]    = new[] { "_RingTopOffset", "_RadialUVInnerRadius", "_InnerRadius", "_RingInner" },

            // Edge fade (v5) — soft alpha vignette on quad borders
            ["_EdgeFadeWidth"]          = new[] { "_EdgeFadeWidth", "_EF_Width", "_EF_Range", "_EdgeFadeRange", "_EFWidth" },

            // Second color (v6) — two-tone tint via mask texture
            ["_SecondColorTex"]         = new[] { "_SecondColorTex", "_SecondColor_Tex", "_2ndColorTex", "_ColorMaskTex" },
            ["_SecondColor"]            = new[] { "_SecondColor", "_2ndColor", "_TintColor2", "_SecondTint" },
            ["_SecondColorSmooth"]      = new[] { "_SecondColorSmooth", "_SecondColorSmoothness", "_2ndColorSmooth" },

            // Pseudo lighting (v7) — bump map carries over for fake-lit shading
            ["_BumpMap"]                = new[] { "_BumpMap", "_NormalMap", "_NormalTex", "_NrmMap",
                                                   "_NormTex", "_BumpTex", "_NrmTex",
                                                   "_Normal", "_Normals", "_Norm", "_Bump",
                                                   "_DetailNormalMap" },
            ["_BumpScale"]              = new[] { "_BumpScale", "_NormalScale", "_NormalStrength",
                                                   "_NormalIntensity", "_NrmStrength" },

            // Fresnel / rim glow (v10) — same target on our side, multiple
            // vendor naming conventions (_FresnelColor, _RimColor, etc.)
            ["_FresnelColor"]           = new[] { "_FresnelColor", "_RimColor", "_FresnelTint", "_RimTint",
                                                   "_EdgeColor", "_FresnelGlow" },
            ["_FresnelPower"]           = new[] { "_FresnelPower", "_RimPower", "_FresnelExponent",
                                                   "_RimExponent", "_FresnelFalloff", "_RimFalloff" },

            // Noise modulation (v8) — Hovl Studio _Noise + _NoiseQuat
            ["_NoiseModTex"]            = new[] { "_Noise", "_NoiseTex", "_NoiseMap", "_NoiseTexture" },
        };

        // Source-side properties that aren't 1:1 property mappings but ARE
        // read by the converter to drive a target-side keyword. Treating
        // them as "mapped" prevents the analyzer from flagging them as
        // data loss — they ARE carrying over, just via keyword translation.
        //
        // Currently:
        //   _SoftParticlesEnabled → enables _SOFTPARTICLES_ON on target
        //   _DistortionEnabled    → enables _DISTORTION_ON on target
        //   _CameraFadingEnabled  → enables _CAMERAFADE_ON on target (v4)
        private static readonly HashSet<string> KeywordDriverProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            // URP / ShaderGraph particle conventions
            "_SoftParticlesEnabled",
            "_DistortionEnabled",
            "_CameraFadingEnabled",
            "_Emission",        // some Shader Graph particle shaders expose this as a float toggle
            // Hovl Studio's emission-enable toggle. Same pattern as
            // _UseSP / _UseDissolve / _UseLighting — float toggle that
            // drives the corresponding _EMISSION_ON keyword on the target.
            "_UseEmission", "_UseGlow",
            // Vendor-typo and short-form alpha-clip / soft-particles toggles.
            // _UseAlphaCliping (single 'p') is a real misspelling that ships
            // in at least one CFXR-derivative pack — alias it so it doesn't
            // keep showing up as data loss.
            "_UseAlphaCliping", "_UseSoft",
            // Hovl Studio also ships the noise-control Vector4 with a
            // descriptive name in some materials. Same meaning as _NoiseQuat
            // (xy=scroll, z=base power, w=glow power).
            "_NoisespeedXYNoisepowerZGlowpowerW",
            // Dual-layer distortion (lightning) packs both layers' scroll
            // velocities into a single Vector4: xy = layer 1 scroll,
            // zw = layer 2 scroll. Converter splits into four scalar
            // _Distortion*Scroll*X/Y properties on the target.
            "_DistortSpeed",
            // CFXR (Cartoon FX Remaster) conventions. The converter reads
            // each of these float toggles and flips the corresponding
            // target-side keyword:
            //   _UseSP        → _SOFTPARTICLES_ON
            //   _UseAlphaClip → _ALPHATEST_ON
            //   _SingleChannel→ _SINGLECHANNEL_ON
            "_UseSP",
            "_UseAlphaClip",
            "_SingleChannel",
            "_UseDissolve",        // drives _DISSOLVE_ON
            "_UseUVDistortion",    // drives _DISTORTION_ON (CFXR)
            // Vector4-to-scalar-pair translations.
            //   _OverlayTex_Scroll → _OverlayScrollX / _OverlayScrollY
            //   _DistortScrolling  → _DistortionScrollX / _DistortionScrollY
            // The xy of each vector is the scroll velocity; zw is vendor-
            // specific noise we don't replicate.
            "_OverlayTex_Scroll",
            "_DistortScrolling",
            // CFXR's float enable flag for overlay. Parallel signal to
            // _OverlayTex being non-null — the converter detects overlay
            // via the texture binding regardless of this flag, so it's
            // already handled.
            "_CFXR_OVERLAYTEX",
            // Legacy soft-particle hardness from Unity's old Particles/
            // Additive shader family. The converter reads its numeric
            // value to derive _SoftParticlesFar when no explicit far
            // distance was provided.
            "_InvFade",
            // Radial UV enable toggle. Drives the target's _RADIAL_UV_ON
            // keyword via the converter. The companion _RingTopOffset
            // carries over directly via the alias table above.
            "_UseRadialUV",
            // Edge fade enable toggle. Drives _EDGE_FADE_ON on the target.
            "_UseEF",
            // Second-color enable toggle. Drives _SECONDCOLOR_ON.
            "_UseSecondColor",
            // Hovl Studio packs noise speed + base power + glow power
            // into a single Vector4. The converter unpacks it into
            // separate target scalars (_NoiseModSpeedX/Y, _NoiseModBasePower,
            // _NoiseModGlowPower). Treated as mapped — vector-to-scalars
            // translation.
            "_NoiseQuat",
            // Lighting enable toggles. Drive _PSEUDO_LIT_ON on the target
            // when combined with a normal-map presence. The converter
            // currently keys pseudo-lighting on bumpmap presence alone, so
            // these float toggles are informational — they confirm the
            // artist intended lighting, but don't change behavior.
            "_UseLighting", "_UseNormalMap",
            // Hovl Studio packs distortion controls into a Vector4:
            // xy = scroll velocity, z = distortion strength, w = unused.
            // The converter unpacks into _DistortionScrollX/Y + the
            // _DistortionStrength fallback chain.
            "_DistortionSpeedXYPowerZ",
            // Hovl Studio's float toggle for soft-particle depth fade.
            // Drives _SOFTPARTICLES_ON via the converter's union of
            // soft-particle signals.
            "_Usedepth",
            // Hovl's soft-particle hardness (companion to _Usedepth).
            // Converter uses its value to derive _SoftParticlesFar
            // (similar to the legacy _InvFade fallback).
            "_Depthpower",
        };

        // Vendor lighting properties that don't have a 1:1 target in DreamPark/
        // Particles but ARE approximated by the pseudo-lighting path when a
        // normal map is present. Shown in their own popup section with a
        // positive explanation rather than being flagged as data loss.
        //
        // Only activates when pseudo-lit will run (i.e. the source has a normal
        // map). If there's no normal map, these really ARE lost — the analyzer
        // falls back to standard severity scoring for them.
        private static readonly Dictionary<string, string> ApproximatedByPseudoLighting = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_ShadowColor"] = "Approximated by DreamPark pseudo-lighting's ambient floor (_FakeLightAmbient). Shaded areas get a uniform brightness scalar instead of a tinted color — they'll look darker, not specifically shadow-colored.",
            ["_DirectLightingRamp"] = "Approximated by DreamPark pseudo-lighting's linear NdotL falloff. The vendor's cel-shading-style ramp gets replaced with a smooth gradient between ambient and full bright.",
            ["_BacklightTransmittance"] = "Backlight / subsurface transmission isn't replicated in DreamPark's pseudo-lighting. Particles won't appear to transmit light from behind. The lit silhouette will still be present, just opaque to back-lighting.",
            ["_UseBackLighting"] = "Backlight feature not replicated (see _BacklightTransmittance). The particle still receives pseudo-lighting on its front face.",
            ["_IndirectLightingMix"] = "Approximated by DreamPark pseudo-lighting's ambient floor (_FakeLightAmbient). The mix-amount dial gets collapsed into the ambient scalar.",
            ["_LightingWorldPosStrength"] = "World-position lighting modulation not replicated. DreamPark's pseudo-lighting is tangent-space (billboard-relative), so lighting is consistent regardless of world position — same effect at the origin or 100m away.",
            // PBR scalars and maps — vendor lit materials author these for
            // a full PBR pipeline. DreamPark pseudo-lighting is a simple
            // NdotL fake — no metallic/roughness response, no IBL. The
            // material WILL render lit (via the normal map) but won't have
            // PBR shading reflectance. Pragmatic for stylized MR particles.
            ["_Metallic"]              = "PBR metalness isn't replicated. DreamPark's pseudo-lighting is a one-direction fake NdotL with no specular reflection model — converted material won't have the metallic sheen response. Looks like a matte version of the original.",
            ["_MetallicGlossMap"]      = "PBR metallic+gloss texture isn't sampled. Same reason as _Metallic above — no PBR shading response in pseudo-lighting.",
            ["_Smoothness"]            = "Surface smoothness / glossiness has no target. DreamPark pseudo-lighting has fixed falloff; converted material won't have variable shininess.",
            ["_Glossiness"]            = "Surface glossiness has no target (same as _Smoothness).",
            ["_GlossMapScale"]         = "Gloss map intensity has no target — no gloss response in pseudo-lighting.",
            ["_OcclusionMap"]          = "Ambient occlusion texture has no target — DreamPark pseudo-lighting uses a flat ambient floor, not per-pixel AO.",
            ["_OcclusionStrength"]     = "Ambient occlusion strength has no target.",
            ["_ParallaxMap"]           = "Parallax displacement mapping has no target — DreamPark pseudo-lighting uses simple normal perturbation without depth offset.",
            ["_Parallax"]              = "Parallax displacement strength has no target.",
        };

        // Keywords the converter explicitly handles. Anything else on the source
        // is unmapped. Treat the blend-mode keywords as handled because
        // ApplyBlendModeKeywords resets them and re-sets the right one.
        private static readonly HashSet<string> HandledKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "_BLENDMODE_ALPHA", "_BLENDMODE_ADDITIVE", "_BLENDMODE_PREMULTIPLIED", "_BLENDMODE_MULTIPLY",
            "_VC_OFF", "_VC_MULTIPLY", "_VC_ADD",
            "_ALPHATEST_ON",
            "SOFTPARTICLES_ON", "_SOFTPARTICLES_ON", "_FADING_ON",
            "_CFXR_SINGLE_CHANNEL", "_SINGLECHANNEL_ON",
            "_CFXR_DISSOLVE", "_DISSOLVE_ON",
            "_CFXR_UV_DISTORTION", "_DISTORTION_ON",
            "_HDR_BOOST_ON",
            // Emission. The converter reads any of these as "emission was
            // intended" and enables _EMISSION_ON on the target. _EMISSION
            // is URP/Standard's gate; the others cover vendor conventions.
            "_EMISSION", "_EMISSION_ON", "_EMISSIVE_ON", "_GLOW_ON", "_SELFILLUM_ON",
            // URP multi-compile keywords that show up on imported URP
            // particle materials but aren't visual data — the converter
            // ignores them entirely because the target shader has its
            // own variant pipeline.
            "_NORMALMAP", "_SURFACE_TYPE_TRANSPARENT", "_RECEIVE_SHADOWS_OFF",
            // Vendor (no leading underscore — that's how they're declared).
            "USE_ALPHA_CLIPING", "USE_SOFT_PARTICLES",
            // URP's alpha-blend mode signal. The converter doesn't read
            // this keyword directly — it infers blend mode from the
            // _SrcBlend/_DstBlend factors which carry the same information
            // unambiguously. Listing it here so the diff stops flagging it
            // as unknown.
            "_ALPHABLEND_ON", "_SURFACE_TYPE_OPAQUE",
            // CFXR vendor keywords we deliberately don't replicate.
            // DreamPark/Particles is unlit and doesn't cast shadows in
            // MR, so dithered-shadow features have no rendering impact
            // in our pipeline.
            "_CFXR_DITHERED_SHADOWS_ON",
            // Niche CFXR feature (text-on-particle color override).
            // Almost never used for environmental VFX; silently dropped.
            "_CFXR_FONT_COLORS",
            // CFXR blend-mode keyword signals. We derive blend mode from
            // _SrcBlend / _DstBlend factors directly, so these are
            // redundant noise on the source side.
            "_CFXR_ADDITIVE", "_CFXR_PREMULTIPLIED", "_CFXR_MULTIPLY",
            // CFXR dissolve / overlay keyword signals. _UseDissolve drives
            // _DISSOLVE_ON via the converter; _CFXR_OVERLAY_ON is the
            // single gate keyword for overlay (now first-class).
            "_CFXR_DISSOLVE_ON", "_CFXR_OVERLAY_ON",
            // CFXR emission power-curve preset (multi_compile variants).
            // pow(emission, 1 / 2 / 3 / 4). We collapse to linear (P1).
            // The unpowered look is slightly softer; converted particles
            // glow at "P1 baseline" regardless of which variant was set.
            "_CFXR_GLOW_POW_OFF", "_CFXR_GLOW_POW_P1",
            "_CFXR_GLOW_POW_P2",  "_CFXR_GLOW_POW_P3",  "_CFXR_GLOW_POW_P4",
            // CFXR overlay intensity preset (multi_compile variants).
            // The vendor shader uses these to switch between 1x/2x/3x
            // overlay multipliers; we collapse this into a single
            // _OverlayStrength scalar (0-1 range). The converted
            // material picks up _OverlayStrength from the source if
            // present, otherwise defaults to 1.0 — a faithful "1X"
            // baseline. Higher variants get clamped to the 0-1 range
            // (acceptable for "renders correctly" coverage).
            "_CFXR_OVERLAYTEX_OFF", "_CFXR_OVERLAYTEX_1X",
            "_CFXR_OVERLAYTEX_2X",  "_CFXR_OVERLAYTEX_3X",
            // CFXR overlay blend-mode variants. The converter reads
            // _CFXR_OVERLAYBLEND (the float) and maps to our single
            // _OverlayBlendMode scalar, so these per-mode keyword
            // variants are redundant.
            "_CFXR_OVERLAYBLEND_A", "_CFXR_OVERLAYBLEND_M",
            "_CFXR_OVERLAYBLEND_AB", "_CFXR_OVERLAYBLEND_OFF",
            // ── KriptoFX RFX1 (Real-FX 1) ────────────────────────────
            // KriptoFX's particle ubershader uses pairs of keywords like
            // Foo_OFF / Foo_ON for every feature toggle. All _OFF variants
            // are silent (feature disabled, nothing to lose). _ON variants
            // for features we DO replicate (alpha clip, additive blend)
            // are also silent; the converter reads them and translates.
            // _ON variants for features we DON'T replicate are listed in
            // CriticalUnhandledKeywords below.
            "BlendAdd", "BlendAlpha", "BlendMultiply", "BlendPremultiply",
            "Clip_OFF", "Clip_ON",                // alpha cutoff (we have _ALPHATEST_ON)
            "FrameBlend_OFF",                     // flipbook blending: OFF state is fine
            "FresnelFade_OFF",                    // fresnel fade: OFF state is fine
            "VertLight_OFF",                      // vertex lighting: OFF state is fine (we're unlit)
            "SoftFade_OFF", "SoftFade_ON",        // soft particles, both states handled
            "Distortion_OFF", "Distortion_ON",    // distortion, both states handled
            // Radial UV (v4) — first-class support. The converter reads
            // both the keyword and CFXR's _UseRadialUV float, and writes
            // _RADIAL_UV_ON on the target. Inner-radius cutoff carries
            // over via _RingTopOffset → _RadialUVInnerRadius alias.
            "_CFXR_RADIAL_UV", "_RADIAL_UV_ON",
            // Edge fade (v5) — first-class support.
            "_CFXR_EDGE_FADING", "_EDGE_FADE_ON",
            // Second color (v6) — first-class support.
            "_CFXR_SECONDCOLOR_LERP", "_SECONDCOLOR_ON",
            // Pseudo lighting (v7) — first-class support. _NORMALMAP
            // was already in the URP-noise list above; we also handle
            // our own _PSEUDO_LIT_ON gate.
            "_PSEUDO_LIT_ON",
            // Noise modulation (v8) — first-class support.
            "_NOISE_MOD_ON",
            // Dual distortion layer (v9) — lightning support.
            "_DISTORTION2_ON",
            // Fresnel / rim glow (v10). _RIM_ON / _FRESNEL_ON / FresnelFade_ON
            // signal the vendor's intent; our shader computes fresnel
            // automatically inside the pseudo-lit block based on
            // _FresnelColor — so these keyword toggles are informational.
            "_RIM_ON", "_FRESNEL_ON", "_FresnelFade_ON", "FresnelFade_ON",
            // CFXR lighting-mode keywords (multi_compile variants).
            // CFXR lit materials select between off / basic / advanced /
            // backlight lighting via these. We approximate via pseudo-
            // lighting regardless of which variant was selected — the
            // resulting visual is the same simpler lit look.
            "_CFXR_LIGHTING_OFF", "_CFXR_LIGHTING_ALL",
            "_CFXR_LIGHTING_BASIC", "_CFXR_LIGHTING_BACK",
        };

        // Keywords that the converter intentionally drops because DreamPark/Particles
        // doesn't model the feature. Surfaced as Critical in the diff so the user
        // sees what they'd lose. (Mirrors MaterialConverter.ExoticParticleKeywords.)
        // Vendor flipbook properties whose feature is APPROXIMATED by the
        // post-convert pass in MaterialConverterExecutor — that pass
        // unconditionally enables Unity's TextureSheetAnimation module on
        // the ParticleSystem whenever tile config is present. The flipbook
        // plays frame-by-frame instead of motion-vector smooth interpolation
        // between frames, but it plays correctly.
        //
        // Independent of pseudo-lighting (no normal map needed) — checked
        // unconditionally in the analyzer.
        private static readonly Dictionary<string, string> ApproximatedByFlipbookAnimation = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_MotionVector"] = "Approximated by Unity's TextureSheetAnimation module — the converter auto-enables it on the ParticleSystem whenever tile config is present. Flipbook plays frame-by-frame instead of motion-vector smooth interpolation. Frames will pop between transitions but the animation plays correctly.",
            ["_FlipbookBlending"] = "Approximated by Unity's TextureSheetAnimation module — same as _MotionVector. The standard module advances the per-frame UV; frames pop between transitions instead of blending smoothly via motion vectors.",
        };

        // Keywords whose feature is APPROXIMATED by an external mechanism
        // (typically the post-convert auto-fixes in MaterialConverterExecutor).
        // Diff entries for these end up in the Approximated section with a
        // positive explanation, not the Critical section. Counterpart to
        // ApproximatedByPseudoLighting / ApproximatedByFlipbookAnimation
        // but for the keyword side of the analyzer.
        private static readonly Dictionary<string, string> ApproximatedKeywords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // URP's actual keyword name (matches the float toggle).
            ["_FLIPBOOKBLENDING_ON"] = "Approximated by Unity's TextureSheetAnimation module — auto-enabled on the ParticleSystem by the converter when tile config is present. Flipbook plays frame-by-frame instead of motion-vector smoothing. Frames pop between transitions but the animation plays correctly.",
            // CFXR / older Standard Particles spelling. Same approximation.
            ["_FLIPBOOK_BLENDING"]   = "Approximated by Unity's TextureSheetAnimation module — auto-enabled on the ParticleSystem by the converter when tile config is present. Flipbook plays frame-by-frame instead of motion-vector smoothing. Frames pop between transitions but the animation plays correctly.",
        };

        private static readonly Dictionary<string, string> CriticalUnhandledKeywords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_REFRACTION_ON"]       = "Screen-space refraction (heat haze, glass distortion). No equivalent in DreamPark/Particles — converted material will be fully opaque/transparent without bending the background.",
            ["_GRADIENT_MAPPING_ON"] = "Color-ramp LUT remapping (1D gradient texture). DreamPark/Particles only does linear base color × vertex color.",
            ["_HSV_SHIFT_ON"]        = "HSV color shift over particle lifetime. No target.",
            // ── KriptoFX RFX1 features we don't replicate ────────────
            // The OFF variants of these keywords are in HandledKeywords;
            // only the ON variants (real feature enabled) get flagged.
            ["FrameBlend_ON"] = "KriptoFX flipbook frame blending. DreamPark/Particles v1 doesn't smooth frame transitions; the converted effect will pop between flipbook frames.",
            ["VertLight_ON"] = "KriptoFX vertex lighting. DreamPark/Particles is unlit; the converted material will not respond to scene lights.",
        };

        // Property name prefixes / exact names that are engine-internal or
        // shader metadata, never visual data. Bucketed as Ignored so they
        // don't bloat the report.
        private static readonly HashSet<string> IgnoredExact = new HashSet<string>(StringComparer.Ordinal)
        {
            // Engine-defined render-state holders that Unity rewrites anyway.
            "_QueueOffset", "_QueueControl", "_Surface", "_WorkflowMode",
            "_AlphaClip", "_AlphaToMask", "_Blend",
            "_CastShadows", "_ReceiveShadows",
            "_ClearCoatMask", "_ClearCoatSmoothness",
            "_AddPrecomputedVelocity",
            "_BlendModePreserveSpecular",
            "_EnvironmentReflections", "_SpecularHighlights",
            "_DstBlendAlpha", "_SrcBlendAlpha",
            "_ZTest", "_ZWriteControl",
            "_SmoothnessTextureChannel",
            // Common stylized-pack metadata that never affects render output.
            "_Version", "_VendorVersion", "_ShaderVariant",
            // CFXR vendor properties for features we don't replicate by
            // design. Surfaced here (rather than as "Will look different")
            // because:
            //   - Dithered shadows: DreamPark particles are unlit and
            //     don't cast shadows in MR, so this feature is moot
            //     regardless of the source value.
            //   - Font colors: niche text-on-particle feature. If a
            //     material was authored to use this, the converted version
            //     loses it, but we accept that — it's not relevant for
            //     environmental VFX which is 99% of what gets imported.
            "_CFXR_DITHERED_SHADOWS", "_ShadowStrength",
            "_UseFontColor",
            // ── CFXR dissolve animation/variant params ───────────────
            // We do static-UV dissolve and one-pass only. These are
            // animation/variant knobs that affect HOW the dissolve
            // animates, not WHETHER it dissolves. Materials with these
            // set will still dissolve correctly with our shader — they
            // just won't have scrolling, double passes, or alt-UV.
            "_DissolveScroll", "_DoubleDissolve", "_UseDissolveOffsetUV",
            // (Overlay support is now first-class — _OverlayTex,
            // _OverlayTex_Scroll, _CFXR_OVERLAYBLEND, _OverlayStrength
            // all carry over via the v3 alias table / KeywordDriverProperties.)
            // ── Vendor blend-mode enums ──────────────────────────────
            // Vendor shaders often expose a _BlendingType / _BlendType
            // enum that drives _SrcBlend / _DstBlend selection. We read
            // the BLEND FACTORS directly (which is the authoritative
            // signal), so the enum is redundant noise.
            "_BlendingType", "_BlendType",
            // ── Glow / emission animation range ──────────────────────
            // Some vendors expose Min/Max/MaxValue to drive emission
            // brightness oscillation over the particle's lifetime via
            // shader animation. We use a single static _EmissionStrength,
            // so a converted particle gets a steady glow instead of a
            // pulse. Accepting this loss for "renders correctly" coverage.
            "_GlowMin", "_GlowMax", "_MaxValue",
            // ── Edge-fade curve exponent ─────────────────────────────
            // Vendor: pow(edge, _EdgeFadePow) for non-linear edge softness.
            // We use a linear edge. Subtle visual difference, not worth a
            // pow() per fragment.
            "_EdgeFadePow",
            // ── CFXR emission-power curve ────────────────────────────
            // CFXR exposes _CFXR_GLOW_POW (Float) + _CFXR_GLOW_POW_P2..P4
            // (multi_compile keyword variants) to apply pow(emission, N)
            // for punchier glow. We have linear emission. Converted
            // particles glow at the unpowered baseline — visible side-by-
            // side but acceptable for "renders correctly" coverage.
            "_CFXR_GLOW_POW",
            // ── CFXR axis fades / niche UV tricks ────────────────────
            // _FadeAlongU       — alpha fades along the U texture coord
            //                     (flames fading toward the top, etc.)
            // _UVDistortionAdd  — additive blend variant for distortion
            //                     (we use the default mix mode)
            "_FadeAlongU", "_UVDistortionAdd",
            // Vendor "height" property — sometimes parallax depth, sometimes
            // unused authoring scratch. Almost always 0 in shipped materials.
            // No parallax in our pipeline; silently drop.
            "_Height",
            // ── URP screen-space refraction blend ────────────────────
            // _DistortionBlend is URP's "how much to composite the
            // distorted scene texture with the original scene" slider
            // for screen-space refraction. We don't read the scene
            // color buffer (we only warp the particle's own UVs), so
            // there's no scene to blend. Materials that actually
            // intend refraction get flagged Critical via the
            // _REFRACTION_ON keyword above; this property is just the
            // dial that goes along with it.
            "_DistortionBlend",
        };

        // ─── Public API ──────────────────────────────────────────────────

        /// <summary>
        /// Analyze a particle material against DreamPark/Particles. Returns a
        /// report describing which properties carry over, which don't, and the
        /// severity of each loss. Safe to call on any material — non-particles
        /// produce an empty report with a note.
        /// </summary>
        public static ParticleDiffReport Analyze(Material src)
        {
            var report = new ParticleDiffReport
            {
                targetShaderName = DreamParkShaderNames.Particles,
            };

            if (src == null) return report;
            if (src.shader == null) return report;

            report.sourceShaderName = src.shader.name;

            // NOTE: we deliberately do NOT gate on IsParticleMaterial(src)
            // here. The planner is the authoritative classifier — it can
            // promote a material to ConvertParticle because of either a
            // shader-name match OR a ParticleSystemRenderer attachment.
            // The latter catches vendor packs (Hovl Studio's HS_Explosion,
            // etc.) whose shader names don't include "particle". If the
            // planner says it's a particle, we trust that and produce a
            // real diff.

            // Build the set of source-property names that the converter knows
            // about (via aliases). Any property on the source shader NOT in
            // this set is either unmapped or ignored.
            var knownSourceAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in ParticleAliases)
                foreach (var alias in kv.Value)
                    knownSourceAliases.Add(alias);
            // Also treat the keyword-driver properties as mapped. These
            // don't have a 1:1 property destination — they're enable flags
            // the converter reads to flip a target-side keyword.
            foreach (var d in KeywordDriverProperties)
                knownSourceAliases.Add(d);

            // Detect whether pseudo-lighting will be active on the converted
            // material. The converter enables _PSEUDO_LIT_ON whenever the
            // source has any flavor of normal map. We mirror that detection
            // here so the analyzer can route vendor lighting properties into
            // the "Approximated by pseudo-lighting" category (positive note)
            // rather than generic data-loss (negative note).
            bool pseudoLitWillBeActive = false;
            foreach (var bumpAlias in new[] {
                "_BumpMap", "_NormalMap", "_NormalTex", "_NrmMap",
                "_NormTex", "_BumpTex", "_NrmTex",
                "_Normal", "_Normals", "_Norm", "_Bump",
                "_DetailNormalMap" })
            {
                if (src.HasProperty(bumpAlias) && src.GetTexture(bumpAlias) != null)
                {
                    pseudoLitWillBeActive = true;
                    break;
                }
            }

            // Walk every property on the source shader.
            var shader = src.shader;
            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);

                // Skip hidden properties — they're shader-internal state, never
                // exposed to authoring, no visual data lost by dropping them.
                var flags = shader.GetPropertyFlags(i);
                if ((flags & ShaderPropertyFlags.HideInInspector) != 0) continue;

                bool isMapped = knownSourceAliases.Contains(name);
                bool isIgnored = IgnoredExact.Contains(name);

                var entry = BuildEntry(src, name, type, i);
                if (isMapped)
                {
                    entry.severity = DiffSeverity.Low; // mapped properties don't impact severity
                    entry.note = "Carries over via converter alias table.";
                    report.mapped.Add(entry);
                    continue;
                }
                if (isIgnored)
                {
                    entry.severity = DiffSeverity.Ignored;
                    entry.note = "Engine-internal or shader-metadata property; no visual data.";
                    report.unmapped.Add(entry);
                    continue;
                }

                // Flipbook feature approximated by the post-convert
                // TextureSheetAnimation auto-enable. Always runs (doesn't
                // depend on any source-side material trait), so this
                // approximation is unconditional.
                if (ApproximatedByFlipbookAnimation.TryGetValue(name, out string flipbookNote))
                {
                    entry.severity = DiffSeverity.Approximated;
                    entry.note = flipbookNote;
                    report.unmapped.Add(entry);
                    continue;
                }

                // Lighting feature replaced by pseudo-lighting? Only count
                // this category as "approximated" when pseudo-lit will
                // actually run — i.e. the source has a normal map. Without
                // a normal map, our pseudo-lighting path is off, so these
                // properties really ARE lost (fall through to standard
                // unmapped scoring with a contextual note).
                if (ApproximatedByPseudoLighting.TryGetValue(name, out string approxNote))
                {
                    if (pseudoLitWillBeActive)
                    {
                        entry.severity = DiffSeverity.Approximated;
                        entry.note = approxNote;
                        report.unmapped.Add(entry);
                        continue;
                    }
                    // No normal map → no pseudo-lit → really lost.
                    ScoreUnmapped(entry, src, name, type);
                    entry.note = "Lighting feature not replicated (material has no normal map for DreamPark pseudo-lighting to activate). "
                               + approxNote;
                    report.unmapped.Add(entry);
                    continue;
                }

                // Genuinely unmapped — score severity.
                ScoreUnmapped(entry, src, name, type);
                report.unmapped.Add(entry);
            }

            // Walk enabled keywords. We can only inspect the SET of valid keywords
            // (shader.keywordSpace) plus what's currently enabled on the material.
            // For each enabled-on-material keyword: handled, exotic, or other.
            foreach (var kw in src.enabledKeywords)
            {
                string name = kw.name;
                var entry = new DiffEntry
                {
                    propertyName = name,
                    type = ShaderPropertyType.Int, // keywords aren't typed; pick something for the UI
                    currentValue = "enabled",
                    isDefault = false,
                };

                if (HandledKeywords.Contains(name))
                {
                    entry.severity = DiffSeverity.Low;
                    entry.note = "Translated by the converter's keyword pass.";
                    continue;  // don't add to unmappedKeywords
                }

                if (ApproximatedKeywords.TryGetValue(name, out string approxKeywordNote))
                {
                    entry.severity = DiffSeverity.Approximated;
                    entry.note = approxKeywordNote;
                    report.unmappedKeywords.Add(entry);
                    continue;
                }

                if (CriticalUnhandledKeywords.TryGetValue(name, out string criticalNote))
                {
                    entry.severity = DiffSeverity.Critical;
                    entry.note = criticalNote;
                    report.unmappedKeywords.Add(entry);
                    continue;
                }

                // Unknown vendor keyword — medium severity. It's enabled, so the
                // vendor shader is doing SOMETHING with it, but we don't know
                // whether the effect is critical or cosmetic.
                entry.severity = DiffSeverity.Medium;
                entry.note = "Unknown vendor keyword. Likely controls a feature the converter doesn't replicate.";
                report.unmappedKeywords.Add(entry);
            }

            return report;
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private static DiffEntry BuildEntry(Material src, string name, ShaderPropertyType type, int propIndex)
        {
            var entry = new DiffEntry
            {
                propertyName = name,
                type = type,
            };

            try
            {
                switch (type)
                {
                    case ShaderPropertyType.Texture:
                    {
                        var tex = src.GetTexture(name);
                        entry.currentValue = tex != null ? $"{tex.name} ({tex.width}×{tex.height})" : "(none)";
                        entry.isDefault = tex == null;
                        break;
                    }
                    case ShaderPropertyType.Color:
                    {
                        var c = src.GetColor(name);
                        Color def = src.shader.GetPropertyDefaultVectorValue(propIndex);
                        entry.currentValue = $"({c.r:0.##}, {c.g:0.##}, {c.b:0.##}, {c.a:0.##})";
                        entry.isDefault = ColorApprox(c, def);
                        break;
                    }
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        float f = src.GetFloat(name);
                        float def = src.shader.GetPropertyDefaultFloatValue(propIndex);
                        entry.currentValue = f.ToString("0.###");
                        entry.isDefault = Mathf.Approximately(f, def);
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        var v = src.GetVector(name);
                        var def = src.shader.GetPropertyDefaultVectorValue(propIndex);
                        entry.currentValue = $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##}, {v.w:0.##})";
                        entry.isDefault = Vector4Approx(v, def);
                        break;
                    }
                    case ShaderPropertyType.Int:
                    {
                        int n = src.GetInteger(name);
                        entry.currentValue = n.ToString();
                        // No GetPropertyDefaultIntValue API; assume 0 default.
                        entry.isDefault = n == 0;
                        break;
                    }
                    default:
                        entry.currentValue = "(unsupported type)";
                        entry.isDefault = true;
                        break;
                }
            }
            catch
            {
                entry.currentValue = "(read failed)";
                entry.isDefault = true;
            }
            return entry;
        }

        private static void ScoreUnmapped(DiffEntry entry, Material src, string name, ShaderPropertyType type)
        {
            // Heuristic naming hints — if the property name CONTAINS one of
            // these, bump severity even when the value looks default. Catches
            // things like "_DistortionMap2" or "_RimColor" without listing
            // every vendor convention.
            string lower = name.ToLowerInvariant();
            // NOTE: "rim" and "fresnel" used to be in here, but we now do
            // rim/fresnel via the pseudo-lit block — naturally gated by
            // normal-map variation. So they're handled via aliases above.
            bool nameHintsCritical = lower.Contains("refract") || lower.Contains("gradient")
                                  || lower.Contains("ramp") || lower.Contains("hsv");
            // "flipbook" used to be here too; now handled by the
            // post-convert TextureSheetAnimation auto-enable — see
            // ApproximatedByFlipbookAnimation above.

            // Base severity by type + default-ness.
            switch (type)
            {
                case ShaderPropertyType.Texture:
                    if (!entry.isDefault)
                    {
                        entry.severity = DiffSeverity.Critical;
                        entry.note = $"Source has a non-null texture in '{name}' with no target slot. The vendor shader is sampling this texture; the converted material won't.";
                    }
                    else
                    {
                        entry.severity = DiffSeverity.Low;
                        entry.note = "Texture slot empty — no visual impact.";
                    }
                    break;

                case ShaderPropertyType.Color:
                    if (!entry.isDefault)
                    {
                        entry.severity = nameHintsCritical ? DiffSeverity.Critical : DiffSeverity.High;
                        entry.note = $"Non-default color value, no target. Visible color shift after conversion.";
                    }
                    else
                    {
                        entry.severity = DiffSeverity.Low;
                        entry.note = "Color at shader default.";
                    }
                    break;

                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    if (!entry.isDefault)
                    {
                        entry.severity = nameHintsCritical ? DiffSeverity.Critical : DiffSeverity.Medium;
                        entry.note = $"Non-default scalar, no target. Subtle but real difference.";
                    }
                    else
                    {
                        entry.severity = DiffSeverity.Low;
                        entry.note = "Scalar at shader default.";
                    }
                    break;

                case ShaderPropertyType.Vector:
                    if (!entry.isDefault)
                    {
                        entry.severity = nameHintsCritical ? DiffSeverity.Critical : DiffSeverity.Medium;
                        entry.note = $"Non-default vector (often UV scroll / tiling), no target.";
                    }
                    else
                    {
                        entry.severity = DiffSeverity.Low;
                    }
                    break;

                default:
                    entry.severity = DiffSeverity.Low;
                    break;
            }
        }

        private static bool ColorApprox(Color a, Vector4 b)
        {
            const float eps = 0.002f;
            return Mathf.Abs(a.r - b.x) < eps
                && Mathf.Abs(a.g - b.y) < eps
                && Mathf.Abs(a.b - b.z) < eps
                && Mathf.Abs(a.a - b.w) < eps;
        }

        private static bool Vector4Approx(Vector4 a, Vector4 b)
        {
            const float eps = 0.002f;
            return Mathf.Abs(a.x - b.x) < eps
                && Mathf.Abs(a.y - b.y) < eps
                && Mathf.Abs(a.z - b.z) < eps
                && Mathf.Abs(a.w - b.w) < eps;
        }
    }
}
#endif
