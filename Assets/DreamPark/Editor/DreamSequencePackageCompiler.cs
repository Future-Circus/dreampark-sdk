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

        /// <summary>
        /// A Sequence is optional, like Arena and Adventure. It counts as
        /// authored once its saved organizer holds at least one visible
        /// attraction that fits the 12 × 18 ft room; anything short of that
        /// is left out of the upload rather than shipped broken.
        /// </summary>
        public static bool IsAuthored(string contentId)
            => ContentSequenceStore.Flatten(ContentSequenceStore.Load(contentId, true))
                .Select(guid => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)))
                .Select(prefab => prefab != null && prefab.GetComponent<PropTemplate>() == null
                    ? prefab.GetComponent<AttractionTemplate>() : null)
                .Any(attraction => attraction != null && !(attraction is DreamSequenceTemplate)
                    && DreamSequenceCompatibility.IsCompatible(attraction));

        /// <summary>Saved Sequence items exist, but none can form a playable room.</summary>
        public static bool IsStarted(string contentId)
            => ContentSequenceStore.Flatten(ContentSequenceStore.Load(contentId, true)).Any();

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
            // Oct 11 2026 (WW A8): the Overlay and Game Over never set the
            // minimum room. The Overlay is an empty, prop-less level whose bake
            // cannot shrink (shrink == authored 12 × 18 ft), so it pinned every
            // Sequence (Wizards Way, SAL, LaserTag) to exactly 12 × 18 ft even
            // when every stage plays in 8 × 12 ft; the runtime already says the
            // Overlay and Game Manager "never define room size"
            // (DreamSequencePackageDefinition.Create). The Game Over is a short
            // end card that fits its room the same way. They still count
            // toward the maximum below.
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
                if (NeedsBake(attraction))
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

        // A stale frame bake silently drops that frame from the ladder (and can
        // make an attraction look Sequence-incompatible), so rebake it like Base.
        private static bool NeedsBake(AttractionTemplate attraction)
        {
            if (!attraction.HasPackingBake) return true;
            IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
            for (int i = 0; i < frames.Count; i++)
            {
                AttractionLayoutFrame frame = frames[i];
                // The baker never bakes a frame without a positive size, so such a
                // frame stays unbaked; rebaking for it would resave the prefab on
                // every compile (every upload) without ever making it valid.
                if (frame == null || frame.sizeFeet.x <= 0f || frame.sizeFeet.y <= 0f) continue;
                if (!attraction.FrameHasValidBake(frame)) return true;
            }
            return false;
        }

        private static Vector2 ExpandMinimum(Vector2 current, AttractionTemplate attraction,
            bool allowRotation)
        {
            Vector2 minimum = LevelMinimumFootprint(attraction, allowRotation);
            return new Vector2(Mathf.Max(current.x, minimum.x),
                Mathf.Max(current.y, minimum.y));
        }

        /// <summary>
        /// One level's contribution to the Sequence minimum room (Sequence axes,
        /// metres). The package minimum is the per-axis max of these, so the
        /// Content Uploader uses this to name the level that limits shrinking.
        /// Throws when the level has no bake or no frame fits the standard room.
        /// </summary>
        internal static Vector2 LevelMinimumFootprint(AttractionTemplate attraction, bool allowRotation)
        {
            if (attraction == null || !attraction.HasPackingBake)
                throw new InvalidOperationException($"Sequence level '{attraction?.name ?? "missing"}' has no packing bake.");
            if (attraction.HasLayoutFrames)
                return MinimumFrameFootprint(attraction, allowRotation);
            Vector2 minimum = attraction.PackingBake.ShrinkFootprintMeters;
            if (allowRotation && DreamSequenceCompatibility.ShouldRotate(attraction))
                minimum = new Vector2(minimum.y, minimum.x);
            return minimum;
        }

        /// <summary>Attraction GUIDs Compile turns into levels, in order (hidden entries skipped).</summary>
        internal static IEnumerable<string> StageGuids(ContentSequenceStore.Data layout)
            => layout == null ? Enumerable.Empty<string>() : Stages(layout).Select(stage => stage.guid);

        private static Vector2 ExpandMaximum(Vector2 current, AttractionTemplate attraction,
            bool allowRotation)
        {
            if (attraction == null || !attraction.HasPackingBake)
                throw new InvalidOperationException($"Sequence level '{attraction?.name ?? "missing"}' has no packing bake.");
            Vector2 maximum;
            if (attraction.HasLayoutFrames)
                maximum = MaximumFrameFootprint(attraction, allowRotation);
            else
            {
                maximum = attraction.PackingBake.GrowFootprintMeters;
                if (allowRotation && DreamSequenceCompatibility.ShouldRotate(attraction))
                    maximum = new Vector2(maximum.y, maximum.x);
            }
            return new Vector2(Mathf.Max(current.x, maximum.x),
                Mathf.Max(current.y, maximum.y));
        }

        /// <summary>
        /// The smallest room (Sequence axes) this level can play in: the shrink of
        /// its smallest-area layout frame that fits the standard 12 × 18 ft room,
        /// in the orientation it fits (rotation only when allowed).
        /// </summary>
        internal static Vector2 MinimumFrameFootprint(AttractionTemplate attraction, bool allowRotation)
        {
            Vector2 room = DreamSequenceCompatibility.StandardRoomMeters;
            List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
            bool found = false;
            AttractionFrameEnvelope best = default;
            bool bestRotated = false;
            foreach (AttractionFrameEnvelope envelope in ladder)
            {
                if (!DreamSequenceCompatibility.TryOrientFrame(envelope, room, allowRotation, out bool rotate))
                    continue;
                // Ladder order is largest → smallest authored, so on an area tie
                // the larger authored layout is kept.
                if (found && Area(envelope.shrinkMeters) >= Area(best.shrinkMeters) - 0.0001f) continue;
                found = true;
                best = envelope;
                bestRotated = rotate;
            }
            if (!found)
            {
                Vector2 smallest = attraction.MinimumFootprintMeters * FeetPerMeter;
                throw new InvalidOperationException(
                    $"Sequence level '{attraction.name}' has no layout frame that shrinks into the 12 × 18 ft room"
                    + (allowRotation ? " either way round" : " without rotating")
                    + $" (its smallest frame shrinks to {smallest.x:0.#} × {smallest.y:0.#} ft). "
                    + "Add a smaller layout frame such as a Minimum frame, then rebake.");
            }
            return bestRotated ? Swap(best.shrinkMeters) : best.shrinkMeters;
        }

        /// <summary>
        /// The largest room (Sequence axes) this level can use: its largest layout
        /// frame's grow, in the orientation that frame takes in the standard room
        /// (unrotated when it cannot fit there, or when rotation is not allowed).
        /// </summary>
        internal static Vector2 MaximumFrameFootprint(AttractionTemplate attraction, bool allowRotation)
        {
            List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
            if (ladder.Count == 0)
                throw new InvalidOperationException($"Sequence level '{attraction.name}' has no packing bake.");
            AttractionFrameEnvelope largest = ladder[0];
            DreamSequenceCompatibility.TryOrientFrame(largest,
                DreamSequenceCompatibility.StandardRoomMeters, allowRotation, out bool rotate);
            return rotate ? Swap(largest.growMeters) : largest.growMeters;
        }

        private const float FeetPerMeter = 3.280839895f;

        private static Vector2 Swap(Vector2 value) => new Vector2(value.y, value.x);

        private static float Area(Vector2 value) => value.x * value.y;
    }
}
#endif
