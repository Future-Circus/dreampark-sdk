#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    internal static class TagLayerSchemaSyncUtility
    {
        internal const int UnityLayerSlots = 32;

        internal sealed class TagLayerSnapshot
        {
            public List<string> tags = new List<string>();
            public List<string> layers = new List<string>();
        }

        internal sealed class ApplyResult
        {
            public bool changed;
            public string error;
        }

        internal sealed class TagRemapResult
        {
            public int filesChanged;
            public int replacements;
        }

        internal sealed class PrefabRefreshResult
        {
            public int prefabsProcessed;
            public int prefabsReserialized;
        }

        internal static string TagManagerPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../ProjectSettings/TagManager.asset"));

        internal static TagLayerSnapshot ReadLocalTagManager()
        {
            var path = TagManagerPath;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("TagManager.asset not found", path);
            }

            var lines = File.ReadAllLines(path).ToList();
            int tagsStart = lines.FindIndex(l => l.Trim() == "tags:");
            int layersStart = lines.FindIndex(l => l.Trim() == "layers:");
            int sortingStart = lines.FindIndex(l => l.Trim() == "m_SortingLayers:");

            if (tagsStart < 0 || layersStart < 0 || sortingStart < 0 || !(tagsStart < layersStart && layersStart < sortingStart))
            {
                throw new InvalidOperationException("Unable to parse TagManager.asset sections");
            }

            var snapshot = new TagLayerSnapshot();

            for (int i = tagsStart + 1; i < layersStart; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("- "))
                {
                    var value = trimmed.Length > 2 ? trimmed.Substring(2) : "";
                    if (!string.IsNullOrEmpty(value))
                    {
                        snapshot.tags.Add(value);
                    }
                }
            }

            for (int i = layersStart + 1; i < sortingStart; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("- "))
                {
                    var value = trimmed.Length > 2 ? trimmed.Substring(2) : "";
                    snapshot.layers.Add(value ?? "");
                }
            }

            while (snapshot.layers.Count < UnityLayerSlots)
            {
                snapshot.layers.Add("");
            }

            if (snapshot.layers.Count > UnityLayerSlots)
            {
                snapshot.layers = snapshot.layers.Take(UnityLayerSlots).ToList();
            }

            return snapshot;
        }

        internal static ApplyResult ApplyCanonicalSchema(List<string> canonicalTags, List<string> canonicalLayers, bool preserveLocalExtras = true)
        {
            var result = new ApplyResult();
            var path = TagManagerPath;
            if (!File.Exists(path))
            {
                result.error = $"TagManager.asset not found at {path}";
                return result;
            }

            var current = ReadLocalTagManager();
            var tags = BuildTargetTagOrder(canonicalTags, current.tags, preserveLocalExtras);

            var layers = (canonicalLayers ?? new List<string>()).Take(UnityLayerSlots).Select(l => l ?? "").ToList();
            while (layers.Count < UnityLayerSlots)
            {
                layers.Add("");
            }

            if (preserveLocalExtras)
            {
                // Safety mode: keep local layer names in currently empty canonical slots so existing content
                // does not lose layer labels before proposal acceptance.
                for (int i = 0; i < UnityLayerSlots && i < current.layers.Count; i++)
                {
                    if (string.IsNullOrEmpty(layers[i]) && !string.IsNullOrEmpty(current.layers[i]))
                    {
                        layers[i] = current.layers[i];
                    }
                }
            }

            var lines = File.ReadAllLines(path).ToList();
            int tagsStart = lines.FindIndex(l => l.Trim() == "tags:");
            int layersStart = lines.FindIndex(l => l.Trim() == "layers:");
            int sortingStart = lines.FindIndex(l => l.Trim() == "m_SortingLayers:");

            if (tagsStart < 0 || layersStart < 0 || sortingStart < 0 || !(tagsStart < layersStart && layersStart < sortingStart))
            {
                result.error = "Unable to parse TagManager.asset sections";
                return result;
            }

            var rewritten = new List<string>();
            rewritten.AddRange(lines.Take(tagsStart + 1));
            rewritten.AddRange(tags.Select(tag => $"  - {tag}"));
            rewritten.Add(lines[layersStart]);
            rewritten.AddRange(layers.Select(layer => $"  - {layer}"));
            rewritten.AddRange(lines.Skip(sortingStart));

            var originalText = string.Join("\n", lines);
            var rewrittenText = string.Join("\n", rewritten);
            if (!string.Equals(originalText, rewrittenText, StringComparison.Ordinal))
            {
                File.WriteAllText(path, rewrittenText + "\n");
                result.changed = true;
                AssetDatabase.Refresh();
                Debug.Log($"[TagLayerSchema] Updated local TagManager from canonical schema: {path}");
            }

            return result;
        }

        internal static List<string> BuildTargetTagOrder(List<string> canonicalTags, List<string> localTags, bool preserveLocalExtras = true)
        {
            var tags = (canonicalTags ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            if (!preserveLocalExtras)
            {
                return tags;
            }

            foreach (var localTag in localTags ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(localTag) && !tags.Contains(localTag))
                {
                    tags.Add(localTag);
                }
            }

            return tags;
        }

        internal static Dictionary<string, string> BuildTagRemapByIndex(List<string> oldTags, List<string> newTags)
        {
            var map = new Dictionary<string, string>();
            int count = Math.Min(oldTags?.Count ?? 0, newTags?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                string oldTag = oldTags[i] ?? "";
                string newTag = newTags[i] ?? "";
                if (string.IsNullOrEmpty(oldTag) || string.IsNullOrEmpty(newTag) || oldTag == newTag)
                {
                    continue;
                }
                if (!map.ContainsKey(oldTag))
                {
                    map.Add(oldTag, newTag);
                }
            }
            return map;
        }

        internal static TagRemapResult RemapContentPrefabsByTagName(string contentId, Dictionary<string, string> tagRemap)
        {
            var result = new TagRemapResult();
            if (string.IsNullOrWhiteSpace(contentId) || tagRemap == null || tagRemap.Count == 0)
            {
                return result;
            }

            string contentRoot = Path.Combine("Assets", "Content", contentId);
            if (!Directory.Exists(contentRoot))
            {
                return result;
            }

            string[] prefabPaths = Directory.GetFiles(contentRoot, "*.prefab", SearchOption.AllDirectories);
            foreach (var prefabPath in prefabPaths)
            {
                string text = File.ReadAllText(prefabPath);
                int localReplacements = 0;
                foreach (var kv in tagRemap)
                {
                    string from = $"m_TagString: {kv.Key}";
                    string to = $"m_TagString: {kv.Value}";
                    if (text.Contains(from))
                    {
                        int count = text.Split(new[] { from }, StringSplitOptions.None).Length - 1;
                        text = text.Replace(from, to);
                        localReplacements += count;
                    }
                }

                if (localReplacements > 0 && text.Length >= 0)
                {
                    File.WriteAllText(prefabPath, text);
                    result.filesChanged++;
                    result.replacements += localReplacements;
                }
            }

            if (result.filesChanged > 0)
            {
                AssetDatabase.Refresh();
            }

            return result;
        }

        internal static PrefabRefreshResult ForceRefreshContentPrefabs(string contentId)
        {
            var result = new PrefabRefreshResult();
            if (string.IsNullOrWhiteSpace(contentId))
            {
                return result;
            }

            string contentRoot = Path.Combine("Assets", "Content", contentId);
            if (!Directory.Exists(contentRoot))
            {
                return result;
            }

            string[] prefabPaths = Directory.GetFiles(contentRoot, "*.prefab", SearchOption.AllDirectories);
            if (prefabPaths == null || prefabPaths.Length == 0)
            {
                return result;
            }

            var assetPaths = prefabPaths
                .Select(p => p.Replace("\\", "/"))
                .ToList();

            result.prefabsProcessed = assetPaths.Count;

            foreach (var assetPath in assetPaths)
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }

            AssetDatabase.ForceReserializeAssets(assetPaths);
            result.prefabsReserialized = assetPaths.Count;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return result;
        }
    }
}
#endif
