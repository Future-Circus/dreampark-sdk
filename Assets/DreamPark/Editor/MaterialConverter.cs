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

        // Unlit variant — used by the MaterialConverter Tool window for materials
        // whose source shader is obviously flat-shaded (URP/Unlit, Mobile/Diffuse,
        // toon shaders, UI/sprite materials). Same property naming convention as
        // DreamPark-UniversalShader (`_baseTex`, `_baseColor`, `_emissionTex`,
        // `_emissionStrength`) so the same mapping tables work; we just skip the
        // PBR-specific property writes (metallic, roughness, AO, normal).
        const string DreamParkUnlitShaderName = "Shader Graphs/DreamPark-Unlit";
        const string DreamParkUnlitFallbackPath = "Assets/DreamPark/Shaders/DreamPark-Unlit.shadergraph";

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
        /// Convert a material to DreamPark-Unlit in place. Same property-mapping
        /// approach as ConvertMaterial but only writes the subset that exists on
        /// the unlit shader (base color, base texture, emission color/texture,
        /// emission strength). Source PBR data (metallic, roughness, AO, normal)
        /// is captured for the log but not written.
        ///
        /// Use when the source material is clearly flat-shaded — vendor UI
        /// shaders, toon/diffuse-only shaders, sprite materials, skybox/decal
        /// shaders. The right-click converter doesn't auto-route here (it always
        /// targets DreamPark-UniversalShader for opaque materials); the
        /// Material Converter window's planner is the canonical caller.
        /// </summary>
        public static bool ConvertMaterialToUnlit(Material src)
        {
            if (src == null) return false;
            if (src.shader != null && src.shader.name == DreamParkUnlitShaderName) return false;

            // Particles never route to Unlit — they have their own pipeline.
            if (IsParticleMaterial(src))
            {
                Debug.LogWarning($"[MaterialConverter] '{src.name}' is a particle material — use ConvertParticleMaterial instead.");
                return false;
            }

            Shader dpUnlit = Shader.Find(DreamParkUnlitShaderName);
            if (dpUnlit == null)
                dpUnlit = AssetDatabase.LoadAssetAtPath<Shader>(DreamParkUnlitFallbackPath);
            if (dpUnlit == null)
            {
                Debug.LogError($"[MaterialConverter] DreamPark-Unlit shader not found at '{DreamParkUnlitShaderName}' or '{DreamParkUnlitFallbackPath}'");
                return false;
            }

            string srcShaderName = src.shader != null ? src.shader.name : "(none)";

            // Capture everything before the shader swap (Unity drops unmapped props).
            var capturedTextures = new Dictionary<string, Texture>();
            var capturedColors   = new Dictionary<string, Color>();
            var capturedFloats   = new Dictionary<string, float>();
            CaptureAllProperties(src, capturedTextures, capturedColors, capturedFloats);

            src.shader = dpUnlit;

            // Base albedo / color texture — same alias list as the lit path.
            if (src.HasProperty("_baseTex"))
            {
                Texture baseTex = ResolveTexture(capturedTextures, TextureMap["_baseTex"]);
                if (baseTex != null) src.SetTexture("_baseTex", baseTex);
            }

            // Tint / base color.
            if (src.HasProperty("_baseColor")
                && TryResolveColor(capturedColors, ColorMap["_baseColor"], out Color tint))
            {
                src.SetColor("_baseColor", tint);
            }

            // Emission texture + strength, when the unlit shader supports them.
            // Most unlit materials don't write emission separately (the base color
            // IS the emission for unlit), but we copy the data through anyway in
            // case the shader graph exposes both.
            if (src.HasProperty("_emissionTex"))
            {
                Texture emTex = ResolveTexture(capturedTextures, TextureMap["_emissionTex"]);
                if (emTex != null) src.SetTexture("_emissionTex", emTex);
            }
            if (src.HasProperty("_emissionStrength")
                && TryResolveColor(capturedColors, new[] { "_EmissionColor", "_EmissiveColor", "_GlowColor" }, out Color emCol))
            {
                float lum = emCol.maxColorComponent;
                if (lum > 0f) src.SetFloat("_emissionStrength", lum);
            }

            EditorUtility.SetDirty(src);
            Debug.Log($"[MaterialConverter] ✓ '{src.name}' converted from '{srcShaderName}' → DreamPark-Unlit");
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
        // these keywords get left on their original shader.
        //
        // History (what used to live here, all promoted out):
        //   _FLIPBOOK_BLENDING     → now Approximated. The post-convert
        //                             pass in MaterialConverterExecutor
        //                             auto-enables Unity's TextureSheetAnimation
        //                             module so the flipbook plays
        //                             frame-by-frame (not motion-vector
        //                             smooth, but it plays). Diff marks
        //                             _MotionVector / _FLIPBOOKBLENDING_ON
        //                             as Approximated severity.
        //   _CFXR_DISSOLVE         → supported (mapped to _DISSOLVE_ON)
        //   _CFXR_UV_DISTORTION    → supported (mapped to _DISTORTION_ON)
        //   _CFXR_DITHERED_SHADOWS → no-op in our pipeline (DreamPark particles
        //                             are unlit and don't cast shadows in MR),
        //                             so materials with this keyword convert
        //                             fine — the keyword just goes nowhere in
        //                             our shader.
        //   _REQUIRE_UV2           → harmless; particle systems already feed
        //                             UV2 when present.
        //
        // The list is currently empty. Kept as a hook for future genuinely-
        // exotic features that we can't approximate (e.g. screen-space
        // refraction, gradient remapping LUTs, full PBR particles).
        static readonly string[] ExoticParticleKeywords = System.Array.Empty<string>();

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
            // NOTE: we deliberately do NOT gate on IsParticleMaterial(src) here.
            // The planner is the authoritative classifier — it can promote a
            // material to ConvertParticle either because the source shader's
            // name matches a particle naming convention OR because the
            // material is attached to a ParticleSystemRenderer in some
            // prefab (the latter catches vendor packs like Hovl Studio's
            // HS_Explosion whose shader names don't include "particle").
            // Trust the caller. Direct API callers (right-click handler,
            // batch entry points) do their own IsParticleMaterial gating
            // before invoking this method.
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
                "_BaseMap", "_MainTex", "_BaseTexture", "_TexBase", "_MainTexture",
                // Standard / vendor renames for the same albedo texture.
                "_Albedo", "_AlbedoMap", "_AlbedoTex",
                "_Diffuse", "_DiffuseMap", "_DiffuseTex",
                "_BaseColorMap", "_ColorMap", "_Tex");
            var src_baseScale = src_baseMap != null
                ? src.GetTextureScale(GetFirstSetTexture(src,
                    "_BaseMap", "_MainTex", "_BaseTexture", "_TexBase", "_MainTexture",
                    "_Albedo", "_AlbedoMap", "_AlbedoTex",
                    "_Diffuse", "_DiffuseMap", "_DiffuseTex",
                    "_BaseColorMap", "_ColorMap", "_Tex") ?? "_MainTex")
                : Vector2.one;
            var src_baseOffset = src_baseMap != null
                ? src.GetTextureOffset(GetFirstSetTexture(src,
                    "_BaseMap", "_MainTex", "_BaseTexture", "_TexBase", "_MainTexture",
                    "_Albedo", "_AlbedoMap", "_AlbedoTex",
                    "_Diffuse", "_DiffuseMap", "_DiffuseTex",
                    "_BaseColorMap", "_ColorMap", "_Tex") ?? "_MainTex")
                : Vector2.zero;

            Color src_tint = TryGetColorFromAliases(src,
                "_Color", "_BaseColor", "_TintColor", "_MainColor", "_AlbedoColor", "_DiffuseColor",
                // Underscore-separated variants used by Hovl Studio + some stylized packs.
                "_Albedo_Color", "_Tint", "_BaseColor_Color")
                ?? Color.white;

            // Emission: capture color, texture, strength, and any flavor
            // of "is emission on" signal. The target shader's _EMISSION_ON
            // keyword gets enabled when ANY of these is present, so a
            // material that the vendor shader was illuminating from
            // _EmissionMap or via _EMISSION keyword carries that intent
            // through even if the color is at default.
            Color src_emission = TryGetColorFromAliases(src,
                "_EmissionColor", "_EmissiveColor", "_HdrColor",
                // Lowercase / underscore variants (Hovl uses _Emission_color),
                // plus glow/selfillum naming.
                "_Emission_color", "_Emission_Color", "_Glow", "_GlowColor", "_SelfIllumColor") ?? Color.black;
            Texture src_emissionMap = TryGetTextureFromAliases(src,
                "_EmissionMap", "_EmissiveMap", "_EmissionTex", "_EmissiveTex",
                "_GlowMap", "_GlowTex", "_SelfIllumMap",
                "_emissive");
            Vector2 src_emissionMapScale = src_emissionMap != null
                ? src.GetTextureScale(GetFirstSetTexture(src,
                    "_EmissionMap", "_EmissiveMap", "_EmissionTex", "_EmissiveTex",
                    "_GlowMap", "_GlowTex", "_SelfIllumMap", "_emissive") ?? "_EmissionMap")
                : Vector2.one;
            Vector2 src_emissionMapOffset = src_emissionMap != null
                ? src.GetTextureOffset(GetFirstSetTexture(src,
                    "_EmissionMap", "_EmissiveMap", "_EmissionTex", "_EmissiveTex",
                    "_GlowMap", "_GlowTex", "_SelfIllumMap", "_emissive") ?? "_EmissionMap")
                : Vector2.zero;
            float src_emissionStrength = TryGetFloatFromAliases(src,
                "_EmissionStrength", "_EmissionIntensity", "_EmissionScale",
                "_EmissionMultiplier", "_EmissionValue", "_EmissionPower",
                "_GlowIntensity", "_GlowStrength", "_GlowPower",
                "_SelfIllumStrength", "_SelfIlluminationStrength")
                ?? 1.0f;
            bool src_emissionKeywordOn = src.IsKeywordEnabled("_EMISSION")
                                      || src.IsKeywordEnabled("_EMISSION_ON")
                                      || src.IsKeywordEnabled("_EMISSIVE_ON")
                                      || src.IsKeywordEnabled("_GLOW_ON")
                                      || src.IsKeywordEnabled("_SELFILLUM_ON")
                                      // Float toggle conventions used by Hovl Studio and
                                      // some Shader Graph particle shaders.
                                      || (src.HasProperty("_UseEmission")
                                          && src.GetFloat("_UseEmission") > 0.5f)
                                      || (src.HasProperty("_UseGlow")
                                          && src.GetFloat("_UseGlow") > 0.5f);

            float src_cutoff = src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f;
            float src_hdrBoost = TryGetFloatFromAliases(src,
                "_HdrBoost", "_HdrMultiply") ?? 1.0f;

            // Vertex color mode: read source _ColorMode (0=Multiply, 1=Add, 2=Subtract,
            // 3=Overlay, 4=Color, 5=Difference). Most legacy particles use 0 (Multiply).
            int src_colorMode = src.HasProperty("_ColorMode") ? Mathf.RoundToInt(src.GetFloat("_ColorMode")) : 0;

            // Legacy Particles/Additive shaders use a single _InvFade scalar
            // as the soft-particle hint: a non-zero value implies the
            // feature is intended. The numeric value is the inverse of the
            // fade distance in the legacy math. Declared up here (not down
            // by src_softOn) because the src_softFar derivation directly
            // below also needs to read it.
            float src_invFade = src.HasProperty("_InvFade") ? src.GetFloat("_InvFade") : 0f;

            // Soft particles: legacy shaders set _SoftParticlesNearFadeDistance/_FarFadeDistance
            // via vector properties or scalars. Try both forms.
            float src_softNear = TryGetFloatFromAliases(src,
                "_SoftParticlesFadeDistanceNear", "_SoftParticlesNearFadeDistance",
                "_SoftFadeNear", "_SoftParticleNear") ?? 0.0f;
            float src_softFar  = TryGetFloatFromAliases(src,
                "_SoftParticlesFadeDistanceFar", "_SoftParticlesFarFadeDistance",
                "_SoftFadeFar", "_SoftParticleFar") ?? 1.0f;
            // Legacy _InvFade fallback: if the source had _InvFade > 0 and
            // didn't provide explicit far/near distances, derive a far
            // distance from the inverse-fade hardness. _InvFade = 0.643 ≈
            // 1.55m fade distance, which is reasonable for an MR play space.
            // Clamped to keep absurd values (e.g. _InvFade near 0) from
            // producing 100m fade ranges.
            if (src_invFade > 0.01f && Mathf.Approximately(src_softFar, 1.0f))
            {
                src_softFar = Mathf.Clamp(1f / src_invFade, 0.25f, 5f);
            }

            // Hovl Studio _Depthpower fallback: same idea as _InvFade.
            // Higher _Depthpower = sharper fade (small distance), lower =
            // softer fade (large distance). 0.5 maps to ~2m fade which
            // is a sensible default for MR particles.
            float src_depthPower = src.HasProperty("_Depthpower") ? src.GetFloat("_Depthpower") : 0f;
            if (src_depthPower > 0.01f && Mathf.Approximately(src_softFar, 1.0f))
            {
                src_softFar = Mathf.Clamp(1f / src_depthPower, 0.25f, 5f);
            }
            // ── Camera fade (URP/Particles) ──────────────────────────────
            // Particle alpha fades out close to the camera plane to avoid
            // clipping into the player's face. Signaled by a float toggle
            // OR a keyword on various vendor shaders.
            bool src_camFadeOn = src.IsKeywordEnabled("_CAMERAFADE_ON")
                              || (src.HasProperty("_CameraFadingEnabled")
                                  && src.GetFloat("_CameraFadingEnabled") > 0.5f);
            float src_camNear = TryGetFloatFromAliases(src,
                "_CameraNearFadeDistance", "_CameraFadeNear", "_NearFadeDistance") ?? 1.0f;
            float src_camFar = TryGetFloatFromAliases(src,
                "_CameraFarFadeDistance",  "_CameraFadeFar",  "_FarFadeDistance")  ?? 2.0f;

            // Soft particles can be signaled by four different conventions
            // on vendor materials — we union them all:
            //   1. Keyword (URP older versions, Standard Particles):
            //      SOFTPARTICLES_ON / _SOFTPARTICLES_ON / _FADING_ON
            //   2. Float property (URP recent versions, ShaderGraph particles):
            //      _SoftParticlesEnabled > 0.5
            //   3. CFX convention (Cartoon FX Remaster, Epic Toon FX-style packs):
            //      _UseSP > 0.5
            //   4. Implicit (some vendor packs): non-zero fade distances even
            //      without explicit enable — we DON'T infer from this because
            //      legitimate "fully crisp" materials sometimes leave the
            //      distances at non-default but rely on the disabled keyword.
            //      Trust the explicit signals only.
            // src_invFade was declared up by src_softFar (it's also needed
            // for the legacy fade-distance derivation). Just check it here.
            bool src_softOn = src.IsKeywordEnabled("SOFTPARTICLES_ON")
                           || src.IsKeywordEnabled("_SOFTPARTICLES_ON")
                           || src.IsKeywordEnabled("_FADING_ON")
                           || (src.HasProperty("_SoftParticlesEnabled")
                               && src.GetFloat("_SoftParticlesEnabled") > 0.5f)
                           || (src.HasProperty("_UseSP")
                               && src.GetFloat("_UseSP") > 0.5f)
                           // Hovl Studio's float toggle.
                           || (src.HasProperty("_Usedepth")
                               && src.GetFloat("_Usedepth") > 0.5f)
                           // Vendor short-form toggle ("Use Soft" → soft particles).
                           || (src.HasProperty("_UseSoft")
                               && src.GetFloat("_UseSoft") > 0.5f)
                           // Same vendor's keyword variant.
                           || src.IsKeywordEnabled("USE_SOFT_PARTICLES")
                           || src_invFade > 0.01f;

            // Single-channel mode (CFXR convention). Three forms:
            //   - _CFXR_SINGLE_CHANNEL keyword (older CFXR)
            //   - _SINGLECHANNEL_ON keyword (our convention)
            //   - _SingleChannel float (current CFXR)
            bool src_singleChannel = src.IsKeywordEnabled("_CFXR_SINGLE_CHANNEL")
                                  || src.IsKeywordEnabled("_SINGLECHANNEL_ON")
                                  || (src.HasProperty("_SingleChannel")
                                      && src.GetFloat("_SingleChannel") > 0.5f);

            // Dissolve. Multiple signal conventions:
            //   - _CFXR_DISSOLVE / _DISSOLVE_ON keywords (CFXR, our convention)
            //   - _UseDissolve float toggle (newer CFXR materials)
            bool src_dissolveOn = src.IsKeywordEnabled("_CFXR_DISSOLVE")
                               || src.IsKeywordEnabled("_DISSOLVE_ON")
                               || (src.HasProperty("_UseDissolve")
                                   && src.GetFloat("_UseDissolve") > 0.5f);
            Texture src_dissolveMap = TryGetTextureFromAliases(src,
                "_DissolveTex", "_DissolveMap", "_NoiseTex", "_DissolveNoise", "_DissolveTexture");
            float src_dissolveAmount = TryGetFloatFromAliases(src,
                "_DissolveAmount", "_Dissolve", "_DissolveProgress", "_DissolveValue") ?? 0f;
            // _DissolveSmooth is CFXR's name for edge softness. Same axis
            // as our _DissolveEdgeWidth — wider value = softer transition.
            float src_dissolveEdge = TryGetFloatFromAliases(src,
                "_DissolveEdgeWidth", "_EdgeWidth", "_DissolveEdge", "_DissolveSmooth") ?? 0.05f;
            Color src_dissolveEdgeColor = TryGetColorFromAliases(src,
                "_DissolveEdgeColor", "_EdgeColor", "_DissolveColor") ?? new Color(1f, 0.5f, 0f, 1f);

            // ── Noise modulation (Hovl Studio _Noise + _NoiseQuat) ───────
            // Hovl Studio packs four params into a single Vector4 they
            // call `_NoiseQuat`: xy = scroll velocity, z = base color
            // modulation power, w = emission ("glow") modulation power.
            // We unpack to individual scalars on the target so the
            // Inspector is artist-friendly.
            Texture src_noiseModTex = TryGetTextureFromAliases(src,
                "_Noise", "_NoiseTex", "_NoiseMap", "_NoiseTexture");
            Vector2 src_noiseModScale = src_noiseModTex != null
                ? src.GetTextureScale(GetFirstSetTexture(src,
                    "_Noise", "_NoiseTex", "_NoiseMap", "_NoiseTexture") ?? "_Noise")
                : Vector2.one;
            Vector2 src_noiseModOffset = src_noiseModTex != null
                ? src.GetTextureOffset(GetFirstSetTexture(src,
                    "_Noise", "_NoiseTex", "_NoiseMap", "_NoiseTexture") ?? "_Noise")
                : Vector2.zero;
            // Hovl Studio's noise control Vector4 ships under two names —
            // the short "_NoiseQuat" or the descriptive
            // "_NoisespeedXYNoisepowerZGlowpowerW". Same packing either way.
            Vector4 src_noiseQuat =
                src.HasProperty("_NoiseQuat")                        ? src.GetVector("_NoiseQuat") :
                src.HasProperty("_NoisespeedXYNoisepowerZGlowpowerW") ? src.GetVector("_NoisespeedXYNoisepowerZGlowpowerW") :
                                                                       Vector4.zero;

            // ── Fresnel / rim glow ──────────────────────────────────────
            // Carries over for materials authored with rim lighting. Our
            // shader computes fresnel inside the pseudo-lit block (uses
            // the same normal sample, ~5 extra ALU). Materials WITHOUT a
            // normal map can still have these properties stored, but the
            // visual effect requires the normal-map variation to produce
            // anything visible — that's expected (vendor materials with
            // fresnel basically always ship with normal maps too).
            Color src_fresnelColor = TryGetColorFromAliases(src,
                "_FresnelColor", "_RimColor", "_FresnelTint", "_RimTint",
                "_EdgeColor", "_FresnelGlow") ?? Color.black;
            float src_fresnelPower = TryGetFloatFromAliases(src,
                "_FresnelPower", "_RimPower", "_FresnelExponent", "_RimExponent",
                "_FresnelFalloff", "_RimFalloff") ?? 4.0f;

            // ── Pseudo lighting (normal map from any source) ─────────────
            // Whenever the source had ANY flavor of normal map bound, we
            // wire it into DreamPark/Particles' pseudo-lit path. This
            // covers CFXR lit materials, URP/Particles/Lit, Standard
            // Particles with bumps, and vendor-pack lit particle shaders.
            // The fake-lighting defaults are chosen so a freshly-converted
            // material looks plausibly lit without per-asset tuning.
            Texture src_bumpMap = TryGetTextureFromAliases(src,
                "_BumpMap", "_NormalMap", "_NormalTex", "_NrmMap",
                "_NormTex", "_BumpTex", "_NrmTex",
                "_Normal", "_Normals", "_Norm", "_Bump",
                "_DetailNormalMap");
            float src_bumpScale = TryGetFloatFromAliases(src,
                "_BumpScale", "_NormalScale", "_NormalStrength", "_NormalIntensity",
                "_NrmStrength") ?? 1.0f;
            Vector2 src_bumpScaleUV = src_bumpMap != null
                ? src.GetTextureScale(GetFirstSetTexture(src,
                    "_BumpMap", "_NormalMap", "_NormalTex", "_NrmMap",
                    "_NormTex", "_BumpTex", "_NrmTex",
                    "_Normal", "_Normals", "_Norm", "_Bump",
                    "_DetailNormalMap") ?? "_BumpMap")
                : Vector2.one;
            Vector2 src_bumpOffsetUV = src_bumpMap != null
                ? src.GetTextureOffset(GetFirstSetTexture(src,
                    "_BumpMap", "_NormalMap", "_NormalTex", "_NrmMap",
                    "_NormTex", "_BumpTex", "_NrmTex",
                    "_Normal", "_Normals", "_Norm", "_Bump",
                    "_DetailNormalMap") ?? "_BumpMap")
                : Vector2.zero;

            // ── Second color (CFXR two-tone tint) ────────────────────────
            // Used by CFXR smoke / fire / magic effects that need a
            // gradient between two colors driven by a mask texture.
            Texture src_secondColorTex = TryGetTextureFromAliases(src,
                "_SecondColorTex", "_SecondColor_Tex", "_2ndColorTex", "_ColorMaskTex");
            Color src_secondColor = TryGetColorFromAliases(src,
                "_SecondColor", "_2ndColor", "_TintColor2", "_SecondTint") ?? Color.white;
            float src_secondColorSmooth = TryGetFloatFromAliases(src,
                "_SecondColorSmooth", "_SecondColorSmoothness", "_2ndColorSmooth") ?? 0.5f;
            bool src_secondColorOn = src.IsKeywordEnabled("_SECONDCOLOR_ON")
                                  || src.IsKeywordEnabled("_CFXR_SECONDCOLOR_LERP")
                                  || (src.HasProperty("_UseSecondColor")
                                      && src.GetFloat("_UseSecondColor") > 0.5f);
            Vector2 src_secondColorScale = src_secondColorTex != null
                ? src.GetTextureScale(GetFirstSetTexture(src,
                    "_SecondColorTex", "_SecondColor_Tex", "_2ndColorTex", "_ColorMaskTex") ?? "_SecondColorTex")
                : Vector2.one;
            Vector2 src_secondColorOffset = src_secondColorTex != null
                ? src.GetTextureOffset(GetFirstSetTexture(src,
                    "_SecondColorTex", "_SecondColor_Tex", "_2ndColorTex", "_ColorMaskTex") ?? "_SecondColorTex")
                : Vector2.zero;

            // ── Edge fade (CFXR) ─────────────────────────────────────────
            // Soft alpha vignette on the rectangular quad borders. CFXR
            // uses this on cloud / smoke / dust effects whose source
            // texture has a hard rectangle silhouette.
            bool src_edgeFadeOn = src.IsKeywordEnabled("_EDGE_FADE_ON")
                               || src.IsKeywordEnabled("_CFXR_EDGE_FADING")
                               || (src.HasProperty("_UseEF")
                                   && src.GetFloat("_UseEF") > 0.5f);
            float src_edgeFadeWidth = TryGetFloatFromAliases(src,
                "_EdgeFadeWidth", "_EF_Width", "_EF_Range", "_EdgeFadeRange",
                "_EFWidth") ?? 0.1f;

            // ── Radial UV (polar transformation for ring effects) ───────
            // Used by CFXR magic circles, shockwaves, halos, portal rings.
            // Signaled by _UseRadialUV float toggle or _CFXR_RADIAL_UV
            // keyword on the source. _RingTopOffset is CFXR's inner-radius
            // cutoff (the size of the hole in the middle of the ring).
            bool src_radialUVOn = src.IsKeywordEnabled("_RADIAL_UV_ON")
                               || src.IsKeywordEnabled("_CFXR_RADIAL_UV")
                               || (src.HasProperty("_UseRadialUV")
                                   && src.GetFloat("_UseRadialUV") > 0.5f);
            float src_ringInnerRadius = TryGetFloatFromAliases(src,
                "_RingTopOffset", "_RadialUVInnerRadius", "_InnerRadius", "_RingInner") ?? 0f;

            // ── Overlay (CFXR perlin overlay): second texture layer ──────
            // Sampled from a second texture, blended over the base color.
            // CFXR uses this for organic breakup detail (the "wisp" look
            // in their smoke, the texture detail in their fire). We
            // support both Multiply (0) and Additive (1) blend modes,
            // with a strength dial and scrolling UVs.
            Texture src_overlayMap = TryGetTextureFromAliases(src,
                "_OverlayTex", "_OverlayMap", "_OverlayTexture", "_NoiseOverlay");
            Vector2 src_overlayScale = src_overlayMap != null
                ? src.GetTextureScale(GetFirstSetTexture(src,
                    "_OverlayTex", "_OverlayMap", "_OverlayTexture", "_NoiseOverlay") ?? "_OverlayTex")
                : Vector2.one;
            Vector2 src_overlayOffset = src_overlayMap != null
                ? src.GetTextureOffset(GetFirstSetTexture(src,
                    "_OverlayTex", "_OverlayMap", "_OverlayTexture", "_NoiseOverlay") ?? "_OverlayTex")
                : Vector2.zero;
            // CFXR exposes scroll as a Vector4 (`_OverlayTex_Scroll`)
            // where xy = scroll velocity (units/sec) and zw are vendor-
            // specific extras we don't replicate. We take xy.
            Vector4 src_overlayScroll = src.HasProperty("_OverlayTex_Scroll")
                ? src.GetVector("_OverlayTex_Scroll")
                : Vector4.zero;
            // _CFXR_OVERLAYBLEND on CFXR materials is a float 0/1 that
            // signals additive (1) vs multiply (0). Other vendors might
            // use a similarly-named property; aliases cover them.
            float src_overlayBlend = TryGetFloatFromAliases(src,
                "_CFXR_OVERLAYBLEND", "_OverlayBlendMode", "_OverlayBlend") ?? 0f;
            float src_overlayStrength = TryGetFloatFromAliases(src,
                "_OverlayStrength", "_OverlayIntensity", "_OverlayAmount") ?? 1f;
            bool src_overlayKeywordOn = src.IsKeywordEnabled("_OVERLAY_ON")
                                     || src.IsKeywordEnabled("_CFXR_OVERLAY_ON")
                                     || src.IsKeywordEnabled("_OVERLAY");

            // UV distortion: distortion tex + strength + scroll speeds.
            //
            // Three signal conventions for the enable flag:
            //   _CFXR_UV_DISTORTION / _DISTORTION_ON keywords (our + CFXR vintage)
            //   _UseUVDistortion float toggle (current CFXR)
            //
            // The strength scalar adds `_Distort` (CFXR's name). Scroll
            // speeds come from scalar pairs (URP convention) OR a packed
            // Vector4 `_DistortScrolling` whose xy is the scroll velocity
            // (CFXR convention). Scalars take priority when both are set.
            // Look up the distortion texture first — the texture binding
            // itself is a signal that the material intends distortion, even
            // for vendor packs that don't ship a corresponding keyword/toggle
            // (Hovl Studio's "_Flow" slot is the canonical example: they
            // drive distortion entirely off the texture being bound).
            // Layer 1: looks for _DistortTex1 first (the lightning-style
            // dual-layer convention) before falling back to generic names.
            Texture src_distortMap = TryGetTextureFromAliases(src,
                "_DistortTex1",  // dual-layer (lightning) convention — try first
                "_DistortionTex", "_DistortionMap", "_DisplacementMap", "_DistortTex",
                // Hovl Studio aliases — flow map = distortion map for our
                // purposes (we don't replicate true flow mapping's two-sample
                // blend, just the basic UV warp).
                "_Flow", "_FlowMap", "_FlowTex");

            // Layer 2: only present on dual-noise materials (lightning,
            // turbulent energy effects). Optional — most particle
            // materials won't have this.
            Texture src_distortMap2 = TryGetTextureFromAliases(src,
                "_DistortTex2", "_DistortionMap2", "_DistortionTex2", "_DistortTex_2");
            float src_distortStrength2 = TryGetFloatFromAliases(src,
                "_DistortionStrength2", "_DistortStrength2", "_Distortion2") ?? 0.1f;
            // Some lightning materials pack BOTH layers' scroll into a
            // single Vector4 (xy = tex1 scroll, zw = tex2 scroll). This
            // is distinct from CFXR's _DistortScrolling (xy only) and
            // Hovl's _DistortionSpeedXYPowerZ (xy=scroll, z=strength).
            Vector4 src_distortSpeed = src.HasProperty("_DistortSpeed")
                ? src.GetVector("_DistortSpeed")
                : Vector4.zero;
            bool src_distortionOn = src.IsKeywordEnabled("_CFXR_UV_DISTORTION")
                                 || src.IsKeywordEnabled("_DISTORTION_ON")
                                 || (src.HasProperty("_UseUVDistortion")
                                     && src.GetFloat("_UseUVDistortion") > 0.5f)
                                 || src_distortMap != null;
            // Vendor packs use a few different packed-vector conventions
            // for distortion control:
            //   CFXR:           _DistortScrolling          (xy = scroll velocity)
            //   Hovl Studio:    _DistortionSpeedXYPowerZ   (xy = scroll, z = strength)
            // Both declared up here so the strength + scroll derivations
            // below can read them. Otherwise CS0841 (use-before-declared).
            Vector4 src_distortScrollVec = src.HasProperty("_DistortScrolling")
                ? src.GetVector("_DistortScrolling")
                : Vector4.zero;
            Vector4 src_distortHovlPacked = src.HasProperty("_DistortionSpeedXYPowerZ")
                ? src.GetVector("_DistortionSpeedXYPowerZ")
                : Vector4.zero;

            // Distortion strength: explicit scalars first, then fall back
            // to the .z component of Hovl's _DistortionSpeedXYPowerZ packed
            // vector (their convention puts strength in Z).
            float src_distortStrength = TryGetFloatFromAliases(src,
                "_DistortionStrength", "_DistortStrength", "_DistortAmount", "_DistortionAmount",
                "_Distort")
                ?? (src_distortHovlPacked.z != 0f ? src_distortHovlPacked.z : 0.1f);
            // Layer 1 scroll velocity — try scalar aliases, then any of
            // the three packed-vector forms (CFXR's _DistortScrolling.xy,
            // Hovl's _DistortionSpeedXYPowerZ.xy, lightning's
            // _DistortSpeed.xy). First non-zero wins.
            float src_distortScrollX = TryGetFloatFromAliases(src,
                "_DistortionScrollX", "_DistortSpeedX", "_DistortionSpeedX")
                ?? (src_distortScrollVec.x  != 0f ? src_distortScrollVec.x
                  : src_distortHovlPacked.x != 0f ? src_distortHovlPacked.x
                  : src_distortSpeed.x);
            float src_distortScrollY = TryGetFloatFromAliases(src,
                "_DistortionScrollY", "_DistortSpeedY", "_DistortionSpeedY")
                ?? (src_distortScrollVec.y  != 0f ? src_distortScrollVec.y
                  : src_distortHovlPacked.y != 0f ? src_distortHovlPacked.y
                  : src_distortSpeed.y);

            // Layer 2 scroll velocity — pulled from _DistortSpeed.zw on
            // dual-noise materials. No scalar aliases since layer 2 is a
            // new feature on our side; vendors only ship the packed form.
            float src_distortScroll2X = src_distortSpeed.z;
            float src_distortScroll2Y = src_distortSpeed.w;

            // Detect blend mode from src/dst blend factors, or fall back to keyword.
            // 5 = SrcAlpha, 10 = OneMinusSrcAlpha, 1 = One.
            float src_srcBlend = src.HasProperty("_SrcBlend") ? src.GetFloat("_SrcBlend") : 5f;
            float src_dstBlend = src.HasProperty("_DstBlend") ? src.GetFloat("_DstBlend") : 10f;
            string blendMode = ResolveBlendMode(src, src_srcBlend, src_dstBlend);

            float src_cull = src.HasProperty("_Cull") ? src.GetFloat("_Cull") : 0f; // default Off (billboards)
            float src_zwrite = src.HasProperty("_ZWrite") ? src.GetFloat("_ZWrite") : 0f;
            int src_renderQueue = src.renderQueue;
            // Alpha test signal — four conventions:
            //   _ALPHATEST_ON keyword (URP / our convention)
            //   _UseAlphaClip float toggle (CFXR)
            //   _UseAlphaCliping float toggle (vendor typo, one 'p')
            //   USE_ALPHA_CLIPING keyword (same vendor)
            //   Clip_ON keyword (KriptoFX RFX1)
            bool src_alphaTest = src.IsKeywordEnabled("_ALPHATEST_ON")
                              || src.IsKeywordEnabled("Clip_ON")
                              || src.IsKeywordEnabled("USE_ALPHA_CLIPING")
                              || (src.HasProperty("_UseAlphaClip")
                                  && src.GetFloat("_UseAlphaClip") > 0.5f)
                              || (src.HasProperty("_UseAlphaCliping")
                                  && src.GetFloat("_UseAlphaCliping") > 0.5f);

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
            src.SetFloat("_Cutoff", src_cutoff);
            src.SetFloat("_HdrBoostMultiplier", src_hdrBoost);

            // ── Emission: full carry-over (color + map + strength + UVs) ──
            // Emission counts as "intended" if any of the following signals
            // were present on the source:
            //   - Source had the _EMISSION keyword enabled (URP/Standard convention)
            //   - _EmissionColor is non-black (artists set a glow color)
            //   - An emission texture is bound in any alias slot
            //   - A vendor "selfillum"/"glow" keyword is enabled
            //
            // When intended, we write the full emission set on the target
            // (color, strength, map, map UVs) and enable _EMISSION_ON so
            // the shader's gated emission path runs. When NOT intended, we
            // still write _EmissionColor for fidelity (in case the target
            // is later inspected and someone wants to enable emission
            // manually), but we don't enable the keyword — keeps the
            // material on the zero-cost variant.
            bool emissionIntended =
                src_emissionKeywordOn
             || src_emission.maxColorComponent > 0.001f
             || src_emissionMap != null;

            src.SetColor("_EmissionColor", src_emission);
            if (src.HasProperty("_EmissionStrength"))
                src.SetFloat("_EmissionStrength", src_emissionStrength);
            if (src.HasProperty("_EmissionMap"))
            {
                // Even when the source had no map, write Texture2D.whiteTexture
                // so the keyword-gated path samples white (identity multiply)
                // instead of pure black, which would silently disable color-
                // only emission.
                src.SetTexture("_EmissionMap", src_emissionMap != null
                    ? src_emissionMap
                    : (Texture)Texture2D.whiteTexture);
                src.SetTextureScale("_EmissionMap", src_emissionMapScale);
                src.SetTextureOffset("_EmissionMap", src_emissionMapOffset);
            }
            SetKeyword(src, "_EMISSION_ON", emissionIntended);
            // Also flip Unity's standard _EMISSION keyword on the target.
            // The DreamPark/Particles shader doesn't read it, but it
            // matches what Unity's GlobalIllumination expects and keeps
            // the material consistent with built-in tooling (Light Explorer,
            // baking pipeline assertions, etc.).
            SetKeyword(src, "_EMISSION", emissionIntended);
            // GlobalIllumination flag — tells Unity's GI system "this
            // material emits light at the inspector value." Realtime is
            // safe even on baked scenes; we'd need a per-prefab decision
            // to use Baked. Realtime is the right default for particles.
            src.globalIlluminationFlags = emissionIntended
                ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                : MaterialGlobalIlluminationFlags.None;
            src.SetFloat("_SoftParticlesNear", src_softNear);
            src.SetFloat("_SoftParticlesFar", src_softFar);

            // Camera fade (URP /Particles parity). Activate the keyword only
            // when the source had it intended; the near/far distances
            // carry over regardless so a user who later enables the toggle
            // in the Inspector gets sensible defaults.
            if (src.HasProperty("_CameraNearFadeDistance"))
                src.SetFloat("_CameraNearFadeDistance", src_camNear);
            if (src.HasProperty("_CameraFarFadeDistance"))
                src.SetFloat("_CameraFarFadeDistance", src_camFar);
            SetKeyword(src, "_CAMERAFADE_ON", src_camFadeOn);
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
                // Vendor packs use wildly different units for distortion
                // (Hovl ships values like 600 because their shader divides
                // internally by some scale factor). Clamp to our shader's
                // Range(0, 0.5) — visual fidelity isn't perfect but we
                // don't blow up the UV warp by passing in huge values.
                // Abs handles vendors that use negative strength to flip
                // direction; our scroll velocities already handle that
                // independently.
                src.SetFloat("_DistortionStrength", Mathf.Clamp(Mathf.Abs(src_distortStrength), 0f, 0.5f));
                src.SetFloat("_DistortionScrollX", src_distortScrollX);
                src.SetFloat("_DistortionScrollY", src_distortScrollY);
                src.SetFloat("_Distortion", 1f);
            }

            // Dual distortion layer (v9) — lightning, energy streams,
            // magical turbulence. Activated whenever a second distortion
            // texture is present on the source. The two layers sum their
            // warps from the original quad UV — that's what gives the
            // characteristic "wiggles but not in any one direction" look
            // that single-layer distortion can't replicate.
            bool distort2Intended = src_distortMap2 != null;
            if (distort2Intended)
            {
                if (src.HasProperty("_DistortionMap2"))
                    src.SetTexture("_DistortionMap2", src_distortMap2);
                if (src.HasProperty("_DistortionStrength2"))
                    src.SetFloat("_DistortionStrength2",
                        Mathf.Clamp(Mathf.Abs(src_distortStrength2), 0f, 0.5f));
                if (src.HasProperty("_DistortionScroll2X"))
                    src.SetFloat("_DistortionScroll2X", src_distortScroll2X);
                if (src.HasProperty("_DistortionScroll2Y"))
                    src.SetFloat("_DistortionScroll2Y", src_distortScroll2Y);
            }
            SetKeyword(src, "_DISTORTION2_ON", distort2Intended);

            // Overlay (v3) — CFXR perlin overlay support.
            // Activated when the source had an overlay texture bound OR
            // any flavor of overlay keyword set. Map present is the
            // stronger signal (a material can't usefully render the
            // feature without a texture), so we gate apply on it.
            bool overlayIntended = src_overlayKeywordOn || src_overlayMap != null;
            if (overlayIntended && src_overlayMap != null)
            {
                if (src.HasProperty("_OverlayTex"))
                {
                    src.SetTexture("_OverlayTex", src_overlayMap);
                    src.SetTextureScale("_OverlayTex", src_overlayScale);
                    src.SetTextureOffset("_OverlayTex", src_overlayOffset);
                }
                if (src.HasProperty("_OverlayBlendMode"))
                    src.SetFloat("_OverlayBlendMode", src_overlayBlend > 0.5f ? 1f : 0f);
                if (src.HasProperty("_OverlayStrength"))
                    src.SetFloat("_OverlayStrength", Mathf.Clamp01(src_overlayStrength));
                if (src.HasProperty("_OverlayScrollX"))
                    src.SetFloat("_OverlayScrollX", src_overlayScroll.x);
                if (src.HasProperty("_OverlayScrollY"))
                    src.SetFloat("_OverlayScrollY", src_overlayScroll.y);
                src.SetFloat("_Overlay", 1f);
            }
            SetKeyword(src, "_OVERLAY_ON", overlayIntended && src_overlayMap != null);

            // Radial UV (v4). Center defaults to (0.5, 0.5) — middle of
            // the texture — and rotation defaults to 0. CFXR doesn't
            // expose either of those on its materials, so the converted
            // material picks up sensible defaults. Inner-radius carries
            // over from CFXR's _RingTopOffset.
            if (src.HasProperty("_RadialUVInnerRadius"))
                src.SetFloat("_RadialUVInnerRadius", Mathf.Clamp(src_ringInnerRadius, 0f, 0.5f));
            SetKeyword(src, "_RADIAL_UV_ON", src_radialUVOn);

            // Edge fade (v5).
            if (src.HasProperty("_EdgeFadeWidth"))
                src.SetFloat("_EdgeFadeWidth", Mathf.Clamp(src_edgeFadeWidth, 0f, 0.5f));
            SetKeyword(src, "_EDGE_FADE_ON", src_edgeFadeOn);

            // Pseudo lighting (v7) — wire a bump map through to the
            // unlit-pipeline fake-lighting path. We unconditionally turn
            // the keyword ON whenever a normal map is present, since the
            // vendor author authored a normal map specifically because
            // they wanted lighting. The fake-light defaults below match
            // what looks good on most CFXR / URP lit particle materials
            // (top-front light, moderate strength, soft ambient floor).
            // Artists can tune per-material via the Inspector.
            bool pseudoLitIntended = src_bumpMap != null;
            if (pseudoLitIntended)
            {
                if (src.HasProperty("_BumpMap"))
                {
                    src.SetTexture("_BumpMap", src_bumpMap);
                    src.SetTextureScale("_BumpMap", src_bumpScaleUV);
                    src.SetTextureOffset("_BumpMap", src_bumpOffsetUV);
                }
                if (src.HasProperty("_BumpScale"))
                    src.SetFloat("_BumpScale", Mathf.Clamp(src_bumpScale, 0f, 2f));
                // First-pass defaults — chosen so a converted material
                // looks plausibly lit without any artist tweaking.
                if (src.HasProperty("_FakeLightDir"))
                    src.SetVector("_FakeLightDir", new Vector4(0f, 1f, 1f, 0f));
                if (src.HasProperty("_FakeLightStrength"))
                    src.SetFloat("_FakeLightStrength", 0.7f);
                if (src.HasProperty("_FakeLightAmbient"))
                    src.SetFloat("_FakeLightAmbient", 0.3f);
            }
            SetKeyword(src, "_PSEUDO_LIT_ON", pseudoLitIntended);

            // Fresnel — write the color/power regardless of whether
            // pseudo-lit is enabled. The shader only computes fresnel
            // inside the _PSEUDO_LIT_ON block, so without a normal map
            // these values are stored but produce no visible effect.
            // That matches the vendor's intent: same data, just gated
            // by the same normal-map dependency it always had.
            if (src.HasProperty("_FresnelColor"))
                src.SetColor("_FresnelColor", src_fresnelColor);
            if (src.HasProperty("_FresnelPower"))
                src.SetFloat("_FresnelPower", Mathf.Clamp(src_fresnelPower, 1f, 16f));

            // Noise modulation (v8) — Hovl Studio _NoiseQuat unpacked.
            // Activate only when a noise texture is present; without it
            // the keyword path would multiply by a default white texture
            // (identity), which is technically harmless but wastes a
            // variant slot. Gate on the texture binding.
            bool noiseModIntended = src_noiseModTex != null;
            if (noiseModIntended)
            {
                if (src.HasProperty("_NoiseModTex"))
                {
                    src.SetTexture("_NoiseModTex", src_noiseModTex);
                    src.SetTextureScale("_NoiseModTex", src_noiseModScale);
                    src.SetTextureOffset("_NoiseModTex", src_noiseModOffset);
                }
                if (src.HasProperty("_NoiseModSpeedX"))
                    src.SetFloat("_NoiseModSpeedX", src_noiseQuat.x);
                if (src.HasProperty("_NoiseModSpeedY"))
                    src.SetFloat("_NoiseModSpeedY", src_noiseQuat.y);
                if (src.HasProperty("_NoiseModBasePower"))
                    src.SetFloat("_NoiseModBasePower", Mathf.Clamp01(src_noiseQuat.z));
                if (src.HasProperty("_NoiseModGlowPower"))
                    src.SetFloat("_NoiseModGlowPower", Mathf.Clamp01(src_noiseQuat.w));
            }
            SetKeyword(src, "_NOISE_MOD_ON", noiseModIntended);

            // Second color (v6) — two-tone tint via mask texture.
            // Gate on having a mask texture: the second-color path is
            // useless without one (the lerp would have no driver). If
            // the source had _UseSecondColor / _CFXR_SECONDCOLOR_LERP
            // set but no mask, we leave _SECONDCOLOR_ON off so the
            // material falls back to single-tone — better than rendering
            // with a hard 50/50 lerp against an all-black mask default.
            bool secondColorIntended = src_secondColorOn && src_secondColorTex != null;
            if (src.HasProperty("_SecondColor"))
                src.SetColor("_SecondColor", src_secondColor);
            if (src.HasProperty("_SecondColorTex") && src_secondColorTex != null)
            {
                src.SetTexture("_SecondColorTex", src_secondColorTex);
                src.SetTextureScale("_SecondColorTex", src_secondColorScale);
                src.SetTextureOffset("_SecondColorTex", src_secondColorOffset);
            }
            if (src.HasProperty("_SecondColorSmooth"))
                src.SetFloat("_SecondColorSmooth", Mathf.Clamp01(src_secondColorSmooth));
            SetKeyword(src, "_SECONDCOLOR_ON", secondColorIntended);

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
