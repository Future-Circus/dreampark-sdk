#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AudioOptimization
{
    /// <summary>
    /// Builds a usage graph for every audio clip under a content folder.
    /// For each clip we capture:
    ///
    ///   1. The current AudioImporter settings (compression format, sample
    ///      rate override, mono toggle, load type, vorbis quality). These
    ///      drive the "Current" column in the review UI and let the planner
    ///      detect already-tight clips.
    ///   2. Duration / source channels / source sample rate — read from
    ///      the AudioImporter's exposed source channel + sample rate
    ///      properties (or the AudioClip itself as a fallback).
    ///   3. Reference graph: which prefabs have an AudioSource pointing at
    ///      the clip, and which .lua.txt scripts mention the clip by name
    ///      (Wizard's Way audio is largely invoked via playSFXByName, so
    ///      a script reference is as important as a prefab reference).
    ///   4. Usage classification: UI / SFX / Voice / Music / Ambient /
    ///      Orphan. Classification combines path heuristics ("/Voice/",
    ///      "/Music/", "/UI/") with duration (anything &gt; 30s gets
    ///      bumped to Music or Ambient).
    /// </summary>
    public static class AudioUsageGraph
    {
        // The set of extensions we recognize as audio source files. .ogg
        // and .mp3 are included so the optimizer can at least tune their
        // importer settings — they won't be re-encoded (already lossy →
        // lossy would be a quality cliff).
        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".ogg", ".mp3", ".aif", ".aiff",
        };

        public static List<AudioUsage> Build(string rootAssetFolder, Action<float, string> onProgress = null)
        {
            if (string.IsNullOrEmpty(rootAssetFolder))
                throw new ArgumentNullException(nameof(rootAssetFolder));

            // ── Step 1: enumerate every AudioClip ──────────────────────
            onProgress?.Invoke(0.02f, "Scanning audio clips...");
            var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { rootAssetFolder });
            var usagesByGuid = new Dictionary<string, AudioUsage>(guids.Length);
            var usagesByName = new Dictionary<string, List<AudioUsage>>(guids.Length, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < guids.Length; i++)
            {
                onProgress?.Invoke(0.02f + 0.28f * i / Mathf.Max(1, guids.Length), "Reading importer settings...");
                string guid = guids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith(rootAssetFolder, StringComparison.OrdinalIgnoreCase)) continue;

                string ext = Path.GetExtension(path);
                if (!AudioExtensions.Contains(ext)) continue;

                var usage = BuildUsage(path, guid);
                if (usage == null) continue;

                usagesByGuid[guid] = usage;
                string nameKey = Path.GetFileNameWithoutExtension(path);
                if (!usagesByName.TryGetValue(nameKey, out var bucket))
                    usagesByName[nameKey] = bucket = new List<AudioUsage>();
                bucket.Add(usage);
            }

            // ── Step 2: AudioSource scan across prefabs ────────────────
            onProgress?.Invoke(0.35f, "Indexing AudioSource references...");
            IndexPrefabReferences(rootAssetFolder, usagesByGuid, onProgress);

            // ── Step 3: Lua script name references ─────────────────────
            // Wizard's Way scripts call `playSFXByName("Footstep_01")` —
            // by-name lookup means there's no direct asset reference. We
            // grep .lua.txt files for clip filenames so the orphan
            // detector doesn't false-positive on by-name clips.
            onProgress?.Invoke(0.65f, "Scanning Lua scripts for clip name references...");
            IndexLuaScriptReferences(rootAssetFolder, usagesByName, onProgress);

            // ── Step 4: classify ───────────────────────────────────────
            onProgress?.Invoke(0.92f, "Classifying clips...");
            foreach (var u in usagesByGuid.Values) Classify(u);

            // Sort by file size desc so the heaviest offenders surface at
            // the top of the review table by default.
            onProgress?.Invoke(1f, $"Done. ({usagesByGuid.Count} clips)");
            return usagesByGuid.Values.OrderByDescending(u => u.fileBytes).ToList();
        }

        // ─── Step 1: per-clip usage construction ────────────────────────

        private static AudioUsage BuildUsage(string assetPath, string guid)
        {
            long bytes = TryFileLength(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            // No importer means Unity hasn't ingested the file yet — skip.
            if (importer == null) return null;

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            // No AudioClip means Unity failed to import it. We still
            // surface the file in the report so the user notices, but
            // there's not enough info to plan against. Skip for v1.
            if (clip == null) return null;

            var defaultSettings = importer.defaultSampleSettings;
            var ext = Path.GetExtension(assetPath);

            return new AudioUsage
            {
                assetPath = assetPath,
                guid = guid,
                extension = ext,
                fileBytes = bytes,

                sourceSampleRate = clip.frequency,
                sourceChannels = clip.channels,
                durationSeconds = clip.length,
                sourceIsCompressed = !ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                                  && !ext.Equals(".aif", StringComparison.OrdinalIgnoreCase)
                                  && !ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase),

                currentCompression = MapFromUnity(defaultSettings.compressionFormat),
                currentLoadType = MapFromUnity(defaultSettings.loadType),
                currentSampleRateOverride = defaultSettings.sampleRateSetting == AudioSampleRateSetting.OverrideSampleRate
                    ? (int)defaultSettings.sampleRateOverride
                    : 0,
                currentForceToMono = importer.forceToMono,
                currentVorbisQuality = defaultSettings.compressionFormat == AudioCompressionFormat.Vorbis
                    ? defaultSettings.quality
                    : 1.0f,

                kind = AudioUsageKind.Orphan, // upgraded by Classify
            };
        }

        private static long TryFileLength(string assetPath)
        {
            try
            {
                string abs = Path.GetFullPath(assetPath);
                return File.Exists(abs) ? new FileInfo(abs).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ─── Step 2: prefab AudioSource scan ────────────────────────────

        private static void IndexPrefabReferences(
            string rootAssetFolder,
            Dictionary<string, AudioUsage> usagesByGuid,
            Action<float, string> onProgress)
        {
            // We walk every prefab under the content folder, instantiate it
            // (in memory only — never written back) and pull AudioSource +
            // AudioClip references out of the result. This catches both
            // direct AudioSource.clip assignments and the kind of clip
            // bank arrays our @var audioclip injections build.
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { rootAssetFolder });
            for (int i = 0; i < prefabGuids.Length; i++)
            {
                onProgress?.Invoke(0.35f + 0.28f * i / Mathf.Max(1, prefabGuids.Length), "Scanning prefabs for AudioSources...");
                string path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                if (string.IsNullOrEmpty(path)) continue;

                // Cheap pass: look at the prefab's serialized dependencies.
                // GetDependencies returns every asset the prefab references,
                // including clips assigned to AudioSource.clip and clips
                // referenced from custom serialized fields (LuaBehaviour
                // @var audioclip injections).
                var deps = AssetDatabase.GetDependencies(path, recursive: false);
                foreach (var dep in deps)
                {
                    string depGuid = AssetDatabase.AssetPathToGUID(dep);
                    if (!usagesByGuid.TryGetValue(depGuid, out var u)) continue;

                    u.referencingPrefabs.Add(path);
                    u.audioSourceRefCount++;
                    if (string.IsNullOrEmpty(u.usageExample))
                        u.usageExample = path;
                }
            }
        }

        // ─── Step 3: Lua name scan ──────────────────────────────────────

        private static void IndexLuaScriptReferences(
            string rootAssetFolder,
            Dictionary<string, List<AudioUsage>> usagesByName,
            Action<float, string> onProgress)
        {
            // playSFXByName("Sword_Hit_03") is the common path in Wizard's
            // Way's Lua scripts. Without checking these the orphan detector
            // would flag every by-name clip as orphaned. We grep .lua.txt
            // files for filenames (without extension) and count any
            // occurrence as a reference.
            string scriptsRoot = Path.Combine(rootAssetFolder, "Scripts");
            string searchRoot = AssetDatabase.IsValidFolder(scriptsRoot) ? scriptsRoot : rootAssetFolder;

            var luaGuids = AssetDatabase.FindAssets("t:TextAsset", new[] { searchRoot });
            for (int i = 0; i < luaGuids.Length; i++)
            {
                onProgress?.Invoke(0.65f + 0.25f * i / Mathf.Max(1, luaGuids.Length), "Scanning Lua scripts...");
                string path = AssetDatabase.GUIDToAssetPath(luaGuids[i]);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) continue;

                string body;
                try { body = File.ReadAllText(Path.GetFullPath(path)); }
                catch { continue; }

                // We only have to scan once per script — for each clip
                // name, IndexOf is O(n) so the inner loop stays cheap even
                // with hundreds of clips.
                foreach (var kvp in usagesByName)
                {
                    string name = kvp.Key;
                    // Wrap in quote-or-word-boundary check: bare name
                    // matches would false-positive on substrings ("Hit" in
                    // "Sword_Hit_03" matching a Lua identifier).
                    if (body.IndexOf("\"" + name + "\"", StringComparison.OrdinalIgnoreCase) < 0 &&
                        body.IndexOf("'" + name + "'", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    foreach (var u in kvp.Value)
                    {
                        u.referencingScripts.Add(path);
                        u.audioSourceRefCount++;
                        if (string.IsNullOrEmpty(u.usageExample))
                            u.usageExample = path;
                    }
                }
            }
        }

        // ─── Step 4: classification ─────────────────────────────────────

        private static void Classify(AudioUsage u)
        {
            // Path heuristics first — creators usually organize audio
            // by purpose, and the folder name is the strongest signal.
            string lower = u.assetPath.Replace('\\', '/').ToLowerInvariant();

            bool pathSaysMusic   = lower.Contains("/music/") || lower.Contains("/bgm/") || lower.Contains("/soundtrack/");
            bool pathSaysVoice   = lower.Contains("/voice/") || lower.Contains("/vo/") || lower.Contains("/dialog");
            bool pathSaysUi      = lower.Contains("/ui/") || lower.Contains("/menu/") || lower.Contains("/hud/");
            bool pathSaysAmbient = lower.Contains("/ambient/") || lower.Contains("/ambience/") || lower.Contains("/loop/");
            bool pathSaysSfx     = lower.Contains("/sfx/") || lower.Contains("/sound/") || lower.Contains("/audio/");

            if (pathSaysMusic)        u.kind = AudioUsageKind.Music;
            else if (pathSaysVoice)   u.kind = AudioUsageKind.Voice;
            else if (pathSaysUi)      u.kind = AudioUsageKind.UI;
            else if (pathSaysAmbient) u.kind = AudioUsageKind.Ambient;
            else if (pathSaysSfx)     u.kind = AudioUsageKind.SFX;
            else
            {
                // Fall back to duration: anything > 30 seconds is almost
                // certainly music or ambient. Below 30 seconds, default
                // to SFX — the most common case and the one whose default
                // policy is safest if we got it wrong.
                if (u.durationSeconds > 30f) u.kind = AudioUsageKind.Music;
                else u.kind = AudioUsageKind.SFX;
            }

            // Orphan override: if nothing references the clip, flag it.
            // The user can still opt to optimize anyway from the review UI.
            if (u.audioSourceRefCount == 0)
            {
                u.kind = AudioUsageKind.Orphan;
                u.note = "No prefab or script references found.";
            }
        }

        // ─── AudioImporter <-> AudioPlan enum mapping ───────────────────

        private static AudioTargetCompression MapFromUnity(AudioCompressionFormat fmt)
        {
            switch (fmt)
            {
                case AudioCompressionFormat.Vorbis: return AudioTargetCompression.Vorbis;
                case AudioCompressionFormat.ADPCM:  return AudioTargetCompression.ADPCM;
                case AudioCompressionFormat.PCM:    return AudioTargetCompression.PCM;
                // MP3/AAC/HEVAG/ATRAC9 are platform-specific or legacy —
                // we treat them as "keep as-is" so the planner offers
                // re-encode-to-Vorbis as the next step.
                default: return AudioTargetCompression.KeepAsIs;
            }
        }

        private static AudioTargetLoadType MapFromUnity(AudioClipLoadType lt)
        {
            switch (lt)
            {
                case AudioClipLoadType.DecompressOnLoad:    return AudioTargetLoadType.DecompressOnLoad;
                case AudioClipLoadType.CompressedInMemory:  return AudioTargetLoadType.CompressedInMemory;
                case AudioClipLoadType.Streaming:           return AudioTargetLoadType.Streaming;
                default: return AudioTargetLoadType.CompressedInMemory;
            }
        }

        public static AudioCompressionFormat MapToUnity(AudioTargetCompression c)
        {
            switch (c)
            {
                case AudioTargetCompression.Vorbis: return AudioCompressionFormat.Vorbis;
                case AudioTargetCompression.ADPCM:  return AudioCompressionFormat.ADPCM;
                case AudioTargetCompression.PCM:    return AudioCompressionFormat.PCM;
                default: return AudioCompressionFormat.Vorbis; // fallback — KeepAsIs should never reach the importer
            }
        }

        public static AudioClipLoadType MapToUnity(AudioTargetLoadType lt)
        {
            switch (lt)
            {
                case AudioTargetLoadType.DecompressOnLoad:   return AudioClipLoadType.DecompressOnLoad;
                case AudioTargetLoadType.CompressedInMemory: return AudioClipLoadType.CompressedInMemory;
                case AudioTargetLoadType.Streaming:          return AudioClipLoadType.Streaming;
                default: return AudioClipLoadType.CompressedInMemory;
            }
        }
    }
}
#endif
