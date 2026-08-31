#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.EditorTools.MaterialConversion
{
    // ─────────────────────────────────────────────────────────────────────
    // Shared data structures for the Material Converter pipeline.
    //
    //   MaterialUsageGraph.Build(rootFolder)
    //       → List<MaterialUsage>          (one per .mat, with shader + in-use info)
    //
    //   MaterialConverterPlanner.Plan(usages)
    //       → List<MaterialPlanRow>        (target shader, kind, skip reason)
    //
    //   MaterialConverterExecutor.Apply(approvedRows)
    //       → MaterialExecuteResult        (calls into the static MaterialConverter API)
    //
    // Mirrors the Texture/Animation/Audio optimizer shape. The window binds
    // directly to a List<MaterialPlanRow> and lets the user toggle approval
    // per row before invoking Apply.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the planner decided to do with this material.
    /// </summary>
    public enum MaterialConvertKind
    {
        /// <summary>Already on DreamPark-Universal / DreamPark-Unlit / DreamPark/Particles. No-op.</summary>
        AlreadyConverted,

        /// <summary>Will be flipped to DreamPark-UniversalShader (lit PBR).</summary>
        ConvertOpaqueToUniversal,

        /// <summary>Will be flipped to DreamPark-Unlit (no lighting — emissive / UI / vertex-color art).</summary>
        ConvertOpaqueToUnlit,

        /// <summary>Will be flipped to DreamPark/Particles (vendor particle shader → unified).</summary>
        ConvertParticle,

        /// <summary>Particle material whose vendor shader uses features (flipbook blending, etc.)
        /// DreamPark/Particles v1 doesn't support. Left on its original shader by design.</summary>
        ExoticParticle,

        /// <summary>Material lives inside an FBX / glTF as an embedded asset; can't be
        /// mutated in place. The user has to use "Extract Materials" in the model
        /// import settings before this material is convertible.</summary>
        ReadOnlyEmbedded,

        /// <summary>No prefab or scene under the scanned content root references this
        /// material. Probably stale. Surfaced so the user can delete or ignore.</summary>
        Orphan,
    }

    /// <summary>
    /// One material as seen by the usage graph: where it lives, what shader it's
    /// on, who references it.
    /// </summary>
    [Serializable]
    public class MaterialUsage
    {
        public string assetPath;        // "Assets/Content/Foo/Materials/Wood.mat"
        public string guid;
        public string shaderName;       // current shader's name, e.g. "Universal Render Pipeline/Lit"
        public bool isEmbeddedInModel;  // imported as a sub-asset of an FBX / glTF / etc.

        // Reverse references. usingPrefabs is the set we use to decide "in use".
        // usingScenes is informational — DreamPark content ships as prefabs, but
        // scenes can pull in materials too if the project uses any.
        public List<string> usingPrefabs = new List<string>();
        public List<string> usingScenes  = new List<string>();

        /// <summary>
        /// True when at least one prefab in the scanned content folder uses
        /// this material on a ParticleSystemRenderer. Strongest available
        /// signal that the material is meant for particles, independent of
        /// the source shader's name — catches vendor shaders like
        /// "Hovl Studio/HS_Explosion" that don't have "particle" in their
        /// name but are clearly authored for particle systems. Drives the
        /// planner's ConvertParticle classification.
        /// </summary>
        public bool isUsedByParticleRenderer;

        public bool InUse => usingPrefabs.Count > 0 || usingScenes.Count > 0;
    }

    /// <summary>
    /// One row of the conversion plan, bound to the review UI.
    /// </summary>
    [Serializable]
    public class MaterialPlanRow
    {
        public MaterialUsage usage;
        public MaterialConvertKind kind;
        public string targetShader;     // human-readable target name (empty for no-op kinds)
        public bool approved = true;    // user checkbox in the review UI
        public bool hardSkip;           // greys out the checkbox (ReadOnly, Orphan, AlreadyConverted)
        public string skipReason;       // populated for no-op rows so the UI can surface why

        /// <summary>
        /// Pre-flight diff against DreamPark/Particles for this material. Only
        /// populated for rows where kind == ConvertParticle OR ExoticParticle
        /// — those are the cases where the user benefits from seeing what
        /// won't carry over before approving the conversion. Null for every
        /// other kind (and not serialized — recomputed on each scan).
        /// </summary>
        [NonSerialized] public ParticleDiffReport particleDiff;

        /// <summary>
        /// UI state: "Used by N prefabs" foldout open/closed. Not serialized;
        /// resets on every scan, which is fine — the foldout is a per-session
        /// inspection affordance, not a saved preference.
        /// </summary>
        [NonSerialized] public bool showUsersExpanded;

        /// <summary>
        /// Cached AssetPreview reference for the row's thumbnail. Once
        /// Unity has rendered a real preview sphere for this material, we
        /// hold the reference here so subsequent OnGUI passes don't have
        /// to re-query AssetPreview. Without this cache, Unity's preview
        /// queue + eviction policy can flap a given material's preview
        /// in/out of cache every few frames, which the row drawer renders
        /// as a flashing thumbnail (preview → swatch → preview → swatch).
        ///
        /// May become a destroyed-object reference if Unity disposes the
        /// underlying texture. Always null-check via `(Texture)` (Unity's
        /// overloaded == handles destroyed objects).
        /// </summary>
        [NonSerialized] public UnityEngine.Texture cachedPreview;

        public bool WillBeModified =>
            approved && !hardSkip && string.IsNullOrEmpty(skipReason)
            && (kind == MaterialConvertKind.ConvertOpaqueToUniversal
             || kind == MaterialConvertKind.ConvertOpaqueToUnlit
             || kind == MaterialConvertKind.ConvertParticle);
    }

    /// <summary>
    /// Output of running the plan. Aggregated across all rows so the window
    /// can show "converted 47 materials, skipped 3 exotic particles".
    /// </summary>
    [Serializable]
    public class MaterialExecuteResult
    {
        public int processed;
        public int converted;
        public int failed;
        public int skipped;

        /// <summary>
        /// Number of prefab ParticleSystemRenderers whose Custom Vertex
        /// Streams got reset to the default set after the material
        /// conversion. Populated by the post-convert renderer-refresh
        /// pass (RefreshParticleSystemRenderers). Surface this in the
        /// completion dialog so the user knows their renderer config
        /// changed too — relevant because it fixes the "whole flipbook
        /// visible at once" symptom on Hovl-style flipbook materials.
        /// </summary>
        public int rendererStreamsReset;

        public List<MaterialExecuteRowResult> rows = new List<MaterialExecuteRowResult>();
    }

    [Serializable]
    public class MaterialExecuteRowResult
    {
        public string materialPath;
        public MaterialConvertKind kind;
        public string fromShader;
        public string toShader;
        public bool ok;
        public string error;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Target-shader constants. Single source of truth so the planner, the
    // executor, the right-click MaterialConverter, and the window all agree.
    // ─────────────────────────────────────────────────────────────────────
    public static class DreamParkShaderNames
    {
        public const string Universal = "Shader Graphs/DreamPark-UniversalShader";
        public const string Unlit     = "Shader Graphs/DreamPark-Unlit";
        public const string Particles = "DreamPark/Particles";

        // Substring fragments (case-insensitive) that flag a source shader as
        // a candidate for DreamPark-Unlit. If the source name matches any of
        // these AND isn't already a DreamPark shader, the planner suggests
        // Unlit over Universal. The user can override per-row in the window.
        public static readonly string[] UnlitSourceHints = new[]
        {
            "unlit",                 // URP/Unlit, Mobile/Unlit, etc.
            "/colored",              // Mobile/Colored, plenty of stylized packs
            "/colorshader",
            "/coloronly",
            "/diffuse",              // Mobile/Diffuse, often used as no-light flat shade
            "/vertexlit",            // technically lit but typically used flat in mobile content
            "/toon",                 // most toon shaders are stylized unlit-equivalents
            "selfillum",             // Legacy/Self-Illumin
            "/skybox/",              // skyboxes use unlit-style sampling
            "/sprites",              // SpriteRenderer materials
            "ui/default",            // UI canvas materials
        };

        public static bool IsDreamParkShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            return shaderName == Universal
                || shaderName == Unlit
                || shaderName == Particles;
        }
    }
}
#endif
