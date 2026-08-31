#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AnimationOptimization
{
    // ─────────────────────────────────────────────────────────────────────
    // Shared data structures for the Animation Optimizer pipeline.
    //
    // The flow:
    //
    //   AnimationUsageGraph.Build(rootFolder)
    //       → List<AnimationUsage>      (one per clip — standalone .anim AND FBX sub-clips)
    //
    //   AnimationOptimizationPlanner.Plan(usages)
    //       → List<AnimationPlanRow>    (strategy + error tolerances + estimated savings)
    //
    //   AnimationOptimizationExecutor.Apply(approvedRows)
    //       → ExecuteResult             (FBX import-settings flip; for standalone rows,
    //                                    re-extract via CopySerialized with GUID preservation)
    //
    // The destructive v1 path (in-place curve simplification + flag-flips on
    // the .anim YAML) is gone. Every path goes through Unity's own keyframe
    // reducer in ModelImporter — the same code that runs when you set
    // "Anim. Compression = Optimal" in the inspector. That algorithm is
    // quaternion-aware and battle-tested; the previous custom simplifier
    // treated rotation x/y/z/w as four independent float curves and
    // broke quaternion normalization, which is what wrecked the spider
    // rig the first time.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What kind of asset this row represents. Drives the executor path.
    /// </summary>
    public enum AnimationRowKind
    {
        /// <summary>
        /// AnimationClip embedded inside a .fbx / .ma / .mb / model file.
        /// Optimization is a one-call ModelImporter setting flip — no GUID
        /// juggling needed because the sub-clip stays as a sub-asset of
        /// the model.
        /// </summary>
        FbxSubClip,

        /// <summary>
        /// Standalone .anim file extracted from an FBX we located. Round-trip
        /// path: tighten the FBX importer settings, re-import, then copy the
        /// now-compressed sub-clip's data to the standalone path while
        /// carrying the original .meta forward so the GUID is preserved.
        /// </summary>
        StandaloneWithSource,

        /// <summary>
        /// Standalone .anim with no detectable FBX source. We can't route it
        /// through Unity's reducer, so v1 leaves these alone. (Future:
        /// quaternion-aware custom reducer for this case.)
        /// </summary>
        StandaloneOrphan,
    }

    /// <summary>
    /// How a clip is referenced in the project. Drives whether it's safe to
    /// compress (a clip nothing references is a candidate for deletion,
    /// not optimization).
    /// </summary>
    public enum AnimationUsageKind
    {
        /// <summary>Referenced by at least one AnimatorController or PlayableAsset that is itself used by a prefab.</summary>
        Active,

        /// <summary>Referenced by a controller, but no prefab references the controller — probably stale.</summary>
        UnusedController,

        /// <summary>Found no references at all. Likely orphaned.</summary>
        Orphan,
    }

    /// <summary>
    /// What kind of clip Unity thinks this is. Affects safety of compression.
    /// </summary>
    public enum AnimationClipKind
    {
        Generic,
        Humanoid,
        Legacy,
    }

    /// <summary>
    /// Detected FBX source for a standalone .anim file. Populated by the
    /// usage graph when it finds a model file with a matching sub-clip.
    /// </summary>
    [Serializable]
    public class FbxSourceMatch
    {
        public string fbxPath;          // "Assets/Content/.../Spider 1.fbx"
        public string subClipName;      // "Idle" — name of the AnimationClip inside the FBX
        public float matchScore;        // composite score (see usage graph)
        public float pathJaccard;       // 0..1 — how much the bone-path sets overlap

        /// <summary>
        /// True when the standalone .anim and the FBX's sub-clip have
        /// substantially different bindings or lengths — implying somebody
        /// edited the standalone after extraction and re-extracting would
        /// lose their work. The planner forces these to KeepAsIs.
        /// </summary>
        public bool divergedFromSource;
    }

    /// <summary>
    /// One discovered animation clip along with everything we learned about
    /// its on-disk shape, project usage, and (if standalone) detected FBX
    /// source. Produced by <see cref="AnimationUsageGraph"/>.
    /// </summary>
    [Serializable]
    public class AnimationUsage
    {
        // ── Identity ─────────────────────────────────────────────────────
        public AnimationRowKind rowKind;
        public string assetPath;          // for FbxSubClip this is the FBX path; for standalone it's the .anim path
        public string clipName;           // sub-clip name in FBX; for standalone = file name without extension
        public string guid;
        public long fileBytes;            // size of the host file on disk (FBX size for sub-clips)
        public bool readOnly;             // true for assets under an immutable package

        // ── Clip metrics (read via AnimationUtility) ─────────────────────
        public AnimationClipKind clipKind;
        public float length;              // seconds
        public float frameRate;           // typically 24, 30, 60
        public int floatCurveCount;
        public int objectCurveCount;
        public int totalKeyframes;
        public int constantCurveCount;

        // ── FBX source (only populated for StandaloneWithSource) ─────────
        public FbxSourceMatch fbxSource;

        // ── Current importer state (only meaningful for FbxSubClip and StandaloneWithSource) ──
        /// <summary>Current `Anim. Compression` setting on the model importer governing this clip.</summary>
        public ModelImporterAnimationCompression currentCompression;

        // ── Usage graph ──────────────────────────────────────────────────
        public AnimationUsageKind kind;
        public List<string> usingControllers = new List<string>();
        public List<string> usingPrefabs = new List<string>();
        public string largestUseExample;
        public string note;
    }

    /// <summary>
    /// What we propose doing to a clip. Replaces the old custom-reducer
    /// strategies — every option here is routed through Unity's built-in
    /// keyframe reducer on the ModelImporter.
    /// </summary>
    public enum OptimizationStrategy
    {
        /// <summary>Don't touch this clip (hard skips and user opt-outs).</summary>
        KeepAsIs,

        /// <summary>
        /// <c>Anim. Compression = KeyframeReduction</c>. Removes redundant
        /// keyframes within the configured error tolerances. Conservative —
        /// the keys are gone but the curve data is still stored as full-fidelity
        /// floats.
        /// </summary>
        KeyframeReduction,

        /// <summary>
        /// Recommended default. <c>Anim. Compression = Optimal</c>. Unity
        /// picks the best of keyframe reduction or dense compression on a
        /// per-clip basis. This is what the FBX importer's "Optimal" dropdown
        /// does.
        /// </summary>
        Optimal,
    }

    /// <summary>
    /// One row of the optimization plan: what the optimizer intends to do
    /// to a given clip, and what the savings are expected to be.
    /// </summary>
    [Serializable]
    public class AnimationPlanRow
    {
        public AnimationUsage usage;

        // ── Decisions (mutable: the review UI lets the user override) ──
        public bool approved = true;
        public bool hardSkip;
        public OptimizationStrategy strategy = OptimizationStrategy.Optimal;

        /// <summary>
        /// Per-channel error tolerances passed to <c>ModelImporter</c>.
        /// Defaults match Unity's own (0.5 / 0.5 / 0.5).
        /// </summary>
        public float rotationError = 0.5f;
        public float positionError = 0.5f;
        public float scaleError = 0.5f;

        // ── Estimates ────────────────────────────────────────────────────
        public long estimatedAfterBytes;
        public string skipReason;

        public bool WillBeModified =>
            approved && string.IsNullOrEmpty(skipReason) && strategy != OptimizationStrategy.KeepAsIs;

        public long EstimatedSavedBytes =>
            WillBeModified ? Math.Max(0, usage.fileBytes - estimatedAfterBytes) : 0;

        public float EstimatedSavingsPercent =>
            usage.fileBytes > 0 && WillBeModified
                ? 100f * (usage.fileBytes - estimatedAfterBytes) / usage.fileBytes
                : 0f;
    }

    /// <summary>
    /// Output of executing the plan. Aggregated across all rows.
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
        public string assetPath;
        public string clipName;
        public AnimationRowKind rowKind;
        public bool ok;
        public long bytesBefore;
        public long bytesAfter;
        public string error;
    }
}
#endif
