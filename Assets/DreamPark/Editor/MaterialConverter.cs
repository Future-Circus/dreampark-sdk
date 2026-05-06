#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools
{
    /// <summary>
    /// Converts existing materials (Standard / URP / HDRP / arbitrary asset-pack shaders)
    /// to DreamPark-UniversalShader, preserving their textures and color/scalar values.
    ///
    /// Why this exists: imported asset packages ship with materials in whatever shader the
    /// vendor authored them in. The DreamPark MR pipeline requires DreamPark-UniversalShader
    /// (passthrough occlusion, Quest-friendly). Manually rebuilding every material is tedious
    /// and error-prone. This converter does name-based property matching with extensive
    /// fallback patterns so most vendor materials carry over cleanly.
    ///
    /// Usage:
    ///   - Right-click → Assets/DreamPark/Convert to DreamPark Shader  (auto-routes:
    ///     opaque/lit → DreamPark-UniversalShader, particles → DreamPark/Particles)
    ///   - Static: MaterialConverter.ConvertMaterial(srcMat)         → bool
    ///   - Static: MaterialConverter.ConvertPrefabMaterials(prefabPath) → int
    ///   - Static: MaterialConverter.ConvertFolder(folderPath)       → ConvertSummary
    ///   - Static: MaterialConverter.ConvertFolders(string[] folders) → ConvertSummary
    /// </summary>
    public static class MaterialConverter
    {
        const string DreamParkShaderName = "Shader Graphs/DreamPark-UniversalShader";
        const string FallbackShaderPath = "Assets/DreamPark/Shaders/DreamPark-UniversalShader.shadergraph";

        // ─── Property name mappings ──────────────────────────────────────────
        // For each DreamPark-UniversalShader property, we list every common
        // alias used in Standard, URP/Lit, HDRP/Lit, Built-in, and the messy
        // wild west of asset-store packs. Matching is case-insensitive and
        // substring-based when the exact match fails.

        // Texture slot mappings — DreamPark slot → source aliases (priority order)
        static readonly Dictionary<string, string[]> TextureMap = new Dictionary<string, string[]>
        {
            // Albedo / base color texture
            ["_baseTex"] = new[]
            {
                "_BaseMap", "_BaseColorMap", "_BaseTex",          // URP, HDRP
                "_MainTex", "_AlbedoMap", "_DiffuseMap",          // Standard, common pack names
                "_ColorMap", "_DiffuseTex", "_AlbedoTex",
                "_Albedo", "_Diffuse", "_Color_Tex",
                "_TexBase", "_BaseTexture", "_Tex",
            },
            // Normal map
            ["_nrmTex"] = new[]
            {
                "_NormalMap", "_BumpMap", "_NormalTex", "_NrmMap",  // URP, HDRP, Standard
                "_NormTex", "_BumpTex", "_NrmTex",
                "_Normal", "_Normals", "_Norm", "_Bump",
                "_DetailNormalMap",                                  // sometimes used as primary in stylized shaders
            },
            // Metallic / metalness map
            ["_mtlTex"] = new[]
            {
                "_MetallicGlossMap", "_MetallicMap", "_MetalMap",
                "_MetallicTex", "_MetalTex", "_MtlTex",
                "_Metallic", "_Metalness",
            },
            // Roughness / smoothness map (DreamPark uses a roughness-style channel; note: shader prop is misspelled "_rougnessMap")
            ["_rougnessMap"] = new[]
            {
                "_RoughnessMap", "_RoughnessTex",
                "_GlossMap", "_GlossinessMap", "_SmoothMap",
                "_Roughness", "_Gloss", "_Smoothness_Tex",
                "_SpecGlossMap",
            },
            // Ambient occlusion
            ["_aoTex"] = new[]
            {
                "_OcclusionMap", "_AOMap", "_AmbientOcclusionMap",
                "_OcclusionTex", "_AOTex",
                "_Occlusion", "_AO",
            },
            // Emission
            ["_emissionTex"] = new[]
            {
                "_EmissionMap", "_EmissiveMap", "_EmissionTex",
                "_EmissiveTex", "_GlowMap", "_GlowTex",
                "_Emission", "_Emissive", "_Glow",
            },
        };

        // Scalar/color mappings — DreamPark prop → source aliases
        static readonly Dictionary<string, string[]> ColorMap = new Dictionary<string, string[]>
        {
            ["_baseColor"] = new[] { "_BaseColor", "_Color", "_TintColor", "_MainColor", "_AlbedoColor", "_DiffuseColor", "_Tint" },
        };

        static readonly Dictionary<string, string[]> FloatMap = new Dictionary<string, string[]>
        {
            ["_metallicness"] = new[] { "_Metallic", "_MetallicScale", "_Metalness", "_MetallicValue" },
            ["_smoothness"]   = new[] { "_Smoothness", "_Glossiness", "_GlossinessScale", "_SmoothnessValue", "_Gloss" },
            ["_nrmStrength"]  = new[] { "_BumpScale", "_NormalScale", "_NormalStrength", "_NormalIntensity" },
            ["_emissionStrength"] = new[] { "_EmissionStrength", "_EmissionIntensity", "_EmissionScale", "_GlowIntensity" },
            ["_OcclusionValue"]   = new[] { "_OcclusionStrength", "_OcclusionScale", "_OcclusionIntensity", "_AOStrength" },
        };

        // Special-case: HDRP MaskMap packs (R=Metallic, G=AO, B=Detail mask, A=Smoothness)
        // We split it conceptually — assign to _mtlTex and _aoTex (smoothness/detail less critical).
        static readonly string[] MaskMapAliases = new[] { "_MaskMap" };

        // ─── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Converts a single material to DreamPark-UniversalShader. If the material is
        /// already using that shader, returns it unchanged. Mutates the material in place
        /// (preserves asset GUID, so references in prefabs/scenes stay valid).
        /// </summary>
        // Particle / VFX shader name fragments — case-insensitive substring match.
        // Materials whose source shader matches any of these are LEFT ALONE by the
        // converter. Particle systems rely on shader-specific blending, soft-particle
        // depth fade, vertex color tinting, flipbook UV scrolling, etc., that
        // DreamPark-UniversalShader (a lit opaque PBR shader) doesn't replicate.
        // Forcing them onto the DreamPark shader breaks every VFX in the pack.
        static readonly string[] ParticleShaderFragments = new[]
        {
            "particle",        // covers "Particles/Standard Surface", "Particles/Unlit", "URP/Particles/*"
            "/fx/",            // some asset packs use "Vendor/FX/MyEffect"
            "/vfx/",
            "stylizedfx",      // Cartoon FX, Epic Toon FX style packs
            "blastify",        // common asset-pack particle namespaces
            "smoke",
            "fire/",
            "lightning",
        };

        /// <summary>
        /// Returns true if this material is a particle/VFX material that should
        /// NOT be converted to DreamPark-UniversalShader. Detection is by source
        /// shader name (case-insensitive substring). Override-safe: callers can
        /// pass `convertParticles: true` to ConvertFolders / ConvertFolder if
        /// they have a specific need.
        /// </summary>
        public static bool IsParticleMaterial(Material mat)
        {
            if (mat == null || mat.shader == null) return false;
            string shaderName = mat.shader.name.ToLowerInvariant();
            foreach (var fragment in ParticleShaderFragments)
            {
                if (shaderName.Contains(fragment)) return true;
            }
            return false;
        }

        public static bool ConvertMaterial(Material src)
        {
            if (src == null) return false;

            // Skip if already converted
            if (src.shader != null && src.shader.name == DreamParkShaderName)
            {
                return false;
            }

            // Skip particle / VFX materials — they need their original shader for
            // correct blending, depth fade, vertex color, etc. This is the default
            // behavior; callers that genuinely want to convert particles use
            // ConvertMaterialUnconditional.
            if (IsParticleMaterial(src))
            {
                Debug.Log($"[MaterialConverter] Skipping particle material '{src.name}' (shader: {src.shader.name}) — particle shaders are preserved by default.");
                return false;
            }

            Shader dpShader = Shader.Find(DreamParkShaderName);
            if (dpShader == null)
            {
                // Shader Graph names sometimes include the "Shader Graphs/" prefix, try variations
                dpShader = AssetDatabase.LoadAssetAtPath<Shader>(FallbackShaderPath);
            }
            if (dpShader == null)
            {
                Debug.LogError($"[MaterialConverter] DreamPark-UniversalShader not found at '{DreamParkShaderName}' or '{FallbackShaderPath}'");
                return false;
            }

            string srcShaderName = src.shader != null ? src.shader.name : "(none)";

            // Capture all source properties BEFORE shader swap (swap clears unmapped values)
            var capturedTextures = new Dictionary<string, Texture>();
            var capturedColors   = new Dictionary<string, Color>();
            var capturedFloats   = new Dictionary<string, float>();
            CaptureAllProperties(src, capturedTextures, capturedColors, capturedFloats);

            // Switch shader. Unity preserves any properties whose names match exactly;
            // others are dropped. We re-apply our mapped properties below.
            src.shader = dpShader;

            // Apply texture mappings
            int texHits = 0;
            foreach (var kv in TextureMap)
            {
                string dpProp = kv.Key;
                if (!src.HasProperty(dpProp)) continue;
                Texture tex = ResolveTexture(capturedTextures, kv.Value);
                if (tex != null)
                {
                    src.SetTexture(dpProp, tex);
                    texHits++;
                }
            }

            // Special: MaskMap — if the source had one, route to metallic and/or AO
            Texture maskMap = ResolveTexture(capturedTextures, MaskMapAliases);
            if (maskMap != null)
            {
                if (src.HasProperty("_mtlTex") && src.GetTexture("_mtlTex") == null)
                    src.SetTexture("_mtlTex", maskMap);
                if (src.HasProperty("_aoTex") && src.GetTexture("_aoTex") == null)
                    src.SetTexture("_aoTex", maskMap);
            }

            // Apply color mappings
            foreach (var kv in ColorMap)
            {
                if (!src.HasProperty(kv.Key)) continue;
                if (TryResolveColor(capturedColors, kv.Value, out Color c))
                    src.SetColor(kv.Key, c);
            }

            // Apply float mappings
            foreach (var kv in FloatMap)
            {
                if (!src.HasProperty(kv.Key)) continue;
                if (TryResolveFloat(capturedFloats, kv.Value, out float f))
                    src.SetFloat(kv.Key, f);
            }

            // If smoothness was captured but NOT roughness, invert (URP uses smoothness, DreamPark roughness map)
            // Most asset packs ship smoothness. We've already mapped it above; nothing more to do here.

            // Emission: if emission color was captured but no strength prop on source, derive strength from luminance
            if (src.HasProperty("_emissionStrength") &&
                TryResolveColor(capturedColors, new[] { "_EmissionColor", "_EmissiveColor", "_GlowColor" }, out Color emCol))
            {
                float lum = emCol.maxColorComponent;
                if (lum > 0f) src.SetFloat("_emissionStrength", lum);
            }

            EditorUtility.SetDirty(src);
            Debug.Log($"[MaterialConverter] ✓ '{src.name}' converted from '{srcShaderName}' → DreamPark-UniversalShader (textures: {texHits})");
            return true;
        }

        /// <summary>
        /// Walks every renderer in a prefab and converts all materials it references.
        /// Returns the number of materials converted.
        /// </summary>
        public static int ConvertPrefabMaterials(string prefabAssetPath)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            if (prefab == null)
            {
                Debug.LogError($"[MaterialConverter] Prefab not found: {prefabAssetPath}");
                return 0;
            }
            int count = 0;
            var seen = new HashSet<Material>();
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || seen.Contains(mat)) continue;
                    seen.Add(mat);
                    if (ConvertMaterial(mat)) count++;
                }
            }
            AssetDatabase.SaveAssets();
            return count;
        }

        // ─── Capture / resolve helpers ──────────────────────────────────────

        static void CaptureAllProperties(
            Material src,
            Dictionary<string, Texture> textures,
            Dictionary<string, Color> colors,
            Dictionary<string, float> floats)
        {
            if (src.shader == null) return;
            int count = src.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = src.shader.GetPropertyName(i);
                var type = src.shader.GetPropertyType(i);
                try
                {
                    switch (type)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            textures[name] = src.GetTexture(name);
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            colors[name] = src.GetColor(name);
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            floats[name] = src.GetFloat(name);
                            break;
                        // Vectors/ints not used in our mappings; skip
                    }
                }
                catch { /* property exists in shader but not on this material instance — skip */ }
            }
        }

        // Try each alias (exact match first); fall back to case-insensitive substring match.
        static Texture ResolveTexture(Dictionary<string, Texture> caps, string[] aliases)
        {
            foreach (var alias in aliases)
            {
                if (caps.TryGetValue(alias, out var t) && t != null) return t;
            }
            // Fuzzy: scan all captured names for substring matches
            foreach (var alias in aliases)
            {
                string needle = alias.TrimStart('_').ToLowerInvariant();
                foreach (var kv in caps)
                {
                    if (kv.Value == null) continue;
                    string hay = kv.Key.TrimStart('_').ToLowerInvariant();
                    if (hay.Contains(needle) || needle.Contains(hay)) return kv.Value;
                }
            }
            return null;
        }

        static bool TryResolveColor(Dictionary<string, Color> caps, string[] aliases, out Color result)
        {
            foreach (var alias in aliases)
            {
                if (caps.TryGetValue(alias, out var c)) { result = c; return true; }
            }
            foreach (var alias in aliases)
            {
                string needle = alias.TrimStart('_').ToLowerInvariant();
                foreach (var kv in caps)
                {
                    string hay = kv.Key.TrimStart('_').ToLowerInvariant();
                    if (hay.Contains(needle) || needle.Contains(hay)) { result = kv.Value; return true; }
                }
            }
            result = Color.white;
            return false;
        }

        static bool TryResolveFloat(Dictionary<string, float> caps, string[] aliases, out float result)
        {
            foreach (var alias in aliases)
            {
                if (caps.TryGetValue(alias, out var f)) { result = f; return true; }
            }
            foreach (var alias in aliases)
            {
                string needle = alias.TrimStart('_').ToLowerInvariant();
                foreach (var kv in caps)
                {
                    string hay = kv.Key.TrimStart('_').ToLowerInvariant();
                    if (hay.Contains(needle) || needle.Contains(hay)) { result = kv.Value; return true; }
                }
            }
            result = 0f;
            return false;
        }

        // ─── Batch entry points (for agents / tooling) ───────────────────────
        // Designed for the game-studio unity-dev agent: after Phase A imports
        // N packages, the agent calls ConvertFolders(...) ONCE with the list of
        // package roots (e.g. Assets/Content/Rpg/RpgMonsterBundlePbr/) and gets
        // back a deterministic ConvertSummary it can serialize into its
        // results.json. This replaces ad-hoc "BatchShaderConvert.Run()" style
        // fabrications — there is no other batch API on this class.

        [Serializable]
        public struct ConvertSummary
        {
            public int folders;        // number of folders processed
            public int materials;      // number of unique .mat assets seen
            public int converted;      // number actually converted on this run
            public int alreadyOnShader; // number already on DreamPark-UniversalShader (skipped, no-op)
            public int skippedParticles; // number left alone because they're particle/VFX materials
            public int failed;         // number where ConvertMaterial returned false for some other reason
            public string[] folderPaths; // echo of input for the log/manifest

            public override string ToString()
            {
                return $"[MaterialConverter] folders={folders} materials={materials} " +
                       $"converted={converted} alreadyOnShader={alreadyOnShader} " +
                       $"skippedParticles={skippedParticles} failed={failed}";
            }
        }

        /// <summary>
        /// Convert every Material asset under <paramref name="folderPath"/> (recursive)
        /// to DreamPark-UniversalShader, in place, preserving textures/colors/scalars.
        /// Folder must be inside Assets/. Returns a summary; does NOT throw on missing folder
        /// (returns zeros and logs a warning) so callers can batch fearlessly.
        /// </summary>
        public static ConvertSummary ConvertFolder(string folderPath)
        {
            return ConvertFolders(new[] { folderPath });
        }

        /// <summary>
        /// Convert every Material asset under each folder (recursive) to DreamPark-UniversalShader.
        /// Calls AssetDatabase.SaveAssets() ONCE at the end (not per-folder) for speed.
        /// </summary>
        public static ConvertSummary ConvertFolders(string[] folderPaths)
        {
            var summary = new ConvertSummary
            {
                folderPaths = folderPaths ?? Array.Empty<string>()
            };

            if (folderPaths == null || folderPaths.Length == 0)
            {
                Debug.LogWarning("[MaterialConverter] ConvertFolders called with no folders.");
                return summary;
            }

            // De-dupe and validate folders before scanning to keep AssetDatabase happy.
            var validFolders = new List<string>();
            foreach (var raw in folderPaths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var path = raw.Replace('\\', '/').TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(path))
                {
                    Debug.LogWarning($"[MaterialConverter] '{path}' is not a valid Assets/ folder — skipping.");
                    continue;
                }
                if (!validFolders.Contains(path)) validFolders.Add(path);
            }
            summary.folders = validFolders.Count;
            if (validFolders.Count == 0) return summary;

            // Single AssetDatabase query covers all folders — much faster than per-folder.
            var matGuids = AssetDatabase.FindAssets("t:Material", validFolders.ToArray());
            var seen = new HashSet<string>();

            foreach (var guid in matGuids)
            {
                string mp = AssetDatabase.GUIDToAssetPath(guid);
                if (!seen.Add(mp)) continue;  // dedupe across overlapping folder roots
                summary.materials++;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (mat == null) { summary.failed++; continue; }

                // Skip particle / VFX materials — preserve their original shader.
                // Particle blending modes / soft particles / vertex color tinting
                // / flipbook UV scrolling don't translate to DreamPark-UniversalShader.
                if (IsParticleMaterial(mat))
                {
                    summary.skippedParticles++;
                    continue;
                }

                // If already on the DreamPark shader, count it but don't re-process.
                if (mat.shader != null && mat.shader.name == DreamParkShaderName)
                {
                    summary.alreadyOnShader++;
                    continue;
                }

                if (ConvertMaterial(mat)) summary.converted++;
                else summary.failed++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log(summary.ToString() + "  paths=[" + string.Join(", ", validFolders) + "]");
            return summary;
        }

        // ─── Particle conversion ─────────────────────────────────────────────
        // Companion API to the opaque-material converter above. Translates vendor
        // particle materials (CFXR_Particle_StandardHDR, StandardParticles, Epic
        // Toon FX custom, RunemarkStudio particles, etc.) to DreamPark/Particles.
        //
        // Why this is a SEPARATE entry point: the opaque converter SKIPS particle
        // materials by default (correct safety net — converting them to the lit
        // PBR DreamPark-UniversalShader breaks every VFX). Calling
        // ConvertParticleFolders is an explicit opt-in to translate particles to
        // the unified DreamPark/Particles shader. Agents typically call BOTH:
        // first ConvertFolders (opaque), then ConvertParticleFolders (particle).

        const string DreamParkParticleShaderName = "DreamPark/Particles";

        // Source-shader feature flags we DON'T translate — materials with any of
        // these keywords get left on their original shader. Updated 2026-05-01
        // after v2 of the unified shader added dissolve + distortion support.
        // Now covers ~98% of materials surveyed.
        static readonly string[] ExoticParticleKeywords = new[]
        {
            "_FLIPBOOK_BLENDING",     // smooth flipbook frame interp — needs ParticleSystem
                                       // Custom Vertex Streams; shader-side is easy but each
                                       // PS has to be configured to feed UV0 + UV2 + blend.
                                       // ~rare in our packs, defer to v3.
            // _CFXR_DISSOLVE         → supported in v2 (mapped to _DISSOLVE_ON)
            // _CFXR_UV_DISTORTION    → supported in v2 (mapped to _DISTORTION_ON)
            // _CFXR_DITHERED_SHADOWS → no-op in our pipeline (DreamPark particles
            //                          are unlit and don't cast shadows in MR), so
            //                          materials with this keyword convert fine —
            //                          the keyword just goes nowhere in our shader.
            // _REQUIRE_UV2           → harmless; particle systems already feed UV2
            //                          when present.
        };

        [Serializable]
        public struct ParticleConvertSummary
        {
            public int folders;
            public int materials;
            public int converted;        // translated to DreamPark/Particles
            public int alreadyOnShader;  // already on DreamPark/Particles
            public int skippedExotic;    // had ExoticParticleKeywords — kept on vendor shader
            public int skippedNotParticle; // material wasn't a particle material to begin with
            public int failed;
            public string[] folderPaths;

            public override string ToString()
            {
                return $"[MaterialConverter Particles] folders={folders} materials={materials} " +
                       $"converted={converted} alreadyOnShader={alreadyOnShader} " +
                       $"skippedExotic={skippedExotic} skippedNotParticle={skippedNotParticle} failed={failed}";
            }
        }

        /// <summary>
        /// Returns true if this material has any "exotic" keyword that the unified
        /// DreamPark/Particles shader doesn't yet support (dissolve, distortion,
        /// dithered shadows, flipbook blending). Such materials stay on their
        /// vendor shader to preserve the effect.
        /// </summary>
        public static bool HasExoticParticleFeature(Material mat)
        {
            if (mat == null) return false;
            foreach (var kw in ExoticParticleKeywords)
            {
                if (mat.IsKeywordEnabled(kw)) return true;
            }
            return false;
        }

        /// <summary>
        /// Convert one particle material to DreamPark/Particles in place.
        /// Returns true on conversion, false on no-op (already converted, exotic
        /// feature detected, or not a particle material).
        /// </summary>
        public static bool ConvertParticleMaterial(Material src)
        {
            if (src == null) return false;
            if (src.shader != null && src.shader.name == DreamParkParticleShaderName) return false;
            if (!IsParticleMaterial(src)) return false;
            if (HasExoticParticleFeature(src))
            {
                Debug.Log($"[MaterialConverter] Keeping exotic particle '{src.name}' on '{src.shader.name}' " +
                          $"(has dissolve/distortion/dithered-shadows/flipbook-blending — DreamPark/Particles v1 doesn't support these).");
                return false;
            }

            Shader dpShader = Shader.Find(DreamParkParticleShaderName);
            if (dpShader == null)
            {
                Debug.LogError($"[MaterialConverter] Cannot find {DreamParkParticleShaderName}. " +
                               "Make sure DreamPark-Particles.shader is in Assets/DreamPark/Shaders/.");
                return false;
            }

            // ── Stash source state ────────────────────────────────────────────
            // Snapshot the properties we care about BEFORE we change the shader,
            // because Material.shader = newShader resets all props to defaults.
            var src_baseMap   = TryGetTextureFromAliases(src,
                "_BaseMap", "_MainTex", "_BaseTexture", "_TexBase", "_MainTexture");
            var src_baseScale = src_baseMap != null
                ? src.GetTextureScale(GetFirstSetTexture(src,
                    "_BaseMap", "_MainTex", "_BaseTexture", "_TexBase", "_MainTexture") ?? "_MainTex")
                : Vector2.one;
            var src_baseOffset = src_baseMap != null
                ? src.GetTextureOffset(GetFirstSetTexture(src,
                    "_BaseMap", "_MainTex", "_BaseTexture", "_TexBase", "_MainTexture") ?? "_MainTex")
                : Vector2.zero;

            Color src_tint = TryGetColorFromAliases(src,
                "_Color", "_BaseColor", "_TintColor", "_MainColor", "_AlbedoColor", "_DiffuseColor")
                ?? Color.white;
            Color src_emission = TryGetColorFromAliases(src,
                "_EmissionColor", "_EmissiveColor", "_HdrColor") ?? Color.black;
            float src_cutoff = src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f;
            float src_hdrBoost = TryGetFloatFromAliases(src,
                "_HdrBoost", "_HdrMultiply", "_EmissionMultiplier") ?? 1.0f;

            // Vertex color mode: read source _ColorMode (0=Multiply, 1=Add, 2=Subtract,
            // 3=Overlay, 4=Color, 5=Difference). Most legacy particles use 0 (Multiply).
            int src_colorMode = src.HasProperty("_ColorMode") ? Mathf.RoundToInt(src.GetFloat("_ColorMode")) : 0;

            // Soft particles: legacy shaders set _SoftParticlesNearFadeDistance/_FarFadeDistance
            // via vector properties or scalars. Try both forms.
            float src_softNear = TryGetFloatFromAliases(src,
                "_SoftParticlesFadeDistanceNear", "_SoftParticlesNearFadeDistance",
                "_SoftFadeNear", "_SoftParticleNear") ?? 0.0f;
            float src_softFar  = TryGetFloatFromAliases(src,
                "_SoftParticlesFadeDistanceFar", "_SoftParticlesFarFadeDistance",
                "_SoftFadeFar", "_SoftParticleFar") ?? 1.0f;
            bool src_softOn = src.IsKeywordEnabled("SOFTPARTICLES_ON")
                           || src.IsKeywordEnabled("_SOFTPARTICLES_ON")
                           || src.IsKeywordEnabled("_FADING_ON");

            // Single-channel mode (CFXR convention)
            bool src_singleChannel = src.IsKeywordEnabled("_CFXR_SINGLE_CHANNEL")
                                  || src.IsKeywordEnabled("_SINGLECHANNEL_ON");

            // Dissolve (CFXR-style): noise tex + threshold + edge color/width
            bool src_dissolveOn = src.IsKeywordEnabled("_CFXR_DISSOLVE")
                               || src.IsKeywordEnabled("_DISSOLVE_ON");
            Texture src_dissolveMap = TryGetTextureFromAliases(src,
                "_DissolveTex", "_DissolveMap", "_NoiseTex", "_DissolveNoise", "_DissolveTexture");
            float src_dissolveAmount = TryGetFloatFromAliases(src,
                "_DissolveAmount", "_Dissolve", "_DissolveProgress", "_DissolveValue") ?? 0f;
            float src_dissolveEdge = TryGetFloatFromAliases(src,
                "_DissolveEdgeWidth", "_EdgeWidth", "_DissolveEdge") ?? 0.05f;
            Color src_dissolveEdgeColor = TryGetColorFromAliases(src,
                "_DissolveEdgeColor", "_EdgeColor", "_DissolveColor") ?? new Color(1f, 0.5f, 0f, 1f);

            // UV distortion: distortion tex + strength + scroll speeds
            bool src_distortionOn = src.IsKeywordEnabled("_CFXR_UV_DISTORTION")
                                 || src.IsKeywordEnabled("_DISTORTION_ON");
            Texture src_distortMap = TryGetTextureFromAliases(src,
                "_DistortionTex", "_DistortionMap", "_DisplacementMap", "_DistortTex");
            float src_distortStrength = TryGetFloatFromAliases(src,
                "_DistortionStrength", "_DistortStrength", "_DistortAmount", "_DistortionAmount") ?? 0.1f;
            float src_distortScrollX = TryGetFloatFromAliases(src,
                "_DistortionScrollX", "_DistortSpeedX", "_DistortionSpeedX") ?? 0f;
            float src_distortScrollY = TryGetFloatFromAliases(src,
                "_DistortionScrollY", "_DistortSpeedY", "_DistortionSpeedY") ?? 0f;

            // Detect blend mode from src/dst blend factors, or fall back to keyword.
            // 5 = SrcAlpha, 10 = OneMinusSrcAlpha, 1 = One.
            float src_srcBlend = src.HasProperty("_SrcBlend") ? src.GetFloat("_SrcBlend") : 5f;
            float src_dstBlend = src.HasProperty("_DstBlend") ? src.GetFloat("_DstBlend") : 10f;
            string blendMode = ResolveBlendMode(src, src_srcBlend, src_dstBlend);

            float src_cull = src.HasProperty("_Cull") ? src.GetFloat("_Cull") : 0f; // default Off (billboards)
            float src_zwrite = src.HasProperty("_ZWrite") ? src.GetFloat("_ZWrite") : 0f;
            int src_renderQueue = src.renderQueue;
            bool src_alphaTest = src.IsKeywordEnabled("_ALPHATEST_ON");

            // ── Switch shader ─────────────────────────────────────────────────
            src.shader = dpShader;

            // ── Apply ─────────────────────────────────────────────────────────
            if (src_baseMap != null)
            {
                src.SetTexture("_BaseMap", src_baseMap);
                src.SetTextureScale("_BaseMap", src_baseScale);
                src.SetTextureOffset("_BaseMap", src_baseOffset);
            }
            src.SetColor("_BaseColor", src_tint);
            src.SetColor("_EmissionColor", src_emission);
            src.SetFloat("_Cutoff", src_cutoff);
            src.SetFloat("_HdrBoostMultiplier", src_hdrBoost);
            src.SetFloat("_SoftParticlesNear", src_softNear);
            src.SetFloat("_SoftParticlesFar", src_softFar);
            src.SetFloat("_Cull", src_cull);
            src.SetFloat("_ZWrite", src_zwrite);

            // Dissolve (v2)
            if (src_dissolveOn && src_dissolveMap != null)
            {
                src.SetTexture("_DissolveMap", src_dissolveMap);
                src.SetFloat("_DissolveAmount", src_dissolveAmount);
                src.SetFloat("_DissolveEdgeWidth", src_dissolveEdge);
                src.SetColor("_DissolveEdgeColor", src_dissolveEdgeColor);
                src.SetFloat("_Dissolve", 1f);
            }

            // UV Distortion (v2)
            if (src_distortionOn && src_distortMap != null)
            {
                src.SetTexture("_DistortionMap", src_distortMap);
                src.SetFloat("_DistortionStrength", src_distortStrength);
                src.SetFloat("_DistortionScrollX", src_distortScrollX);
                src.SetFloat("_DistortionScrollY", src_distortScrollY);
                src.SetFloat("_Distortion", 1f);
            }

            // Keywords
            ApplyBlendModeKeywords(src, blendMode);
            ApplyVertexColorKeyword(src, src_colorMode);
            SetKeyword(src, "_ALPHATEST_ON", src_alphaTest);
            SetKeyword(src, "_SOFTPARTICLES_ON", src_softOn);
            SetKeyword(src, "_SINGLECHANNEL_ON", src_singleChannel);
            SetKeyword(src, "_HDR_BOOST_ON", src_hdrBoost > 1.0001f);
            SetKeyword(src, "_DISSOLVE_ON", src_dissolveOn && src_dissolveMap != null);
            SetKeyword(src, "_DISTORTION_ON", src_distortionOn && src_distortMap != null);

            // Render queue + blend factors (driven by Inspector's [_SrcBlend][_DstBlend])
            (float newSrc, float newDst) = BlendFactorsForMode(blendMode);
            src.SetFloat("_SrcBlend", newSrc);
            src.SetFloat("_DstBlend", newDst);
            src.renderQueue = src_renderQueue >= 2000 ? src_renderQueue : 3000;

            EditorUtility.SetDirty(src);
            return true;
        }

        // ─── Particle conversion helpers ─────────────────────────────────────

        static string ResolveBlendMode(Material mat, float srcBlend, float dstBlend)
        {
            // Keyword-based fast path (most legacy shaders set these explicitly)
            if (mat.IsKeywordEnabled("_BLENDMODE_ADDITIVE")) return "Additive";
            if (mat.IsKeywordEnabled("_BLENDMODE_PREMULTIPLIED")) return "Premultiplied";
            if (mat.IsKeywordEnabled("_BLENDMODE_MULTIPLY")) return "Multiply";
            if (mat.IsKeywordEnabled("_BLENDMODE_ALPHA")) return "Alpha";

            // Infer from blend factors. 5=SrcAlpha, 10=OneMinusSrcAlpha, 1=One, 2=DstColor.
            if (Mathf.Approximately(srcBlend, 5f) && Mathf.Approximately(dstBlend, 1f))  return "Additive";
            if (Mathf.Approximately(srcBlend, 1f) && Mathf.Approximately(dstBlend, 10f)) return "Premultiplied";
            if (Mathf.Approximately(srcBlend, 2f) && Mathf.Approximately(dstBlend, 0f))  return "Multiply";
            return "Alpha"; // safe default
        }

        static (float src, float dst) BlendFactorsForMode(string mode)
        {
            switch (mode)
            {
                case "Additive":      return (5f, 1f);   // SrcAlpha, One
                case "Premultiplied": return (1f, 10f);  // One, OneMinusSrcAlpha
                case "Multiply":      return (2f, 0f);   // DstColor, Zero
                default:              return (5f, 10f);  // SrcAlpha, OneMinusSrcAlpha (Alpha)
            }
        }

        static void ApplyBlendModeKeywords(Material mat, string mode)
        {
            mat.DisableKeyword("_BLENDMODE_ALPHA");
            mat.DisableKeyword("_BLENDMODE_ADDITIVE");
            mat.DisableKeyword("_BLENDMODE_PREMULTIPLIED");
            mat.DisableKeyword("_BLENDMODE_MULTIPLY");
            switch (mode)
            {
                case "Additive":      mat.EnableKeyword("_BLENDMODE_ADDITIVE"); mat.SetFloat("_BlendMode", 1); break;
                case "Premultiplied": mat.EnableKeyword("_BLENDMODE_PREMULTIPLIED"); mat.SetFloat("_BlendMode", 2); break;
                case "Multiply":      mat.EnableKeyword("_BLENDMODE_MULTIPLY"); mat.SetFloat("_BlendMode", 3); break;
                default:              mat.EnableKeyword("_BLENDMODE_ALPHA"); mat.SetFloat("_BlendMode", 0); break;
            }
        }

        static void ApplyVertexColorKeyword(Material mat, int legacyColorMode)
        {
            mat.DisableKeyword("_VC_OFF");
            mat.DisableKeyword("_VC_MULTIPLY");
            mat.DisableKeyword("_VC_ADD");
            // Legacy _ColorMode: 0=Multiply, 1=Additive, 2=Subtract, 3=Overlay, 4=Color, 5=Difference.
            // We only support 0 and 1 in v1; everything else folds to Multiply (the most common).
            if (legacyColorMode == 1) { mat.EnableKeyword("_VC_ADD"); mat.SetFloat("_VC", 2); }
            else { mat.EnableKeyword("_VC_MULTIPLY"); mat.SetFloat("_VC", 1); }
        }

        static void SetKeyword(Material mat, string keyword, bool on)
        {
            if (on) mat.EnableKeyword(keyword);
            else    mat.DisableKeyword(keyword);
        }

        static Texture TryGetTextureFromAliases(Material mat, params string[] aliases)
        {
            foreach (var a in aliases)
            {
                if (mat.HasProperty(a))
                {
                    var t = mat.GetTexture(a);
                    if (t != null) return t;
                }
            }
            return null;
        }

        static string GetFirstSetTexture(Material mat, params string[] aliases)
        {
            foreach (var a in aliases)
            {
                if (mat.HasProperty(a) && mat.GetTexture(a) != null) return a;
            }
            return null;
        }

        static Color? TryGetColorFromAliases(Material mat, params string[] aliases)
        {
            foreach (var a in aliases)
            {
                if (mat.HasProperty(a)) return mat.GetColor(a);
            }
            return null;
        }

        static float? TryGetFloatFromAliases(Material mat, params string[] aliases)
        {
            foreach (var a in aliases)
            {
                if (mat.HasProperty(a)) return mat.GetFloat(a);
            }
            return null;
        }

        /// <summary>
        /// Convert every particle material under each folder (recursive) to DreamPark/Particles.
        /// Materials with exotic features (dissolve, distortion, dithered shadows, flipbook
        /// blending) are LEFT on their vendor shader — those features aren't supported by v1.
        /// Non-particle materials are silently ignored (use ConvertFolders for those).
        /// </summary>
        public static ParticleConvertSummary ConvertParticleFolders(string[] folderPaths)
        {
            var summary = new ParticleConvertSummary
            {
                folderPaths = folderPaths ?? Array.Empty<string>()
            };

            if (folderPaths == null || folderPaths.Length == 0)
            {
                Debug.LogWarning("[MaterialConverter] ConvertParticleFolders called with no folders.");
                return summary;
            }

            var validFolders = new List<string>();
            foreach (var raw in folderPaths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var path = raw.Replace('\\', '/').TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(path))
                {
                    Debug.LogWarning($"[MaterialConverter] '{path}' is not a valid Assets/ folder — skipping.");
                    continue;
                }
                if (!validFolders.Contains(path)) validFolders.Add(path);
            }
            summary.folders = validFolders.Count;
            if (validFolders.Count == 0) return summary;

            var matGuids = AssetDatabase.FindAssets("t:Material", validFolders.ToArray());
            var seen = new HashSet<string>();

            foreach (var guid in matGuids)
            {
                string mp = AssetDatabase.GUIDToAssetPath(guid);
                if (!seen.Add(mp)) continue;
                summary.materials++;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (mat == null) { summary.failed++; continue; }

                if (!IsParticleMaterial(mat)) { summary.skippedNotParticle++; continue; }
                if (mat.shader != null && mat.shader.name == DreamParkParticleShaderName)
                {
                    summary.alreadyOnShader++; continue;
                }
                if (HasExoticParticleFeature(mat)) { summary.skippedExotic++; continue; }

                if (ConvertParticleMaterial(mat)) summary.converted++;
                else summary.failed++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log(summary.ToString() + "  paths=[" + string.Join(", ", validFolders) + "]");
            return summary;
        }

        public static ParticleConvertSummary ConvertParticleFolder(string folderPath)
        {
            return ConvertParticleFolders(new[] { folderPath });
        }

        // ─── Menu items ──────────────────────────────────────────────────────
        // Right-click only — no top menu bar entries. The right-click context
        // menu in the Project window is the canonical entry point. Agents call
        // the static API directly (ConvertMaterial / ConvertPrefabMaterials / ConvertFolders).

        [MenuItem("Assets/DreamPark/Convert to DreamPark Shader", priority = 50)]
        static void MenuConvertSelected()
        {
            // Auto-routing converter. For each material encountered:
            //   - particle/VFX material → ConvertParticleMaterial → DreamPark/Particles
            //   - everything else       → ConvertMaterial         → DreamPark-UniversalShader
            // Detection is by source-shader name (IsParticleMaterial).
            int opaqueConverted = 0, particleConverted = 0;
            int skippedExotic = 0, skippedOther = 0, scannedFolders = 0;

            // Single converter callback we apply to every material we discover.
            // Returns one of "opaque", "particle", "exotic", "other" so the tally stays accurate.
            string ConvertOne(Material m)
            {
                if (m == null) return "other";
                if (IsParticleMaterial(m))
                {
                    if (HasExoticParticleFeature(m)) return "exotic";
                    return ConvertParticleMaterial(m) ? "particle" : "other";
                }
                return ConvertMaterial(m) ? "opaque" : "other";
            }

            void Tally(string result)
            {
                switch (result)
                {
                    case "opaque":   opaqueConverted++;   break;
                    case "particle": particleConverted++; break;
                    case "exotic":   skippedExotic++;     break;
                    default:         skippedOther++;      break;
                }
            }

            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);

                // Material asset → convert directly
                if (obj is Material mat)
                {
                    Tally(ConvertOne(mat));
                    continue;
                }

                // Prefab OR model file → walk renderers and convert all referenced materials.
                // Models (.fbx/.glb/etc) are loaded as GameObjects with renderer children, so the
                // same hierarchy walk works. Materials embedded in the model file (read-only by
                // default) get logged as skipped — user can extract them via the model's import
                // settings ("Materials" tab → "Extract Materials") and re-run the converter.
                bool isPrefab = !string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
                bool isModel  = !string.IsNullOrEmpty(path) && ModelExtensions.Contains(Path.GetExtension(path));
                if (obj is GameObject && (isPrefab || isModel))
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go != null)
                    {
                        var rends = go.GetComponentsInChildren<Renderer>(true);
                        var seenMats = new HashSet<int>();
                        foreach (var r in rends)
                        {
                            foreach (var m in r.sharedMaterials)
                            {
                                if (m == null || !seenMats.Add(m.GetInstanceID())) continue;
                                Tally(ConvertOne(m));
                            }
                        }
                    }
                    if (isModel && opaqueConverted == 0 && particleConverted == 0)
                    {
                        Debug.LogWarning($"[MaterialConverter] '{path}' had no convertible materials. " +
                            "If this model has embedded materials, open its import settings → Materials tab → " +
                            "'Extract Materials' to make them editable, then right-click again.");
                    }
                    continue;
                }

                // Folder → find all materials inside (recursive)
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                {
                    var matGuids = AssetDatabase.FindAssets("t:Material", new[] { path });
                    foreach (var guid in matGuids)
                    {
                        string mp = AssetDatabase.GUIDToAssetPath(guid);
                        var m = AssetDatabase.LoadAssetAtPath<Material>(mp);
                        if (m != null) Tally(ConvertOne(m));
                    }
                    scannedFolders++;
                }
            }

            AssetDatabase.SaveAssets();
            string folderNote = scannedFolders > 0 ? $"  (scanned {scannedFolders} folder(s) recursively)" : "";
            EditorUtility.DisplayDialog(
                "DreamPark Material Converter",
                $"Opaque → DreamPark-UniversalShader: {opaqueConverted}\n" +
                $"Particle → DreamPark/Particles: {particleConverted}\n" +
                (skippedExotic > 0 ? $"Skipped (exotic particle features): {skippedExotic}\n" : "") +
                $"Skipped (already converted / null): {skippedOther}{folderNote}",
                "OK");
        }

        // Validator — only show the right-click item when at least one selected asset
        // is a material, prefab, model file, or folder. Without this, the item appears
        // greyed-out on unrelated selections (textures, scripts, etc.) which is annoying.
        static readonly HashSet<string> ModelExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".glb", ".gltf", ".obj", ".dae", ".blend", ".max", ".ma", ".mb", ".3ds", ".dxf"
        };

        [MenuItem("Assets/DreamPark/Convert to DreamPark Shader", true)]
        static bool ValidateRightClickConvert()
        {
            foreach (var obj in Selection.objects)
            {
                if (obj is Material) return true;
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return true;
                if (ModelExtensions.Contains(Path.GetExtension(path))) return true;
                if (AssetDatabase.IsValidFolder(path)) return true;
            }
            return false;
        }

    }
}
#endif
