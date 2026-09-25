#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>Converts GUID-backed organizer data into a catalog-scoped runtime recipe.</summary>
    internal static class DreamParkPackageCompiler
    {
        internal static string ManifestPath(string contentId)
            => $"Assets/Content/{contentId}/{DreamParkPackageManifest.AssetName}.asset";

        internal static DreamParkPackageManifest Compile(string sourceContentId, string targetContentId = null)
        {
            if (string.IsNullOrEmpty(sourceContentId))
                throw new ArgumentException("A source content id is required.", nameof(sourceContentId));
            if (string.IsNullOrEmpty(targetContentId)) targetContentId = sourceContentId;
            string root = $"Assets/Content/{sourceContentId}";
            if (!AssetDatabase.IsValidFolder(root))
                throw new InvalidOperationException("Missing content folder: " + root);

            string playerAddress = FindPlayerAddress(root, targetContentId);
            string managerPath = DreamSequenceGenerator.ContainerPrefabPath(sourceContentId);
            GameObject manager = AssetDatabase.LoadAssetAtPath<GameObject>(managerPath);
            if (manager == null)
                managerPath = DreamSequenceGenerator.EnsureContainer(sourceContentId);
            string managerAddress = targetContentId + "/Game Container";
            var manifest = AssetDatabase.LoadAssetAtPath<DreamParkPackageManifest>(ManifestPath(sourceContentId));
            if (manifest == null)
            {
                manifest = ScriptableObject.CreateInstance<DreamParkPackageManifest>();
                AssetDatabase.CreateAsset(manifest, ManifestPath(sourceContentId));
            }

            manifest.schemaVersion = DreamParkPackageManifest.CurrentSchemaVersion;
            manifest.contentId = targetContentId;
            manifest.arena = CompileArenaRecipe(sourceContentId, targetContentId,
                playerAddress, managerAddress);
            manifest.adventure = CompileRecipe(sourceContentId, targetContentId, false,
                playerAddress, managerAddress);
            manifest.sequence = CompileRecipe(sourceContentId, targetContentId, true,
                playerAddress, managerAddress);
            if (manifest.arena == null && manifest.adventure == null && manifest.sequence == null)
                Debug.LogWarning($"[DreamParkPackageCompiler] {sourceContentId} has no complete package recipe.");
            if (manifest.sequence != null)
            {
                CopySequencePackingMetadata(sourceContentId, manifest.sequence);
                ValidateSequenceOrder(sourceContentId, manifest.sequence);
            }
            manifest.packageRevision = ComputePackageRevision(manifest);
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
            return manifest;
        }

        internal static PackageRecipe CompileArenaRecipe(string sourceContentId,
            string targetContentId, string playerAddress, string managerAddress)
        {
            string root = $"Assets/Content/{sourceContentId}";
            var candidates = AssetDatabase.FindAssets("t:Prefab", new[] { root })
                .Select(guid => new { guid, path = AssetDatabase.GUIDToAssetPath(guid) })
                .Where(item => !string.IsNullOrEmpty(item.path)
                    && item.path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0
                    && !DreamSequenceGenerator.IsSpecialLevelPath(sourceContentId, item.path))
                .Select(item => new
                {
                    item.guid,
                    item.path,
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.path),
                })
                .Where(item => item.prefab != null
                    && item.prefab.GetComponent<DreamSequenceTemplate>() == null
                    && (item.prefab.GetComponent<AttractionTemplate>() != null
                        || item.prefab.GetComponent<PropTemplate>() != null))
                .OrderBy(item => item.prefab.GetComponent<PropTemplate>() != null ? 1 : 0)
                .ThenBy(item => item.prefab.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ArenaPackageStore.Data layout = ArenaPackageStore.LoadAndReconcile(sourceContentId,
                candidates.Select(item => ArenaPackageStore.CandidateForPrefab(item.guid, item.prefab)));
            if (layout.buckets == null || layout.buckets.Count == 0) return null;
            if (string.IsNullOrEmpty(playerAddress))
                throw new InvalidOperationException("arena package has no PlayerRig prefab.");

            var recipe = new PackageRecipe
            {
                kind = "arena",
                playerAddress = playerAddress,
                gameManagerAddress = managerAddress,
            };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArenaPackageStore.Bucket sourceBucket in layout.buckets)
            {
                if (sourceBucket == null || sourceBucket.guids == null || sourceBucket.guids.Count == 0)
                    continue;
                var bucket = new ArenaSizeBucket
                {
                    widthFeet = sourceBucket.widthFeet,
                    lengthFeet = sourceBucket.lengthFeet,
                };
                foreach (string guid in sourceBucket.guids)
                {
                    string occurrenceId = "arena-" + guid;
                    int before = recipe.occurrences.Count;
                    AddOccurrence(recipe, ids, occurrenceId, guid, "stage", null,
                        sourceContentId, targetContentId, false, true,
                        sourceBucket.widthFeet, sourceBucket.lengthFeet);
                    if (recipe.occurrences.Count > before)
                        bucket.occurrenceIds.Add(occurrenceId);
                }
                if (bucket.occurrenceIds.Count > 0) recipe.arenaBuckets.Add(bucket);
            }
            if (recipe.occurrences.Count == 0) return null;
            if (recipe.arenaBuckets.Count > 200 || recipe.occurrences.Count > 2000)
                throw new InvalidOperationException("arena package exceeds Web release limits.");
            return recipe;
        }

        internal static PackageRecipe CompileRecipe(string sourceContentId, string targetContentId,
            bool sequenceMode, string playerAddress, string managerAddress)
        {
            string layoutPath = sequenceMode
                ? $"Assets/Content/{sourceContentId}/.dreampark-dream-sequence.json"
                : ContentSequenceStore.AssetPath(sourceContentId);
            if (!File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", layoutPath))))
                return null;

            string root = $"Assets/Content/{sourceContentId}";
            var available = AssetDatabase.FindAssets("t:Prefab", new[] { root })
                .Where(g => !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g)))
                .Where(g => AssetDatabase.GUIDToAssetPath(g).IndexOf("/ThirdPartyLocal/",
                    StringComparison.OrdinalIgnoreCase) < 0)
                .Where(g => !DreamSequenceGenerator.IsSpecialLevelPath(sourceContentId,
                    AssetDatabase.GUIDToAssetPath(g)))
                .Where(g => IsPlacementPrefab(AssetDatabase.GUIDToAssetPath(g)))
                .ToList();
            var attractions = available.Where(g =>
                AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g))
                    ?.GetComponent<AttractionTemplate>() != null).ToList();
            ValidateAuthoredReferences(ContentSequenceStore.Load(sourceContentId, sequenceMode),
                new HashSet<string>(available, StringComparer.Ordinal), sequenceMode);
            ContentSequenceStore.Data layout = ContentSequenceStore.LoadAndReconcile(sourceContentId,
                available, attractions, sequenceMode);
            if (!sequenceMode && (!layout.hasExplicitEndpoints
                || string.IsNullOrEmpty(layout.startGuid) || string.IsNullOrEmpty(layout.endGuid)))
                return null;

            var recipe = new PackageRecipe
            {
                kind = sequenceMode ? "sequence" : "adventure",
                playerAddress = playerAddress,
                gameManagerAddress = managerAddress,
                sequenceDefinitionAddress = sequenceMode
                    ? DreamSequencePackageDefinition.AddressFor(targetContentId) : null,
            };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!sequenceMode)
                AddOccurrence(recipe, ids, "start", layout.startGuid, "start", null,
                    sourceContentId, targetContentId, false);

            foreach (ContentSequenceStore.Entry item in layout.items ?? new List<ContentSequenceStore.Entry>())
            {
                if (item == null || item.hidden) continue;
                if (item.IsWorld)
                {
                    string groupId = RequiredId(item.id, "Group");
                    if (!ids.Add(groupId))
                        throw new InvalidOperationException("Duplicate package occurrence id: " + groupId);
                    recipe.groups.Add(new PackageGroupOccurrence
                    {
                        occurrenceId = groupId,
                        name = string.IsNullOrWhiteSpace(item.name) ? "Group" : item.name,
                        order = recipe.occurrences.Count,
                    });
                    for (int i = 0; i < (item.attractionGuids?.Count ?? 0); i++)
                    {
                        string childId = i < (item.attractionIds?.Count ?? 0)
                            ? item.attractionIds[i] : null;
                        AddOccurrence(recipe, ids, childId, item.attractionGuids[i], "stage", groupId,
                            sourceContentId, targetContentId, sequenceMode);
                    }
                }
                else
                    AddOccurrence(recipe, ids, item.id, item.attractionGuid, "stage", null,
                        sourceContentId, targetContentId, sequenceMode);
            }

            if (!sequenceMode)
                AddOccurrence(recipe, ids, "end", layout.endGuid, "end", null,
                    sourceContentId, targetContentId, false);

            if (recipe.occurrences.Count == 0) return null;
            if (sequenceMode && recipe.occurrences.All(o => o.role != "stage")) return null;
            if (recipe.groups.Count > 200 || recipe.occurrences.Count > 2000)
                throw new InvalidOperationException($"{recipe.kind} package exceeds Web release limits.");
            if (string.IsNullOrEmpty(recipe.playerAddress))
                throw new InvalidOperationException($"{recipe.kind} package has no PlayerRig prefab.");
            return recipe;
        }

        private static void ValidateAuthoredReferences(ContentSequenceStore.Data authored,
            HashSet<string> available, bool sequenceMode)
        {
            // Reconciliation is useful for the editor's reusable library, but
            // must not silently erase a deleted prefab from an authored
            // package. In particular that could make required content vanish
            // from a released recipe without any placement failure.
            if (authored == null || !authored.hasExplicitEndpoints) return;
            var referenced = new List<string>();
            if (!sequenceMode)
            {
                referenced.Add(authored.startGuid);
                referenced.Add(authored.endGuid);
            }
            foreach (ContentSequenceStore.Entry item in authored.items ?? new List<ContentSequenceStore.Entry>())
            {
                if (item == null || item.hidden) continue;
                if (item.IsWorld) referenced.AddRange(item.attractionGuids ?? new List<string>());
                else referenced.Add(item.attractionGuid);
            }
            foreach (string guid in referenced)
            {
                if (string.IsNullOrEmpty(guid) || !available.Contains(guid))
                    throw new InvalidOperationException($"{(sequenceMode ? "Sequence" : "Adventure")} package references a missing or invalid prefab ({guid ?? "no GUID"}). Remove or replace the placement before upload.");
            }
        }

        private static void AddOccurrence(PackageRecipe recipe, HashSet<string> ids,
            string occurrenceId, string guid, string role, string groupId,
            string sourceContentId, string targetContentId, bool sequenceMode,
            bool arenaMode = false, int widthFeet = 0, int lengthFeet = 0)
        {
            occurrenceId = RequiredId(occurrenceId, role);
            if (!ids.Add(occurrenceId))
                throw new InvalidOperationException("Duplicate package occurrence id: " + occurrenceId);
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                throw new InvalidOperationException($"Package occurrence '{occurrenceId}' has no prefab: {guid}");
            AttractionTemplate attraction = prefab.GetComponent<AttractionTemplate>();
            PropTemplate prop = prefab.GetComponent<PropTemplate>();
            if (attraction == null && prop == null)
                throw new InvalidOperationException($"Package occurrence '{occurrenceId}' is not an attraction or prop.");
            if (sequenceMode)
            {
                if (attraction == null || attraction is DreamSequenceTemplate
                    || !attraction.HasPackingBake || !DreamSequenceCompatibility.IsCompatible(attraction))
                {
                    if (attraction != null && attraction.gameRequiresAttraction)
                        throw new InvalidOperationException(
                            $"Required Sequence occurrence '{prefab.name}' cannot fit its 12 × 18 ft room at its baked shrink size.");
                    // The editor shows incompatible assets dimmed to explain why
                    // they will not participate. Only included attractions are
                    // emitted, matching DreamSequencePackageCompiler's order.
                    Debug.LogWarning($"[DreamParkPackageCompiler] Skipping incompatible Sequence occurrence '{prefab.name}'.");
                    return;
                }
            }
            else if (!arenaMode && attraction != null && !attraction.HasPackingBake)
                throw new InvalidOperationException($"Adventure occurrence '{prefab.name}' has no packing bake.");
            string resourceAddress = attraction != null
                ? $"{targetContentId}/Levels/{attraction.size}/{Path.GetFileNameWithoutExtension(path)}"
                : $"{targetContentId}/Props/{prop.category}/{Path.GetFileNameWithoutExtension(path)}";
            if (!path.StartsWith($"Assets/Content/{sourceContentId}/", StringComparison.Ordinal))
                throw new InvalidOperationException("Package occurrence is outside its content folder: " + path);
            var occurrence = new PackageOccurrence
            {
                occurrenceId = occurrenceId,
                resourceAddress = resourceAddress,
                role = prop != null ? "prop" : role,
                // Arena candidates are mutually exclusive alternatives. A candidate cannot be
                // required independently of the bucket selection, even when its source
                // Attraction marks itself as game-required for Adventure/Sequence.
                required = !arenaMode && (role == "start" || role == "end"
                    || (attraction != null && attraction.gameRequiresAttraction)),
                order = recipe.occurrences.Count,
                groupOccurrenceId = groupId,
                widthFeet = widthFeet,
                lengthFeet = lengthFeet,
            };
            CopyPackingMetadata(occurrence, attraction, prop);
            recipe.occurrences.Add(occurrence);
        }

        private static void CopyPackingMetadata(PackageOccurrence occurrence,
            AttractionTemplate attraction, PropTemplate prop)
        {
            if (occurrence == null) return;
            if (attraction != null)
            {
                Vector2 authored = new Vector2(attraction.Size.x, attraction.Size.z);
                occurrence.authoredFootprintMeters = authored;
                occurrence.safeFootprintMeters = authored;
                occurrence.shrinkFootprintMeters = authored;
                occurrence.growFootprintMeters = authored;
                occurrence.essentialShrinkFootprintMeters = authored;
                occurrence.safeAreaInset = attraction.safeAreaInset;
                if (attraction.HasPackingBake)
                {
                    AttractionPackingBake bake = attraction.PackingBake;
                    occurrence.authoredFootprintMeters = bake.AuthoredFootprintMeters;
                    occurrence.safeFootprintMeters = bake.SafeFootprintMeters;
                    occurrence.shrinkFootprintMeters = bake.ShrinkFootprintMeters;
                    occurrence.growFootprintMeters = bake.GrowFootprintMeters;
                    occurrence.essentialShrinkFootprintMeters = bake.EssentialShrinkFootprintMeters;
                    occurrence.shrinkScale = bake.ShrinkScale;
                    occurrence.growScale = bake.GrowScale;
                    occurrence.essentialShrinkScale = bake.EssentialShrinkScale;
                    occurrence.hasEssentialProps = bake.HasEssentialProps;
                }
                occurrence.hasPackingMetadata = authored.x > 0f && authored.y > 0f;
                return;
            }

            if (prop != null)
            {
                if (!prop.TryGetAuthoredFootprintMeters(out Vector2 footprint)) return;
                occurrence.authoredFootprintMeters = footprint;
                occurrence.safeFootprintMeters = footprint;
                occurrence.shrinkFootprintMeters = footprint;
                occurrence.growFootprintMeters = footprint;
                occurrence.essentialShrinkFootprintMeters = footprint;
                occurrence.hasPackingMetadata = footprint.x > 0f && footprint.y > 0f;
            }
        }

        private static void CopySequencePackingMetadata(string sourceContentId,
            PackageRecipe recipe)
        {
            DreamSequencePackageDefinition definition = AssetDatabase.LoadAssetAtPath<
                DreamSequencePackageDefinition>(DreamSequencePackageCompiler.DefinitionPath(sourceContentId));
            if (definition == null || recipe == null) return;

            Vector2 authored = new Vector2(DreamSequenceTemplate.StandardWidthFeet,
                DreamSequenceTemplate.StandardLengthFeet) * 0.3048f;
            Vector2 minimum = definition.minimumRoomMeters;
            if (minimum.x <= 0f || minimum.y <= 0f) minimum = authored;
            Vector2 maximum = definition.maximumRoomMeters;
            if (maximum.x <= 0f || maximum.y <= 0f) maximum = authored;
            maximum = new Vector2(Mathf.Max(authored.x, maximum.x),
                Mathf.Max(authored.y, maximum.y));

            recipe.hasPackingMetadata = authored.x > 0f && authored.y > 0f;
            recipe.authoredFootprintMeters = authored;
            recipe.safeFootprintMeters = authored;
            recipe.shrinkFootprintMeters = minimum;
            recipe.growFootprintMeters = maximum;
            recipe.essentialShrinkFootprintMeters = minimum;
            recipe.shrinkScale = new Vector2(minimum.x / authored.x, minimum.y / authored.y);
            recipe.growScale = new Vector2(maximum.x / authored.x, maximum.y / authored.y);
            recipe.essentialShrinkScale = recipe.shrinkScale;
            recipe.safeAreaInset = 0f;
            recipe.hasEssentialProps = false;
        }

        private static string ComputePackageRevision(DreamParkPackageManifest manifest)
        {
            string previous = manifest.packageRevision;
            manifest.packageRevision = "";
            string json = JsonUtility.ToJson(manifest, false);
            manifest.packageRevision = previous;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                return string.Concat(digest.Select(value => value.ToString("x2")));
            }
        }

        private static string RequiredId(string id, string description)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(description + " has no stable occurrence id.");
            return id;
        }

        internal static bool IsValidAuthoredReference(string contentId, string guid)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(contentId)) return false;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string root = $"Assets/Content/{contentId}/";
            return !string.IsNullOrEmpty(path)
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0
                && !DreamSequenceGenerator.IsSpecialLevelPath(contentId, path)
                && IsPlacementPrefab(path);
        }

        private static bool IsPlacementPrefab(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return prefab != null && (prefab.GetComponent<AttractionTemplate>() != null
                || prefab.GetComponent<PropTemplate>() != null);
        }

        private static string FindPlayerAddress(string root, string targetContentId)
        {
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .OrderBy(p => string.Equals(Path.GetFileNameWithoutExtension(p), "Player",
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string path in paths)
            {
                if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<PlayerRig>() != null)
                    return targetContentId + "/" + Path.GetFileNameWithoutExtension(path);
            }
            return null;
        }

        private static void ValidateSequenceOrder(string sourceContentId, PackageRecipe recipe)
        {
            DreamSequencePackageDefinition definition = AssetDatabase.LoadAssetAtPath<
                DreamSequencePackageDefinition>(DreamSequencePackageCompiler.DefinitionPath(sourceContentId));
            if (definition == null)
                throw new InvalidOperationException("Sequence package definition has not been compiled.");
            List<string> manifestOrder = recipe.occurrences.Select(o => o.resourceAddress).ToList();
            List<string> definitionOrder = (definition.levels ?? new List<DreamSequenceLevel>())
                .Select(o => o.address).ToList();
            if (manifestOrder.Count != definitionOrder.Count
                || manifestOrder.Where((address, index) =>
                    !string.Equals(address.Substring(address.IndexOf("/Levels/", StringComparison.Ordinal)),
                        definitionOrder[index].Substring(definitionOrder[index].IndexOf("/Levels/", StringComparison.Ordinal)),
                        StringComparison.Ordinal)).Any())
                throw new InvalidOperationException("Sequence manifest and lazy-load definition have different level order.");
        }
    }
}
#endif
