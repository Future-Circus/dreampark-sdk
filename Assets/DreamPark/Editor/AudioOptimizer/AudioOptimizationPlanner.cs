#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DreamPark.EditorTools.AudioOptimization
{
    /// <summary>
    /// Turns a list of <see cref="AudioUsage"/> records into a list of
    /// <see cref="AudioPlanRow"/>s that describe what the executor should
    /// do. The planner is the single source of truth for default
    /// decisions — the review UI surfaces every choice and lets the user
    /// override, but the seed value comes from here.
    ///
    /// Decisions, in order:
    ///
    ///   1. Hard-skip rows we should never touch (corrupt importers,
    ///      unsupported source formats).
    ///   2. Pick a target compression / sample rate / mono flag / load
    ///      type from the per-usage <see cref="AudioSizingPolicy"/>.
    ///   3. Decide whether to replace the source file on disk. Only WAV
    ///      sources go through the WAV → OGG re-encode path. .ogg / .mp3
    ///      sources are settings-only changes (no transcoding — already
    ///      lossy, transcoding would compound quality loss).
    ///   4. Estimate post-optimization bytes via the policy's bitrate
    ///      curve. Used by the savings column and the summary header.
    ///   5. Soft-skip rows where the policy and the current importer
    ///      already agree (already-tight). User can still force them on
    ///      from the UI.
    /// </summary>
    public static class AudioOptimizationPlanner
    {
        public static List<AudioPlanRow> Plan(List<AudioUsage> usages)
        {
            var rows = new List<AudioPlanRow>(usages.Count);
            foreach (var u in usages)
            {
                rows.Add(PlanOne(u));
            }
            return rows;
        }

        private static AudioPlanRow PlanOne(AudioUsage u)
        {
            var row = new AudioPlanRow
            {
                usage = u,
                approved = true,
                targetExtension = u.extension, // default: don't change
            };

            // ── Hard skips ─────────────────────────────────────────────
            // Zero-byte / un-imported clips: nothing useful we can do.
            if (u.fileBytes <= 0 || u.durationSeconds <= 0f)
            {
                row.hardSkip = true;
                row.approved = false;
                row.skipReason = "Clip has no duration or zero bytes — Unity failed to import it.";
                row.targetCompression = AudioTargetCompression.KeepAsIs;
                row.estimatedAfterBytes = u.fileBytes;
                return row;
            }

            // ── Decide policy ──────────────────────────────────────────
            var policy = AudioSizingPolicy.Recommend(u.kind, u.durationSeconds, u.sourceChannels);
            row.targetCompression = policy.compression;
            row.targetLoadType = policy.loadType;
            row.targetSampleRate = ClampSampleRateToSource(policy.sampleRate, u.sourceSampleRate);
            row.targetForceToMono = policy.forceToMono;
            row.targetVorbisQuality = policy.vorbisQuality;

            // ── Source replacement decision ────────────────────────────
            // .wav → .ogg: yes (the big win). Encoded sources stay as-is —
            // transcoding ogg→ogg or mp3→ogg compounds quality loss.
            bool isWav = u.extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
            bool isAif = u.extension.Equals(".aif", StringComparison.OrdinalIgnoreCase)
                      || u.extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase);
            bool sourceIsUncompressed = isWav || isAif;

            if (isWav)
            {
                row.sourceWillBeReplaced = true;
                row.targetExtension = ".ogg";
            }
            else if (isAif)
            {
                // AIFF is also uncompressed PCM but our reader doesn't
                // handle the AIFF chunk layout. Skip in v1 with a clear
                // note so the user can convert manually if they care.
                row.hardSkip = true;
                row.approved = false;
                row.skipReason = "AIFF source — v1 only re-encodes .wav. Convert to .wav first if you want disk savings.";
            }
            else
            {
                // .ogg / .mp3: importer-only optimization. Source stays
                // exactly as it is on disk.
                row.sourceWillBeReplaced = false;
            }

            // ── Soft-skip already-tight rows ───────────────────────────
            // If the importer settings already match what the policy
            // recommends AND the source doesn't need replacement, there's
            // nothing to do. Soft skip (user can still force it on).
            if (!row.sourceWillBeReplaced && SettingsAlreadyMatch(u, row))
            {
                row.skipReason = "Importer already matches recommended settings.";
                row.approved = false;
                row.estimatedAfterBytes = u.fileBytes;
                return row;
            }

            // ── Estimate the after-bytes ───────────────────────────────
            int targetSampleRate = row.targetSampleRate > 0 ? row.targetSampleRate : u.sourceSampleRate;
            int targetChannels = row.targetForceToMono ? 1 : u.sourceChannels;
            row.estimatedAfterBytes = AudioSizingPolicy.EstimateBytes(
                row.targetCompression,
                targetSampleRate,
                targetChannels,
                u.durationSeconds,
                row.targetVorbisQuality);

            // If we're NOT replacing the source on disk, the file-on-disk
            // size doesn't change — only the runtime/build size does. Show
            // the source size as "current" but cap savings at the build-
            // time delta. For settings-only rows we estimate that the on-
            // disk file stays the same — the savings live in the Unity
            // build pipeline. Show this honestly: if a settings-only row
            // would actually have the same on-disk size, surface that
            // expectation.
            if (!row.sourceWillBeReplaced)
            {
                // Settings-only: on-disk size is unchanged. The savings
                // happen at build time. We surface this by showing the
                // estimated build-time size in the after column; the user
                // sees "this clip will produce X bytes in the bundle"
                // instead of "this file on disk will shrink to X bytes".
                // The summary header still adds the diff so creators see
                // the actual bundle-size win, which is what they care
                // about for OTA download budgets.
            }

            return row;
        }

        /// <summary>
        /// If the policy recommends 44 kHz but the source is only 32 kHz,
        /// keep the source rate — we never upsample. Always clamp to a
        /// value &le; source.
        /// </summary>
        private static int ClampSampleRateToSource(int policyRate, int sourceRate)
        {
            if (policyRate <= 0) return 0;
            if (sourceRate <= 0) return policyRate;
            return policyRate < sourceRate ? policyRate : 0; // 0 = "preserve source"
        }

        /// <summary>
        /// Does the importer's current configuration already match the
        /// policy's recommendation closely enough that re-applying would
        /// be a no-op? We check the four levers the executor would push:
        /// compression format, load type, sample rate override, mono.
        /// Vorbis quality is allowed to drift by ±0.05 since the importer
        /// rounds.
        /// </summary>
        private static bool SettingsAlreadyMatch(AudioUsage u, AudioPlanRow row)
        {
            if (u.currentCompression != row.targetCompression) return false;
            if (u.currentLoadType != row.targetLoadType) return false;

            int targetRate = row.targetSampleRate;
            int currentRate = u.currentSampleRateOverride;
            // "Preserve source" (0) is equivalent to currentSampleRateOverride
            // matching the source — i.e. there's nothing to override.
            if (targetRate == 0 && currentRate != 0 && currentRate != u.sourceSampleRate) return false;
            if (targetRate != 0 && currentRate != targetRate) return false;

            if (u.currentForceToMono != row.targetForceToMono) return false;

            if (row.targetCompression == AudioTargetCompression.Vorbis
                && Mathf.Abs(u.currentVorbisQuality - row.targetVorbisQuality) > 0.05f)
                return false;

            return true;
        }
    }
}
#endif
