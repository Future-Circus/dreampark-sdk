#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>Compiles the saved organizer into an addressable recipe, not a prefab.</summary>
    internal static class DreamSequencePackageCompiler
    {
        public static string DefinitionPath(string contentId)
            => $"Assets/Content/{contentId}/DreamSequence/{DreamSequencePackageDefinition.AssetName}.asset";

        public static DreamSequencePackageDefinition Compile(string contentId)
            => Compile(contentId, contentId);

        // The local assets stay in sourceContentId's folder for beta uploads,
        // but the built catalog and every lazy-load address use targetContentId.
        public static DreamSequencePackageDefinition Compile(string contentId, string targetContentId)
        {
            if (string.IsNullOrEmpty(contentId))
                throw new ArgumentException("A content id is required.", nameof(contentId));
            if (string.IsNullOrEmpty(targetContentId))
                throw new ArgumentException("A target content id is required.", nameof(targetContentId));
            DreamSequenceGenerator.EnsureScaffold(contentId);
            string root = $"Assets/Content/{contentId}";
            var all = new List<string>();
            var attractions = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (DreamSequenceGenerator.IsSpecialLevelPath(contentId, path)
                    || string.Equals(path, DreamSequenceGenerator.SequencePrefabPath(contentId),
                        StringComparison.OrdinalIgnoreCase)) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponent<DreamSequenceTemplate>() != null) continue;
                if (prefab.GetComponent<AttractionTemplate>() != null)
                {
                    all.Add(guid);
                    attractions.Add(guid);
                }
                else if (prefab.GetComponent<PropTemplate>() != null) all.Add(guid);
            }

            ContentSequenceStore.Data layout = ContentSequenceStore.LoadAndReconcile(
                contentId, all, attractions, true);
            string pathForDefinition = DefinitionPath(contentId);
            DreamSequencePackageDefinition definition = AssetDatabase.LoadAssetAtPath<
                DreamSequencePackageDefinition>(pathForDefinition);
            if (definition == null)
            {
                definition = ScriptableObject.CreateInstance<DreamSequencePackageDefinition>();
                AssetDatabase.CreateAsset(definition, pathForDefinition);
            }
            definition.sequenceName = "Dream Sequence";
            definition.startLevelPrefab = LoadSpecial(DreamSequenceGenerator.StartLevelPath(contentId));
            definition.overlayLevelPrefab = LoadSpecial(DreamSequenceGenerator.OverlayLevelPath(contentId));
            definition.gameOverLevelPrefab = LoadSpecial(DreamSequenceGenerator.GameOverLevelPath(contentId));
            definition.gameManagerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                DreamSequenceGenerator.ContainerPrefabPath(contentId));
            if (definition.gameManagerPrefab == null)
                throw new InvalidOperationException("Sequence Game Manager prefab is missing after baking.");
            if (definition.gameManagerPrefab == null)
                throw new InvalidOperationException("Sequence Game Manager prefab is missing.");

            Vector2 authored = new Vector2(DreamSequenceTemplate.StandardWidthFeet,
                DreamSequenceTemplate.StandardLengthFeet) * 0.3048f;
            Vector2 minimum = Vector2.zero;
            Vector2 maximum = authored;
            minimum = ExpandMinimum(minimum, definition.startLevelPrefab.GetComponent<AttractionTemplate>(), false);
            minimum = ExpandMinimum(minimum, definition.overlayLevelPrefab.GetComponent<AttractionTemplate>(), false);
            minimum = ExpandMinimum(minimum, definition.gameOverLevelPrefab.GetComponent<AttractionTemplate>(), false);
            maximum = ExpandMaximum(maximum, definition.startLevelPrefab.GetComponent<AttractionTemplate>(), false);
            maximum = ExpandMaximum(maximum, definition.overlayLevelPrefab.GetComponent<AttractionTemplate>(), false);
            maximum = ExpandMaximum(maximum, definition.gameOverLevelPrefab.GetComponent<AttractionTemplate>(), false);
            definition.levels ??= new List<DreamSequenceLevel>();
            definition.levels.Clear();
            foreach (var stage in Stages(layout))
            {
                string guid = stage.guid;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AttractionTemplate attraction = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                if (attraction == null || attraction is DreamSequenceTemplate) continue;
                if (!attraction.HasPackingBake)
                {
                    if (!AttractionPackingBaker.BakeIntoAsset(attraction))
                        throw new InvalidOperationException($"Could not bake Sequence attraction '{path}'.");
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    attraction = prefab?.GetComponent<AttractionTemplate>();
                }
                if (attraction == null || !DreamSequenceCompatibility.IsCompatible(attraction)) continue;
                minimum = ExpandMinimum(minimum, attraction, true);
                maximum = ExpandMaximum(maximum, attraction, true);
                definition.levels.Add(new DreamSequenceLevel
                {
                    sourceGuid = guid,
                    displayName = prefab.name,
                    address = $"{targetContentId}/Levels/{attraction.size}/{System.IO.Path.GetFileNameWithoutExtension(path)}",
                    occurrenceId = stage.occurrenceId,
                    groupOccurrenceId = stage.groupId,
                    sourceGroupId = stage.sourceGroupId,
                    groupName = stage.groupName,
                });
            }
            if (minimum.x > authored.x + 0.001f || minimum.y > authored.y + 0.001f)
                throw new InvalidOperationException($"Sequence minimum {minimum} exceeds its 12 × 18 ft room.");
            // Baking stage prefabs can reimport the scaffold's referenced
            // prefabs. Rebind from asset paths after the bake pass so the
            // persisted definition never keeps stale Unity object handles.
            definition.startLevelPrefab = LoadSpecial(DreamSequenceGenerator.StartLevelPath(contentId));
            definition.overlayLevelPrefab = LoadSpecial(DreamSequenceGenerator.OverlayLevelPath(contentId));
            definition.gameOverLevelPrefab = LoadSpecial(DreamSequenceGenerator.GameOverLevelPath(contentId));
            definition.gameManagerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                DreamSequenceGenerator.ContainerPrefabPath(contentId));
            definition.minimumRoomMeters = minimum;
            definition.maximumRoomMeters = maximum;
            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssets();
            return definition;
        }

        private static IEnumerable<(string guid, string occurrenceId, string groupId,
            string sourceGroupId, string groupName)> Stages(
            ContentSequenceStore.Data layout)
        {
            foreach (ContentSequenceStore.Entry item in layout.items ?? new List<ContentSequenceStore.Entry>())
            {
                if (item == null || item.hidden) continue;
                if (!item.IsWorld)
                {
                    if (!string.IsNullOrEmpty(item.attractionGuid))
                        yield return (item.attractionGuid, item.id, null, null, null);
                    continue;
                }
                for (int i = 0; i < (item.attractionGuids?.Count ?? 0); i++)
                {
                    string id = i < (item.attractionIds?.Count ?? 0) ? item.attractionIds[i] : null;
                    yield return (item.attractionGuids[i], id, item.id,
                        item.sourceGroupId, item.name);
                }
            }
        }

        private static GameObject LoadSpecial(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException("Sequence level prefab is missing: " + path);
            return prefab;
        }

        private static Vector2 ExpandMinimum(Vector2 current, AttractionTemplate attraction,
            bool allowRotation)
        {
            if (attraction == null || !attraction.HasPackingBake)
                throw new InvalidOperationException($"Sequence level '{attraction?.name ?? "missing"}' has no packing bake.");
            Vector2 minimum = attraction.PackingBake.ShrinkFootprintMeters;
            if (allowRotation && DreamSequenceCompatibility.ShouldRotate(attraction))
                minimum = new Vector2(minimum.y, minimum.x);
            return new Vector2(Mathf.Max(current.x, minimum.x),
                Mathf.Max(current.y, minimum.y));
        }

        private static Vector2 ExpandMaximum(Vector2 current, AttractionTemplate attraction,
            bool allowRotation)
        {
            if (attraction == null || !attraction.HasPackingBake)
                throw new InvalidOperationException($"Sequence level '{attraction?.name ?? "missing"}' has no packing bake.");
            Vector2 maximum = attraction.PackingBake.GrowFootprintMeters;
            if (allowRotation && DreamSequenceCompatibility.ShouldRotate(attraction))
                maximum = new Vector2(maximum.y, maximum.x);
            return new Vector2(Mathf.Max(current.x, maximum.x),
                Mathf.Max(current.y, maximum.y));
        }
    }
}
#endif
