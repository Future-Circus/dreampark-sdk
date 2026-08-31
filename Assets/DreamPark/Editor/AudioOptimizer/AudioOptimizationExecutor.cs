#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AudioOptimization
{
    /// <summary>
    /// Applies a list of approved <see cref="AudioPlanRow"/>s. The pass
    /// structure mirrors <c>TextureOptimizationExecutor</c> deliberately:
    ///
    ///   1. Encode pass (only for rows where the source will be replaced —
    ///      i.e. .wav rows). Reads each WAV inline (PCM parser is nested
    ///      inside <see cref="OggVorbisEncoderBootstrap"/>),
    ///      resamples / mixes to mono per the row, writes a temp .ogg
    ///      via <see cref="OggVorbisEncoderBootstrap"/>, carries the
    ///      original .meta file to the .ogg path to preserve GUIDs, and
    ///      deletes the original .wav.
    ///   2. Importer settings pass (every approved row). Applies
    ///      compression format / sample rate override / mono / load type
    ///      / vorbis quality to the AudioImporter — whether or not the
    ///      source file itself was replaced.
    ///
    /// The GUID-preservation step is the linchpin for the encode pass.
    /// If we just wrote a new .ogg and let Unity assign it a fresh GUID,
    /// every prefab AudioSource and every Lua name-by-asset-mapping
    /// would break. Carrying the .meta file across the rename is the
    /// only reliable way to keep references stable through a format
    /// change.
    /// </summary>
    public static class AudioOptimizationExecutor
    {
        /// <summary>
        /// Apply every row of <paramref name="rows"/> that has
        /// <see cref="AudioPlanRow.WillBeModified"/> = true. Pass
        /// <paramref name="dryRun"/> = true to compute results without
        /// committing anything — useful for a preview button.
        /// </summary>
        public static AudioExecuteResult Apply(
            IList<AudioPlanRow> rows,
            bool dryRun,
            Action<float, string> onProgress = null)
        {
            var result = new AudioExecuteResult();

            // Defense-in-depth: the window's OnEnable triggers a one-time
            // auto-install, so OggVorbisEncoder should always be ready
            // by the time Apply runs. But if anyone calls Apply
            // programmatically — or a manual install got deleted mid-
            // session — fail loudly instead of silently doing nothing.
            //
            // We only require the encoder when at least one row actually
            // needs re-encoding. Settings-only batches (no .wav rows) can
            // proceed without it.
            bool needsEncoder = false;
            foreach (var r in rows)
                if (r != null && r.WillBeModified && r.sourceWillBeReplaced) { needsEncoder = true; break; }

            if (needsEncoder && !OggVorbisEncoderBootstrap.IsAvailable)
            {
                EditorUtility.DisplayDialog(
                    "Audio Optimizer not ready",
                    "OggVorbisEncoder isn't loaded. Close and reopen the Audio Optimizer window — "
                    + "it auto-installs on first use.",
                    "OK");
                return result;
            }

            // Snapshot the work list so caller mutations during the run
            // don't desync us.
            var work = new List<AudioPlanRow>();
            foreach (var r in rows) if (r != null && r.WillBeModified) work.Add(r);

            // ── Pass 1: encode WAV → OGG for source-replacement rows ──
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    var row = work[i];
                    string label = Path.GetFileName(row.usage.assetPath);
                    onProgress?.Invoke(0.05f + 0.75f * i / Mathf.Max(1, work.Count),
                        (dryRun ? "Previewing: " : "Encoding: ") + label);

                    var rr = ApplyRowPass1(row, dryRun);
                    result.rows.Add(rr);
                    result.processed++;
                    if (rr.ok) result.succeeded++; else result.failed++;
                    result.bytesBefore += rr.bytesBefore;
                    result.bytesAfter += rr.bytesAfter;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            // ── Pass 2: apply AudioImporter settings ──────────────────
            // Has to run AFTER Refresh because the AudioImporter at the
            // new (.ogg) path doesn't exist until Unity has re-imported.
            // Pair each plan row with its Pass-1 result so we set
            // settings on whichever file Pass 1 actually wrote.
            if (!dryRun)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 0; i < work.Count; i++)
                    {
                        onProgress?.Invoke(0.85f + 0.15f * i / Mathf.Max(1, work.Count),
                            "Applying importer settings...");
                        var rr = i < result.rows.Count ? result.rows[i] : null;
                        if (rr != null && rr.ok)
                            ApplyImporterSettings(work[i], rr.finalPath);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }
            }

            onProgress?.Invoke(1f, "Done.");
            return result;
        }

        // ─── Per-row execution: encode pass ─────────────────────────────

        private static AudioExecuteRowResult ApplyRowPass1(AudioPlanRow row, bool dryRun)
        {
            var rr = new AudioExecuteRowResult
            {
                sourcePath = row.usage.assetPath,
                bytesBefore = row.usage.fileBytes,
            };

            try
            {
                string srcAssetPath = row.usage.assetPath;
                string srcAbs = Path.GetFullPath(srcAssetPath);

                if (!File.Exists(srcAbs))
                {
                    rr.error = "Source file missing at " + srcAbs;
                    Debug.LogError($"[AudioOptimizer] {srcAssetPath}: source not found at {srcAbs}.");
                    return rr;
                }

                // Settings-only row: nothing to encode. Pass 2 will tweak
                // the importer. Report success with bytesAfter = bytesBefore
                // (no on-disk change).
                if (!row.sourceWillBeReplaced)
                {
                    rr.ok = true;
                    rr.finalPath = srcAssetPath;
                    rr.bytesAfter = row.usage.fileBytes;
                    return rr;
                }

                // From here on we're re-encoding. Sanity guard.
                if (row.targetCompression == AudioTargetCompression.KeepAsIs)
                {
                    rr.ok = true;
                    rr.finalPath = srcAssetPath;
                    rr.bytesAfter = row.usage.fileBytes;
                    return rr;
                }

                string baseNoExt = Path.Combine(
                    Path.GetDirectoryName(srcAssetPath) ?? "",
                    Path.GetFileNameWithoutExtension(srcAssetPath));
                string newAssetPath = baseNoExt + row.targetExtension;
                string tempAbs = Path.GetFullPath(baseNoExt + ".__audopt_tmp" + row.targetExtension);

                // ── Step 1: encode to temp path ────────────────────────
                // Never write directly to the final path — if the encoder
                // throws halfway, the original .wav is still intact.
                try
                {
                    OggVorbisEncoderBootstrap.EncodeWavToOgg(
                        wavAbsolutePath: srcAbs,
                        oggAbsolutePath: tempAbs,
                        targetSampleRate: row.targetSampleRate,
                        forceToMono: row.targetForceToMono,
                        quality: row.targetVorbisQuality);
                }
                catch (Exception encErr)
                {
                    rr.error = "Encode failed: " + encErr.Message;
                    Debug.LogError($"[AudioOptimizer] {srcAssetPath}: encoder threw: {encErr}");
                    try { if (File.Exists(tempAbs)) File.Delete(tempAbs); } catch { /* ignore */ }
                    return rr;
                }

                long afterBytes = new FileInfo(tempAbs).Length;
                rr.bytesAfter = afterBytes;
                rr.finalPath = newAssetPath;

                if (dryRun)
                {
                    try { File.Delete(tempAbs); } catch { /* ignore */ }
                    rr.ok = true;
                    return rr;
                }

                // ── Step 2: carry the .meta file to preserve the GUID ──
                string srcMeta = srcAbs + ".meta";
                string newAbs = Path.GetFullPath(newAssetPath);
                string newMeta = newAbs + ".meta";

                bool sameExtension = string.Equals(
                    Path.GetExtension(srcAssetPath),
                    row.targetExtension,
                    StringComparison.OrdinalIgnoreCase);

                if (sameExtension)
                {
                    // Same extension: replace contents in place.
                    File.Copy(tempAbs, srcAbs, overwrite: true);
                    File.Delete(tempAbs);
                    rr.finalPath = srcAssetPath; // unchanged
                }
                else
                {
                    // Cross-extension (.wav → .ogg) — the GUID-preservation
                    // path. Refuse if a sibling Foo.ogg already exists
                    // next to Foo.wav (we'd silently destroy hand-authored
                    // content).
                    if (File.Exists(newAbs))
                    {
                        File.Delete(tempAbs);
                        rr.error = $"Target file already exists: {newAssetPath}. "
                                 + "Move or delete it manually, then re-run the optimizer.";
                        return rr;
                    }
                    if (File.Exists(newMeta))
                    {
                        File.Delete(tempAbs);
                        rr.error = $"Target meta file already exists: {newAssetPath}.meta. "
                                 + "Move or delete it manually, then re-run the optimizer.";
                        return rr;
                    }

                    File.Move(tempAbs, newAbs);
                    if (File.Exists(srcMeta))
                        File.Move(srcMeta, newMeta);
                    File.Delete(srcAbs);
                }

                rr.ok = true;
                return rr;
            }
            catch (Exception e)
            {
                rr.error = e.Message;
                Debug.LogError($"[AudioOptimizer] Failed to optimize {row.usage.assetPath}: {e}");
                return rr;
            }
        }

        // ─── Importer settings pass ─────────────────────────────────────

        private static void ApplyImporterSettings(AudioPlanRow row, string finalAssetPath)
        {
            // finalAssetPath is whatever Pass 1 actually wrote — same as
            // the source for settings-only rows, or the renamed .ogg for
            // re-encode rows.
            string path = string.IsNullOrEmpty(finalAssetPath) ? row.usage.assetPath : finalAssetPath;

            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[AudioOptimizer] No AudioImporter at {path} — skipping settings pass.");
                return;
            }

            importer.forceToMono = row.targetForceToMono;

            // Apply default platform sample settings. We push the
            // override to the default settings (which apply to every
            // platform that doesn't have an overriding entry).
            var settings = importer.defaultSampleSettings;
            settings.compressionFormat = AudioUsageGraph.MapToUnity(row.targetCompression);
            settings.loadType = AudioUsageGraph.MapToUnity(row.targetLoadType);
            settings.quality = Mathf.Clamp01(row.targetVorbisQuality);

            if (row.targetSampleRate > 0)
            {
                settings.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                settings.sampleRateOverride = (uint)row.targetSampleRate;
            }
            else
            {
                // No override requested → use Unity's "Preserve Sample
                // Rate" mode which keeps the source rate.
                settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
                settings.sampleRateOverride = (uint)row.usage.sourceSampleRate;
            }

            importer.defaultSampleSettings = settings;

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }
    }
}
#endif
