#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AnimationOptimization
{
    /// <summary>
    /// Turns a list of <see cref="AnimationUsage"/> rows into a list of
    /// <see cref="AnimationPlanRow"/> proposals. With the FBX-round-trip
    /// architecture, the planner's job is dramatically simpler than the
    /// in-place reducer's was — it just decides skip-vs-modify and which
    /// of two strategies (KeyframeReduction vs Optimal) to default to.
    ///
    /// Hard skips:
    ///   - Legacy clips (driven by Animation component, not Animator).
    ///   - Read-only assets (in immutable packages).
    ///   - Standalone orphans (no FBX source detected; can't be optimized
    ///     in v1).
    ///   - Standalone-with-source rows where the standalone has diverged
    ///     from the FBX (hand-edited after extraction — round-tripping
    ///     would clobber the edits).
    ///
    /// Soft skips (visible but unticked; user can flip on):
    ///   - Already at Optimal compression and bytes are reasonable.
    ///   - Orphan-by-usage (no controller/prefab references — consider
    ///     deleting instead).
    /// </summary>
    public static class AnimationOptimizationPlanner
    {
        public static List<AnimationPlanRow> Plan(List<AnimationUsage> usages)
        {
            var plan = new List<AnimationPlanRow>(usages.Count);
            foreach (var u in usages)
                plan.Add(PlanOne(u));
            return plan;
        }

        private static AnimationPlanRow PlanOne(AnimationUsage u)
        {
            var row = new AnimationPlanRow
            {
                usage = u,
                approved = true,
                strategy = OptimizationStrategy.Optimal,
            };

            // ── Hard skips ─────────────────────────────────────────────
            if (u.clipKind == AnimationClipKind.Legacy)
                return HardSkipRow(row, "Legacy clip — driven by Animation component. Optimize manually.");

            if (u.readOnly)
                return HardSkipRow(row, "Read-only asset (package). Can't modify.");

            if (u.rowKind == AnimationRowKind.StandaloneOrphan)
                return HardSkipRow(row, "No FBX source detected. Standalone .anim files without a source FBX can't be re-extracted; manual review required.");

            if (u.rowKind == AnimationRowKind.StandaloneWithSource &&
                u.fbxSource != null && u.fbxSource.divergedFromSource)
                return HardSkipRow(row,
                    $"Standalone has diverged from {System.IO.Path.GetFileName(u.fbxSource.fbxPath)} — re-extraction would lose your edits. Resolve manually.");

            if (u.totalKeyframes <= 0 || (u.floatCurveCount + u.objectCurveCount) == 0)
                return HardSkipRow(row, "No curves to optimize.");

            // ── Soft skips ─────────────────────────────────────────────
            string softSkipReason = null;

            // Already-optimal detection. If the host FBX is already on
            // "Optimal" AND the file isn't huge, there's likely nothing to
            // gain. Still a soft skip — user can override if the importer
            // settings are stale.
            if (u.currentCompression == ModelImporterAnimationCompression.Optimal &&
                u.fileBytes < 2 * 1024 * 1024)
            {
                softSkipReason = "Already on Optimal compression and file is small. Re-run only if importer settings changed.";
            }
            else if (u.kind == AnimationUsageKind.UnusedController)
            {
                softSkipReason = "Referenced by controllers no prefab uses — consider deleting instead.";
            }
            else if (u.kind == AnimationUsageKind.Orphan && u.rowKind == AnimationRowKind.StandaloneWithSource)
            {
                softSkipReason = "No controller or prefab references found — verify before optimizing.";
            }

            if (softSkipReason != null)
            {
                row.approved = false;
                row.skipReason = softSkipReason;
            }

            // ── Strategy default ───────────────────────────────────────
            // Humanoid clips bake into muscle curves where the importer's
            // "Optimal" mode handles the math correctly; we keep them on
            // Optimal too. The user can drop to KeyframeReduction per-row
            // if they suspect Optimal is being too aggressive.
            row.strategy = OptimizationStrategy.Optimal;

            // ── Estimate after-bytes ───────────────────────────────────
            row.estimatedAfterBytes = EstimateBytes(u, row);

            // If the estimate predicts zero or negative savings AND we
            // aren't already soft-skipped, soft-skip with a sensible
            // reason so the row doesn't appear "approve me" for no reason.
            if (row.estimatedAfterBytes >= u.fileBytes && string.IsNullOrEmpty(row.skipReason))
            {
                row.approved = false;
                row.skipReason = "Predicted no savings.";
            }

            return row;
        }

        private static AnimationPlanRow HardSkipRow(AnimationPlanRow row, string reason)
        {
            row.approved = false;
            row.hardSkip = true;
            row.skipReason = reason;
            row.strategy = OptimizationStrategy.KeepAsIs;
            row.estimatedAfterBytes = row.usage.fileBytes;
            return row;
        }

        /// <summary>
        /// Estimate file size after Unity's importer compression runs.
        /// The math is empirical — Unity's Optimal mode typically gives
        /// 5–15× reduction on bloated standalone .anim files (the spider
        /// pack is a textbook case) and modest reductions on FBX clips
        /// that are already in a tighter format.
        /// </summary>
        public static long EstimateBytes(AnimationUsage u, AnimationPlanRow row)
        {
            if (u.fileBytes <= 0) return 0;

            // Rough reduction factor by strategy and current state. We err
            // on the conservative side — Unity's actual reducer usually
            // exceeds these numbers.
            double factor = 1.0;
            switch (row.strategy)
            {
                case OptimizationStrategy.Optimal:
                    factor = u.currentCompression == ModelImporterAnimationCompression.Optimal ? 1.0 : 0.20;
                    break;
                case OptimizationStrategy.KeyframeReduction:
                    factor = u.currentCompression == ModelImporterAnimationCompression.KeyframeReduction ? 1.0 : 0.40;
                    break;
                case OptimizationStrategy.KeepAsIs:
                    factor = 1.0;
                    break;
            }

            // For FBX sub-clips, the fileBytes is the full FBX size. We
            // can't shrink the mesh + materials part — only the embedded
            // animations. Estimate the animation portion as
            // 1 - factor of the per-clip share, applied just to that share.
            if (u.rowKind == AnimationRowKind.FbxSubClip)
            {
                // Animation data inside an FBX is typically 30–60% of file
                // size for animation-heavy models, less for static meshes.
                // Use 40% as the default attribution — refined per-clip if
                // we ever break out per-clip byte accounting.
                double clipShare = 0.40;
                double newClipShare = clipShare * factor;
                double newTotal = u.fileBytes * (1 - clipShare + newClipShare);
                long est = (long)newTotal;
                if (est < 1024) est = 1024;
                if (est > u.fileBytes) est = u.fileBytes;
                return est;
            }

            // Standalone .anim — the file IS the animation, so factor applies
            // directly to the whole file size.
            {
                long est = (long)(u.fileBytes * factor);
                if (est < 256) est = 256;
                if (est > u.fileBytes) est = u.fileBytes;
                return est;
            }
        }
    }
}
#endif
