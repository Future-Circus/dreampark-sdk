#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AnimationOptimization
{
    /// <summary>
    /// Applies a list of approved <see cref="AnimationPlanRow"/>s by routing
    /// every row through Unity's own keyframe reducer on the ModelImporter.
    /// Two execution paths, chosen by the row's <see cref="AnimationRowKind"/>:
    ///
    ///   • <b>FbxSubClip</b> — just flip the host FBX's importer settings
    ///     (<c>animationCompression</c>, plus the three error tolerances)
    ///     and call <c>SaveAndReimport</c>. The sub-clip's curves are
    ///     replaced in-place by Unity's reducer; no GUID work required
    ///     because the sub-clip stays as a sub-asset of the FBX.
    ///
    ///   • <b>StandaloneWithSource</b> — three-step dance:
    ///       1. Stash the standalone .anim's .meta file content.
    ///       2. Set the source FBX's importer settings and re-import. Now
    ///          the FBX's embedded sub-clip carries the reduced curves.
    ///       3. CopySerialized from that sub-clip into a fresh AnimationClip,
    ///          DeleteAsset the original standalone, CreateAsset the new one
    ///          at the same path, then overwrite the freshly-generated .meta
    ///          with the stashed content. Unity's next ImportAsset call
    ///          picks up the carried-over GUID, preserving every reference.
    ///
    /// We never write into the spider .anim YAML ourselves — Unity does it.
    /// That eliminates the entire class of "broke quaternion normalization"
    /// bug that wrecked the first attempt.
    /// </summary>
    public static class AnimationOptimizationExecutor
    {
        public static ExecuteResult Apply(
            IList<AnimationPlanRow> rows,
            bool dryRun,
            Action<float, string> onProgress = null)
        {
            var result = new ExecuteResult();
            if (rows == null) return result;

            // Snapshot the work list — caller mutation during the run would
            // desync us.
            var work = new List<AnimationPlanRow>();
            foreach (var r in rows) if (r != null && r.WillBeModified) work.Add(r);
            if (work.Count == 0) return result;

            // Group rows by host FBX path so we only re-import each FBX
            // once even when multiple of its sub-clips (or standalone
            // descendants) are approved with the same settings.
            //   - FbxSubClip rows are keyed by their assetPath (the FBX).
            //   - StandaloneWithSource rows are keyed by fbxSource.fbxPath.
            // Within a group, every row contributes its strategy + tolerances;
            // we resolve to the strongest setting any row asked for (Optimal
            // beats KeyframeReduction beats Off) and the tightest tolerance
            // any row specified — that way a per-row stricter tolerance
            // wins instead of being silently dropped.
            var groups = GroupByFbx(work);

            AssetDatabase.StartAssetEditing();
            try
            {
                int groupIndex = 0;
                foreach (var group in groups)
                {
                    groupIndex++;
                    string fbxPath = group.Key;
                    onProgress?.Invoke(0.05f + 0.55f * groupIndex / Mathf.Max(1, groups.Count),
                        $"Re-importing {Path.GetFileName(fbxPath)}... ({groupIndex}/{groups.Count})");

                    ApplyImporterSettingsForGroup(fbxPath, group.Value, dryRun, result);
                }
            }
            finally
            {
                // Re-import everyone now that all importer settings are set.
                AssetDatabase.StopAssetEditing();
                if (!dryRun)
                {
                    AssetDatabase.Refresh();
                }
            }

            // Pass 2: for StandaloneWithSource rows only, copy the reduced
            // sub-clip data back to the standalone .anim path with GUID
            // preservation. FBX sub-clip rows are already done at this point
            // — their reduction is in-place inside the FBX, no further
            // action needed.
            int standaloneIndex = 0;
            int standaloneCount = work.Count(r => r.usage.rowKind == AnimationRowKind.StandaloneWithSource);
            foreach (var row in work)
            {
                if (row.usage.rowKind != AnimationRowKind.StandaloneWithSource) continue;
                standaloneIndex++;
                onProgress?.Invoke(0.6f + 0.35f * standaloneIndex / Mathf.Max(1, standaloneCount),
                    $"Carrying compressed data back to {Path.GetFileName(row.usage.assetPath)}...");
                var rr = ApplyStandaloneRoundTrip(row, dryRun);
                result.rows.Add(rr);
                result.processed++;
                if (rr.ok) result.succeeded++; else result.failed++;
                result.bytesBefore += rr.bytesBefore;
                result.bytesAfter += rr.bytesAfter;
            }

            // Pass 3: re-stat FBX sub-clip rows now that the importer has
            // run. (We deferred this until after the round-trip pass so we
            // don't double-count bytes for rows whose data was re-extracted
            // out of the FBX into a standalone — those bytes don't shrink
            // the FBX, just the .anim.)
            foreach (var entry in result.rows)
            {
                if (entry.rowKind != AnimationRowKind.FbxSubClip) continue;
                if (!entry.ok) continue;
                long realAfter = TryFileLength(entry.assetPath);
                if (realAfter > 0)
                {
                    result.bytesAfter -= entry.bytesAfter;
                    entry.bytesAfter = realAfter;
                    result.bytesAfter += realAfter;
                }
            }

            onProgress?.Invoke(1f, "Done.");
            return result;
        }

        // ─── Step 1: group rows by host FBX ─────────────────────────────

        /// <summary>
        /// Returns a dictionary keyed by FBX asset path. Each value is the
        /// resolved settings (strategy + tolerances) for that FBX, after
        /// merging every row that targets it.
        /// </summary>
        private static Dictionary<string, FbxGroupSettings> GroupByFbx(List<AnimationPlanRow> work)
        {
            var groups = new Dictionary<string, FbxGroupSettings>();
            foreach (var row in work)
            {
                string fbxPath = row.usage.rowKind == AnimationRowKind.FbxSubClip
                    ? row.usage.assetPath
                    : row.usage.fbxSource?.fbxPath;

                if (string.IsNullOrEmpty(fbxPath)) continue;

                if (!groups.TryGetValue(fbxPath, out var settings))
                {
                    settings = new FbxGroupSettings
                    {
                        compression = ToImporterCompression(row.strategy),
                        rotationError = row.rotationError,
                        positionError = row.positionError,
                        scaleError = row.scaleError,
                        rows = new List<AnimationPlanRow>(),
                    };
                    groups[fbxPath] = settings;
                }
                else
                {
                    // Resolve the strongest strategy across rows (Optimal
                    // beats KeyframeReduction beats Off).
                    settings.compression = MergeStrongest(settings.compression, ToImporterCompression(row.strategy));
                    settings.rotationError = Mathf.Min(settings.rotationError, row.rotationError);
                    settings.positionError = Mathf.Min(settings.positionError, row.positionError);
                    settings.scaleError = Mathf.Min(settings.scaleError, row.scaleError);
                }
                settings.rows.Add(row);
            }
            return groups;
        }

        private class FbxGroupSettings
        {
            public ModelImporterAnimationCompression compression;
            public float rotationError;
            public float positionError;
            public float scaleError;
            public List<AnimationPlanRow> rows;
        }

        private static ModelImporterAnimationCompression ToImporterCompression(OptimizationStrategy s)
        {
            switch (s)
            {
                case OptimizationStrategy.Optimal:          return ModelImporterAnimationCompression.Optimal;
                case OptimizationStrategy.KeyframeReduction: return ModelImporterAnimationCompression.KeyframeReduction;
                case OptimizationStrategy.KeepAsIs:
                default:                                    return ModelImporterAnimationCompression.Off;
            }
        }

        private static ModelImporterAnimationCompression MergeStrongest(
            ModelImporterAnimationCompression a, ModelImporterAnimationCompression b)
        {
            // Ordering: Optimal > KeyframeReductionAndCompression >
            // KeyframeReduction > Off. We pick the strongest of the two.
            return (int)Rank(a) >= (int)Rank(b) ? a : b;
        }

        private static int Rank(ModelImporterAnimationCompression c)
        {
            switch (c)
            {
                case ModelImporterAnimationCompression.Optimal: return 3;
                case ModelImporterAnimationCompression.KeyframeReductionAndCompression: return 2;
                case ModelImporterAnimationCompression.KeyframeReduction: return 1;
                default: return 0;
            }
        }

        // ─── Step 2: flip the FBX importer + reimport ───────────────────

        private static void ApplyImporterSettingsForGroup(
            string fbxPath,
            FbxGroupSettings settings,
            bool dryRun,
            ExecuteResult result)
        {
            // Record byte size BEFORE the importer runs. For sub-clip rows
            // this is the FBX's pre-reimport size; we'll re-stat in pass 3.
            long bytesBefore = TryFileLength(fbxPath);
            int subClipRowCount = settings.rows.Count(r => r.usage.rowKind == AnimationRowKind.FbxSubClip);

            // Record an entry for each FbxSubClip row in this group. The
            // standalone rows get their own ExecuteRowResult in pass 2.
            //
            // Byte accounting: an FBX may contain multiple sub-clips we're
            // touching, but the FBX is one file on disk — we don't want to
            // count its bytes N times. We attribute an equal share to each
            // sub-clip row and seed both bytesBefore AND bytesAfter on the
            // result entry so pass 3's "re-stat and adjust" math doesn't
            // start from zero (which would produce negative savings).
            long bytesPerRow = subClipRowCount > 0 ? bytesBefore / subClipRowCount : bytesBefore;
            foreach (var row in settings.rows)
            {
                if (row.usage.rowKind == AnimationRowKind.FbxSubClip)
                {
                    result.rows.Add(new ExecuteRowResult
                    {
                        assetPath = fbxPath,
                        clipName = row.usage.clipName,
                        rowKind = AnimationRowKind.FbxSubClip,
                        bytesBefore = bytesPerRow,
                        bytesAfter = bytesPerRow, // placeholder; updated in pass 3
                        ok = false, // upgraded below if the importer flip succeeds
                    });
                    result.bytesBefore += bytesPerRow;
                    result.bytesAfter  += bytesPerRow; // seed so pass-3 subtraction lands on the right total
                    result.processed++;
                }
            }

            if (dryRun) return;

            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                MarkFbxGroupFailed(fbxPath, "Source FBX has no ModelImporter — file may be missing or wrong type.", result);
                return;
            }

            try
            {
                importer.animationCompression = settings.compression;
                importer.animationRotationError = settings.rotationError;
                importer.animationPositionError = settings.positionError;
                importer.animationScaleError = settings.scaleError;
                importer.SaveAndReimport();

                // Mark all FbxSubClip rows for this FBX as succeeded.
                foreach (var entry in result.rows)
                {
                    if (entry.rowKind == AnimationRowKind.FbxSubClip
                        && entry.assetPath == fbxPath
                        && !entry.ok && string.IsNullOrEmpty(entry.error))
                    {
                        entry.ok = true;
                        result.succeeded++;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnimationOptimizer] Importer reimport failed for {fbxPath}: {e}");
                MarkFbxGroupFailed(fbxPath, e.Message, result);
            }
        }

        private static void MarkFbxGroupFailed(string fbxPath, string error, ExecuteResult result)
        {
            foreach (var entry in result.rows)
            {
                if (entry.rowKind == AnimationRowKind.FbxSubClip
                    && entry.assetPath == fbxPath
                    && !entry.ok && string.IsNullOrEmpty(entry.error))
                {
                    entry.error = error;
                    result.failed++;
                }
            }
        }

        // ─── Step 3: standalone round-trip with GUID preservation ───────

        /// <summary>
        /// For a <see cref="AnimationRowKind.StandaloneWithSource"/> row:
        /// the host FBX has just been re-imported with compression settings,
        /// so its embedded sub-clip carries the reduced curve data. We
        /// <em>overwrite the existing standalone clip in place</em> by
        /// copying the sub-clip's serialized fields onto it, then restoring
        /// the standalone's pre-existing clip settings and events.
        ///
        /// Why in-place instead of delete-and-recreate? Two reasons:
        ///
        ///   1. <b>GUID preservation is automatic.</b> The standalone asset
        ///      is never destroyed; its asset path, GUID, and every
        ///      reference to it stay valid by definition. No fragile
        ///      .meta-file-juggling required.
        ///   2. <b>The standalone's loop time / cycle offset / events
        ///      survive.</b> <c>CopySerialized</c> would otherwise replace
        ///      these with whatever the FBX's ClipAnimation entry had,
        ///      which for a re-extracted vendor pack is almost certainly
        ///      different from what the user has authored on the standalone.
        ///      We snapshot them before the copy and restore after.
        /// </summary>
        private static ExecuteRowResult ApplyStandaloneRoundTrip(AnimationPlanRow row, bool dryRun)
        {
            var rr = new ExecuteRowResult
            {
                assetPath = row.usage.assetPath,
                clipName = row.usage.clipName,
                rowKind = AnimationRowKind.StandaloneWithSource,
                bytesBefore = row.usage.fileBytes,
            };

            try
            {
                string fbxPath = row.usage.fbxSource?.fbxPath;
                string subClipName = row.usage.fbxSource?.subClipName;
                if (string.IsNullOrEmpty(fbxPath) || string.IsNullOrEmpty(subClipName))
                {
                    rr.error = "Source FBX or sub-clip name missing.";
                    return rr;
                }

                // Load the freshly-compressed sub-clip out of the re-imported
                // FBX. Important: don't cache this between Apply calls — the
                // sub-asset reference is invalidated by SaveAndReimport.
                AnimationClip subClip = null;
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                {
                    if (obj is AnimationClip c && c.name == subClipName)
                    {
                        subClip = c;
                        break;
                    }
                }
                if (subClip == null)
                {
                    rr.error = $"Couldn't find sub-clip '{subClipName}' in {fbxPath} after reimport. Was it renamed?";
                    return rr;
                }

                // Load the existing standalone clip — we'll overwrite its
                // data in place.
                var existingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(row.usage.assetPath);
                if (existingClip == null)
                {
                    rr.error = $"Couldn't load existing standalone clip at {row.usage.assetPath}.";
                    return rr;
                }

                if (dryRun)
                {
                    rr.bytesAfter = AnimationOptimizationPlanner.EstimateBytes(row.usage, row);
                    rr.ok = true;
                    return rr;
                }

                // ── Snapshot the standalone's customizations ────────────
                // CopySerialized will overwrite ALL serialized fields,
                // including the AnimationClipSettings block (loopTime,
                // loopBlend, cycleOffset, mirror, hasAdditiveReferencePose,
                // startTime, stopTime, orientationOffsetY, level, etc.) and
                // the events array. If we don't restore these afterward,
                // every standalone that the user has marked Loop Time = true
                // will silently stop looping after the round-trip.
                var stashedSettings = AnimationUtility.GetAnimationClipSettings(existingClip);
                var stashedEvents = AnimationUtility.GetAnimationEvents(existingClip);
                string preservedName = existingClip.name;

                // ── In-place copy from sub-clip ─────────────────────────
                // EditorUtility.CopySerialized preserves the destination
                // object's identity (asset path, GUID) and only overwrites
                // its serialized fields. That's exactly the semantic we
                // want — compressed data goes in, asset stays put.
                EditorUtility.CopySerialized(subClip, existingClip);
                existingClip.name = preservedName;

                // ── Restore the standalone's customizations ─────────────
                AnimationUtility.SetAnimationClipSettings(existingClip, stashedSettings);
                AnimationUtility.SetAnimationEvents(existingClip, stashedEvents);

                EditorUtility.SetDirty(existingClip);
                AssetDatabase.SaveAssetIfDirty(existingClip);

                long realAfter = TryFileLength(row.usage.assetPath);
                rr.bytesAfter = realAfter > 0 ? realAfter : row.usage.fileBytes;
                rr.ok = true;
                return rr;
            }
            catch (Exception e)
            {
                rr.error = e.Message;
                Debug.LogError($"[AnimationOptimizer] Round-trip failed for {row.usage.assetPath}: {e}");
                return rr;
            }
        }

        // ─── Helpers ────────────────────────────────────────────────────

        private static long TryFileLength(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }
    }
}
#endif
