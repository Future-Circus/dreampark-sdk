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
    /// Version-controlled progression metadata authored by the Content Uploader.
    /// Top-level entries are either an attraction or a World; a World owns its own
    /// ordered attraction list. Every attraction appears exactly once.
    /// </summary>
    public static class ContentSequenceStore
    {
        public const int CurrentVersion = 2;

        [Serializable]
        public sealed class Entry
        {
            public string kind = "attraction";
            public string id;
            public string name;
            public string attractionGuid;
            public List<string> attractionGuids = new List<string>();
            public bool hidden;

            public bool IsWorld => string.Equals(kind, "world", StringComparison.Ordinal);
        }

        [Serializable]
        public sealed class Data
        {
            public int schemaVersion = CurrentVersion;
            public List<Entry> items = new List<Entry>();
        }

        public static string AssetPath(string contentId)
            => $"Assets/Content/{contentId}/.dreampark-sequence.json";

        public static Data LoadAndReconcile(string contentId, IEnumerable<string> attractionGuids)
        {
            var data = Load(contentId);
            var available = new HashSet<string>(attractionGuids.Where(g => !string.IsNullOrEmpty(g)), StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            data.items = (data.items ?? new List<Entry>()).Where(item => item != null).ToList();
            foreach (var item in data.items)
            {
                if (item.IsWorld)
                {
                    item.attractionGuids = (item.attractionGuids ?? new List<string>())
                        .Where(g => available.Contains(g) && claimed.Add(g)).ToList();
                    if (string.IsNullOrEmpty(item.id)) item.id = Guid.NewGuid().ToString("N");
                    if (string.IsNullOrWhiteSpace(item.name)) item.name = "New World";
                }
            }

            data.items = data.items.Where(item => item.IsWorld
                || (!string.IsNullOrEmpty(item.attractionGuid)
                    && available.Contains(item.attractionGuid)
                    && claimed.Add(item.attractionGuid))).ToList();

            foreach (string guid in available.OrderBy(GuidToName, StringComparer.OrdinalIgnoreCase))
            {
                if (!claimed.Contains(guid))
                {
                    int firstHidden = data.items.FindIndex(item => item.hidden);
                    var entry = new Entry { attractionGuid = guid };
                    if (firstHidden >= 0) data.items.Insert(firstHidden, entry);
                    else data.items.Add(entry);
                }
            }
            data.items = data.items.Where(item => !item.hidden)
                .Concat(data.items.Where(item => item.hidden)).ToList();
            return data;
        }

        public static Data Load(string contentId)
        {
            string fullPath = ToFullPath(AssetPath(contentId));
            if (!File.Exists(fullPath)) return new Data();
            try
            {
                var data = JsonUtility.FromJson<Data>(File.ReadAllText(fullPath)) ?? new Data();
                if (data.schemaVersion > CurrentVersion)
                    throw new InvalidOperationException($"Sequence metadata is schema v{data.schemaVersion}; this SDK supports v{CurrentVersion}.");
                data.schemaVersion = CurrentVersion;
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ContentSequenceStore] Could not read {fullPath}: {e.Message}");
                return new Data();
            }
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
        {
            if (data == null) return new Data();
            return JsonUtility.FromJson<Data>(JsonUtility.ToJson(data)) ?? new Data();
        }

        public static IEnumerable<string> Flatten(Data data, bool includeHidden = false)
        {
            if (data?.items == null) yield break;
            foreach (var item in data.items)
            {
                if (item == null) continue;
                if (item.IsWorld)
                {
                    foreach (string guid in item.attractionGuids ?? new List<string>()) yield return guid;
                }
                else if (!string.IsNullOrEmpty(item.attractionGuid) && (includeHidden || !item.hidden))
                    yield return item.attractionGuid;
            }
        }

        public static bool IsHidden(Data data, string guid)
            => data?.items != null && data.items.Any(item => item != null && !item.IsWorld
                && item.hidden && item.attractionGuid == guid);

        public static bool SetHidden(Data data, string guid, bool hidden)
        {
            if (data?.items == null || string.IsNullOrEmpty(guid)) return false;
            bool exists = data.items.Any(item => item != null && !item.IsWorld && item.attractionGuid == guid)
                || data.items.Any(item => item != null && item.IsWorld
                    && (item.attractionGuids ?? new List<string>()).Contains(guid));
            if (!exists) return false;

            data.items.RemoveAll(item => item != null && !item.IsWorld && item.attractionGuid == guid);
            foreach (Entry world in data.items.Where(item => item != null && item.IsWorld))
                (world.attractionGuids ?? (world.attractionGuids = new List<string>())).Remove(guid);

            var entry = new Entry { attractionGuid = guid, hidden = hidden };
            if (hidden) data.items.Add(entry);
            else
            {
                int firstHidden = data.items.FindIndex(item => item.hidden);
                if (firstHidden >= 0) data.items.Insert(firstHidden, entry);
                else data.items.Add(entry);
            }
            return true;
        }

        /// <summary>
        /// Moves a World or attraction using stable entry identities rather than
        /// indices captured while IMGUI is drawing. Target formats are:
        /// top-before|a:guid, top-before|w:id, top-after|..., top-end, and
        /// world|worldId|childIndex.
        /// </summary>
        public static bool TryMove(Data data, string source, string target)
        {
            if (data?.items == null || string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return false;

            if (source.StartsWith("w:", StringComparison.Ordinal))
            {
                int from = FindTopEntry(data, source);
                if (from < 0 || !TryResolveTopInsertion(data, target, out int to)) return false;

                Entry world = data.items[from];
                data.items.RemoveAt(from);
                if (from < to) to--;
                data.items.Insert(Math.Max(0, Math.Min(to, data.items.Count)), world);
                return true;
            }

            if (!source.StartsWith("a:", StringComparison.Ordinal)) return false;
            string guid = source.Substring(2);
            int sourceTop = data.items.FindIndex(x => x != null && !x.IsWorld && x.attractionGuid == guid);
            if (sourceTop >= 0 && data.items[sourceTop].hidden) return false;
            Entry sourceWorld = data.items.FirstOrDefault(x => x != null && x.IsWorld
                && (x.attractionGuids ?? new List<string>()).Contains(guid));
            int sourceChild = sourceWorld != null ? sourceWorld.attractionGuids.IndexOf(guid) : -1;

            Entry destinationWorld = null;
            int destinationChild = -1;
            int destinationTop = -1;
            if (target.StartsWith("world|", StringComparison.Ordinal))
            {
                string[] parts = target.Split('|');
                if (parts.Length != 3 || !int.TryParse(parts[2], out destinationChild)) return false;
                destinationWorld = data.items.FirstOrDefault(x => x != null && x.IsWorld && x.id == parts[1]);
                if (destinationWorld == null) return false;
            }
            else if (!TryResolveTopInsertion(data, target, out destinationTop)) return false;

            data.items.RemoveAll(x => x != null && !x.IsWorld && x.attractionGuid == guid);
            foreach (Entry world in data.items.Where(x => x != null && x.IsWorld))
                (world.attractionGuids ?? (world.attractionGuids = new List<string>())).Remove(guid);

            if (destinationWorld != null)
            {
                if (sourceWorld == destinationWorld && sourceChild >= 0 && sourceChild < destinationChild)
                    destinationChild--;
                destinationWorld.attractionGuids.Insert(
                    Math.Max(0, Math.Min(destinationChild, destinationWorld.attractionGuids.Count)), guid);
            }
            else
            {
                if (sourceTop >= 0 && sourceTop < destinationTop) destinationTop--;
                data.items.Insert(Math.Max(0, Math.Min(destinationTop, data.items.Count)),
                    new Entry { attractionGuid = guid });
            }
            return true;
        }

        public static string EntryToken(Entry entry)
            => entry != null && entry.IsWorld ? "w:" + entry.id : "a:" + entry?.attractionGuid;

        private static bool TryResolveTopInsertion(Data data, string target, out int index)
        {
            index = -1;
            if (target == "top-end")
            {
                index = data.items.Count;
                return true;
            }

            const string before = "top-before|";
            const string after = "top-after|";
            bool placeAfter;
            string anchor;
            if (target.StartsWith(before, StringComparison.Ordinal))
            {
                placeAfter = false;
                anchor = target.Substring(before.Length);
            }
            else if (target.StartsWith(after, StringComparison.Ordinal))
            {
                placeAfter = true;
                anchor = target.Substring(after.Length);
            }
            else return false;

            index = FindTopEntry(data, anchor);
            if (index < 0) return false;
            if (placeAfter) index++;
            return true;
        }

        private static int FindTopEntry(Data data, string token)
        {
            if (token.StartsWith("w:", StringComparison.Ordinal))
            {
                string id = token.Substring(2);
                return data.items.FindIndex(x => x != null && x.IsWorld && x.id == id);
            }
            if (token.StartsWith("a:", StringComparison.Ordinal))
            {
                string guid = token.Substring(2);
                return data.items.FindIndex(x => x != null && !x.IsWorld && x.attractionGuid == guid);
            }
            return -1;
        }

        private static string GuidToName(string guid)
            => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid));

        private static string ToFullPath(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
    }
}
#endif
