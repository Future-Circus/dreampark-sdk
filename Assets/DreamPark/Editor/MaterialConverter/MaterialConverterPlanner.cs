#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DreamPark.EditorTools;  // for MaterialConverter (the static API)

namespace DreamPark.EditorTools.MaterialConversion
{
    /// <summary>
    /// Takes a list of material usages and decides what to do with each one.
    ///
    /// Routing rules (first match wins):
    ///
    ///   1. Already on DreamPark-Universal / Unlit / Particles  → AlreadyConverted (skip)
    ///   2. Embedded inside an FBX / glTF (not standalone .mat) → ReadOnlyEmbedded (skip + extract hint)
    ///   3. Not referenced by any prefab/scene under content    → Orphan (soft-skip)
    ///   4. Source shader is a particle/VFX shader
    ///        a. has exotic features (flipbook blending, etc.)  → ExoticParticle (skip)
    ///        b. otherwise                                      → ConvertParticle  → DreamPark/Particles
    ///   5. Source shader name matches Unlit hints              → ConvertOpaqueToUnlit
    ///   6. Anything else                                       → ConvertOpaqueToUniversal
    ///
    /// The window lets the user override the kind per row (Universal ↔ Unlit
    /// is the common toggle — particles are detected from the source shader
    /// and shouldn't be re-routed by hand). All "needs convert" rows default
    /// to approved = true; skip kinds default to approved = false + hardSkip.
    /// </summary>
    public static class MaterialConverterPlanner
    {
        public static List<MaterialPlanRow> Plan(List<MaterialUsage> usages)
        {
            var rows = new List<MaterialPlanRow>(usages?.Count ?? 0);
            if (usages == null) return rows;

            foreach (var u in usages)
            {
                rows.Add(ClassifyOne(u));
            }
            return rows;
        }

        private static MaterialPlanRow ClassifyOne(MaterialUsage u)
        {
            var row = new MaterialPlanRow { usage = u };

            // (1) Already on one of the canonical DreamPark shaders.
            if (DreamParkShaderNames.IsDreamParkShader(u.shaderName))
            {
                row.kind = MaterialConvertKind.AlreadyConverted;
                row.targetShader = u.shaderName;
                row.approved = false;
                row.hardSkip = true;
                row.skipReason = "Already on a DreamPark shader";
                return row;
            }

            // (2) Embedded sub-assets — can't be mutated in place. The right-
            // click MaterialConverter logs the same hint; we surface it as a
            // skipReason so the user sees it in the table without having to
            // run the converter to discover the problem.
            if (u.isEmbeddedInModel)
            {
                row.kind = MaterialConvertKind.ReadOnlyEmbedded;
                row.targetShader = "";
                row.approved = false;
                row.hardSkip = true;
                row.skipReason = "Embedded in model — use Materials → Extract Materials first";
                return row;
            }

            // (3) Orphan — not reached from any prefab/scene under content.
            // Soft-skip (no hardSkip flag) so the user can opt in and force
            // a conversion if the material is referenced from somewhere we
            // didn't walk (Lua addresses, EditorPrefs, etc.).
            if (!u.InUse)
            {
                row.kind = MaterialConvertKind.Orphan;
                row.targetShader = "";
                row.approved = false;
                row.hardSkip = false;
                row.skipReason = "Not referenced by any prefab or scene under content";
                return row;
            }

            // To classify particles, we need a Material instance. Load it
            // once and reuse for the rest of the checks.
            var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(u.assetPath);
            if (mat == null)
            {
                row.kind = MaterialConvertKind.Orphan;
                row.approved = false;
                row.hardSkip = true;
                row.skipReason = "Material asset failed to load";
                return row;
            }

            // (4) Particle path.
            //
            // Two signals can promote a material into the particle path:
            //   a) Source shader name matches ParticleShaderFragments
            //      (e.g. "Particles/Standard Unlit", "URP/Particles/Lit",
            //      "Stylized FX/Beam"). Cheap, but fails on vendor packs
            //      that use shader names like "Hovl Studio/HS_Explosion".
            //   b) Material is attached to a ParticleSystemRenderer in
            //      at least one prefab. Authoritative — if it's on a
            //      particle system, it's a particle material, full stop.
            //
            // We OR both signals so the long tail of weird vendor names
            // doesn't need to keep growing the ParticleShaderFragments
            // list. The runtime-particle-renderer check catches the rest.
            bool isParticle = MaterialConverter.IsParticleMaterial(mat)
                           || u.isUsedByParticleRenderer;
            if (isParticle)
            {
                // Always compute the diff for particle materials — both for
                // exotic-skipped rows (so the user sees what they'd lose if
                // they force-converted) and for normal converts (so they
                // know whether the conversion is clean or lossy before
                // approving). The diff is cheap (one shader-property walk).
                row.particleDiff = ParticleConversionDiff.Analyze(mat);

                if (MaterialConverter.HasExoticParticleFeature(mat))
                {
                    row.kind = MaterialConvertKind.ExoticParticle;
                    row.targetShader = u.shaderName; // stays on vendor shader
                    row.approved = false;
                    row.hardSkip = true;
                    row.skipReason = "Has exotic particle features (flipbook blending / etc.) — DreamPark/Particles v1 can't replicate";
                    return row;
                }
                row.kind = MaterialConvertKind.ConvertParticle;
                row.targetShader = DreamParkShaderNames.Particles;

                // If the diff flagged critical issues, leave the row UNCHECKED by
                // default — the user should look at the diff before agreeing.
                // High / Medium / Low rows default to approved as before.
                row.approved = row.particleDiff == null || row.particleDiff.CriticalCount == 0;
                return row;
            }

            // (5) Unlit candidate? Match by source shader name fragments.
            //     Conservative — only routes obvious unlit/UI/sprite/toon
            //     shaders to DreamPark-Unlit. Everything else goes to the
            //     lit DreamPark-UniversalShader.
            string shaderLower = (u.shaderName ?? "").ToLowerInvariant();
            foreach (var hint in DreamParkShaderNames.UnlitSourceHints)
            {
                if (shaderLower.Contains(hint))
                {
                    row.kind = MaterialConvertKind.ConvertOpaqueToUnlit;
                    row.targetShader = DreamParkShaderNames.Unlit;
                    row.approved = true;
                    return row;
                }
            }

            // (6) Default — convert to DreamPark-UniversalShader.
            row.kind = MaterialConvertKind.ConvertOpaqueToUniversal;
            row.targetShader = DreamParkShaderNames.Universal;
            row.approved = true;
            return row;
        }
    }
}
#endif
