using System;
using System.Collections.Generic;
using System.IO;
using DreamPark;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Deterministic editor bake for AttractionTemplate's flexible packing poses.
/// Runtime only interpolates serialized positions; it never scans colliders or clusters.
/// </summary>
internal static class AttractionPackingBaker
{
    private const float FeetToMeters = 0.3048f;
    private const int SearchIterations = 18;
    internal static bool AutoBakeInProgress { get; set; }

    private sealed class Item
    {
        public PropTemplate template;
        public Transform transform;
        public Vector3 authored;
        public Vector3 authoredInAttraction;
        public Vector3 shrunk;
        public Vector3 grown;
        public Vector3 essentialShrunk;
        public Vector2 size;
        public Vector2 offset;
        public float yawRadians;
        public bool essential;
        public int group;
    }

    private struct Rect
    {
        public Vector2 center;
        public Vector2 half;
    }

    internal static bool Bake(AttractionTemplate attraction, bool recordUndo = true)
    {
        if (attraction == null) return false;
        if (recordUndo) Undo.RecordObject(attraction, "Bake attraction packing variants");

        List<Item> items = CollectItems(attraction);
        Vector2 authored = attraction.DimensionsInFeet * FeetToMeters;
        Vector2 safe = attraction.GetSafeFootprintMeters();

        if (items.Count == 0)
        {
            Vector2 emptyGrowthScale = new Vector2(
                Mathf.Max(1f, attraction.maxGrowthScale.x),
                Mathf.Max(1f, attraction.maxGrowthScale.y));
            attraction.SetPackingBake(new AttractionPackingBake(
                authored, safe, authored, Vector2.Scale(authored, emptyGrowthScale), authored,
                new List<AttractionPropPackingPose>()));
            EditorUtility.SetDirty(attraction);
            return true;
        }

        HashSet<long> authoredOverlaps = FindOverlaps(items, 1f, false, attraction.shrinkClearanceMeters);
        Vector3[] shrunkPositions = PackInwardPositions(
            items, authoredOverlaps, false, attraction.shrinkClearanceMeters);
        Vector3[] essentialShrunkPositions = PackInwardPositions(
            items, authoredOverlaps, true, attraction.shrinkClearanceMeters);

        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            item.shrunk = ToParentLocal(attraction.transform, item.transform.parent,
                shrunkPositions[i]);
            item.essentialShrunk = item.essential
                ? ToParentLocal(attraction.transform, item.transform.parent,
                    essentialShrunkPositions[i])
                : item.authored;
        }

        AssignGrowGroups(items, authored, attraction.growGroupGapMeters, attraction.growAlignmentToleranceMeters);
        Vector2 requestedGrowthScale = new Vector2(
            Mathf.Max(1f, attraction.maxGrowthScale.x),
            Mathf.Max(1f, attraction.maxGrowthScale.y));
        ApplyGroupedGrowth(attraction.transform, items, requestedGrowthScale);

        Vector2 shrink = Min(authored, MeasureSymmetricFootprint(attraction.transform, items, Pose.Shrunk, false, authored));
        // Growth is an authored reservation target, not merely the tight bounds of
        // the moved props. This makes the runtime contract exact: authored width *
        // X scale and authored length * Z scale are always the reserved dimensions.
        Vector2 grow = Vector2.Scale(authored, requestedGrowthScale);
        Vector2 essentialShrink = Min(authored, MeasureSymmetricFootprint(attraction.transform, items, Pose.EssentialShrunk, true, authored));

        var poses = new List<AttractionPropPackingPose>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            poses.Add(new AttractionPropPackingPose(
                item.transform,
                PathFrom(attraction.transform, item.transform),
                item.transform.name,
                item.template != null ? item.template.resourceName : "",
                item.essential,
                item.transform.gameObject.activeSelf,
                item.group,
                item.size,
                item.authored,
                item.shrunk,
                item.grown,
                item.essentialShrunk));
        }

        attraction.SetPackingBake(new AttractionPackingBake(authored, safe, shrink, grow, essentialShrink, poses));
        EditorUtility.SetDirty(attraction);
        PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
        return true;
    }

    /// <summary>
    /// Bake the prefab asset when the inspector is showing a scene instance. This
    /// keeps packing data in the distributable attraction rather than as a scene-only
    /// prefab override. Non-prefab objects still bake in place for test tooling.
    /// </summary>
    internal static bool BakeIntoAsset(AttractionTemplate attraction)
    {
        if (attraction == null) return false;
        string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(attraction.gameObject);
        if (string.IsNullOrEmpty(prefabPath)) return Bake(attraction);

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            AttractionTemplate prefabAttraction = root.GetComponent<AttractionTemplate>();
            if (prefabAttraction == null)
            {
                Debug.LogError($"[AttractionPacking] '{prefabPath}' has no AttractionTemplate on its root.");
                return false;
            }

            // The inspector target can contain authoring edits that have not yet
            // been written to the prefab asset (especially while in Prefab Mode).
            // Carry those values into the isolated prefab copy before baking so
            // the bake and its debug slider limits reflect what the user sees.
            prefabAttraction.maxGrowthScale = attraction.maxGrowthScale;
            if (!Bake(prefabAttraction, false)) return false;
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
        RevertPackingOverrides(attraction);
        Debug.Log($"[AttractionPacking] Baked packing data into '{prefabPath}'.");
        return true;
    }

    internal static int BakeAllInContent(string contentId, bool showProgress = false)
    {
        if (AutoBakeInProgress) return 0;
        string root = string.IsNullOrEmpty(contentId) ? "Assets/Content" : $"Assets/Content/{contentId}";
        if (!AssetDatabase.IsValidFolder(root)) return 0;

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { root });
        int baked = 0;
        AutoBakeInProgress = true;
        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AttractionTemplate attraction = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                if (attraction == null || attraction is DreamSequenceTemplate) continue;
                if (showProgress)
                    EditorUtility.DisplayProgressBar("Baking attraction packing", path,
                        guids.Length > 0 ? (float)i / guids.Length : 1f);
                if (Bake(attraction, false)) baked++;
            }
            if (baked > 0) AssetDatabase.SaveAssets();
        }
        finally
        {
            AutoBakeInProgress = false;
            if (showProgress) EditorUtility.ClearProgressBar();
        }
        return baked;
    }

    private static void RevertPackingOverrides(AttractionTemplate attraction)
    {
        if (!PrefabUtility.IsPartOfPrefabInstance(attraction)) return;
        var serialized = new SerializedObject(attraction);
        string[] fields =
        {
            "packingBake",
            "packingPreviewScale",
            "packingPreviewEssentialOnly",
            "showPackingGizmos"
        };
        for (int i = 0; i < fields.Length; i++)
        {
            SerializedProperty property = serialized.FindProperty(fields[i]);
            if (property != null && property.prefabOverride)
                PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
        }
        serialized.UpdateIfRequiredOrScript();
    }

    private static List<Item> CollectItems(AttractionTemplate attraction)
    {
        var essentialRoots = new HashSet<Transform>();
        if (attraction.essentialProps != null)
        {
            for (int i = 0; i < attraction.essentialProps.Count; i++)
            {
                Transform selected = attraction.essentialProps[i];
                if (selected == null || !selected.IsChildOf(attraction.transform)) continue;
                Transform root = ResolvePackingRoot(attraction.transform, selected);
                if (root != null) essentialRoots.Add(root);
            }
        }

        var result = new List<Item>();
        var claimedDirectChildren = new HashSet<Transform>();
        PropTemplate[] all = attraction.GetComponentsInChildren<PropTemplate>(true);
        for (int i = 0; i < all.Length; i++)
        {
            PropTemplate prop = all[i];
            if (prop == null || prop.transform == attraction.transform) continue;
            if (IsEntirePackingRootHidden(prop.transform)) continue;

            // Move only top-level nested props. A PropTemplate inside another prop moves
            // with its owner and recording both would apply the displacement twice.
            PropTemplate ancestor = prop.transform.parent != null
                ? prop.transform.parent.GetComponentInParent<PropTemplate>(true)
                : null;
            if (ancestor != null && ancestor.transform.IsChildOf(attraction.transform)) continue;

            Transform directChild = DirectChildOf(attraction.transform, prop.transform);
            if (directChild != null) claimedDirectChildren.Add(directChild);

            Vector2 size = prop.FootprintMeters;
            Vector3 relativeForward = attraction.transform.InverseTransformDirection(prop.transform.forward);

            result.Add(new Item
            {
                template = prop,
                transform = prop.transform,
                authored = prop.transform.localPosition,
                authoredInAttraction = attraction.transform.InverseTransformPoint(prop.transform.position),
                shrunk = prop.transform.localPosition,
                grown = prop.transform.localPosition,
                essentialShrunk = prop.transform.localPosition,
                size = size,
                offset = prop.footprintOffsetMeters,
                yawRadians = Mathf.Atan2(relativeForward.x, relativeForward.z),
                essential = essentialRoots.Contains(prop.transform),
                group = -1,
            });
        }

        // Older attractions often predate PropTemplate and contain ordinary child
        // hierarchies. Treat each top-level child with renderable/collider geometry as
        // one movable prop so legacy content gains a real shrink range after upgrade.
        for (int i = 0; i < attraction.transform.childCount; i++)
        {
            Transform child = attraction.transform.GetChild(i);
            if (claimedDirectChildren.Contains(child)) continue;
            if (IsEntirePackingRootHidden(child)) continue;
            if (!TryMeasureLocalFootprint(child, out Vector2 size, out Vector2 offset)) continue;

            Vector3 relativeForward = attraction.transform.InverseTransformDirection(child.forward);
            result.Add(new Item
            {
                template = null,
                transform = child,
                authored = child.localPosition,
                authoredInAttraction = attraction.transform.InverseTransformPoint(child.position),
                shrunk = child.localPosition,
                grown = child.localPosition,
                essentialShrunk = child.localPosition,
                size = size,
                offset = offset,
                yawRadians = Mathf.Atan2(relativeForward.x, relativeForward.z),
                essential = essentialRoots.Contains(child),
                group = -1,
            });
        }
        return result;
    }

    private static Transform ResolvePackingRoot(Transform attractionRoot, Transform selected)
    {
        PropTemplate owner = selected.GetComponentInParent<PropTemplate>(true);
        if (owner != null && owner.transform.IsChildOf(attractionRoot))
        {
            PropTemplate ancestor = owner;
            while (ancestor.transform.parent != null)
            {
                PropTemplate next = ancestor.transform.parent.GetComponentInParent<PropTemplate>(true);
                if (next == null || !next.transform.IsChildOf(attractionRoot)) break;
                ancestor = next;
            }
            return ancestor.transform;
        }
        return DirectChildOf(attractionRoot, selected);
    }

    private static Transform DirectChildOf(Transform root, Transform descendant)
    {
        if (root == null || descendant == null || !descendant.IsChildOf(root)) return null;
        Transform cursor = descendant;
        while (cursor.parent != null && cursor.parent != root) cursor = cursor.parent;
        return cursor.parent == root ? cursor : null;
    }

    private static bool TryMeasureLocalFootprint(Transform root, out Vector2 size, out Vector2 offset)
    {
        bool any = false;
        Vector3 minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        // Prefer authored collision: foliage, VFX and animated silhouettes can be far
        // wider than the actual floor space a legacy prop occupies. Renderer bounds
        // are only a fallback when the content has no usable collider footprint.
        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (IsHiddenPackingGeometry(colliders[i].transform, root)) continue;
            Bounds bounds = colliders[i].bounds;
            if (bounds.size.sqrMagnitude <= Mathf.Epsilon) continue;
            EncapsulateWorldBounds(root, bounds, ref minimum, ref maximum);
            any = true;
        }

        if (!any)
        {
            MeshFilter[] meshes = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshes.Length; i++)
            {
                MeshFilter filter = meshes[i];
                if (filter.sharedMesh == null) continue;
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer == null || IsHiddenPackingGeometry(filter.transform, root)) continue;
                EncapsulateBounds(root, filter.transform.localToWorldMatrix, filter.sharedMesh.bounds, ref minimum, ref maximum);
                any = true;
            }

            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinned[i];
                if (renderer.sharedMesh == null) continue;
                if (IsHiddenPackingGeometry(renderer.transform, root)) continue;
                EncapsulateBounds(root, renderer.transform.localToWorldMatrix, renderer.localBounds, ref minimum, ref maximum);
                any = true;
            }
        }

        if (!any)
        {
            size = Vector2.zero;
            offset = Vector2.zero;
            return false;
        }

        Vector3 localScale = root.localScale;
        size = new Vector2(
            (maximum.x - minimum.x) * Mathf.Abs(localScale.x),
            (maximum.z - minimum.z) * Mathf.Abs(localScale.z));
        Vector3 center = (minimum + maximum) * 0.5f;
        offset = new Vector2(center.x * localScale.x, center.z * localScale.z);
        return size.x > 0.001f && size.y > 0.001f;
    }

    private static bool IsEntirePackingRootHidden(Transform root)
    {
        if (NameSuggestsHiddenGeometry(root.name)) return true;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;
        for (int i = 0; i < renderers.Length; i++)
            if (!RendererUsesOnlyHiddenMaterials(renderers[i])) return false;
        return true;
    }

    private static bool IsHiddenPackingGeometry(Transform geometry, Transform packingRoot)
    {
        for (Transform current = geometry; current != null; current = current.parent)
        {
            if (NameSuggestsHiddenGeometry(current.name)) return true;
            Renderer renderer = current.GetComponent<Renderer>();
            if (renderer != null && RendererUsesOnlyHiddenMaterials(renderer)) return true;
            if (current == packingRoot) break;
        }
        return false;
    }

    private static bool RendererUsesOnlyHiddenMaterials(Renderer renderer)
    {
        Material[] materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0) return false;
        for (int i = 0; i < materials.Length; i++)
            if (!IsOccluderOrInvisibleMaterial(materials[i])) return false;
        return true;
    }

    private static bool IsOccluderOrInvisibleMaterial(Material material)
    {
        if (material == null) return false;
        string shaderName = material.shader != null ? material.shader.name : string.Empty;
        if (NameSuggestsHiddenGeometry(material.name + " " + shaderName)) return true;
        if (material.HasProperty("_BaseColor") && material.GetColor("_BaseColor").a <= 0.01f)
            return true;
        if (material.HasProperty("_Color") && material.GetColor("_Color").a <= 0.01f)
            return true;
        return false;
    }

    private static bool NameSuggestsHiddenGeometry(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string lower = value.ToLowerInvariant();
        return lower.Contains("occlud")
            || lower.Contains("invisible")
            || lower.Contains("depthmask")
            || lower.Contains("depth mask")
            || lower.Contains("gizmo");
    }

    private static void EncapsulateBounds(
        Transform root,
        Matrix4x4 localToWorld,
        Bounds bounds,
        ref Vector3 minimum,
        ref Vector3 maximum)
    {
        Vector3 extents = bounds.extents;
        Vector3 center = bounds.center;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 point = center + Vector3.Scale(extents, new Vector3(x, y, z));
            Vector3 local = root.InverseTransformPoint(localToWorld.MultiplyPoint3x4(point));
            minimum = Vector3.Min(minimum, local);
            maximum = Vector3.Max(maximum, local);
        }
    }

    private static void EncapsulateWorldBounds(
        Transform root,
        Bounds bounds,
        ref Vector3 minimum,
        ref Vector3 maximum)
    {
        Vector3 extents = bounds.extents;
        Vector3 center = bounds.center;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 world = center + Vector3.Scale(extents, new Vector3(x, y, z));
            Vector3 local = root.InverseTransformPoint(world);
            minimum = Vector3.Min(minimum, local);
            maximum = Vector3.Max(maximum, local);
        }
    }

    private static Vector3 ScaleFromOrigin(Vector3 position, Vector2 scale) =>
        new Vector3(position.x * scale.x, position.y, position.z * scale.y);

    private static Vector3[] PackInwardPositions(
        List<Item> items,
        HashSet<long> authoredOverlaps,
        bool essentialOnly,
        float clearance)
    {
        var positions = new Vector3[items.Count];
        int considered = 0;
        for (int i = 0; i < items.Count; i++)
        {
            positions[i] = items[i].authoredInAttraction;
            if (!essentialOnly || items[i].essential) considered++;
        }
        if (considered == 0) return positions;

        // Alternate axes so clearing space on one axis can unlock more movement on
        // the other. Each axis compactor merges only the pairs that actually touch;
        // the new cluster then continues inward while unrelated props keep moving.
        for (int pass = 0; pass < 3; pass++)
        {
            CompactAxis(items, positions, authoredOverlaps, essentialOnly, clearance, true);
            CompactAxis(items, positions, authoredOverlaps, essentialOnly, clearance, false);
        }
        return positions;
    }

    private static void CompactAxis(
        List<Item> items,
        Vector3[] positions,
        HashSet<long> authoredOverlaps,
        bool essentialOnly,
        float clearance,
        bool xAxis)
    {
        int[] groups = new int[items.Count];
        for (int i = 0; i < groups.Length; i++) groups[i] = i;

        for (int mergePass = 0; mergePass < items.Count; mergePass++)
        {
            Dictionary<int, float> translations = BuildAxisTranslations(
                items, positions, groups, essentialOnly, xAxis);
            if (translations.Count == 0) return;

            float safeTravel = FindSafeGroupTravel(
                items, positions, groups, translations, authoredOverlaps,
                essentialOnly, clearance, xAxis);
            ApplyGroupTravel(items, positions, groups, translations, essentialOnly, xAxis, safeTravel);
            if (safeTravel >= 0.9999f) return;

            Dictionary<int, float> nextTranslations = BuildAxisTranslations(
                items, positions, groups, essentialOnly, xAxis);
            if (!MergeBlockingGroups(
                    items, positions, groups, nextTranslations, authoredOverlaps,
                    essentialOnly, clearance, xAxis))
                return;
        }
    }

    private static Dictionary<int, float> BuildAxisTranslations(
        List<Item> items,
        Vector3[] positions,
        int[] groups,
        bool essentialOnly,
        bool xAxis)
    {
        var sums = new Dictionary<int, float>();
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < items.Count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            int group = Find(groups, i);
            float coordinate = xAxis ? positions[i].x : positions[i].z;
            sums[group] = sums.TryGetValue(group, out float sum) ? sum + coordinate : coordinate;
            counts[group] = counts.TryGetValue(group, out int count) ? count + 1 : 1;
        }

        var translations = new Dictionary<int, float>();
        foreach (KeyValuePair<int, float> pair in sums)
            translations[pair.Key] = -(pair.Value / counts[pair.Key]);
        return translations;
    }

    private static float FindSafeGroupTravel(
        List<Item> items,
        Vector3[] positions,
        int[] groups,
        Dictionary<int, float> translations,
        HashSet<long> authoredOverlaps,
        bool essentialOnly,
        float clearance,
        bool xAxis)
    {
        const int sweepSteps = 64;
        float previous = 0f;
        for (int step = 1; step <= sweepSteps; step++)
        {
            float candidate = (float)step / sweepSteps;
            if (!HasNewGroupedOverlap(
                    items, positions, groups, translations, authoredOverlaps,
                    essentialOnly, clearance, xAxis, candidate))
            {
                previous = candidate;
                continue;
            }

            float low = previous;
            float high = candidate;
            for (int iteration = 0; iteration < SearchIterations; iteration++)
            {
                float mid = (low + high) * 0.5f;
                if (HasNewGroupedOverlap(
                        items, positions, groups, translations, authoredOverlaps,
                        essentialOnly, clearance, xAxis, mid))
                    high = mid;
                else
                    low = mid;
            }
            return low;
        }
        return 1f;
    }

    private static bool MergeBlockingGroups(
        List<Item> items,
        Vector3[] positions,
        int[] groups,
        Dictionary<int, float> translations,
        HashSet<long> authoredOverlaps,
        bool essentialOnly,
        float clearance,
        bool xAxis)
    {
        const float probeTravel = 0.01f;
        var merges = new List<Vector2Int>();
        for (int i = 0; i < items.Count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            for (int j = i + 1; j < items.Count; j++)
            {
                if (essentialOnly && !items[j].essential) continue;
                if (authoredOverlaps.Contains(PairKey(i, j))) continue;
                int groupA = Find(groups, i);
                int groupB = Find(groups, j);
                if (groupA == groupB) continue;
                Rect a = BoundsAt(items[i], TravelPosition(
                    positions[i], translations[groupA], xAxis, probeTravel));
                Rect b = BoundsAt(items[j], TravelPosition(
                    positions[j], translations[groupB], xAxis, probeTravel));
                if (!Overlaps(a, b, clearance)) continue;
                merges.Add(new Vector2Int(groupA, groupB));
            }
        }

        // Resolve the whole collision frame before changing group membership.
        // This keeps every lookup tied to the translations that produced the
        // frame, even when several groups make contact simultaneously.
        for (int i = 0; i < merges.Count; i++)
            Union(groups, merges[i].x, merges[i].y);
        return merges.Count > 0;
    }

    private static bool HasNewGroupedOverlap(
        List<Item> items,
        Vector3[] positions,
        int[] groups,
        Dictionary<int, float> translations,
        HashSet<long> authoredOverlaps,
        bool essentialOnly,
        float clearance,
        bool xAxis,
        float travel)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            int groupA = Find(groups, i);
            Rect a = BoundsAt(items[i], TravelPosition(
                positions[i], translations[groupA], xAxis, travel));
            for (int j = i + 1; j < items.Count; j++)
            {
                if (essentialOnly && !items[j].essential) continue;
                if (authoredOverlaps.Contains(PairKey(i, j))) continue;
                int groupB = Find(groups, j);
                if (groupA == groupB) continue;
                Rect b = BoundsAt(items[j], TravelPosition(
                    positions[j], translations[groupB], xAxis, travel));
                if (Overlaps(a, b, clearance)) return true;
            }
        }
        return false;
    }

    private static void ApplyGroupTravel(
        List<Item> items,
        Vector3[] positions,
        int[] groups,
        Dictionary<int, float> translations,
        bool essentialOnly,
        bool xAxis,
        float travel)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            int group = Find(groups, i);
            positions[i] = TravelPosition(positions[i], translations[group], xAxis, travel);
        }
    }

    private static Vector3 TravelPosition(Vector3 position, float translation, bool xAxis, float travel)
    {
        if (xAxis) position.x += translation * travel;
        else position.z += translation * travel;
        return position;
    }

    private static Vector2 FindMinimumScale(List<Item> items, HashSet<long> authoredOverlaps, bool essentialOnly, float clearance)
    {
        int considered = 0;
        for (int i = 0; i < items.Count; i++) if (!essentialOnly || items[i].essential) considered++;
        if (considered <= 1) return Vector2.zero;

        // Find two safe axis-independent endpoints. Trying both axis orders avoids
        // always giving X or Z priority when a diagonal layout creates a tradeoff;
        // choose the valid pair with the smaller rectangular area.
        Vector2 xFirst = Vector2.one;
        xFirst.x = FindMinimumAxisScale(items, authoredOverlaps, essentialOnly, clearance, xFirst, true);
        xFirst.y = FindMinimumAxisScale(items, authoredOverlaps, essentialOnly, clearance, xFirst, false);
        xFirst.x = FindMinimumAxisScale(items, authoredOverlaps, essentialOnly, clearance, xFirst, true);

        Vector2 zFirst = Vector2.one;
        zFirst.y = FindMinimumAxisScale(items, authoredOverlaps, essentialOnly, clearance, zFirst, false);
        zFirst.x = FindMinimumAxisScale(items, authoredOverlaps, essentialOnly, clearance, zFirst, true);
        zFirst.y = FindMinimumAxisScale(items, authoredOverlaps, essentialOnly, clearance, zFirst, false);

        return xFirst.x * xFirst.y <= zFirst.x * zFirst.y ? xFirst : zFirst;
    }

    private static float FindMinimumAxisScale(
        List<Item> items,
        HashSet<long> authoredOverlaps,
        bool essentialOnly,
        float clearance,
        Vector2 fixedScale,
        bool xAxis)
    {
        float low = 0f;
        float high = 1f;
        for (int iteration = 0; iteration < SearchIterations; iteration++)
        {
            float mid = (low + high) * 0.5f;
            Vector2 candidate = fixedScale;
            if (xAxis) candidate.x = mid;
            else candidate.y = mid;
            if (HasNewOverlap(items, candidate, essentialOnly, clearance, authoredOverlaps)) low = mid;
            else high = mid;
        }
        return high;
    }

    private static HashSet<long> FindOverlaps(List<Item> items, float scale, bool essentialOnly, float clearance)
    {
        Vector2 scaleXZ = new Vector2(scale, scale);
        var overlaps = new HashSet<long>();
        for (int i = 0; i < items.Count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            Rect a = BoundsAt(items[i], ScaleFromOrigin(items[i].authoredInAttraction, scaleXZ));
            for (int j = i + 1; j < items.Count; j++)
            {
                if (essentialOnly && !items[j].essential) continue;
                Rect b = BoundsAt(items[j], ScaleFromOrigin(items[j].authoredInAttraction, scaleXZ));
                if (Overlaps(a, b, clearance)) overlaps.Add(PairKey(i, j));
            }
        }
        return overlaps;
    }

    private static bool HasNewOverlap(List<Item> items, Vector2 scale, bool essentialOnly, float clearance, HashSet<long> ignored)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            Rect a = BoundsAt(items[i], ScaleFromOrigin(items[i].authoredInAttraction, scale));
            for (int j = i + 1; j < items.Count; j++)
            {
                if (essentialOnly && !items[j].essential) continue;
                if (ignored.Contains(PairKey(i, j))) continue;
                Rect b = BoundsAt(items[j], ScaleFromOrigin(items[j].authoredInAttraction, scale));
                if (Overlaps(a, b, clearance)) return true;
            }
        }
        return false;
    }

    private static void AssignGrowGroups(
        List<Item> items,
        Vector2 authoredFootprint,
        float gap,
        float alignmentTolerance)
    {
        int[] parent = new int[items.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        for (int i = 0; i < items.Count; i++)
        {
            Rect a = BoundsAt(items[i], items[i].authoredInAttraction);
            for (int j = i + 1; j < items.Count; j++)
            {
                Rect b = BoundsAt(items[j], items[j].authoredInAttraction);
                if (IsOversizedForGrouping(items[i], authoredFootprint)
                    || IsOversizedForGrouping(items[j], authoredFootprint))
                    continue;

                float signedGapX = Mathf.Abs(a.center.x - b.center.x) - a.half.x - b.half.x;
                float signedGapZ = Mathf.Abs(a.center.y - b.center.y) - a.half.y - b.half.y;
                bool orderlyAlongX = Mathf.Abs(a.center.y - b.center.y) <= alignmentTolerance
                    && signedGapX >= -alignmentTolerance
                    && signedGapX <= gap * 2f;
                bool orderlyAlongZ = Mathf.Abs(a.center.x - b.center.x) <= alignmentTolerance
                    && signedGapZ >= -alignmentTolerance
                    && signedGapZ <= gap * 2f;
                bool samePropCluster = SamePropFamily(items[i].transform.name, items[j].transform.name)
                    && signedGapX <= gap
                    && signedGapZ <= gap;
                if (orderlyAlongX || orderlyAlongZ || samePropCluster) Union(parent, i, j);
            }
        }

        var ids = new Dictionary<int, int>();
        for (int i = 0; i < items.Count; i++)
        {
            int root = Find(parent, i);
            if (!ids.TryGetValue(root, out int id))
            {
                id = ids.Count;
                ids[root] = id;
            }
            items[i].group = id;
        }
    }

    private static bool IsOversizedForGrouping(Item item, Vector2 authoredFootprint)
    {
        return item.size.x > authoredFootprint.x + 0.001f
            || item.size.y > authoredFootprint.y + 0.001f;
    }

    private static bool SamePropFamily(string first, string second)
    {
        return string.Equals(GrowthFamilyName(first), GrowthFamilyName(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string GrowthFamilyName(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string result = value.Trim();
        int copySuffix = result.LastIndexOf(" (", StringComparison.Ordinal);
        if (copySuffix > 0 && result.EndsWith(")", StringComparison.Ordinal))
            result = result.Substring(0, copySuffix);
        const string variantSuffix = " Variant";
        if (result.EndsWith(variantSuffix, StringComparison.OrdinalIgnoreCase))
            result = result.Substring(0, result.Length - variantSuffix.Length);
        return result;
    }

    private static void ApplyGroupedGrowth(Transform attractionRoot, List<Item> items, Vector2 scale)
    {
        var sums = new Dictionary<int, Vector2>();
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < items.Count; i++)
        {
            int group = items[i].group;
            Vector2 p = new Vector2(items[i].authoredInAttraction.x, items[i].authoredInAttraction.z);
            sums[group] = sums.TryGetValue(group, out Vector2 sum) ? sum + p : p;
            counts[group] = counts.TryGetValue(group, out int count) ? count + 1 : 1;
        }

        var deltas = new Dictionary<int, Vector2>();
        foreach (var pair in sums)
        {
            Vector2 centroid = pair.Value / counts[pair.Key];
            deltas[pair.Key] = new Vector2(
                centroid.x * (scale.x - 1f),
                centroid.y * (scale.y - 1f));
        }

        for (int i = 0; i < items.Count; i++)
        {
            Vector2 delta = deltas[items[i].group];
            Vector3 p = items[i].authoredInAttraction;
            Vector3 grownInAttraction = new Vector3(p.x + delta.x, p.y, p.z + delta.y);
            items[i].grown = ToParentLocal(attractionRoot, items[i].transform.parent, grownInAttraction);
        }
    }

    private enum Pose { Shrunk, Grown, EssentialShrunk }

    private static Vector2 MeasureSymmetricFootprint(Transform attractionRoot, List<Item> items, Pose pose, bool essentialOnly, Vector2 fallback)
    {
        float extentX = 0f;
        float extentZ = 0f;
        bool anyX = false;
        bool anyZ = false;
        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            if (essentialOnly && !item.essential) continue;
            Vector3 parentLocal = pose == Pose.Shrunk ? item.shrunk
                : pose == Pose.Grown ? item.grown
                : item.essentialShrunk;
            Vector3 position = ToAttractionLocal(attractionRoot, item.transform.parent, parentLocal);
            Rect rect = BoundsAt(item, position);
            Rect authoredRect = BoundsAt(item, item.authoredInAttraction);
            // A legacy helper that already overflows an authored axis at scale 1
            // cannot meaningfully define that axis's shrink floor. Preserve the
            // existing overflow while allowing normal in-bounds layout props to pack.
            if (rect.half.x * 2f <= fallback.x + 0.001f
                && Mathf.Abs(authoredRect.center.x) + authoredRect.half.x <= fallback.x * 0.5f + 0.001f)
            {
                extentX = Mathf.Max(extentX, Mathf.Abs(rect.center.x) + rect.half.x);
                anyX = true;
            }
            if (rect.half.y * 2f <= fallback.y + 0.001f
                && Mathf.Abs(authoredRect.center.y) + authoredRect.half.y <= fallback.y * 0.5f + 0.001f)
            {
                extentZ = Mathf.Max(extentZ, Mathf.Abs(rect.center.y) + rect.half.y);
                anyZ = true;
            }
        }
        return new Vector2(anyX ? extentX * 2f : fallback.x, anyZ ? extentZ * 2f : fallback.y);
    }

    private static Rect BoundsAt(Item item, Vector3 localPosition)
    {
        float yaw = item.yawRadians;
        float c = Mathf.Abs(Mathf.Cos(yaw));
        float s = Mathf.Abs(Mathf.Sin(yaw));
        Vector2 halfLocal = item.size * 0.5f;
        Vector2 half = new Vector2(c * halfLocal.x + s * halfLocal.y, s * halfLocal.x + c * halfLocal.y);

        Vector2 rotatedOffset = new Vector2(
            Mathf.Cos(yaw) * item.offset.x - Mathf.Sin(yaw) * item.offset.y,
            Mathf.Sin(yaw) * item.offset.x + Mathf.Cos(yaw) * item.offset.y);
        return new Rect
        {
            center = new Vector2(localPosition.x, localPosition.z) + rotatedOffset,
            half = half,
        };
    }

    private static Vector3 ToParentLocal(Transform attractionRoot, Transform parent, Vector3 positionInAttraction)
    {
        Vector3 world = attractionRoot.TransformPoint(positionInAttraction);
        return parent != null ? parent.InverseTransformPoint(world) : world;
    }

    private static Vector3 ToAttractionLocal(Transform attractionRoot, Transform parent, Vector3 parentLocalPosition)
    {
        Vector3 world = parent != null ? parent.TransformPoint(parentLocalPosition) : parentLocalPosition;
        return attractionRoot.InverseTransformPoint(world);
    }

    private static bool Overlaps(Rect a, Rect b, float clearance)
    {
        float pad = Mathf.Max(0f, clearance) * 0.5f;
        return Mathf.Abs(a.center.x - b.center.x) < a.half.x + b.half.x + pad * 2f
            && Mathf.Abs(a.center.y - b.center.y) < a.half.y + b.half.y + pad * 2f;
    }

    private static long PairKey(int a, int b) => ((long)a << 32) | (uint)b;

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB) parent[rootB] = rootA;
    }

    private static Vector2 Min(Vector2 a, Vector2 b) =>
        new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));

    private static string PathFrom(Transform root, Transform child)
    {
        if (root == child) return "";
        var names = new List<string>();
        Transform cursor = child;
        while (cursor != null && cursor != root)
        {
            names.Add(cursor.name);
            cursor = cursor.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    [MenuItem("DreamPark/Troubleshooting/Bake Attraction Packing Variants")]
    private static void BakeAll()
    {
        UpgradeLegacyAttractionPrefabs(false);
        int baked = BakeAllInContent(null, showProgress: true);
        Debug.Log($"[AttractionPacking] Baked {baked} attraction prefab(s).");
    }

    [MenuItem("DreamPark/Troubleshooting/Bake Selected Attraction Packing Variant")]
    private static void BakeSelected()
    {
        AttractionTemplate attraction = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<AttractionTemplate>()
            : null;
        if (attraction == null) return;
        BakeIntoAsset(attraction);
        AssetDatabase.SaveAssets();
        SceneView.RepaintAll();
    }

    [MenuItem("DreamPark/Troubleshooting/Bake Selected Attraction Packing Variant", true)]
    private static bool CanBakeSelected() =>
        Selection.activeGameObject != null
        && Selection.activeGameObject.GetComponentInParent<AttractionTemplate>() != null;

    [MenuItem("DreamPark/Troubleshooting/Upgrade Legacy Attraction Templates")]
    private static void UpgradeLegacyAttractionTemplatesMenu()
    {
        int upgraded = UpgradeLegacyAttractionPrefabs(true);
        if (upgraded == 0)
            Debug.Log("[AttractionPacking] No legacy A_ attraction templates needed upgrading.");
    }

    /// <summary>
    /// Existing content may still use LevelTemplate directly even though A_ prefabs
    /// are attractions. Swap only the MonoScript GUID in the prefab YAML so the
    /// component fileID and every reference to it remain intact; all inherited
    /// LevelTemplate fields keep their serialized values.
    /// </summary>
    internal static int UpgradeLegacyAttractionPrefabs(bool logEach)
    {
        MonoScript levelScript = FindScript(typeof(LevelTemplate));
        MonoScript attractionScript = FindScript(typeof(AttractionTemplate));
        if (levelScript == null || attractionScript == null)
        {
            Debug.LogError("[AttractionPacking] Could not locate LevelTemplate/AttractionTemplate scripts for migration.");
            return 0;
        }

        string levelGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(levelScript));
        string attractionGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(attractionScript));
        string needle = $"m_Script: {{fileID: 11500000, guid: {levelGuid}, type: 3}}";
        string replacement = $"m_Script: {{fileID: 11500000, guid: {attractionGuid}, type: 3}}";
        var changed = new List<string>();

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Content" });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (!Path.GetFileNameWithoutExtension(path).StartsWith("A_", StringComparison.Ordinal)) continue;

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            LevelTemplate level = prefab != null ? prefab.GetComponent<LevelTemplate>() : null;
            if (level == null || level.GetType() != typeof(LevelTemplate)) continue;

            string yaml = File.ReadAllText(path);
            int first = yaml.IndexOf(needle, StringComparison.Ordinal);
            if (first < 0) continue;
            if (yaml.IndexOf(needle, first + needle.Length, StringComparison.Ordinal) >= 0)
            {
                Debug.LogWarning($"[AttractionPacking] Skipped '{path}': it contains multiple LevelTemplate script references.");
                continue;
            }

            yaml = yaml.Substring(0, first) + replacement + yaml.Substring(first + needle.Length);
            File.WriteAllText(path, yaml);
            changed.Add(path);
            if (logEach) Debug.Log($"[AttractionPacking] Upgraded '{path}' to AttractionTemplate.");
        }

        for (int i = 0; i < changed.Count; i++)
            AssetDatabase.ImportAsset(changed[i], ImportAssetOptions.ForceUpdate);
        if (changed.Count > 0) AssetDatabase.SaveAssets();
        return changed.Count;
    }

    private static MonoScript FindScript(Type type)
    {
        string[] candidates = AssetDatabase.FindAssets(type.Name + " t:MonoScript", new[] { "Assets/DreamPark" });
        for (int i = 0; i < candidates.Length; i++)
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(candidates[i]));
            if (script != null && script.GetClass() == type) return script;
        }
        return null;
    }
}

/// <summary>
/// Refreshes serialized packing data in the same prefab save operation that
/// captured the author's edits. It never calls SaveAssets, so it cannot create
/// a recursive save loop; the guard also protects bulk baker-triggered saves.
/// </summary>
internal sealed class AttractionPackingPrefabSaveProcessor : AssetModificationProcessor
{
    private static string[] OnWillSaveAssets(string[] paths)
    {
        if (AttractionPackingBaker.AutoBakeInProgress) return paths;

        AttractionPackingBaker.AutoBakeInProgress = true;
        try
        {
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)
                    || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    || !path.StartsWith("Assets/Content/", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    AttractionTemplate attraction = null;
                    var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
                    if (stage != null && stage.prefabContentsRoot != null
                        && string.Equals(stage.assetPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        attraction = stage.prefabContentsRoot.GetComponent<AttractionTemplate>();
                    }
                    else
                    {
                        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        attraction = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                    }

                    if (attraction == null || attraction is DreamSequenceTemplate) continue;
                    AttractionPackingBaker.Bake(attraction, false);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AttractionPacking] Auto-bake failed for '{path}': {e.Message}");
                }
            }
        }
        finally
        {
            AttractionPackingBaker.AutoBakeInProgress = false;
        }
        return paths;
    }
}

[CustomEditor(typeof(AttractionTemplate))]
internal sealed class AttractionTemplatePackingEditor : Editor
{
    private static Material packingGhostMaterial;

    private void OnDisable()
    {
        if (packingGhostMaterial == null) return;
        DestroyImmediate(packingGhostMaterial);
        packingGhostMaterial = null;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var attraction = (AttractionTemplate)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Flexible Packing Bake", EditorStyles.boldLabel);

        Vector2 maxGrowth = attraction.maxGrowthScale;
        EditorGUI.BeginChangeCheck();
        maxGrowth.x = Mathf.Max(1f, EditorGUILayout.FloatField(
            new GUIContent("Baked Max Multiplier X", "Maximum local-X/width multiplier to bake. 1.0 is the current authored layout; 1.25 means 25% wider."),
            maxGrowth.x));
        maxGrowth.y = Mathf.Max(1f, EditorGUILayout.FloatField(
            new GUIContent("Baked Max Multiplier Z", "Maximum local-Z/length multiplier to bake. 1.0 is the current authored layout; 1.25 means 25% longer."),
            maxGrowth.y));
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(attraction, "Change attraction maximum growth");
            attraction.maxGrowthScale = maxGrowth;
            EditorUtility.SetDirty(attraction);
            PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
        }

        bool showGizmos = EditorGUILayout.Toggle("Show Debug Packing Gizmos", attraction.ShowPackingGizmos);
        if (showGizmos != attraction.ShowPackingGizmos)
        {
            Undo.RecordObject(attraction, "Toggle attraction packing gizmos");
            attraction.SetShowPackingGizmos(showGizmos);
            EditorUtility.SetDirty(attraction);
            SceneView.RepaintAll();
        }

        if (attraction.HasPackingBake)
        {
            AttractionPackingBake bake = attraction.PackingBake;
            EditorGUILayout.HelpBox(
                $"Original: {FormatFeet(bake.AuthoredFootprintMeters)}\n" +
                $"Minimum Footprint: {FormatFeet(bake.ShrinkFootprintMeters)} " +
                $"{FormatPercentDifference(bake.ShrinkFootprintMeters, bake.AuthoredFootprintMeters)}\n" +
                $"Maximum Footprint: {FormatFeet(bake.GrowFootprintMeters)} " +
                $"{FormatPercentDifference(bake.GrowFootprintMeters, bake.AuthoredFootprintMeters)}",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Editor Debug Preview", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                Application.isPlaying
                    ? "Editor-only Play Mode override: these controls apply the baked layout to the live child props. " +
                      "The changes are discarded when Play Mode ends. 1.000 is exactly as authored."
                    : "Debug visualization only in Edit Mode; authored child transforms and Attraction Size are unchanged. " +
                      "Enter Play Mode to exercise this layout on the live props. 1.000 is exactly as authored.",
                MessageType.None);

            Vector2 minimum = bake.ShrinkScale;
            Vector2 maximum = bake.GrowScale;
            Vector2 preview = attraction.PackingPreviewScale;
            preview.x = Mathf.Clamp(preview.x, minimum.x, maximum.x);
            preview.y = Mathf.Clamp(preview.y, minimum.y, maximum.y);

            EditorGUI.BeginChangeCheck();
            preview.x = EditorGUILayout.Slider(
                new GUIContent("Debug Width Multiplier (X)", "Direct multiplier of the current authored width. 1.0 is exactly as authored."),
                preview.x, minimum.x, maximum.x);
            preview.y = EditorGUILayout.Slider(
                new GUIContent("Debug Length Multiplier (Z)", "Direct multiplier of the current authored length. 1.0 is exactly as authored."),
                preview.y, minimum.y, maximum.y);
            bool previewChanged = EditorGUI.EndChangeCheck();

            if (previewChanged || preview != attraction.PackingPreviewScale)
            {
                Undo.RecordObject(attraction, "Preview attraction packing scale");
                attraction.SetPackingPreview(preview, false);
                EditorUtility.SetDirty(attraction);
                SceneView.RepaintAll();
            }

            Vector2 currentFootprint = attraction.GetPackingFootprintMeters(preview, false);
            EditorGUILayout.LabelField(
                "Debug Footprint Preview",
                FormatFeet(currentFootprint));
        }
        else
        {
            EditorGUILayout.HelpBox("No flexible packing data has been baked. Runtime falls back to the authored rectangle.", MessageType.Warning);
        }

        if (GUILayout.Button("Bake Packing Variants"))
        {
            AttractionPackingBaker.BakeIntoAsset(attraction);
            attraction.ResetPackingVariant();
            AssetDatabase.SaveAssets();
            SceneView.RepaintAll();
        }
        if (GUILayout.Button("Reset Debug Preview to 1.0 (Authored)"))
        {
            Undo.RecordObject(attraction, "Reset attraction packing preview");
            attraction.ResetPackingVariant();
            EditorUtility.SetDirty(attraction);
            SceneView.RepaintAll();
        }
    }

    private void OnSceneGUI()
    {
        var attraction = (AttractionTemplate)target;
        if (attraction == null || !attraction.ShowPackingGizmos) return;

        Vector2 authored = attraction.HasPackingBake
            ? attraction.PackingBake.AuthoredFootprintMeters
            : attraction.DimensionsInFeet * 0.3048f;
        Vector2 safe = attraction.GetSafeFootprintMeters();

        Matrix4x4 previous = Handles.matrix;
        Color previousColor = Handles.color;
        Handles.matrix = attraction.transform.localToWorldMatrix;

        DrawFootprint(authored, 0.01f, new Color(1f, 1f, 1f, 0.55f), "Authored / 1.0");
        if (safe.x > 0f && safe.y > 0f)
            DrawFootprint(safe, 0.03f, new Color(0.1f, 0.9f, 1f, 0.9f), "Safe");

        if (attraction.HasPackingBake)
        {
            AttractionPackingBake bake = attraction.PackingBake;
            DrawFootprint(bake.ShrinkFootprintMeters, 0.05f, new Color(0.2f, 0.55f, 1f, 0.95f), "Minimum shrink");
            DrawFootprint(bake.GrowFootprintMeters, 0.07f, new Color(0.25f, 1f, 0.35f, 0.9f), "Baked maximum");
            if (bake.HasEssentialProps)
                DrawFootprint(bake.EssentialShrinkFootprintMeters, 0.09f, new Color(1f, 0.2f, 0.85f, 0.95f), "Essential minimum");

            Vector2 current = attraction.GetPackingFootprintMeters(
                attraction.PackingPreviewScale,
                false);
            DrawFootprint(current, 0.12f, new Color(1f, 0.75f, 0.05f, 1f), "Debug preview");
        }

        Handles.matrix = previous;
        DrawPackingGhosts(attraction);
        DrawEssentialProps(attraction);
        Handles.color = previousColor;
    }

    private static void DrawPackingGhosts(AttractionTemplate attraction)
    {
        if (!attraction.HasPackingBake || Event.current.type != EventType.Repaint) return;
        Material material = GetPackingGhostMaterial();
        if (material == null) return;

        const bool essentialOnly = false;
        IReadOnlyList<AttractionPropPackingPose> poses = attraction.PackingBake.Props;
        for (int i = 0; i < poses.Count; i++)
        {
            AttractionPropPackingPose pose = poses[i];
            if (pose == null || pose.Prop == null || !pose.AuthoredActive) continue;
            if (essentialOnly && !pose.Essential) continue;

            Transform root = pose.Prop;
            Vector3 previewLocalPosition = attraction.GetPackingPoseLocalPosition(
                pose,
                attraction.PackingPreviewScale,
                essentialOnly);
            Matrix4x4 targetRoot = root.parent != null
                ? root.parent.localToWorldMatrix * Matrix4x4.TRS(previewLocalPosition, root.localRotation, root.localScale)
                : Matrix4x4.TRS(previewLocalPosition, root.rotation, root.lossyScale);
            Matrix4x4 authoredToPreview = targetRoot * root.worldToLocalMatrix;

            Color color = pose.Essential
                ? new Color(1f, 0.2f, 0.85f, 0.24f)
                : new Color(1f, 0.72f, 0.05f, 0.2f);
            material.SetColor("_Color", color);
            material.SetPass(0);

            bool foundMesh = false;
            bool drewMesh = false;
            MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int meshIndex = 0; meshIndex < meshFilters.Length; meshIndex++)
            {
                MeshFilter filter = meshFilters[meshIndex];
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                foundMesh = true;
                Renderer renderer = filter.GetComponent<Renderer>();
                if (!ShouldDrawGhostRenderer(renderer, root)) continue;
                Matrix4x4 matrix = authoredToPreview * filter.transform.localToWorldMatrix;
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (!ShouldDrawGhostSubMesh(renderer, subMesh)) continue;
                    Graphics.DrawMeshNow(mesh, matrix, subMesh);
                    drewMesh = true;
                }
            }

            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int meshIndex = 0; meshIndex < skinned.Length; meshIndex++)
            {
                SkinnedMeshRenderer renderer = skinned[meshIndex];
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                foundMesh = true;
                if (!ShouldDrawGhostRenderer(renderer, root)) continue;
                Matrix4x4 matrix = authoredToPreview * renderer.transform.localToWorldMatrix;
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (!ShouldDrawGhostSubMesh(renderer, subMesh)) continue;
                    Graphics.DrawMeshNow(mesh, matrix, subMesh);
                    drewMesh = true;
                }
            }

            if (!foundMesh && !drewMesh)
            {
                Matrix4x4 previous = Handles.matrix;
                Handles.matrix = targetRoot;
                Handles.color = new Color(color.r, color.g, color.b, 0.8f);
                Handles.DrawWireCube(Vector3.zero, new Vector3(
                    pose.FootprintMeters.x,
                    0.05f,
                    pose.FootprintMeters.y));
                Handles.matrix = previous;
            }
        }
    }

    private static bool ShouldDrawGhostRenderer(Renderer renderer, Transform propRoot)
    {
        if (renderer == null || !renderer.enabled || renderer.forceRenderingOff) return false;
        if (!renderer.gameObject.activeInHierarchy) return false;
        if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return false;

        for (Transform current = renderer.transform; current != null; current = current.parent)
        {
            if (NameSuggestsHiddenGeometry(current.name)) return false;
            if (current == propRoot) break;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0) return true;
        for (int i = 0; i < materials.Length; i++)
            if (!IsOccluderOrInvisibleMaterial(materials[i])) return true;
        return false;
    }

    private static bool ShouldDrawGhostSubMesh(Renderer renderer, int subMesh)
    {
        Material[] materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0) return true;
        Material material = materials[Mathf.Min(subMesh, materials.Length - 1)];
        return !IsOccluderOrInvisibleMaterial(material);
    }

    private static bool IsOccluderOrInvisibleMaterial(Material material)
    {
        if (material == null) return false;
        string shaderName = material.shader != null ? material.shader.name : string.Empty;
        if (NameSuggestsHiddenGeometry(material.name + " " + shaderName)) return true;

        if (material.HasProperty("_BaseColor") && material.GetColor("_BaseColor").a <= 0.01f)
            return true;
        if (material.HasProperty("_Color") && material.GetColor("_Color").a <= 0.01f)
            return true;
        return false;
    }

    private static bool NameSuggestsHiddenGeometry(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string lower = value.ToLowerInvariant();
        return lower.Contains("occlud")
            || lower.Contains("invisible")
            || lower.Contains("depthmask")
            || lower.Contains("depth mask")
            || lower.Contains("gizmo");
    }

    private static Material GetPackingGhostMaterial()
    {
        if (packingGhostMaterial != null) return packingGhostMaterial;
        Shader shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null) return null;
        packingGhostMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        packingGhostMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        packingGhostMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        packingGhostMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        packingGhostMaterial.SetInt("_ZWrite", 0);
        return packingGhostMaterial;
    }

    private static void DrawFootprint(Vector2 footprint, float height, Color color, string label)
    {
        if (footprint.x <= 0f || footprint.y <= 0f) return;
        Handles.color = color;
        Handles.DrawWireCube(
            new Vector3(0f, height, 0f),
            new Vector3(footprint.x, 0.015f, footprint.y));
        Handles.Label(
            new Vector3(footprint.x * 0.5f, height, footprint.y * 0.5f),
            $"{label}  {footprint.x:0.##} × {footprint.y:0.##}m");
    }

    private static void DrawEssentialProps(AttractionTemplate attraction)
    {
        if (attraction.essentialProps == null) return;
        Handles.color = new Color(1f, 0.2f, 0.85f, 1f);
        for (int i = 0; i < attraction.essentialProps.Count; i++)
        {
            Transform selected = attraction.essentialProps[i];
            if (selected == null) continue;
            PropTemplate prop = selected.GetComponentInParent<PropTemplate>(true);
            Transform root = prop != null ? prop.transform : selected;
            Vector2 size = prop != null ? prop.FootprintMeters : Vector2.one * 0.25f;
            Matrix4x4 previous = Handles.matrix;
            Handles.matrix = root.localToWorldMatrix;
            Handles.DrawWireCube(Vector3.up * 0.16f, new Vector3(size.x, 0.04f, size.y));
            Handles.matrix = previous;
            Handles.Label(root.position + Vector3.up * 0.2f, "Essential: " + root.name);
        }
    }

    private static string FormatFeet(Vector2 meters)
    {
        const float metersPerFoot = 0.3048f;
        return $"{meters.x / metersPerFoot:0.##} ft × {meters.y / metersPerFoot:0.##} ft";
    }

    private static string FormatPercentDifference(Vector2 footprint, Vector2 authored)
    {
        float x = authored.x > 0f ? (footprint.x / authored.x - 1f) * 100f : 0f;
        float z = authored.y > 0f ? (footprint.y / authored.y - 1f) * 100f : 0f;
        if (Mathf.Abs(x - z) <= 0.5f)
            return $"({FormatSignedPercent((x + z) * 0.5f)})";
        return $"({FormatSignedPercent(x)} X, {FormatSignedPercent(z)} Z)";
    }

    private static string FormatSignedPercent(float value)
    {
        if (Mathf.Abs(value) < 0.05f) return "0%";
        return value > 0f ? $"+{value:0.#}%" : $"{value:0.#}%";
    }
}
