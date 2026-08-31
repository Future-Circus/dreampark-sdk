#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.EditorTools.TextureOptimization
{
    /// <summary>
    /// Turns a list of <see cref="TextureUsage"/> rows into a list of
    /// <see cref="TexturePlanRow"/> proposals. The planner makes three
    /// decisions per texture:
    ///
    ///   1. Skip or modify?       (lightmaps, skyboxes, normal maps, and
    ///                              already-tight textures are skipped)
    ///   2. Target format?        (PNG if alpha is actually used, JPG otherwise)
    ///   3. Target resolution?    (256/512/1024 from world bounds — falls
    ///                              back to current Max Size when no bounds)
    ///
    /// The planner emits *proposals*, not commits. The review UI shows
    /// these to the creator who can flip individual rows or change a
    /// row's target resolution before the executor runs.
    /// </summary>
    public static class TextureOptimizationPlanner
    {
        /// <summary>
        /// Estimated bytes-per-pixel after re-encode, by format. These are
        /// engineering rule-of-thumb numbers based on typical Quest-grade
        /// content (photoreal-ish, not flat-color cartoon). Used to size
        /// the "estimated after" column in the review UI — close enough to
        /// drive prioritization, not a guarantee. The executor reports the
        /// real number once it runs.
        /// </summary>
        private const double BytesPerPixelPng = 1.5;     // ~RGBA8 at typical PNG compression
        private const double BytesPerPixelJpg = 0.25;    // Quality 90 baseline

        /// <summary>JPG encoding quality for opaque textures.</summary>
        public const int JpgQuality = 90;

        public static List<TexturePlanRow> Plan(List<TextureUsage> usages)
        {
            var plan = new List<TexturePlanRow>(usages.Count);
            foreach (var u in usages)
                plan.Add(PlanOne(u));
            return plan;
        }

        private static TexturePlanRow PlanOne(TextureUsage u)
        {
            var row = new TexturePlanRow
            {
                usage = u,
                approved = true,
            };

            // ── Hard-skip cases — never auto-mutate ──────────────────────
            // These rows get hardSkip = true so the review UI greys out
            // the checkbox: re-encoding a baked lightmap or a cubemap is
            // always wrong, not just usually wrong.
            if (u.kind == TextureUsageKind.Lightmap)
                return HardSkipRow(row, "Lightmap — leave to bake pipeline.");
            if (u.kind == TextureUsageKind.Skybox)
                return HardSkipRow(row, "Cubemap / skybox source — preserve format.");
            if (u.currentImporterType == "Cookie")
                return HardSkipRow(row, "Light cookie — preserve format.");

            // Normal maps must stay PNG (lossless, linear color space).
            // Their *Max Size* can still be tightened, so we plan a row but
            // pin the format.
            bool forcePng = u.role == TextureRole.Normal
                         || u.role == TextureRole.MaskOrMetallic
                         || u.role == TextureRole.Roughness
                         || u.role == TextureRole.Occlusion
                         || !u.sRGBTexture
                         || u.currentImporterType == "NormalMap";

            // Soft-skip: textures the user didn't enable to be auto-resized
            // because we couldn't compute bounds. UI textures, particles,
            // and unused-material rows show in the review with skip reasons
            // — the creator can manually flip approved=true and pick a
            // size from the dropdown if they want.
            string softSkipReason = null;
            if (u.kind == TextureUsageKind.UI)
                softSkipReason = "UI texture — review manually (no world bounds).";
            else if (u.kind == TextureUsageKind.Particle)
                softSkipReason = "Particle texture — review manually (bounds dynamic).";
            else if (u.kind == TextureUsageKind.Orphan)
                softSkipReason = "No prefab/material references found — verify before resizing.";
            else if (u.kind == TextureUsageKind.UnusedMaterial)
                softSkipReason = "Used by materials no prefab references — consider deleting.";

            // ── Format decision ─────────────────────────────────────────
            if (forcePng)
            {
                row.targetFormat = TargetFormat.PNG;
            }
            else if (u.hasAlphaChannel)
            {
                // Alpha channel present — must be PNG. (The executor does
                // a sample-the-alpha-channel pass to confirm; if alpha is
                // all-255 it'll downgrade to JPG on the fly.)
                row.targetFormat = TargetFormat.PNG;
            }
            else
            {
                row.targetFormat = TargetFormat.JPG;
            }

            // ── Resolution decision ─────────────────────────────────────
            int recommended = TextureSizingPolicy.RecommendMaxSize(u.maxRendererSizeMeters);
            // Never propose a larger size than the source — we don't
            // upsample, only down.
            int srcLargest = Mathf.Max(u.sourceWidth, u.sourceHeight);
            int target = srcLargest > 0 ? Mathf.Min(recommended, srcLargest) : recommended;

            row.targetMaxSize = target;
            row.targetImporterMaxSize = target;
            (row.targetWidth, row.targetHeight) = ScaleKeepingAspect(u.sourceWidth, u.sourceHeight, target);

            // ── Already-tight detection ─────────────────────────────────
            // If the source is already at or below the target AND already
            // in a sensible format (PNG with alpha or JPG without), skip.
            // This is a SOFT skip — the user can still force a re-encode
            // by ticking the row, in case our estimate was wrong.
            bool alreadyRightFormat =
                (row.targetFormat == TargetFormat.PNG && (u.extension == ".png")) ||
                (row.targetFormat == TargetFormat.JPG && (u.extension == ".jpg" || u.extension == ".jpeg"));
            bool alreadyRightSize = srcLargest > 0 && srcLargest <= target;

            if (alreadyRightFormat && alreadyRightSize && softSkipReason == null)
                return SoftSkipRow(row, $"Already {u.extension.TrimStart('.')} @ {u.sourceWidth}×{u.sourceHeight} — no change needed.");

            // Honor the soft-skip from above (but the row stays
            // user-toggleable in the UI).
            if (softSkipReason != null)
            {
                row.approved = false;
                row.skipReason = softSkipReason;
            }

            // ── Estimate after-bytes ────────────────────────────────────
            row.estimatedAfterBytes = EstimateBytes(row.targetWidth, row.targetHeight, row.targetFormat);

            // Guard: if our estimate says the after-size is BIGGER than
            // the source (can happen for tiny lossless PNGs already at
            // target resolution), skip — the user gets no benefit.
            if (row.estimatedAfterBytes >= u.fileBytes && string.IsNullOrEmpty(row.skipReason))
                return SoftSkipRow(row, "Re-encode wouldn't shrink the file — already efficient.");

            return row;
        }

        /// <summary>
        /// Hard skip — the optimizer will never re-encode this texture.
        /// The review UI greys out the checkbox.
        /// </summary>
        private static TexturePlanRow HardSkipRow(TexturePlanRow row, string reason)
        {
            row.approved = false;
            row.hardSkip = true;
            row.skipReason = reason;
            row.targetFormat = TargetFormat.KeepAsIs;
            row.estimatedAfterBytes = row.usage.fileBytes;
            return row;
        }

        /// <summary>
        /// Soft skip — the planner doesn't recommend re-encoding, but the
        /// user can override by ticking the checkbox. Used for orphans,
        /// already-tight files, no-win re-encodes, etc.
        /// </summary>
        private static TexturePlanRow SoftSkipRow(TexturePlanRow row, string reason)
        {
            row.approved = false;
            row.hardSkip = false;
            row.skipReason = reason;
            // Leave targetFormat / targetMaxSize at the planner's preferred
            // values so the row is immediately re-encodable if the user
            // ticks it. The window keeps the dropdowns active for soft
            // skips.
            if (row.targetFormat == TargetFormat.KeepAsIs)
            {
                row.targetFormat = row.usage.hasAlphaChannel ? TargetFormat.PNG : TargetFormat.JPG;
                row.estimatedAfterBytes = EstimateBytes(row.targetWidth, row.targetHeight, row.targetFormat);
            }
            return row;
        }

        /// <summary>
        /// Estimate the byte size of a re-encoded texture. The numbers are
        /// deliberately rough — they exist to drive the review UI's
        /// "estimated savings" column, not to make billing decisions.
        /// </summary>
        public static long EstimateBytes(int width, int height, TargetFormat format)
        {
            if (width <= 0 || height <= 0) return 0;
            long pixels = (long)width * height;
            switch (format)
            {
                case TargetFormat.PNG:     return (long)(pixels * BytesPerPixelPng);
                case TargetFormat.JPG:     return (long)(pixels * BytesPerPixelJpg);
                case TargetFormat.KeepAsIs:
                default:                   return 0;
            }
        }

        /// <summary>
        /// Scale (w, h) so the largest dimension equals <paramref name="maxDim"/>,
        /// preserving aspect ratio. Output is rounded to even pixels (Unity
        /// is friendlier to even-dim textures even when we don't enforce POT).
        /// </summary>
        public static (int w, int h) ScaleKeepingAspect(int width, int height, int maxDim)
        {
            if (width <= 0 || height <= 0) return (maxDim, maxDim);
            int srcLargest = Mathf.Max(width, height);
            if (srcLargest <= maxDim) return (width, height);

            float ratio = (float)maxDim / srcLargest;
            int w = Mathf.Max(1, Mathf.RoundToInt(width * ratio));
            int h = Mathf.Max(1, Mathf.RoundToInt(height * ratio));
            // Round to even.
            if ((w & 1) == 1) w++;
            if ((h & 1) == 1) h++;
            return (w, h);
        }
    }
}
#endif
