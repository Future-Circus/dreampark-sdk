#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// Version-controlled Arena authoring metadata. Every placeable belongs to
    /// exactly one rotation-normalized whole-foot bucket. New assets opt in
    /// automatically; authors persist exclusions, priority, and any deliberate
    /// size-bucket overrides.
    /// </summary>
    public static class ArenaPackageStore
    {
        public const int CurrentVersion = 2;
        public const float FeetPerMeter = 1f / 0.3048f;

        [Serializable]
        public sealed class Bucket
        {
            public int widthFeet = 1;
            public int lengthFeet = 1;
            public List<string> guids = new List<string>();

            public string Label => widthFeet + " × " + lengthFeet + " ft";
        }

        [Serializable]
        public sealed class Data
        {
            public int schemaVersion = CurrentVersion;
            public List<Bucket> buckets = new List<Bucket>();
            public List<string> excludedGuids = new List<string>();
            public List<SizeOverride> sizeOverrides = new List<SizeOverride>();
        }

        [Serializable]
        public sealed class SizeOverride
        {
            public string guid;
            public int widthFeet = 1;
            public int lengthFeet = 1;
        }

        public readonly struct Candidate
        {
            public readonly string guid;
            public readonly int widthFeet;
            public readonly int lengthFeet;

            public bool IsValid => !string.IsNullOrEmpty(guid)
                && widthFeet > 0 && lengthFeet > 0;

            public Candidate(string guid, float widthFeet, float lengthFeet)
            {
                this.guid = guid;
                if (TryNormalizeSize(widthFeet, lengthFeet, out int width, out int length))
                {
                    this.widthFeet = width;
                    this.lengthFeet = length;
                }
                else
                {
                    this.widthFeet = 0;
                    this.lengthFeet = 0;
                }
            }
        }

        public static string AssetPath(string contentId)
            => $"Assets/Content/{contentId}/.dreampark-arena.json";

        public static Candidate CandidateForPrefab(string guid, GameObject prefab)
        {
            // Arena placement is deliberately opt-in by component type and size.
            // In particular, a plain LevelTemplate is not an AttractionTemplate:
            // silently treating an unknown prefab as 1 x 1 ft makes unrelated
            // levels appear as the highest-priority small-room experience.
            if (prefab == null || string.IsNullOrEmpty(guid)) return default;
            AttractionTemplate attraction = prefab.GetComponent<AttractionTemplate>();
            if (attraction != null)
            {
                Vector2 feet = attraction.DimensionsInFeet;
                if (attraction.HasPackingBake)
                    feet = attraction.PackingBake.ShrinkFootprintMeters * FeetPerMeter;
                return new Candidate(guid, feet.x, feet.y);
            }
            PropTemplate prop = prefab.GetComponent<PropTemplate>();
            if (prop == null || !prop.TryGetAuthoredFootprintMeters(out Vector2 footprintMeters))
                return default;
            Vector2 propFeet = footprintMeters * FeetPerMeter;
            return new Candidate(guid, propFeet.x, propFeet.y);
        }

        public static Data LoadAndReconcile(string contentId, IEnumerable<Candidate> candidates)
            => Reconcile(Load(contentId), candidates);

        public static Data Reconcile(Data data, IEnumerable<Candidate> candidates)
        {
            data = data ?? new Data();
            data.buckets = data.buckets ?? new List<Bucket>();
            data.excludedGuids = data.excludedGuids ?? new List<string>();
            data.sizeOverrides = data.sizeOverrides ?? new List<SizeOverride>();

            var ordered = (candidates ?? Enumerable.Empty<Candidate>())
                .Where(candidate => candidate.IsValid)
                .GroupBy(candidate => candidate.guid, StringComparer.Ordinal)
                .Select(group => group.First()).ToList();
            var available = ordered.ToDictionary(candidate => candidate.guid,
                candidate => candidate, StringComparer.Ordinal);
            var overrides = new Dictionary<string, (int width, int length)>(StringComparer.Ordinal);
            foreach (SizeOverride item in data.sizeOverrides.Where(item => item != null))
            {
                if (string.IsNullOrEmpty(item.guid)
                    || !available.TryGetValue(item.guid, out Candidate natural)) continue;
                NormalizeSize(item.widthFeet, item.lengthFeet,
                    out int overrideWidth, out int overrideLength);
                if (overrideWidth <= 0 || overrideLength <= 0
                    || (overrideWidth == natural.widthFeet
                        && overrideLength == natural.lengthFeet)) continue;
                overrides[item.guid] = (overrideWidth, overrideLength);
            }
            data.sizeOverrides = overrides.Select(pair => new SizeOverride
            {
                guid = pair.Key,
                widthFeet = pair.Value.width,
                lengthFeet = pair.Value.length,
            }).ToList();
            data.excludedGuids = data.excludedGuids.Where(available.ContainsKey)
                .Distinct(StringComparer.Ordinal).ToList();
            var excluded = new HashSet<string>(data.excludedGuids, StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var buckets = new Dictionary<(int width, int length), Bucket>();

            // Preserve the author's current priority whenever the asset still
            // belongs to the same effective bucket. A footprint edit moves it to
            // the end of its new bucket unless the author explicitly overrode it.
            foreach (Bucket existing in data.buckets.Where(bucket => bucket != null))
            {
                foreach (string guid in existing.guids ?? new List<string>())
                {
                    if (!available.TryGetValue(guid, out Candidate candidate)
                        || excluded.Contains(guid)) continue;
                    EffectiveSize(candidate, overrides, out int width, out int length);
                    if (existing.widthFeet != width || existing.lengthFeet != length
                        || !claimed.Add(guid)) continue;
                    Bucket bucket = GetOrCreate(buckets, width, length);
                    bucket.guids.Add(guid);
                }
            }

            foreach (Candidate candidate in ordered)
            {
                if (excluded.Contains(candidate.guid) || !claimed.Add(candidate.guid)) continue;
                EffectiveSize(candidate, overrides, out int width, out int length);
                GetOrCreate(buckets, width, length).guids.Add(candidate.guid);
            }

            data.buckets = buckets.Values.Where(bucket => bucket.guids.Count > 0)
                .OrderBy(bucket => (long)bucket.widthFeet * bucket.lengthFeet)
                .ThenBy(bucket => bucket.widthFeet)
                .ThenBy(bucket => bucket.lengthFeet).ToList();
            data.schemaVersion = CurrentVersion;
            return data;
        }

        public static bool SetExcluded(Data data, IEnumerable<Candidate> candidates,
            string guid, bool excluded)
        {
            if (data == null || string.IsNullOrEmpty(guid)) return false;
            data.excludedGuids = data.excludedGuids ?? new List<string>();
            data.buckets = data.buckets ?? new List<Bucket>();
            bool changed = false;
            if (excluded)
            {
                foreach (Bucket bucket in data.buckets)
                    changed |= (bucket?.guids?.RemoveAll(item => item == guid) ?? 0) > 0;
                if (!data.excludedGuids.Contains(guid))
                {
                    data.excludedGuids.Add(guid);
                    changed = true;
                }
            }
            else
            {
                changed = data.excludedGuids.Remove(guid);
            }
            Reconcile(data, candidates);
            return changed;
        }

        public static bool Move(Data data, int widthFeet, int lengthFeet,
            string guid, int delta)
        {
            if (data?.buckets == null || string.IsNullOrEmpty(guid) || delta == 0) return false;
            NormalizeSize(widthFeet, lengthFeet, out int normalizedWidth, out int normalizedLength);
            Bucket bucket = data.buckets.FirstOrDefault(item => item != null
                && item.widthFeet == normalizedWidth && item.lengthFeet == normalizedLength);
            if (bucket?.guids == null) return false;
            int from = bucket.guids.IndexOf(guid);
            int to = Mathf.Clamp(from + delta, 0, bucket.guids.Count - 1);
            if (from < 0 || from == to) return false;
            bucket.guids.RemoveAt(from);
            bucket.guids.Insert(to, guid);
            return true;
        }

        public static bool MoveRelative(Data data, int widthFeet, int lengthFeet,
            string sourceGuid, string targetGuid, bool after)
            => MoveRelative(data, widthFeet, lengthFeet, widthFeet, lengthFeet,
                sourceGuid, targetGuid, after);

        public static bool MoveRelative(Data data, int sourceWidthFeet, int sourceLengthFeet,
            int targetWidthFeet, int targetLengthFeet, string sourceGuid, string targetGuid,
            bool after)
        {
            if (data?.buckets == null || string.IsNullOrEmpty(sourceGuid)
                || string.IsNullOrEmpty(targetGuid) || sourceGuid == targetGuid) return false;
            NormalizeSize(sourceWidthFeet, sourceLengthFeet,
                out int sourceWidth, out int sourceLength);
            NormalizeSize(targetWidthFeet, targetLengthFeet,
                out int targetWidth, out int targetLength);
            Bucket sourceBucket = FindBucket(data, sourceWidth, sourceLength);
            Bucket targetBucket = FindBucket(data, targetWidth, targetLength);
            if (sourceBucket?.guids == null || targetBucket?.guids == null) return false;

            int from = sourceBucket.guids.IndexOf(sourceGuid);
            int anchor = targetBucket.guids.IndexOf(targetGuid);
            if (from < 0 || anchor < 0) return false;
            int to = anchor + (after ? 1 : 0);
            if (sourceBucket == targetBucket)
            {
                if (from < to) to--;
                if (from == to) return false;
            }

            sourceBucket.guids.RemoveAt(from);
            targetBucket.guids.Insert(Mathf.Clamp(to, 0, targetBucket.guids.Count), sourceGuid);
            if (sourceBucket != targetBucket)
            {
                SetSizeOverride(data, sourceGuid, targetWidth, targetLength);
                RemoveEmptyBucketsAndSort(data);
            }
            return true;
        }

        public static bool MoveToBucketEnd(Data data, int sourceWidthFeet,
            int sourceLengthFeet, int targetWidthFeet, int targetLengthFeet, string sourceGuid)
        {
            if (data?.buckets == null || string.IsNullOrEmpty(sourceGuid)) return false;
            NormalizeSize(sourceWidthFeet, sourceLengthFeet,
                out int sourceWidth, out int sourceLength);
            NormalizeSize(targetWidthFeet, targetLengthFeet,
                out int targetWidth, out int targetLength);
            Bucket sourceBucket = FindBucket(data, sourceWidth, sourceLength);
            Bucket targetBucket = FindBucket(data, targetWidth, targetLength);
            if (sourceBucket?.guids == null || targetBucket?.guids == null) return false;
            int from = sourceBucket.guids.IndexOf(sourceGuid);
            if (from < 0 || (sourceBucket == targetBucket
                && from == sourceBucket.guids.Count - 1)) return false;
            sourceBucket.guids.RemoveAt(from);
            targetBucket.guids.Add(sourceGuid);
            if (sourceBucket != targetBucket)
            {
                SetSizeOverride(data, sourceGuid, targetWidth, targetLength);
                RemoveEmptyBucketsAndSort(data);
            }
            return true;
        }

        public static bool HasSizeOverride(Data data, string guid)
            => data?.sizeOverrides != null && data.sizeOverrides.Any(item => item != null
                && string.Equals(item.guid, guid, StringComparison.Ordinal));

        public static bool ClearSizeOverride(Data data, IEnumerable<Candidate> candidates,
            string guid)
        {
            if (data?.sizeOverrides == null || string.IsNullOrEmpty(guid)) return false;
            bool changed = data.sizeOverrides.RemoveAll(item => item != null
                && string.Equals(item.guid, guid, StringComparison.Ordinal)) > 0;
            if (changed) Reconcile(data, candidates);
            return changed;
        }

        public static Data Load(string contentId)
        {
            string fullPath = ToFullPath(AssetPath(contentId));
            if (!File.Exists(fullPath)) return new Data();
            try
            {
                return Deserialize(File.ReadAllText(fullPath));
            }
            catch (InvalidOperationException)
            {
                // A newer SDK may own fields this version cannot preserve.
                // Propagate instead of returning an empty layout: callers such
                // as RefreshContentRoots auto-save the reconciled result and
                // would otherwise destructively downgrade the author's file.
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ArenaPackageStore] Could not read {fullPath}: {e.Message}");
                return new Data();
            }
        }

        internal static Data Deserialize(string json)
        {
            Data data = JsonUtility.FromJson<Data>(json) ?? new Data();
            if (data.schemaVersion > CurrentVersion)
                throw new InvalidOperationException(
                    $"Arena metadata is schema v{data.schemaVersion}; this SDK supports v{CurrentVersion}.");
            data.schemaVersion = CurrentVersion;
            return data;
        }

        public static void Save(string contentId, Data data)
        {
            if (string.IsNullOrEmpty(contentId) || data == null) return;
            data.schemaVersion = CurrentVersion;
            string path = AssetPath(contentId);
            string fullPath = ToFullPath(path);
            string json = JsonUtility.ToJson(data, true) + "\n";
            if (File.Exists(fullPath) && File.ReadAllText(fullPath) == json) return;
            File.WriteAllText(fullPath, json);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        public static Data Clone(Data data)
            => data == null ? new Data()
                : JsonUtility.FromJson<Data>(JsonUtility.ToJson(data)) ?? new Data();

        public static void NormalizeSize(float widthFeet, float lengthFeet,
            out int normalizedWidth, out int normalizedLength)
        {
            if (!TryNormalizeSize(widthFeet, lengthFeet,
                out normalizedWidth, out normalizedLength))
            {
                normalizedWidth = 0;
                normalizedLength = 0;
            }
        }

        private static bool TryNormalizeSize(float widthFeet, float lengthFeet,
            out int normalizedWidth, out int normalizedLength)
        {
            normalizedWidth = 0;
            normalizedLength = 0;
            if (!IsFinitePositive(widthFeet) || !IsFinitePositive(lengthFeet)) return false;
            int width = Mathf.Max(1, Mathf.FloorToInt(widthFeet));
            int length = Mathf.Max(1, Mathf.FloorToInt(lengthFeet));
            normalizedWidth = Mathf.Min(width, length);
            normalizedLength = Mathf.Max(width, length);
            return true;
        }

        private static bool IsFinitePositive(float value)
            => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);

        private static void EffectiveSize(Candidate candidate,
            Dictionary<string, (int width, int length)> overrides,
            out int widthFeet, out int lengthFeet)
        {
            if (overrides.TryGetValue(candidate.guid, out var size))
            {
                widthFeet = size.width;
                lengthFeet = size.length;
                return;
            }
            widthFeet = candidate.widthFeet;
            lengthFeet = candidate.lengthFeet;
        }

        private static Bucket FindBucket(Data data, int widthFeet, int lengthFeet)
            => data.buckets.FirstOrDefault(item => item != null
                && item.widthFeet == widthFeet && item.lengthFeet == lengthFeet);

        private static void SetSizeOverride(Data data, string guid,
            int widthFeet, int lengthFeet)
        {
            data.sizeOverrides = data.sizeOverrides ?? new List<SizeOverride>();
            data.sizeOverrides.RemoveAll(item => item != null
                && string.Equals(item.guid, guid, StringComparison.Ordinal));
            data.sizeOverrides.Add(new SizeOverride
            {
                guid = guid,
                widthFeet = widthFeet,
                lengthFeet = lengthFeet,
            });
        }

        private static void RemoveEmptyBucketsAndSort(Data data)
        {
            data.buckets = data.buckets.Where(bucket => bucket?.guids != null
                    && bucket.guids.Count > 0)
                .OrderBy(bucket => (long)bucket.widthFeet * bucket.lengthFeet)
                .ThenBy(bucket => bucket.widthFeet)
                .ThenBy(bucket => bucket.lengthFeet).ToList();
        }

        private static Bucket GetOrCreate(Dictionary<(int width, int length), Bucket> buckets,
            int widthFeet, int lengthFeet)
        {
            var key = (widthFeet, lengthFeet);
            if (!buckets.TryGetValue(key, out Bucket bucket))
            {
                bucket = new Bucket { widthFeet = widthFeet, lengthFeet = lengthFeet };
                buckets.Add(key, bucket);
            }
            return bucket;
        }

        private static string ToFullPath(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
    }
}
#endif
