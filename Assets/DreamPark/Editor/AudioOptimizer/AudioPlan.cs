#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.EditorTools.AudioOptimization
{
    // ─────────────────────────────────────────────────────────────────────
    // Shared data structures for the Audio Optimizer pipeline.
    //
    // The flow:
    //
    //   AudioUsageGraph.Build(rootFolder)
    //       → List<AudioUsage>           (one per clip — with classification + AudioImporter snapshot)
    //
    //   AudioOptimizationPlanner.Plan(usages)
    //       → List<AudioPlanRow>         (target encoding + load type + estimated savings)
    //
    //   AudioOptimizationExecutor.Apply(approvedRows)
    //       → AudioExecuteResult         (.wav → .ogg on disk + tightened AudioImporter)
    //
    // The pattern mirrors the Texture Optimizer deliberately so creators
    // who already know one tool can use the other without re-learning the
    // mental model. Anywhere a decision could differ — usage taxonomy,
    // sizing thresholds, format choice — the audio version uses
    // audio-appropriate vocabulary but keeps the same structural shape.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How an audio clip is referenced in the project. Drives both the
    /// compression aggressiveness (a tiny UI tick can be 22 kHz mono;
    /// background music needs stereo 44.1 kHz) and the runtime load type
    /// (streaming for music, decompressed-on-load for short SFX).
    /// </summary>
    public enum AudioUsageKind
    {
        /// <summary>UI sound effect — clicks, beeps, hovers. Short, mono-friendly, frequent.</summary>
        UI,

        /// <summary>Diegetic sound effect — footsteps, impacts, magic zaps. Short to medium.</summary>
        SFX,

        /// <summary>Spoken voice / dialogue. Mono is fine; medium-length.</summary>
        Voice,

        /// <summary>Background music or stingers. Stereo, long, deserves streaming.</summary>
        Music,

        /// <summary>Ambient loop — wind, water, room tone. Stereo, long, often looped.</summary>
        Ambient,

        /// <summary>Found no references at all. Likely orphaned.</summary>
        Orphan,
    }

    /// <summary>
    /// Compression format the AudioImporter should target. We expose the
    /// three that Unity's runtime supports natively across every player
    /// platform we ship to (Quest/Android/iOS/Windows/macOS). AAC/MP3 are
    /// deliberately omitted — they decode slower on Quest and don't beat
    /// Vorbis at the quality levels we use.
    /// </summary>
    public enum AudioTargetCompression
    {
        /// <summary>Keep the clip exactly as-is (skip rows and orphans by default).</summary>
        KeepAsIs,

        /// <summary>Vorbis (.ogg-style). Best size/quality for music and most SFX.</summary>
        Vorbis,

        /// <summary>ADPCM — fast decode, decent size, good for very short rapid-fire SFX.</summary>
        ADPCM,

        /// <summary>Uncompressed PCM. Used only as an escape hatch for clips Vorbis would mangle.</summary>
        PCM,
    }

    /// <summary>
    /// AudioImporter load type. Mirrors Unity's enum but lives here so the
    /// plan can carry a target without dragging in UnityEditor for code
    /// that runs in non-editor contexts (tests, JSON serialization).
    /// </summary>
    public enum AudioTargetLoadType
    {
        /// <summary>Decompress at load — best for short SFX played many times per second.</summary>
        DecompressOnLoad,

        /// <summary>Stay compressed in memory; decompress on play — best for voice / medium SFX.</summary>
        CompressedInMemory,

        /// <summary>Stream from disk — best for long music / ambient.</summary>
        Streaming,
    }

    /// <summary>
    /// One discovered audio clip plus everything we learned about its
    /// usage. Produced by <see cref="AudioUsageGraph"/>.
    /// </summary>
    [Serializable]
    public class AudioUsage
    {
        // ── Identity ─────────────────────────────────────────────────────
        public string assetPath;           // "Assets/Content/Foo/Audio/Punch.wav"
        public string guid;
        public string extension;           // ".wav", ".ogg", ".mp3", ".aif", ".aiff"
        public long fileBytes;             // sizeof the source file on disk

        // ── Imported clip info (from AudioImporter / AudioClip) ─────────
        public int sourceSampleRate;       // e.g. 44100, 48000
        public int sourceChannels;         // 1 = mono, 2 = stereo
        public float durationSeconds;
        public bool sourceIsCompressed;    // true for .ogg/.mp3 sources, false for .wav/.aif

        // ── Current AudioImporter settings ──────────────────────────────
        public AudioTargetCompression currentCompression;
        public AudioTargetLoadType currentLoadType;
        public int currentSampleRateOverride;  // 0 if no override
        public bool currentForceToMono;
        public float currentVorbisQuality;     // 0..1; default 1.0 means "preserve everything"

        // ── Usage graph ─────────────────────────────────────────────────
        public AudioUsageKind kind;
        public List<string> referencingPrefabs = new List<string>();
        public List<string> referencingScripts = new List<string>(); // .lua.txt files that reference by name
        public int audioSourceRefCount;

        /// <summary>
        /// Path of an example prefab/script that uses this clip — lets the
        /// review UI ping it for context.
        /// </summary>
        public string usageExample;

        /// <summary>
        /// Free-form reason populated only when kind is Orphan. The review
        /// UI surfaces this so the creator can decide what to do.
        /// </summary>
        public string note;
    }

    /// <summary>
    /// One row of the optimization plan: what the optimizer intends to do
    /// to a given clip, and what the savings are expected to be. The
    /// review UI binds directly to a list of these.
    /// </summary>
    [Serializable]
    public class AudioPlanRow
    {
        public AudioUsage usage;             // source info (read-only after planning)

        // ── Decisions (mutable: the review UI lets the user override) ──
        public bool approved = true;         // user checkbox in the review UI

        /// <summary>
        /// True for rows the planner refuses to mutate ever. Used for
        /// already-tight clips that have nothing left to optimize, and
        /// for unknown source formats we can't decode. The review UI
        /// greys out the checkbox. Orphans are SOFT skips — they remain
        /// toggleable so the user can force the re-encode.
        /// </summary>
        public bool hardSkip;

        // The two things the executor cares about:
        //   1. Should the source file be re-encoded (.wav → .ogg)?
        //   2. What AudioImporter settings should the new file get?
        //
        // (1) is driven by sourceWillBeReplaced + targetExtension.
        // (2) is everything else below.

        public bool sourceWillBeReplaced;    // true when we'll write a new .ogg next to / over the .wav
        public string targetExtension;       // ".ogg" or unchanged source extension

        public AudioTargetCompression targetCompression;
        public AudioTargetLoadType targetLoadType;
        public int targetSampleRate;         // 0 = preserve source rate; otherwise 22050/32000/44100/48000
        public bool targetForceToMono;
        public float targetVorbisQuality = 0.7f;  // 0..1, used when targetCompression == Vorbis

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

    /// <summary>
    /// Output of executing the plan. Aggregated across all rows so the
    /// final report can show "saved 240 MB across 412 clips".
    /// </summary>
    [Serializable]
    public class AudioExecuteResult
    {
        public int processed;
        public int succeeded;
        public int failed;
        public long bytesBefore;
        public long bytesAfter;

        public List<AudioExecuteRowResult> rows = new List<AudioExecuteRowResult>();

        public long BytesSaved => Math.Max(0, bytesBefore - bytesAfter);

        public float PercentSaved =>
            bytesBefore > 0 ? 100f * (bytesBefore - bytesAfter) / bytesBefore : 0f;
    }

    [Serializable]
    public class AudioExecuteRowResult
    {
        public string sourcePath;     // path before mutation (e.g. "...Punch.wav")
        public string finalPath;      // path after mutation (e.g. "...Punch.ogg")
        public bool ok;
        public long bytesBefore;
        public long bytesAfter;
        public string error;          // populated when ok=false
    }

    // ─────────────────────────────────────────────────────────────────────
    // Sizing + compression policy. Single source of truth so the planner,
    // the UI's override dropdowns, and the bytes estimator agree.
    //
    // Defaults are tuned for "game-friendly" — clearly smaller than the
    // source, but conservative enough to not introduce audible artifacts
    // in a VR park played on Quest. Creators can override per-row in the
    // review UI before committing.
    // ─────────────────────────────────────────────────────────────────────
    public static class AudioSizingPolicy
    {
        // ── Sample rates the override dropdown can pick ──────────────────
        public static readonly int[] AllowedSampleRates = { 22050, 32000, 44100, 48000 };

        // ── Vorbis quality presets ──────────────────────────────────────
        // Quality is Unity's 0..1 scale (passed through to libvorbis as
        // the same value). Approximate bitrates at 44.1 kHz stereo:
        //   0.50  ≈   96 kbps   (audible loss on music)
        //   0.70  ≈  128 kbps   (transparent for most game audio)
        //   0.80  ≈  160 kbps   (transparent for music)
        //   0.90  ≈  220 kbps   (overkill for everything but mastering refs)
        public const float QualitySfx = 0.70f;
        public const float QualityVoice = 0.70f;
        public const float QualityMusic = 0.80f;
        public const float QualityAmbient = 0.70f;
        public const float QualityUi = 0.50f;     // UI clicks are short — quality drop is inaudible

        // ── Per-usage policy ────────────────────────────────────────────
        public struct Policy
        {
            public AudioTargetCompression compression;
            public AudioTargetLoadType loadType;
            public int sampleRate;          // 0 = keep source rate
            public bool forceToMono;
            public float vorbisQuality;
        }

        /// <summary>
        /// Pick a target policy for a clip based on its usage kind and
        /// duration. Duration matters for load-type decisions: a 30-second
        /// "SFX" is really an ambient stinger and should stream, not
        /// decompress-on-load (which would balloon RAM).
        /// </summary>
        public static Policy Recommend(AudioUsageKind kind, float durationSeconds, int sourceChannels)
        {
            switch (kind)
            {
                case AudioUsageKind.UI:
                    return new Policy
                    {
                        compression = AudioTargetCompression.Vorbis,
                        loadType = AudioTargetLoadType.DecompressOnLoad,
                        sampleRate = 22050,
                        forceToMono = true,
                        vorbisQuality = QualityUi,
                    };

                case AudioUsageKind.SFX:
                    // Short SFX (<5s) decompress on load; medium SFX
                    // stay compressed in memory; anything 30s+ that got
                    // tagged SFX is really an ambient pad — stream it.
                    return new Policy
                    {
                        compression = AudioTargetCompression.Vorbis,
                        loadType = durationSeconds < 5f
                            ? AudioTargetLoadType.DecompressOnLoad
                            : (durationSeconds < 30f
                                ? AudioTargetLoadType.CompressedInMemory
                                : AudioTargetLoadType.Streaming),
                        sampleRate = 22050,
                        forceToMono = true,
                        vorbisQuality = QualitySfx,
                    };

                case AudioUsageKind.Voice:
                    // Voice tolerates 22 kHz mono — the spoken-word
                    // frequency range tops out around 4-5 kHz. Keep
                    // compressed-in-memory so a 30-line VO bank doesn't
                    // eat 200 MB of RAM.
                    return new Policy
                    {
                        compression = AudioTargetCompression.Vorbis,
                        loadType = AudioTargetLoadType.CompressedInMemory,
                        sampleRate = 22050,
                        forceToMono = true,
                        vorbisQuality = QualityVoice,
                    };

                case AudioUsageKind.Music:
                    // Music gets the full treatment: stereo, 44.1 kHz,
                    // streamed from disk so it never pre-loads.
                    return new Policy
                    {
                        compression = AudioTargetCompression.Vorbis,
                        loadType = AudioTargetLoadType.Streaming,
                        sampleRate = 44100,
                        forceToMono = false,
                        vorbisQuality = QualityMusic,
                    };

                case AudioUsageKind.Ambient:
                    // Ambient loops are mostly stereo but tolerate lower
                    // sample rates. Stream them — they're usually long.
                    return new Policy
                    {
                        compression = AudioTargetCompression.Vorbis,
                        loadType = AudioTargetLoadType.Streaming,
                        sampleRate = 32000,
                        forceToMono = sourceChannels == 1,
                        vorbisQuality = QualityAmbient,
                    };

                case AudioUsageKind.Orphan:
                default:
                    // Orphans aren't auto-modified. The planner returns a
                    // soft-skip; if the user opts in, they get the SFX
                    // policy as a sane fallback.
                    return new Policy
                    {
                        compression = AudioTargetCompression.Vorbis,
                        loadType = AudioTargetLoadType.CompressedInMemory,
                        sampleRate = 22050,
                        forceToMono = true,
                        vorbisQuality = QualitySfx,
                    };
            }
        }

        // ─── Bytes estimation ───────────────────────────────────────────

        /// <summary>
        /// Estimate post-encode file size on disk. Accurate to ~10% for
        /// Vorbis; exact for PCM/ADPCM. Drives the savings column in the
        /// review UI — we never tell the user "you'll save X bytes" and
        /// then come back with half that.
        /// </summary>
        public static long EstimateBytes(
            AudioTargetCompression compression,
            int sampleRate,
            int channels,
            float durationSeconds,
            float vorbisQuality)
        {
            if (durationSeconds <= 0f) return 0;
            if (sampleRate <= 0) sampleRate = 44100;
            if (channels <= 0) channels = 1;

            switch (compression)
            {
                case AudioTargetCompression.Vorbis:
                {
                    // libvorbis quality → approximate average bitrate.
                    // Numbers are from the libvorbis quality curve at
                    // 44.1 kHz stereo — we scale linearly by sample-rate
                    // ratio and halve for mono.
                    float kbpsAt441Stereo;
                    if (vorbisQuality <= 0.10f)      kbpsAt441Stereo =  48f;
                    else if (vorbisQuality <= 0.30f) kbpsAt441Stereo =  64f;
                    else if (vorbisQuality <= 0.50f) kbpsAt441Stereo =  96f;
                    else if (vorbisQuality <= 0.65f) kbpsAt441Stereo = 112f;
                    else if (vorbisQuality <= 0.75f) kbpsAt441Stereo = 128f;
                    else if (vorbisQuality <= 0.85f) kbpsAt441Stereo = 160f;
                    else if (vorbisQuality <= 0.95f) kbpsAt441Stereo = 220f;
                    else                              kbpsAt441Stereo = 320f;

                    float channelScale = channels >= 2 ? 1.0f : 0.55f;
                    float rateScale = Mathf.Clamp(sampleRate / 44100f, 0.4f, 1.2f);
                    float kbps = kbpsAt441Stereo * channelScale * rateScale;
                    return (long)(kbps * 1000f / 8f * durationSeconds);
                }

                case AudioTargetCompression.ADPCM:
                    // ADPCM is exactly 4 bits per sample.
                    return (long)(sampleRate * channels * 0.5f * durationSeconds);

                case AudioTargetCompression.PCM:
                    // 16-bit PCM.
                    return (long)(sampleRate * channels * 2 * durationSeconds);

                case AudioTargetCompression.KeepAsIs:
                default:
                    return 0;
            }
        }
    }
}
#endif
