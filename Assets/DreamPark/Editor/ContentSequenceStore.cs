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
    /// Park Asset libraries contain each asset once. Package layouts contain
    /// reusable placement instances, so the same asset or Group can appear more
    /// than once while every occurrence keeps a stable identity.
    /// </summary>
    public static class ContentSequenceStore
    {
        public const int CurrentVersion = 6;

        [Serializable]
        public sealed class Entry
        {
            public string kind = "attraction";
            public string id;
            public string sourceGroupId;
            public string name;
            public string attractionGuid;
            public List<string> attractionGuids = new List<string>();
            public List<string> attractionIds = new List<string>();
            public bool hidden;

            public bool IsWorld => string.Equals(kind, "world", StringComparison.Ordinal);
        }

        [Serializable]
        public sealed class Data
        {
            public int schemaVersion = CurrentVersion;
            public bool hasExplicitEndpoints;
            public string startGuid;
            public string endGuid;
            public List<Entry> items = new List<Entry>();
            public List<string> unsortedGuids = new List<string>();
            public List<string> hiddenGuids = new List<string>();
        }

        public static string AssetPath(string contentId)
            => $"Assets/Content/{contentId}/.dreampark-sequence.json";

        public static string LibraryAssetPath(string contentId)
            => $"Assets/Content/{contentId}/.dreampark-library.json";

        public static Data LoadAndReconcile(string contentId, IEnumerable<string> attractionGuids)
        {
            return Reconcile(Load(contentId), attractionGuids);
        }

        public static Data LoadAndReconcile(string contentId, IEnumerable<string> allGuids,
            IEnumerable<string> endpointCandidates, bool sequenceMode)
        {
            Data data = Reconcile(Load(contentId, sequenceMode), allGuids);
            if (!data.hasExplicitEndpoints)
                MigrateToExplicitEndpoints(data, endpointCandidates, sequenceMode);
            return Reconcile(data, allGuids);
        }

        public static Data Reconcile(Data data, IEnumerable<string> attractionGuids)
        {
            data = data ?? new Data();
            var orderedAvailable = attractionGuids.Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.Ordinal).ToList();
            var available = new HashSet<string>(orderedAvailable, StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            data.unsortedGuids = data.unsortedGuids ?? new List<string>();
            data.hiddenGuids = data.hiddenGuids ?? new List<string>();
            data.hiddenGuids = data.hiddenGuids.Where(available.Contains)
                .Distinct(StringComparer.Ordinal).ToList();
            if (data.hasExplicitEndpoints)
            {
                if (!available.Contains(data.startGuid)) data.startGuid = null;
                if (!available.Contains(data.endGuid)) data.endGuid = null;
            }

            data.items = (data.items ?? new List<Entry>()).Where(item => item != null).ToList();
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.items)
            {
                EnsureUniqueId(item, usedIds);
                if (item.IsWorld)
                {
                    NormalizeWorldChildren(item, available, usedIds, data.hasExplicitEndpoints, claimed);
                    if (string.IsNullOrWhiteSpace(item.name)) item.name = "New Group";
                }
            }

            data.items = data.items.Where(item => item.IsWorld
                || (!string.IsNullOrEmpty(item.attractionGuid)
                    && available.Contains(item.attractionGuid)
                    && (data.hasExplicitEndpoints || claimed.Add(item.attractionGuid)))).ToList();

            if (data.hasExplicitEndpoints)
            {
                // Package membership is now independent from the reusable Park
                // Assets library. There is no package-owned "unused" inventory.
                data.unsortedGuids.Clear();
            }

            foreach (string guid in orderedAvailable)
            {
                if (!data.hasExplicitEndpoints && !claimed.Contains(guid))
                {
                    int firstHidden = data.items.FindIndex(item => item.hidden);
                    var entry = NewPlacement(guid);
                    if (firstHidden >= 0) data.items.Insert(firstHidden, entry);
                    else data.items.Add(entry);
                }
            }
            data.items = data.items.Where(item => !item.hidden)
                .Concat(data.items.Where(item => item.hidden)).ToList();
            return data;
        }

        public static Data Load(string contentId)
            => Load(contentId, false);

        public static Data Load(string contentId, bool sequenceMode)
        {
            string fullPath = ToFullPath(sequenceMode
                ? $"Assets/Content/{contentId}/.dreampark-dream-sequence.json"
                : AssetPath(contentId));
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

        public static Data LoadLibraryAndReconcile(string contentId, IEnumerable<string> allGuids,
            Data adventureFallback = null)
        {
            string fullPath = ToFullPath(LibraryAssetPath(contentId));
            Data data;
            if (File.Exists(fullPath)) data = LoadPath(fullPath);
            else
            {
                data = new Data();
                if (adventureFallback != null)
                {
                    var hiddenFallback = new HashSet<string>(
                        adventureFallback.hiddenGuids ?? new List<string>());
                    foreach (Entry item in adventureFallback.items ?? new List<Entry>())
                        data.items.Add(CloneEntry(item));
                    var alreadyAdded = new HashSet<string>(Flatten(data, true));
                    foreach (string guid in new[] { adventureFallback.startGuid, adventureFallback.endGuid }
                        .Concat(adventureFallback.unsortedGuids ?? new List<string>()))
                    {
                        if (!string.IsNullOrEmpty(guid) && alreadyAdded.Add(guid))
                            data.items.Add(new Entry
                            {
                                attractionGuid = guid,
                                hidden = hiddenFallback.Contains(guid)
                            });
                    }
                }
            }
            data.hasExplicitEndpoints = false;
            data.startGuid = null;
            data.endGuid = null;
            data.unsortedGuids.Clear();
            data.hiddenGuids.Clear();
            return Reconcile(data, allGuids);
        }

        public static void SaveLibrary(string contentId, Data data)
        {
            if (string.IsNullOrEmpty(contentId) || data == null) return;
            data.schemaVersion = CurrentVersion;
            string path = LibraryAssetPath(contentId);
            string fullPath = ToFullPath(path);
            string json = JsonUtility.ToJson(data, true) + "\n";
            if (File.Exists(fullPath) && File.ReadAllText(fullPath) == json) return;
            File.WriteAllText(fullPath, json);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        public static void Save(string contentId, Data data)
            => Save(contentId, data, false);

        public static void Save(string contentId, Data data, bool sequenceMode)
        {
            if (string.IsNullOrEmpty(contentId) || data == null) return;
            data.schemaVersion = CurrentVersion;
            string path = sequenceMode
                ? $"Assets/Content/{contentId}/.dreampark-dream-sequence.json"
                : AssetPath(contentId);
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
            if (data.hasExplicitEndpoints && !string.IsNullOrEmpty(data.startGuid))
                yield return data.startGuid;
            foreach (var item in data.items)
            {
                if (item == null) continue;
                if (item.hidden && !includeHidden) continue;
                if (item.IsWorld)
                {
                    foreach (string guid in item.attractionGuids ?? new List<string>()) yield return guid;
                }
                else if (!string.IsNullOrEmpty(item.attractionGuid))
                    yield return item.attractionGuid;
            }
            if (data.hasExplicitEndpoints && !string.IsNullOrEmpty(data.endGuid))
                yield return data.endGuid;
        }

        public static bool IsHidden(Data data, string guid)
            => data != null && (data.hasExplicitEndpoints
                ? (data.hiddenGuids ?? new List<string>()).Contains(guid)
                : data.items != null && data.items.Any(item => item != null && !item.IsWorld
                    && item.hidden && item.attractionGuid == guid));

        public static bool SetHidden(Data data, string guid, bool hidden)
        {
            if (data?.items == null || string.IsNullOrEmpty(guid)) return false;
            if (data.hasExplicitEndpoints)
            {
                if (guid == data.startGuid || guid == data.endGuid) return false;
                bool explicitExists = RemoveLeaf(data, guid);
                if (!explicitExists && !(data.unsortedGuids ?? new List<string>()).Contains(guid)) return false;
                data.unsortedGuids = data.unsortedGuids ?? new List<string>();
                data.hiddenGuids = data.hiddenGuids ?? new List<string>();
                if (!data.unsortedGuids.Contains(guid)) data.unsortedGuids.Add(guid);
                if (hidden && !data.hiddenGuids.Contains(guid)) data.hiddenGuids.Add(guid);
                if (!hidden) data.hiddenGuids.Remove(guid);
                return true;
            }
            bool exists = data.items.Any(item => item != null && !item.IsWorld && item.attractionGuid == guid)
                || data.items.Any(item => item != null && item.IsWorld
                    && (item.attractionGuids ?? new List<string>()).Contains(guid));
            if (!exists) return false;

            data.items.RemoveAll(item => item != null && !item.IsWorld && item.attractionGuid == guid);
            foreach (Entry world in data.items.Where(item => item != null && item.IsWorld))
                (world.attractionGuids ?? (world.attractionGuids = new List<string>())).Remove(guid);

            var entry = NewPlacement(guid);
            entry.hidden = hidden;
            if (hidden) data.items.Add(entry);
            else
            {
                int firstHidden = data.items.FindIndex(item => item.hidden);
                if (firstHidden >= 0) data.items.Insert(firstHidden, entry);
                else data.items.Add(entry);
            }
            return true;
        }

        public static bool SetGroupHidden(Data data, string groupId, bool hidden)
        {
            if (data?.items == null || string.IsNullOrEmpty(groupId)) return false;
            Entry group = data.items.FirstOrDefault(x => x != null && x.IsWorld && x.id == groupId);
            if (group == null) return false;
            group.hidden = hidden;
            data.items.Remove(group);
            if (hidden) data.items.Add(group);
            else
            {
                int firstHidden = data.items.FindIndex(x => x != null && x.hidden);
                if (firstHidden >= 0) data.items.Insert(firstHidden, group);
                else data.items.Add(group);
            }
            return true;
        }

        public static bool RemoveFromPackage(Data data, string guid)
        {
            if (data == null || string.IsNullOrEmpty(guid)
                || guid == data.startGuid || guid == data.endGuid) return false;
            return RemoveLeaf(data, guid);
        }

        public static bool RemovePlacementFromPackage(Data data, string placementId)
        {
            if (data?.items == null || string.IsNullOrEmpty(placementId)) return false;
            int top = data.items.FindIndex(x => x != null && !x.IsWorld && x.id == placementId);
            if (top >= 0)
            {
                data.items.RemoveAt(top);
                return true;
            }

            foreach (Entry group in data.items.Where(x => x != null && x.IsWorld))
            {
                EnsureChildIds(group);
                int child = group.attractionIds.IndexOf(placementId);
                if (child < 0) continue;
                group.attractionIds.RemoveAt(child);
                group.attractionGuids.RemoveAt(child);
                return true;
            }
            return false;
        }

        public static bool RemoveGroupFromPackage(Data data, string groupId)
        {
            if (data?.items == null || string.IsNullOrEmpty(groupId)) return false;
            Entry group = data.items.FirstOrDefault(x => x != null && x.IsWorld && x.id == groupId);
            if (group == null) return false;
            data.items.Remove(group);
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
                if (data.hasExplicitEndpoints && (target == "start-slot" || target == "end-slot"
                    || target == "unsorted-end")) return false;
                int from = FindTopEntry(data, source);
                if (from < 0 || !TryResolveTopInsertion(data, target, out int to)) return false;

                Entry world = data.items[from];
                data.items.RemoveAt(from);
                if (from < to) to--;
                data.items.Insert(Math.Max(0, Math.Min(to, data.items.Count)), world);
                return true;
            }

            if (data.hasExplicitEndpoints
                && (source.StartsWith("a:", StringComparison.Ordinal)
                    || source.StartsWith("p:", StringComparison.Ordinal)))
                return TryMoveExplicitLeaf(data, source, target);

            if (!source.StartsWith("a:", StringComparison.Ordinal)) return false;
            string guid = source.Substring(2);
            int sourceTop = data.items.FindIndex(x => x != null && !x.IsWorld && x.attractionGuid == guid);
            if (sourceTop >= 0 && data.items[sourceTop].hidden) return false;
            Entry sourceEntry = sourceTop >= 0 ? data.items[sourceTop] : null;
            Entry sourceWorld = data.items.FirstOrDefault(x => x != null && x.IsWorld
                && (x.attractionGuids ?? new List<string>()).Contains(guid));
            int sourceChild = sourceWorld != null ? sourceWorld.attractionGuids.IndexOf(guid) : -1;
            if (sourceWorld != null) EnsureChildIds(sourceWorld);
            string sourceId = sourceEntry?.id ?? (sourceChild >= 0 ? sourceWorld.attractionIds[sourceChild] : null);

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
            {
                EnsureChildIds(world);
                int index = world.attractionGuids.IndexOf(guid);
                if (index < 0) continue;
                world.attractionGuids.RemoveAt(index);
                world.attractionIds.RemoveAt(index);
            }

            if (destinationWorld != null)
            {
                if (sourceWorld == destinationWorld && sourceChild >= 0 && sourceChild < destinationChild)
                    destinationChild--;
                destinationWorld.attractionGuids.Insert(
                    Math.Max(0, Math.Min(destinationChild, destinationWorld.attractionGuids.Count)), guid);
                destinationWorld.attractionIds.Insert(
                    Math.Max(0, Math.Min(destinationChild, destinationWorld.attractionIds.Count)),
                    string.IsNullOrEmpty(sourceId) ? Guid.NewGuid().ToString("N") : sourceId);
            }
            else
            {
                if (sourceTop >= 0 && sourceTop < destinationTop) destinationTop--;
                data.items.Insert(Math.Max(0, Math.Min(destinationTop, data.items.Count)),
                    sourceEntry ?? new Entry { id = sourceId, attractionGuid = guid });
            }
            return true;
        }

        public static string EntryToken(Entry entry)
            => entry != null && entry.IsWorld ? "w:" + entry.id : "a:" + entry?.attractionGuid;

        public static string PlacementToken(Entry entry)
            => entry != null && entry.IsWorld ? "w:" + entry.id : "p:" + entry?.id;

        public static string ChildPlacementToken(Entry group, int index)
        {
            EnsureChildIds(group);
            return group != null && index >= 0 && index < group.attractionIds.Count
                ? "p:" + group.attractionIds[index] : null;
        }

        public static string PlacementGuid(Data data, string placementId)
        {
            if (data?.items == null || string.IsNullOrEmpty(placementId)) return null;
            Entry top = data.items.FirstOrDefault(x => x != null && !x.IsWorld && x.id == placementId);
            if (top != null) return top.attractionGuid;
            foreach (Entry group in data.items.Where(x => x != null && x.IsWorld))
            {
                EnsureChildIds(group);
                int index = group.attractionIds.IndexOf(placementId);
                if (index >= 0) return group.attractionGuids[index];
            }
            return null;
        }

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
            if (token.StartsWith("p:", StringComparison.Ordinal))
            {
                string id = token.Substring(2);
                return data.items.FindIndex(x => x != null && !x.IsWorld && x.id == id);
            }
            return -1;
        }

        public static void MigrateToExplicitEndpoints(Data data, IEnumerable<string> endpointCandidates,
            bool sequenceMode)
        {
            if (data == null || data.hasExplicitEndpoints) return;
            List<string> oldOrder = Flatten(data, true).Distinct(StringComparer.Ordinal).ToList();
            var oldHidden = new HashSet<string>(data.items.Where(x => x != null && !x.IsWorld && x.hidden)
                .Select(x => x.attractionGuid), StringComparer.Ordinal);
            var candidates = new HashSet<string>(endpointCandidates ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            string start = sequenceMode ? null : oldOrder.FirstOrDefault(candidates.Contains);
            string end = sequenceMode ? null : oldOrder.LastOrDefault(candidates.Contains);
            int startIndex = oldOrder.IndexOf(start);
            int endIndex = oldOrder.IndexOf(end);
            var allowedMiddle = new HashSet<string>(oldOrder.Where((g, i) => startIndex >= 0 && endIndex > startIndex
                && i > startIndex && i < endIndex), StringComparer.Ordinal);

            foreach (Entry world in data.items.Where(x => x != null && x.IsWorld))
            {
                world.attractionGuids.Remove(start);
                world.attractionGuids.Remove(end);
            }
            data.items.RemoveAll(x => x != null && !x.IsWorld
                && (x.attractionGuid == start || x.attractionGuid == end || sequenceMode));

            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry item in data.items)
            {
                if (item.IsWorld)
                    foreach (string guid in item.attractionGuids) assigned.Add(guid);
                else if (!string.IsNullOrEmpty(item.attractionGuid)) assigned.Add(item.attractionGuid);
            }

            // With no World there was no explicit authored grouping boundary. Keep
            // only Start/End assigned and make everything else visibly Unsorted.
            bool hasWorld = data.items.Any(x => x != null && x.IsWorld);
            if (!hasWorld || sequenceMode)
            {
                data.items.Clear();
                assigned.Clear();
            }
            else
            {
                foreach (Entry world in data.items.Where(x => x != null && x.IsWorld))
                    world.attractionGuids = world.attractionGuids.Where(allowedMiddle.Contains).ToList();
                data.items.RemoveAll(x => x != null && !x.IsWorld && !allowedMiddle.Contains(x.attractionGuid));
                assigned.Clear();
                foreach (Entry item in data.items)
                {
                    if (item.IsWorld) foreach (string guid in item.attractionGuids) assigned.Add(guid);
                    else assigned.Add(item.attractionGuid);
                }
            }

            data.startGuid = start;
            data.endGuid = end;
            data.unsortedGuids = oldOrder.Where(g => g != start && g != end && !assigned.Contains(g)).ToList();
            data.hiddenGuids = oldOrder.Where(oldHidden.Contains).ToList();
            data.items.RemoveAll(x => x != null && !x.IsWorld && x.hidden);
            data.hasExplicitEndpoints = true;
            data.schemaVersion = CurrentVersion;
        }

        private static bool TryMoveExplicitLeaf(Data data, string source, string target)
        {
            bool copy = source.StartsWith("a:", StringComparison.Ordinal);
            string placementId = copy ? null : source.StartsWith("p:", StringComparison.Ordinal)
                ? source.Substring(2) : null;
            string guid = copy ? source.Substring(2) : null;
            Entry sourceEntry = null;
            Entry sourceWorld = null;
            int sourceTop = -1;
            int sourceChild = -1;
            if (!copy)
            {
                sourceTop = data.items.FindIndex(x => x != null && !x.IsWorld && x.id == placementId);
                if (sourceTop >= 0)
                {
                    sourceEntry = data.items[sourceTop];
                    guid = sourceEntry.attractionGuid;
                }
                else
                {
                    foreach (Entry group in data.items.Where(x => x != null && x.IsWorld))
                    {
                        EnsureChildIds(group);
                        int index = group.attractionIds.IndexOf(placementId);
                        if (index < 0) continue;
                        sourceWorld = group;
                        sourceChild = index;
                        guid = group.attractionGuids[index];
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(guid)) return false;
            Entry destinationWorld = null;
            int destinationChild = -1;
            int destinationTop = -1;
            if (target.StartsWith("world|", StringComparison.Ordinal))
            {
                string[] parts = target.Split('|');
                if (parts.Length != 3 || !int.TryParse(parts[2], out destinationChild)) return false;
                destinationWorld = data.items.FirstOrDefault(x => x != null && x.IsWorld && x.id == parts[1]);
                if (destinationWorld == null) return false;
                EnsureChildIds(destinationWorld);
            }
            else if (target != "start-slot" && target != "end-slot"
                && !TryResolveTopInsertion(data, target, out destinationTop)) return false;

            if (!copy)
            {
                if (sourceTop >= 0) data.items.RemoveAt(sourceTop);
                else if (sourceWorld != null)
                {
                    sourceWorld.attractionGuids.RemoveAt(sourceChild);
                    sourceWorld.attractionIds.RemoveAt(sourceChild);
                }
                else return false;
            }

            if (target == "start-slot") data.startGuid = guid;
            else if (target == "end-slot") data.endGuid = guid;
            else if (destinationWorld != null)
            {
                if (sourceWorld == destinationWorld && sourceChild >= 0 && sourceChild < destinationChild)
                    destinationChild--;
                int index = Mathf.Clamp(destinationChild, 0, destinationWorld.attractionGuids.Count);
                destinationWorld.attractionGuids.Insert(index, guid);
                destinationWorld.attractionIds.Insert(index,
                    copy ? Guid.NewGuid().ToString("N") : placementId);
            }
            else
            {
                if (sourceTop >= 0 && sourceTop < destinationTop) destinationTop--;
                Entry placement = copy ? NewPlacement(guid) : sourceEntry ?? new Entry
                {
                    id = placementId,
                    attractionGuid = guid,
                };
                data.items.Insert(Mathf.Clamp(destinationTop, 0, data.items.Count), placement);
            }
            if (target == "start-slot" || target == "end-slot")
                (data.unsortedGuids ?? (data.unsortedGuids = new List<string>())).Remove(guid);
            return true;
        }

        private static bool RemoveLeaf(Data data, string guid)
        {
            bool found = false;
            if (data.startGuid == guid) { data.startGuid = null; found = true; }
            if (data.endGuid == guid) { data.endGuid = null; found = true; }
            found |= data.items.RemoveAll(x => x != null && !x.IsWorld && x.attractionGuid == guid) > 0;
            foreach (Entry world in data.items.Where(x => x != null && x.IsWorld))
            {
                EnsureChildIds(world);
                int index;
                while ((index = world.attractionGuids.IndexOf(guid)) >= 0)
                {
                    world.attractionGuids.RemoveAt(index);
                    world.attractionIds.RemoveAt(index);
                    found = true;
                }
            }
            found |= (data.unsortedGuids ?? (data.unsortedGuids = new List<string>())).Remove(guid);
            return found;
        }

        private static Entry NewPlacement(string guid)
            => new Entry { id = Guid.NewGuid().ToString("N"), attractionGuid = guid };

        private static void EnsureUniqueId(Entry entry, HashSet<string> usedIds)
        {
            if (entry == null) return;
            if (string.IsNullOrEmpty(entry.id) || !usedIds.Add(entry.id))
            {
                entry.id = Guid.NewGuid().ToString("N");
                usedIds.Add(entry.id);
            }
        }

        private static void EnsureChildIds(Entry group)
        {
            if (group == null) return;
            group.attractionGuids = group.attractionGuids ?? new List<string>();
            group.attractionIds = group.attractionIds ?? new List<string>();
            while (group.attractionIds.Count > group.attractionGuids.Count)
                group.attractionIds.RemoveAt(group.attractionIds.Count - 1);
            while (group.attractionIds.Count < group.attractionGuids.Count)
                group.attractionIds.Add(Guid.NewGuid().ToString("N"));
        }

        private static void NormalizeWorldChildren(Entry group, HashSet<string> available,
            HashSet<string> usedIds, bool reusablePlacements, HashSet<string> claimed)
        {
            EnsureChildIds(group);
            var guids = new List<string>();
            var ids = new List<string>();
            for (int i = 0; i < group.attractionGuids.Count; i++)
            {
                string guid = group.attractionGuids[i];
                if (!available.Contains(guid) || (!reusablePlacements && !claimed.Add(guid))) continue;
                string id = group.attractionIds[i];
                if (string.IsNullOrEmpty(id) || !usedIds.Add(id))
                {
                    id = Guid.NewGuid().ToString("N");
                    usedIds.Add(id);
                }
                guids.Add(guid);
                ids.Add(id);
            }
            group.attractionGuids = guids;
            group.attractionIds = ids;
        }

        private static Data LoadPath(string fullPath)
        {
            try
            {
                var data = JsonUtility.FromJson<Data>(File.ReadAllText(fullPath)) ?? new Data();
                if (data.schemaVersion > CurrentVersion)
                    throw new InvalidOperationException($"Organizer metadata is schema v{data.schemaVersion}; this SDK supports v{CurrentVersion}.");
                data.schemaVersion = CurrentVersion;
                data.items = data.items ?? new List<Entry>();
                data.unsortedGuids = data.unsortedGuids ?? new List<string>();
                data.hiddenGuids = data.hiddenGuids ?? new List<string>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ContentSequenceStore] Could not read {fullPath}: {e.Message}");
                return new Data();
            }
        }

        private static Entry CloneEntry(Entry entry)
        {
            if (entry == null) return null;
            return new Entry
            {
                kind = entry.kind,
                id = entry.id,
                sourceGroupId = entry.sourceGroupId,
                name = entry.name,
                attractionGuid = entry.attractionGuid,
                attractionGuids = new List<string>(entry.attractionGuids ?? new List<string>()),
                attractionIds = new List<string>(entry.attractionIds ?? new List<string>()),
                hidden = entry.hidden,
            };
        }

        private static string GuidToName(string guid)
            => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid));

        private static string ToFullPath(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
    }
}
#endif
