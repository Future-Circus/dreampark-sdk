#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.PreUploadChecks
{
    /// <summary>
    /// Where the material checks look for materials (Oct 2026). Kept outside the checks'
    /// own !DREAMPARKCORE guard so it is unit-tested in core too
    /// (PreUploadMaterialSourcesTests).
    ///
    /// meta-occlusion used to read only MeshRenderer / SkinnedMeshRenderer materials of
    /// the content roots, and the roots never included the Sequence special levels.
    /// Zombiez shipped 29 materials drawing over the guest's hands with no finding:
    /// materials handed to Lua through a LuaBehaviour's Material injections (assigned at
    /// runtime), ParticleSystemRenderer materials and trail materials, and everything on
    /// the Start / Overlay / Game Over levels.
    /// </summary>
    public static class PreUploadMaterialSources
    {
        public enum Source
        {
            Mesh,            // MeshRenderer / SkinnedMeshRenderer
            Particle,        // ParticleSystemRenderer.sharedMaterial(s)
            ParticleTrail,   // ParticleSystemRenderer.trailMaterial
            LuaInjection,    // LuaBehaviour / EasyLua materialInjections[].value
        }

        public struct Hit
        {
            public Material material;
            public Source source;
            public Component component;
        }

        /// <summary>
        /// Every material under <paramref name="root"/> that ends up drawn in the world:
        /// mesh renderers, particle renderers (main + trail) and Lua material injections.
        /// Inactive children are included. A material reached twice by the same source is
        /// listed once; a material reached by two sources is listed once per source, so a
        /// caller can tell a particle material from a mesh one.
        ///
        /// Deliberately NOT collected: LineRenderer, TrailRenderer, SpriteRenderer,
        /// BillboardRenderer and UI materials (see MetaOcclusionCheck's scoping notes).
        /// </summary>
        public static List<Hit> Collect(GameObject root)
        {
            var hits = new List<Hit>();
            if (root == null) return hits;
            var seen = new HashSet<(int, Source)>();

            void Add(Material m, Source s, Component c)
            {
                if (m == null) return;
                if (!seen.Add((m.GetInstanceID(), s))) return;
                hits.Add(new Hit { material = m, source = s, component = c });
            }

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                {
                    foreach (var m in r.sharedMaterials) Add(m, Source.Mesh, r);
                }
                else if (r is ParticleSystemRenderer psr)
                {
                    Add(psr.sharedMaterial, Source.Particle, psr);
                    // sharedMaterials on a ParticleSystemRenderer is [material, trailMaterial];
                    // read the trail explicitly so a renderer with only a trail is still seen.
                    Material trail = psr.trailMaterial;
                    foreach (var m in psr.sharedMaterials)
                        if (m != null && m != trail) Add(m, Source.Particle, psr);
                    Add(trail, Source.ParticleTrail, psr);
                }
            }

            foreach (var inj in root.GetComponentsInChildren<ILuaInjectable>(true))
            {
                var injections = inj.materialInjections;
                if (injections == null) continue;
                foreach (var mi in injections)
                    if (mi != null) Add(mi.value, Source.LuaInjection, inj as Component);
            }

            return hits;
        }

        /// <summary>
        /// The prefab paths a material check scans: the content roots plus the Sequence
        /// special levels that exist. ContentRootScanner leaves the special levels out on
        /// purpose (they are not user-facing roots for naming, previews or bundling), but
        /// they ship in the Sequence and their materials draw like any other.
        /// Order: roots as given, then special levels; duplicates dropped.
        /// </summary>
        public static List<string> WithSpecialLevels(
            IEnumerable<string> rootPaths, IEnumerable<string> specialLevelPaths, Func<string, bool> exists)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rootPaths != null)
                foreach (var p in rootPaths)
                    if (!string.IsNullOrEmpty(p) && seen.Add(p)) result.Add(p);
            if (specialLevelPaths != null)
                foreach (var p in specialLevelPaths)
                    if (!string.IsNullOrEmpty(p) && (exists == null || exists(p)) && seen.Add(p)) result.Add(p);
            return result;
        }
    }
}
#endif
