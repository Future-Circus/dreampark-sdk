#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.EditorTools.TextureOptimization
{
    // ─────────────────────────────────────────────────────────────────────
    // Shared data structures for the Texture Optimizer pipeline.
    //
    // The flow is:
    //
    //   TextureUsageGraph.Build(rootFolder)
    //       → List<TextureUsage>          (one per texture, with bounds + usage classification)
    //
    //   TextureOptimizationPlanner.Plan(usages)
    //       → List<TexturePlanRow>        (target format + resolution + estimated savings)
    //
    //   TextureOptimizationExecutor.Apply(approvedRows)
    //       → ExecuteResult               (rewritten source files + tightened importer settings)
    //
    // Keeping these as plain data classes makes the pipeline testable, lets
    // the UI sort/filter/search without holding scene refs, and lets us emit
    // a JSON report for QA after a batch run.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How a texture is referenced in the project. Drives whether we can
    /// confidently auto-resize it (a world prop has bounds; a UI sprite
    /// doesn't).
    /// </summary>
    public enum TextureUsageKind
    {
        /// <summary>Used by a Renderer material — has world-space bounds.</summary>
        WorldRenderer,

        /// <summary>UI Image/RawImage/Sprite in a Canvas — pixel-space, no world size.</summary>
        UI,

        /// <summary>Particle system texture sheet — bounds unreliable.</summary>
        Particle,

        /// <summary>Skybox / reflection probe / cubemap source.</summary>
        Skybox,

        /// <summary>Lightmap output baked by the editor — leave alone.</summary>
        Lightmap,

        /// <summary>Decoder couldn't find any reference. Likely orphaned.</summary>
        Orphan,

        /// <summary>Used by a material but no prefab references that material — probably stale.</summary>
        UnusedMaterial,
    }

    /// <summary>
    /// Roles a texture can play inside a material. Affects whether we treat
    /// it as a candidate for JPG (color/emission) vs forced PNG (normal,
    /// metallic, roughness, mask — sRGB-sensitive linear data).
    /// </summary>
    public enum TextureRole
    {
        Unknown,
        Albedo,
        Normal,
        MaskOrMetallic,
        Emission,
        Occlusion,
        Roughness,
        Detail,
    }

    /// <summary>
    /// One discovered texture along with everything we learned about its
    /// usage in the project. Produced by <see cref="TextureUsageGraph"/>.
    /// </summary>
    [Serializable]
    public class TextureUsage
    {
        // ── Identity ─────────────────────────────────────────────────────
        public string assetPath;          // "Assets/Content/Foo/Textures/Bar.tga"
        public string guid;
        public string extension;          // ".tga", ".tif", ".png", ".jpg", ".tiff"
        public long fileBytes;            // sizeof the source file on disk

        // ── Imported texture info (from AssetDatabase) ──────────────────
        public int sourceWidth;           // pixel width Unity sees post-import
        public int sourceHeight;
        public bool hasAlphaChannel;      // TextureImporter.DoesSourceTextureHaveAlpha
        public bool isReadable;
        public bool sRGBTexture;
        public int currentMaxSize;        // TextureImporter.maxTextureSize
        public string currentImporterType;// "Default", "NormalMap", "Sprite", "Lightmap", "Cookie"

        // ── Usage graph ─────────────────────────────────────────────────
        public TextureUsageKind kind;
        public TextureRole role;
        public List<string> usingMaterials = new List<string>();  // material asset paths
        public List<string> usingPrefabs = new List<string>();    // prefab asset paths

        /// <summary>
        /// Largest world-space bounds size (max-of-XYZ in meters) of any
        /// renderer that uses any material that uses this texture. Zero if
        /// we never found a world renderer. Drives the resolution decision.
        /// </summary>
        public float maxRendererSizeMeters;

        /// <summary>
        /// Asset path of the largest renderer's containing prefab. Lets us
        /// ping the offending prop in the Project window from the UI.
        /// </summary>
        public string largestUseExample;

        /// <summary>
        /// Free-form reason why we couldn't classify this texture (only
        /// populated when kind == Orphan or UnusedMaterial). Surfaced in
        /// the review UI so the creator can decide what to do.
        /// </summary>
        public string note;
    }

    /// <summary>
    /// One row of the optimization plan: what the optimizer intends to do
    /// to a given texture, and what the savings are expected to be. The
    /// review UI binds directly to a list of these.
    /// </summary>
    [Serializable]
    public class TexturePlanRow
    {
        public TextureUsage usage;           // source info (read-only after planning)

        // ── Decisions (mutable: the review UI lets the user override) ──
        public bool approved = true;         // user checkbox in the review UI
        /// <summary>
        /// True for rows the planner refuses to mutate ever (lightmaps,
        /// cubemaps, cookies). The review UI greys out the checkbox so
        /// the user can't accidentally force a re-encode of a baked
        /// output. Already-tight and orphan rows are SOFT skips — they
        /// remain toggleable so the user can force the re-encode.
        /// </summary>
        public bool hardSkip;
        public TargetFormat targetFormat;    // PNG or JPG
        public int targetMaxSize;            // 256, 512, 1024, 2048 — clamped to source size
        public int targetWidth;              // computed = min(sourceWidth, targetMaxSize) rounded down to nearest POT
        public int targetHeight;
        public int targetImporterMaxSize;    // value to set on TextureImporter.maxTextureSize
        public bool useCrunchCompression = true;
        public int crunchQuality = 50;       // 0-100; 50 is Unity's default sweet spot

        // ── Estimates ────────────────────────────────────────────────────
        public long estimatedAfterBytes;
        public string skipReason;            // populated when we're choosing not to mutate this one

        public bool WillBeModified =>
            approved && string.IsNullOrEmpty(skipReason);

        public long EstimatedSavedBytes =>
            WillBeModified ? Math.Max(0, usage.fileBytes - estimatedAfterBytes) : 0;

        public float EstimatedSavingsPercent =>
            usage.fileBytes > 0 && WillBeModified
                ? 100f * (usage.fileBytes - estimatedAfterBytes) / usage.fileBytes
                : 0f;
    }

    public enum TargetFormat
    {
        /// <summary>Keep the texture exactly as-is (used for skip rows and lightmaps).</summary>
        KeepAsIs,

        /// <summary>Re-encode as PNG (transparent textures, normal maps, masks).</summary>
        PNG,

        /// <summary>Re-encode as JPG quality 90 (opaque color textures).</summary>
        JPG,
    }

    /// <summary>
    /// Output of executing the plan. Aggregated across all rows so the
    /// final report can show "saved 1.4 GB across 312 textures".
    /// </summary>
    [Serializable]
    public class ExecuteResult
    {
        public int processed;
        public int succeeded;
        public int failed;
        public long bytesBefore;
        public long bytesAfter;

        public List<ExecuteRowResult> rows = new List<ExecuteRowResult>();

        public long BytesSaved => Math.Max(0, bytesBefore - bytesAfter);

        public float PercentSaved =>
            bytesBefore > 0 ? 100f * (bytesBefore - bytesAfter) / bytesBefore : 0f;
    }

    [Serializable]
    public class ExecuteRowResult
    {
        public string sourcePath;      // path before mutation (e.g. "...Bar.tga")
        public string finalPath;       // path after mutation (e.g. "...Bar.png")
        public bool ok;
        public long bytesBefore;
        public long bytesAfter;
        public string error;           // populated when ok=false
    }

    // ─────────────────────────────────────────────────────────────────────
    // Resolution policy. Single source of truth so the planner and the UI's
    // override dropdown agree.
    // ─────────────────────────────────────────────────────────────────────
    public static class TextureSizingPolicy
    {
        /// <summary>
        /// Choose a target Max-Size for a texture based on the largest
        /// world-space size (in meters) of any renderer using it.
        ///
        /// Thresholds are deliberately conservative — a creator can always
        /// bump a row up via the override dropdown in the review UI. The
        /// thresholds assume an MR play space where the player views props
        /// from roughly 0.3-3m. Tweak in one place if telemetry says
        /// otherwise.
        /// </summary>
        public static int RecommendMaxSize(float largestAxisMeters)
        {
            if (largestAxisMeters <= 0f)    return 1024; // unknown → assume worst case
            if (largestAxisMeters < 0.20f)  return 256;  // tiny props: coins, switches, candles
            if (largestAxisMeters < 0.75f)  return 512;  // hand-held: weapons, books, mugs
            if (largestAxisMeters < 3.0f)   return 1024; // characters, statues, furniture
            return 1024;                                 // hard cap — anything bigger is rare
                                                         // and should be hand-tuned per asset.
        }

        public static readonly int[] AllowedSizes = { 256, 512, 1024, 2048 };
    }
}
#endif
