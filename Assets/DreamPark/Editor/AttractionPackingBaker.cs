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
        public List<Vector2> footprintPolygon;
        public bool essential;
        public int group;

        // size, offset and yawRadians never change after an Item is built, so the
        // rotated half-extents and offset are computed once instead of on every
        // pair test (six sin/cos per BoundsAt call made the packing search spend
        // most of an hour in trig on Zombiez, Oct 10 2026). Same expressions as
        // before, so results are bit-identical.
        private bool geometryCached;
        private Vector2 cachedHalf;
        private Vector2 cachedRotatedOffset;

        public Vector2 Half
        {
            get { EnsureGeometry(); return cachedHalf; }
        }

        public Vector2 RotatedOffset
        {
            get { EnsureGeometry(); return cachedRotatedOffset; }
        }

        private void EnsureGeometry()
        {
            if (geometryCached) return;
            float yaw = yawRadians;
            float c = Mathf.Abs(Mathf.Cos(yaw));
            float s = Mathf.Abs(Mathf.Sin(yaw));
            Vector2 halfLocal = size * 0.5f;
            cachedHalf = new Vector2(c * halfLocal.x + s * halfLocal.y, s * halfLocal.x + c * halfLocal.y);
            cachedRotatedOffset = new Vector2(
                Mathf.Cos(yaw) * offset.x - Mathf.Sin(yaw) * offset.y,
                Mathf.Sin(yaw) * offset.x + Mathf.Cos(yaw) * offset.y);
            geometryCached = true;
        }
    }

    private sealed class FollowerItem
    {
        public AttractionPackingFollower component;
        public Transform transform;
        public Vector3 authoredPosition;
        public Vector3 authoredLocalScale;
        public Vector2 size;
        public Vector2 offset;
        public float yawRadians;
    }

    private struct Rect
    {
        public Vector2 center;
        public Vector2 half;
    }

    // Collision rule for the compactor: do items i and j (at rects a and b) block
    // each other? Null everywhere means the original rule, ItemsOverlap.
    private delegate bool PairOverlapRule(int i, Rect a, int j, Rect b);

    // Test seam: invoked inside a frame bake after the frame's overrides are applied.
    internal static Action<AttractionTemplate, AttractionLayoutFrame> FrameBakeHookForTests;

    internal static bool Bake(AttractionTemplate attraction, bool recordUndo = true)
    {
        if (attraction == null) return false;
        if (recordUndo) Undo.RecordObject(attraction, "Bake attraction packing variants");

        // Collider.bounds can lag behind freshly edited prefab transforms. The
        // follower's visible footprint must be measured at its authored scale.
        Physics.SyncTransforms();

        // essentialProps predates layout frames: it becomes the Minimum frame once.
        MigrateLegacyEssentialProps(attraction);

        Vector2 authored = attraction.DimensionsInFeet * FeetToMeters;
        if (!attraction.HasLayoutFrames)
        {
            // Frame-less attractions: exactly the original algorithm and results.
            // Once migrated, essentialProps belong to the Minimum frame; if the creator
            // later deleted that frame, the hidden legacy list must not come back as
            // an invisible essential variant.
            // Wall layers keep the frame-less algorithm but run on the layout
            // pipeline: their extras are inactive in Base, so the bake must skip
            // inactive objects, and Base states are needed to restore anything a
            // wall layer moved when that side is not walled.
            bool walls = attraction.HasWallLayers;
            attraction.SetPackingBake(BakeLayout(attraction, authored,
                !attraction.LegacyEssentialMigrated, walls));
            attraction.SetLayoutBaseStates(walls
                ? CollectLayoutBaseStates(attraction)
                : new List<AttractionLayoutBaseState>());
            if (walls) LogWallCombinationIssues(attraction);
            EditorUtility.SetDirty(attraction);
            PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
            return true;
        }

        // With frames every bake ignores objects that are inactive in that frame,
        // so extras that only exist in big frames never block the smaller ones.
        AttractionPackingBake baseBake = BakeFrame(attraction, null, authored);
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame != null) frame.SetBake(BakeFrame(attraction, frame, frame.SizeMeters));
        }
        baseBake = ChainGrowth(attraction, baseBake, authored);

        // Old readers only understand Base + "essential": make essential mean
        // the Minimum frame so legacy ladders reserve the right minimum.
        AttractionLayoutFrame minimum = attraction.MinimumFrame;
        bool mirrorsMinimum = attraction.FrameHasValidBake(minimum);
        var mirrored = new AttractionPackingBake(
            baseBake.AuthoredFootprintMeters,
            baseBake.SafeFootprintMeters,
            baseBake.ShrinkFootprintMeters,
            baseBake.GrowFootprintMeters,
            mirrorsMinimum ? minimum.Bake.ShrinkFootprintMeters : baseBake.ShrinkFootprintMeters,
            new List<AttractionPropPackingPose>(baseBake.Props),
            new List<AttractionPackingFollowerPose>(baseBake.Followers),
            mirrorsMinimum);
        mirrored.SetLayoutFootprintMeters(baseBake.LayoutFootprintMeters);
        mirrored.SetLayoutIssue(baseBake.LayoutIssue);
        mirrored.SetRequested(baseBake.RequestedFootprintMeters, baseBake.RequestedGrowScale);
        attraction.SetPackingBake(mirrored);
        attraction.SetLayoutBaseStates(CollectLayoutBaseStates(attraction));
        if (attraction.HasWallLayers) LogWallCombinationIssues(attraction);
        EditorUtility.SetDirty(attraction);
        PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
        return true;
    }

    /// <summary>
    /// Bake one frame (null = Base) in place: snapshot the hierarchy, apply the
    /// frame's overrides, bake at the declared size, then restore the snapshot —
    /// even when the bake throws. The bake references the live transforms and
    /// records the declared size and growth it was asked for.
    /// </summary>
    private static AttractionPackingBake BakeFrame(AttractionTemplate attraction, AttractionLayoutFrame frame,
        Vector2 declared, Vector2? growthScale = null)
    {
        if (frame != null && (declared.x <= 0f || declared.y <= 0f)) return null;

        LayoutHierarchySnapshot snapshot = LayoutHierarchySnapshot.Capture(attraction.transform);
        try
        {
            if (frame != null)
            {
                ApplyFrameOverrides(attraction, frame);
                FrameBakeHookForTests?.Invoke(attraction, frame);
            }
            Physics.SyncTransforms();
            // The declared size IS the authored footprint: content may overhang it on
            // purpose, and the creator's objects are never moved to make them fit.
            AttractionPackingBake bake = BakeLayout(attraction, declared, false, true, true, growthScale);
            bake.SetRequested(declared, RequestedGrowthScale(attraction));
            return bake;
        }
        finally
        {
            snapshot.Restore();
            Physics.SyncTransforms();
        }
    }

    /// <summary>
    /// Continuous coverage. Walk the ladder (largest → smallest authored area, the
    /// order TrySelectFrame uses) and let each frame grow, per axis, at least to
    ///   • every larger frame's shrink — until a larger frame fits, this one is the
    ///     one chosen, so it must be able to fill the space; and
    ///   • the point on the Size line (smallest shrink → largest grow, the debug
    ///     preview's slider) where the next larger frame starts to fit. The first
    ///     rule alone leaves a gap when that larger frame's shrink is lopsided (it
    ///     fails on one axis while the line has long passed it on the other).
    /// Frames that need more than authored × the growth multiplier are re-baked with
    /// the larger per-axis scale (same poses and shrink; only growth differs).
    /// Spaces far off that line on one axis can still leave margin on that axis.
    /// </summary>
    private static AttractionPackingBake ChainGrowth(AttractionTemplate attraction, AttractionPackingBake baseBake,
        Vector2 baseDeclared)
    {
        attraction.SetPackingBake(baseBake);
        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        if (ladder.Count < 2) return baseBake;

        Vector2 requested = RequestedGrowthScale(attraction);
        GetSizeLine(ladder, out Vector2 lineStart, out Vector2 lineEnd);
        Vector2 largerShrink = Vector2.zero;
        float takeover = float.PositiveInfinity;
        for (int k = 0; k < ladder.Count; k++)
        {
            AttractionFrameEnvelope envelope = ladder[k];
            float reach = SizeLineReach(envelope.shrinkMeters, lineStart, lineEnd);
            if (k > 0)
            {
                Vector2 need = largerShrink;
                if (reach < takeover && !float.IsPositiveInfinity(takeover))
                    need = Vector2.Max(need, Vector2.Lerp(lineStart, lineEnd, Mathf.Min(1f, takeover)));
                Vector2 plain = Vector2.Scale(envelope.authoredMeters, requested);
                if (need.x > plain.x + 0.0001f || need.y > plain.y + 0.0001f)
                {
                    Vector2 scale = Vector2.Max(requested, new Vector2(
                        need.x / envelope.authoredMeters.x, need.y / envelope.authoredMeters.y));
                    if (envelope.frameId == AttractionTemplate.BaseFrameId)
                    {
                        baseBake = BakeFrame(attraction, null, baseDeclared, scale);
                        attraction.SetPackingBake(baseBake);
                    }
                    else
                    {
                        AttractionLayoutFrame frame = attraction.FindFrame(envelope.frameId);
                        frame?.SetBake(BakeFrame(attraction, frame, frame.SizeMeters, scale));
                    }
                }
            }
            largerShrink = Vector2.Max(largerShrink, envelope.shrinkMeters);
            takeover = Mathf.Min(takeover, reach);
        }
        return baseBake;
    }

    /// <summary>
    /// The Size line through a ladder: from the smallest-area shrink (the smallest
    /// size any single frame reaches — AttractionTemplate.MinimumFootprintMeters) to
    /// the componentwise largest grow. Shared with the inspector's Size slider.
    /// </summary>
    internal static void GetSizeLine(List<AttractionFrameEnvelope> ladder, out Vector2 start, out Vector2 end)
    {
        start = Vector2.zero;
        end = Vector2.zero;
        if (ladder == null || ladder.Count == 0) return;
        int smallest = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].shrinkMeters.x * ladder[i].shrinkMeters.y
                < ladder[smallest].shrinkMeters.x * ladder[smallest].shrinkMeters.y - 0.0001f)
                smallest = i;
        start = ladder[smallest].shrinkMeters;
        for (int i = 0; i < ladder.Count; i++) end = Vector2.Max(end, ladder[i].growMeters);
        end = Vector2.Max(end, start);
    }

    // First t on the Size line where a frame with this shrink fits; +∞ if never.
    private static float SizeLineReach(Vector2 shrink, Vector2 start, Vector2 end)
    {
        float reach = 0f;
        for (int axis = 0; axis < 2; axis++)
        {
            float need = shrink[axis] - start[axis];
            if (need <= 0.0001f) continue;
            float span = end[axis] - start[axis];
            if (span <= 0.00001f) return float.PositiveInfinity;
            reach = Mathf.Max(reach, need / span);
        }
        return reach > 1.000001f ? float.PositiveInfinity : reach;
    }

    private static Vector2 RequestedGrowthScale(AttractionTemplate attraction) => new Vector2(
        Mathf.Max(1f, attraction.maxGrowthScale.x),
        Mathf.Max(1f, attraction.maxGrowthScale.y));

    /// <summary>
    /// The packing algorithm for one layout. The authored footprint is a parameter
    /// (frames bake at their own size without touching Size/customSize).
    /// includeLegacyEssential computes the essentialProps variant; without it the
    /// essential fields equal the shrink fields. skipInactive ignores packing roots
    /// and followers that are inactive under the attraction root.
    /// separableLayout (layout-frame attractions) makes every independent X/Z scale
    /// pair collision-free and stops the shrink pose over-compacting: see
    /// PackSeparable, ApplySeparableGrowth and FindGridCollision. Frame-less
    /// attractions keep the original joint compaction exactly.
    /// </summary>
    private static AttractionPackingBake BakeLayout(
        AttractionTemplate attraction,
        Vector2 authored,
        bool includeLegacyEssential,
        bool skipInactive,
        bool separableLayout = false,
        Vector2? growthScaleOverride = null)
    {
        List<Item> items = CollectItems(attraction, includeLegacyEssential, skipInactive);
        List<FollowerItem> followers = CollectFollowers(attraction, skipInactive);
        // Same inset rule as AttractionTemplate.GetSafeFootprintMeters.
        Vector2 safe = authored * (1f - Mathf.Clamp(attraction.safeAreaInset, 0f, 0.49f) * 2f);
        Vector2 layoutFootprint = MeasureAuthoredLayout(items);

        if (items.Count == 0)
        {
            Vector2 emptyGrowthScale = growthScaleOverride ?? new Vector2(
                Mathf.Max(1f, attraction.maxGrowthScale.x),
                Mathf.Max(1f, attraction.maxGrowthScale.y));
            var empty = new AttractionPackingBake(
                authored, safe, authored, Vector2.Scale(authored, emptyGrowthScale), authored,
                new List<AttractionPropPackingPose>(),
                BuildFollowerPoses(followers, items, attraction.transform,
                    authored, authored, authored, Vector2.Scale(authored, emptyGrowthScale),
                    attraction.shrinkClearanceMeters, includeLegacyEssential));
            empty.SetLayoutFootprintMeters(layoutFootprint);
            return empty;
        }

        // Only genuine authored intersections are exempt from collision checks.
        // Items that merely start closer than the requested clearance must still
        // become blockers; otherwise a tight grid can collapse rows onto each other.
        HashSet<long> authoredOverlaps = FindOverlaps(items, 1f, false, 0f);
        Vector2 separableShrink = Vector2.zero;
        Vector3[] shrunkPositions;
        if (separableLayout)
        {
            // Packed against the content's own extent (content may overhang the
            // declared size), then anchored to the declared size: the reserved
            // footprint shrinks by the same ratio as the content, so content and
            // footprint keep their authored proportion across the whole shrink range.
            // Content inside the declared size keeps the original rule (shrink = packed).
            shrunkPositions = PackSeparable(items, authoredOverlaps, attraction.shrinkClearanceMeters,
                Vector2.Max(layoutFootprint, authored), out Vector2 packed);
            separableShrink = new Vector2(
                DeclaredRelativeShrink(authored.x, layoutFootprint.x, packed.x),
                DeclaredRelativeShrink(authored.y, layoutFootprint.y, packed.y));
        }
        else
        {
            shrunkPositions = PackInwardPositions(items, authoredOverlaps, false, attraction.shrinkClearanceMeters);
        }
        Vector3[] essentialShrunkPositions = includeLegacyEssential
            ? PackInwardPositions(items, authoredOverlaps, true, attraction.shrinkClearanceMeters)
            : null;

        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            item.shrunk = ToParentLocal(attraction.transform, item.transform.parent,
                shrunkPositions[i]);
            if (!includeLegacyEssential)
                item.essentialShrunk = item.shrunk;
            else
                item.essentialShrunk = item.essential
                    ? ToParentLocal(attraction.transform, item.transform.parent,
                        essentialShrunkPositions[i])
                    : item.authored;
        }

        // Keep shrink based on the same coarse roots it has always used. Growth can
        // then expand legacy grouping roots (for example A_CoinZone) into their
        // repeated children without changing the proven shrink result.
        Vector2 shrink = separableLayout
            ? separableShrink
            : Min(authored, MeasureSymmetricFootprint(attraction.transform, items, Pose.Shrunk, false, authored));
        Vector2 essentialShrink = includeLegacyEssential
            ? Min(authored, MeasureSymmetricFootprint(attraction.transform, items, Pose.EssentialShrunk, true, authored))
            : shrink;
        items = ExpandLegacyRepeatedRunsForGrowth(attraction.transform, items, skipInactive);

        AssignGrowGroups(items, authored, attraction.growGroupGapMeters, attraction.growAlignmentToleranceMeters);
        Vector2 requestedGrowthScale = growthScaleOverride ?? new Vector2(
            Mathf.Max(1f, attraction.maxGrowthScale.x),
            Mathf.Max(1f, attraction.maxGrowthScale.y));
        if (separableLayout)
            ApplySeparableGrowth(attraction.transform, items, requestedGrowthScale,
                attraction.shrinkClearanceMeters);
        else
            ApplyGroupedGrowth(
                attraction.transform,
                items,
                requestedGrowthScale);

        // Growth is an authored reservation target, not merely the tight bounds of
        // the moved props. This makes the runtime contract exact: authored width *
        // X scale and authored length * Z scale are always the reserved dimensions.
        Vector2 grow = Vector2.Scale(authored, requestedGrowthScale);

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

        var bake = new AttractionPackingBake(authored, safe, shrink, grow,
            essentialShrink, poses, BuildFollowerPoses(followers, items, attraction.transform,
                authored, shrink, essentialShrink, grow, attraction.shrinkClearanceMeters,
                includeLegacyEssential));
        bake.SetLayoutFootprintMeters(layoutFootprint);
        if (separableLayout)
            bake.SetLayoutIssue(FindGridCollision(attraction.transform, items, bake.ShrinkScale,
                bake.GrowScale, attraction.shrinkClearanceMeters, authored));
        return bake;
    }

    /// <summary>
    /// shrink = packed × min(1, declared / asPlaced): equal to the packed extent while
    /// the content fits the declared size, and declared × packed / asPlaced when it
    /// overhangs (both extents measured the same symmetric way, so off-centre content
    /// cancels in the ratio). Never more than declared.
    /// </summary>
    private static float DeclaredRelativeShrink(float declared, float asPlaced, float packed)
    {
        if (asPlaced <= 0.00001f) return declared;
        return Mathf.Min(declared, packed * Mathf.Min(1f, declared / asPlaced));
    }

    /// <summary>Symmetric footprint of the packing items exactly as authored.</summary>
    private static Vector2 MeasureAuthoredLayout(List<Item> items)
    {
        float extentX = 0f;
        float extentZ = 0f;
        for (int i = 0; i < items.Count; i++)
        {
            Rect rect = BoundsAt(items[i], items[i].authoredInAttraction);
            extentX = Mathf.Max(extentX, Mathf.Abs(rect.center.x) + rect.half.x);
            extentZ = Mathf.Max(extentZ, Mathf.Abs(rect.center.y) + rect.half.y);
        }
        return new Vector2(extentX * 2f, extentZ * 2f);
    }

    /// <summary>
    /// One-time migration: legacy essentialProps become a Minimum frame that turns
    /// off every active, non-essential packing root. The list itself is kept.
    /// </summary>
    internal static bool MigrateLegacyEssentialProps(AttractionTemplate attraction)
    {
        if (attraction == null || attraction.LegacyEssentialMigrated) return false;
        bool anyEssential = false;
        if (attraction.essentialProps != null)
            for (int i = 0; i < attraction.essentialProps.Count && !anyEssential; i++)
            {
                Transform selected = attraction.essentialProps[i];
                anyEssential = selected != null && selected != attraction.transform
                    && selected.IsChildOf(attraction.transform);
            }
        if (!anyEssential) return false;
        if (attraction.MinimumFrame != null)
        {
            // A Minimum frame already supersedes the legacy list. Record that, or
            // deleting that frame later would resurrect a migrated one on the next bake.
            attraction.LegacyEssentialMigrated = true;
            EditorUtility.SetDirty(attraction);
            return false;
        }

        var minimum = new AttractionLayoutFrame
        {
            id = AttractionTemplate.MinimumFrameId,
            name = "Minimum",
            kind = AttractionLayoutFrameKind.Minimum,
            sizeFeet = attraction.DimensionsInFeet,
        };
        List<Item> roots = CollectItems(attraction, true, false);
        for (int i = 0; i < roots.Count; i++)
        {
            Item root = roots[i];
            if (root.essential || !root.transform.gameObject.activeSelf) continue;
            minimum.overrides.Add(new AttractionLayoutOverride
            {
                path = PathFrom(attraction.transform, root.transform),
                target = root.transform,
                setActive = true,
                active = false,
            });
        }

        var frames = new List<AttractionLayoutFrame>(attraction.LayoutFrames) { minimum };
        attraction.SetLayoutFrames(frames);
        attraction.LegacyEssentialMigrated = true;
        EditorUtility.SetDirty(attraction);
        return true;
    }

    /// <summary>
    /// Base state of every transform a frame or bake may touch: all override
    /// targets plus every prop/follower referenced by the Base or a frame bake.
    /// Captured while the hierarchy is in its Base state.
    /// </summary>
    private static List<AttractionLayoutBaseState> CollectLayoutBaseStates(AttractionTemplate attraction)
    {
        Transform root = attraction.transform;
        var seen = new HashSet<Transform>();
        var states = new List<AttractionLayoutBaseState>();

        void Add(Transform target)
        {
            if (target == null || target == root || !target.IsChildOf(root) || !seen.Add(target)) return;
            states.Add(new AttractionLayoutBaseState
            {
                path = PathFrom(root, target),
                target = target,
                active = target.gameObject.activeSelf,
                localPosition = target.localPosition,
                localRotation = target.localRotation,
                localScale = target.localScale,
            });
        }

        void AddBake(AttractionPackingBake bake)
        {
            if (bake == null) return;
            for (int i = 0; i < bake.Props.Count; i++) Add(bake.Props[i]?.Prop);
            for (int i = 0; i < bake.Followers.Count; i++) Add(bake.Followers[i]?.Follower);
        }

        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame?.overrides == null) continue;
            for (int j = 0; j < frame.overrides.Count; j++)
                Add(ResolveOverrideTarget(root, frame.overrides[j]));
        }
        IReadOnlyList<AttractionWallLayer> layers = attraction.WallLayers;
        for (int i = 0; i < layers.Count; i++)
        {
            AttractionWallLayer layer = layers[i];
            if (layer?.overrides == null) continue;
            for (int j = 0; j < layer.overrides.Count; j++)
                Add(ResolveOverrideTarget(root, layer.overrides[j]));
        }
        AddBake(attraction.PackingBake);
        for (int i = 0; i < frames.Count; i++) AddBake(frames[i]?.Bake);
        return states;
    }

    /// <summary>
    /// Resolve an override under root: the serialized reference while it still
    /// lives there (refreshing a stale path after renames), else the path.
    /// </summary>
    internal static Transform ResolveOverrideTarget(Transform root, AttractionLayoutOverride change)
    {
        if (change == null || root == null) return null;
        Transform target = change.Resolve(root);
        if (target != null && target == change.target)
        {
            string current = PathFrom(root, target);
            if (current != change.path) change.path = current;
        }
        return target;
    }

    /// <summary>Apply a frame's overrides to the live hierarchy (no Undo).</summary>
    internal static void ApplyFrameOverrides(AttractionTemplate attraction, AttractionLayoutFrame frame)
    {
        if (attraction == null || frame?.overrides == null) return;
        for (int i = 0; i < frame.overrides.Count; i++)
        {
            AttractionLayoutOverride change = frame.overrides[i];
            Transform target = ResolveOverrideTarget(attraction.transform, change);
            if (target == null) continue;
            if (change.setPosition) target.localPosition = change.localPosition;
            if (change.setRotation) target.localRotation = change.localRotation;
            if (change.setScale) target.localScale = change.localScale;
            if (change.setActive && target.gameObject.activeSelf != change.active)
                target.gameObject.SetActive(change.active);
        }
    }

    /// <summary>
    /// Inspector warnings for an attraction's layout frames. Reads serialized data
    /// only (it never applies a frame), so it is cheap enough to call per repaint.
    /// </summary>
    internal static List<string> GetFrameWarnings(AttractionTemplate attraction)
    {
        var warnings = new List<string>();
        if (attraction == null || !attraction.HasLayoutFrames) return warnings;

        Transform root = attraction.transform;
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        var ids = new HashSet<string>();
        int minimumCount = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame == null) continue;
            string label = FrameLabel(frame);
            if (frame.kind == AttractionLayoutFrameKind.Minimum)
            {
                minimumCount++;
                if (frame.id != AttractionTemplate.MinimumFrameId)
                    warnings.Add($"{label}: the Minimum frame's id must be '{AttractionTemplate.MinimumFrameId}'.");
            }
            else if (!AttractionTemplate.IsValidFrameId(frame.id)
                     || frame.id == AttractionTemplate.BaseFrameId
                     || frame.id == AttractionTemplate.MinimumFrameId)
            {
                warnings.Add($"{label}: '{frame.id}' is not a valid frame id (lowercase letters, digits and '-', at most 32; 'base' and 'minimum' are reserved).");
            }
            if (!string.IsNullOrEmpty(frame.id) && !ids.Add(frame.id))
                warnings.Add($"{label}: another frame already uses the id '{frame.id}'.");
            if (frame.sizeFeet.x <= 0f || frame.sizeFeet.y <= 0f)
                warnings.Add($"{label}: its size must be larger than 0 × 0 ft.");

            if (frame.overrides != null)
                for (int j = 0; j < frame.overrides.Count; j++)
                {
                    AttractionLayoutOverride change = frame.overrides[j];
                    if (change != null && change.Resolve(root) == null)
                        warnings.Add($"{label}: override target '{change.path}' was not found. It was renamed, moved or deleted.");
                }

            if (!attraction.FrameHasValidBake(frame))
            {
                warnings.Add($"{label}: its bake is missing or out of date. Re-bake the attraction.");
                continue;
            }
            Vector2 layout = frame.Bake.LayoutFootprintMeters;
            Vector2 size = frame.SizeMeters;
            if (frame.Bake.Version >= 11)
            {
                string overhang = OverhangNote(DisplayName(frame), frame.Bake);
                if (overhang != null) warnings.Add(overhang);
            }
            else if (layout.x > size.x + 0.05f || layout.y > size.y + 0.05f)
            {
                warnings.Add($"{label}: its layout ({FormatFeet(layout)}) overflows the frame size ({FormatFeet(size)}).");
            }
            if (!string.IsNullOrEmpty(frame.Bake.LayoutIssue))
                warnings.Add($"{label}: {frame.Bake.LayoutIssue}. Space those props further apart in this frame, or turn one off.");
        }
        if (attraction.HasPackingBake && attraction.PackingBake.Version >= 11)
        {
            string overhang = OverhangNote("Base", attraction.PackingBake);
            if (overhang != null) warnings.Insert(0, overhang);
        }
        if (attraction.HasPackingBake && !string.IsNullOrEmpty(attraction.PackingBake.LayoutIssue))
            warnings.Add($"Base: {attraction.PackingBake.LayoutIssue}. Space those props further apart, or turn one off in Base.");
        if (minimumCount > 1)
            warnings.Add("More than one Minimum frame exists; only the first is used.");

        AttractionLayoutFrame minimum = attraction.MinimumFrame;
        if (attraction.HasPackingBake && attraction.FrameHasValidBake(minimum))
        {
            Vector2 minimumShrink = minimum.Bake.ShrinkFootprintMeters;
            Vector2 baseShrink = attraction.PackingBake.ShrinkFootprintMeters;
            if (minimumShrink.x * minimumShrink.y >= baseShrink.x * baseShrink.y - 0.0001f)
                warnings.Add($"{FrameLabel(minimum)}: it shrinks to {FormatFeet(minimumShrink)}, which is no smaller than Base ({FormatFeet(baseShrink)}). Turn off more of what the game can live without.");
        }

        var sized = new List<KeyValuePair<string, Vector2>>
        {
            new KeyValuePair<string, Vector2>("Base", attraction.DimensionsInFeet),
        };
        // The Minimum frame is Base-sized by default (Create Minimum Frame, migration)
        // and is defined by what it turns off: the ladder still reaches it whenever a
        // same-size frame's shrink does not fit, so a shared size is not a mistake.
        for (int i = 0; i < frames.Count; i++)
            if (frames[i] != null && frames[i].kind != AttractionLayoutFrameKind.Minimum)
                sized.Add(new KeyValuePair<string, Vector2>(FrameLabel(frames[i]), frames[i].sizeFeet));
        for (int i = 0; i < sized.Count; i++)
        for (int j = i + 1; j < sized.Count; j++)
        {
            Vector2 a = sized[i].Value;
            Vector2 b = sized[j].Value;
            if (Mathf.Abs(a.x - b.x) <= 0.01f && Mathf.Abs(a.y - b.y) <= 0.01f)
                warnings.Add($"{sized[i].Key} and {sized[j].Key} have the same size ({a.x:0.##} × {a.y:0.##} ft); only one of them can ever be chosen.");
        }
        return warnings;
    }

    // ------------------------------------------------------------------ Wall layers

    /// <summary>Last combination check per attraction (instance id), for the inspector.</summary>
    internal static readonly Dictionary<int, List<string>> WallCombinationIssues = new Dictionary<int, List<string>>();

    /// <summary>
    /// Inspector warnings for wall layers. Serialized data only (never applies a
    /// layer), cheap enough per repaint.
    /// </summary>
    internal static List<string> GetWallLayerWarnings(AttractionTemplate attraction)
    {
        var warnings = new List<string>();
        if (attraction == null || !attraction.HasWallLayers) return warnings;
        Transform root = attraction.transform;
        var sides = new HashSet<AttractionWallSide>();
        var ids = new HashSet<string>();
        IReadOnlyList<AttractionWallLayer> layers = attraction.WallLayers;
        for (int i = 0; i < layers.Count; i++)
        {
            AttractionWallLayer layer = layers[i];
            if (layer == null) continue;
            string label = AttractionLayoutFrameEditSession.WallLabel(layer.side);
            if (!sides.Add(layer.side))
                warnings.Add($"{label}: there is more than one layer for this side; only the first is used.");
            if (string.IsNullOrEmpty(layer.id) || !ids.Add(layer.id))
                warnings.Add($"{label}: its id is missing or shared with another layer. Delete it and add it again.");
            if (layer.overrides == null || layer.overrides.Count == 0)
            {
                warnings.Add($"{label}: it doesn't change anything yet. Edit it to add or move things for this wall.");
                continue;
            }
            for (int j = 0; j < layer.overrides.Count; j++)
            {
                AttractionLayoutOverride change = layer.overrides[j];
                if (change == null) continue;
                Transform target = change.Resolve(root);
                if (target == null)
                {
                    warnings.Add($"{label}: override target '{change.path}' was not found. It was renamed, moved or deleted.");
                    continue;
                }
                AttractionPackingFollower follower = target.GetComponent<AttractionPackingFollower>();
                if (change.setPosition && follower != null && follower.enabled && follower.followPacking)
                    warnings.Add($"{label}: '{change.path}' follows packing, which places it every time; this layer's move of it is ignored.");
            }
        }
        if (WallCombinationIssues.TryGetValue(attraction.GetInstanceID(), out List<string> combos))
            warnings.AddRange(combos);
        return warnings;
    }

    private static void LogWallCombinationIssues(AttractionTemplate attraction)
    {
        List<string> issues = FindWallCombinationIssues(attraction);
        WallCombinationIssues[attraction.GetInstanceID()] = issues;
        for (int i = 0; i < issues.Count; i++)
            Debug.LogWarning($"[AttractionPacking] '{attraction.name}' wall layers: {issues[i]}");
    }

    /// <summary>
    /// Apply every combination of walled sides (Base and every baked frame, at
    /// authored size) and report objects a wall layer placed on top of something
    /// they did not already overlap in that layout without walls. Warnings only.
    /// The hierarchy is restored afterwards, even when this throws.
    /// </summary>
    // Seconds FindWallCombinationIssues may spend per attraction (tests may lower it).
    internal static double WallCheckBudgetSeconds = 20d;

    internal static List<string> FindWallCombinationIssues(AttractionTemplate attraction)
    {
        var issues = new List<string>();
        if (attraction == null || !attraction.HasWallLayers) return issues;
        Transform root = attraction.transform;
        List<AttractionWallSide> sides = attraction.WallLayerSides;
        var layouts = new List<string> { AttractionTemplate.BaseFrameId };
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame == null || !attraction.FrameHasValidBake(frame)) continue;
            layouts.Add(frame.kind == AttractionLayoutFrameKind.Minimum ? AttractionTemplate.MinimumFrameId : frame.id);
        }

        var seen = new HashSet<string>();
        LayoutHierarchySnapshot snapshot = LayoutHierarchySnapshot.Capture(root);
        // Warnings only, and it re-applies the whole layout (layouts × 2^sides) times,
        // so it is time-boxed: an attraction with many frames and walls must not hold
        // a save or a publish (Zombiez: 10 layouts × 16 combinations per level).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int combinations = layouts.Count * (1 << sides.Count);
        int checkedCombinations = 0;
        try
        {
            for (int l = 0; l < layouts.Count; l++)
            {
                if (clock.Elapsed.TotalSeconds > WallCheckBudgetSeconds)
                {
                    issues.Add($"wall check stopped after {WallCheckBudgetSeconds:0} s, with {checkedCombinations} of {combinations} layout and wall combinations checked; the rest were not checked.");
                    break;
                }
                string layoutId = layouts[l];
                checkedCombinations++;
                attraction.ApplyLayout(layoutId, Vector2.one, AttractionWallMask.None);
                Physics.SyncTransforms();
                HashSet<long> baseline = OverlappingPairs(root, null);

                for (int bits = 1; bits < 1 << sides.Count; bits++)
                {
                    var mask = AttractionWallMask.None;
                    for (int s = 0; s < sides.Count; s++)
                        if ((bits & (1 << s)) != 0) mask |= AttractionWallSides.Mask(sides[s]);
                    attraction.ApplyLayout(layoutId, Vector2.one, mask);
                    Physics.SyncTransforms();
                    checkedCombinations++;
                    HashSet<Transform> moved = WallLayerTargets(attraction, mask);
                    foreach (long pair in OverlappingPairs(root, moved))
                    {
                        if (baseline.Contains(pair)) continue;
                        string a = ItemName(root, pair, true), b = ItemName(root, pair, false);
                        string key = a + "|" + b;
                        if (!seen.Add(key)) continue;
                        string layoutName = layoutId == AttractionTemplate.BaseFrameId ? "Base"
                            : AttractionLayoutFrameEditSession.Label(attraction.FindFrame(layoutId), layoutId);
                        issues.Add($"with walls {AttractionWallSides.ToWire(mask).Replace(",", " + ")} in {layoutName}, '{a}' overlaps '{b}'. Move one of them in its wall layer, or reorder the layers.");
                    }
                }
            }
        }
        finally
        {
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.None);
            snapshot.Restore();
            Physics.SyncTransforms();
        }
        return issues;
    }

    private static HashSet<Transform> WallLayerTargets(AttractionTemplate attraction, AttractionWallMask mask)
    {
        var targets = new HashSet<Transform>();
        IReadOnlyList<AttractionWallLayer> layers = attraction.WallLayers;
        for (int i = 0; i < layers.Count; i++)
        {
            AttractionWallLayer layer = layers[i];
            if (layer?.overrides == null || !AttractionWallSides.Has(mask, layer.side)) continue;
            for (int j = 0; j < layer.overrides.Count; j++)
            {
                Transform target = layer.overrides[j]?.Resolve(attraction.transform);
                if (target != null && target.gameObject.activeInHierarchy) targets.Add(TopLevelUnder(attraction.transform, target));
            }
        }
        return targets;
    }

    // Overlap pairs are keyed by the top-level children's sibling indices.
    private static HashSet<long> OverlappingPairs(Transform root, HashSet<Transform> onlyInvolving)
    {
        var pairs = new HashSet<long>();
        var items = new List<KeyValuePair<Transform, UnityEngine.Rect>>();
        Vector2 footprint = Vector2.zero;
        AttractionTemplate attraction = root.GetComponent<AttractionTemplate>();
        if (attraction != null) footprint = attraction.RuntimeFootprintMeters;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (!child.gameObject.activeInHierarchy) continue;
            AttractionPackingFollower follower = child.GetComponent<AttractionPackingFollower>();
            if (follower != null && follower.enabled && follower.followPacking) continue;   // floors/borders
            if (!TryLocalXZBounds(root, child, out UnityEngine.Rect bounds)) continue;
            // Something as big as the attraction itself (a floor, a skybox) overlaps everything.
            if (footprint.x > 0f && bounds.width * bounds.height >= footprint.x * footprint.y * 0.6f) continue;
            items.Add(new KeyValuePair<Transform, UnityEngine.Rect>(child, bounds));
        }
        for (int i = 0; i < items.Count; i++)
        for (int j = i + 1; j < items.Count; j++)
        {
            if (onlyInvolving != null && !onlyInvolving.Contains(items[i].Key) && !onlyInvolving.Contains(items[j].Key)) continue;
            UnityEngine.Rect a = items[i].Value, b = items[j].Value;
            float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            float d = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            if (w <= 0.02f || d <= 0.02f) continue;
            int x = items[i].Key.GetSiblingIndex(), y = items[j].Key.GetSiblingIndex();
            pairs.Add(((long)Mathf.Min(x, y) << 32) | (uint)Mathf.Max(x, y));
        }
        return pairs;
    }

    private static string ItemName(Transform root, long pair, bool first)
    {
        int index = first ? (int)(pair >> 32) : (int)(pair & 0xffffffff);
        return index >= 0 && index < root.childCount ? root.GetChild(index).name : "?";
    }

    private static Transform TopLevelUnder(Transform root, Transform target)
    {
        Transform cursor = target;
        while (cursor != null && cursor.parent != root) cursor = cursor.parent;
        return cursor ?? target;
    }

    private static bool TryLocalXZBounds(Transform root, Transform target, out UnityEngine.Rect bounds)
    {
        bounds = default;
        bool any = false;
        float minX = 0f, maxX = 0f, minZ = 0f, maxZ = 0f;
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(false);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || renderer is ParticleSystemRenderer || renderer is TrailRenderer
                || renderer is LineRenderer) continue;
            Bounds world = renderer.bounds;
            Vector3 c = world.center, e = world.extents;
            for (int k = 0; k < 8; k++)
            {
                Vector3 corner = c + new Vector3((k & 1) != 0 ? e.x : -e.x, (k & 2) != 0 ? e.y : -e.y, (k & 4) != 0 ? e.z : -e.z);
                Vector3 local = root.InverseTransformPoint(corner);
                if (!any) { minX = maxX = local.x; minZ = maxZ = local.z; any = true; }
                else
                {
                    minX = Mathf.Min(minX, local.x); maxX = Mathf.Max(maxX, local.x);
                    minZ = Mathf.Min(minZ, local.z); maxZ = Mathf.Max(maxZ, local.z);
                }
            }
        }
        if (any) bounds = UnityEngine.Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        return any;
    }

    /// <summary>
    /// Informational: the content as placed extends past the declared size (allowed —
    /// the declared size is kept). The amount is the worst side's overhang per axis
    /// (the as-placed footprint is symmetric about the pivot). Null when it fits.
    /// </summary>
    private static string OverhangNote(string displayName, AttractionPackingBake bake)
    {
        Vector2 declared = bake.AuthoredFootprintMeters;
        Vector2 layout = bake.LayoutFootprintMeters;
        Vector2 overhang = Vector2.Max(Vector2.zero, (layout - declared) * 0.5f);
        if (overhang.x <= 0.05f && overhang.y <= 0.05f) return null;
        return $"‘{displayName}’: content overhangs its {FormatFeet(declared)} frame by up to " +
               $"{FormatFeet(overhang)} (allowed: content that overflows the frame keeps the size you set).";
    }

    private static string DisplayName(AttractionLayoutFrame frame) =>
        string.IsNullOrEmpty(frame.name) ? frame.id : frame.name;

    private static string FrameLabel(AttractionLayoutFrame frame)
    {
        string displayName = string.IsNullOrEmpty(frame.name) ? frame.id : frame.name;
        return frame.kind == AttractionLayoutFrameKind.Minimum
            ? $"Minimum frame '{displayName}'"
            : $"Frame '{displayName}'";
    }

    private static string FormatFeet(Vector2 meters) =>
        $"{meters.x / FeetToMeters:0.##} × {meters.y / FeetToMeters:0.##} ft";

    /// <summary>
    /// Active state + local TRS of every transform under an attraction root, so a
    /// temporary frame state can always be undone exactly.
    /// </summary>
    internal sealed class LayoutHierarchySnapshot
    {
        private readonly List<Transform> transforms = new List<Transform>();
        private readonly List<bool> active = new List<bool>();
        private readonly List<Vector3> positions = new List<Vector3>();
        private readonly List<Quaternion> rotations = new List<Quaternion>();
        private readonly List<Vector3> scales = new List<Vector3>();

        internal static LayoutHierarchySnapshot Capture(Transform root)
        {
            var snapshot = new LayoutHierarchySnapshot();
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform target = all[i];
                if (target == root) continue;
                snapshot.transforms.Add(target);
                snapshot.active.Add(target.gameObject.activeSelf);
                snapshot.positions.Add(target.localPosition);
                snapshot.rotations.Add(target.localRotation);
                snapshot.scales.Add(target.localScale);
            }
            return snapshot;
        }

        internal void Restore()
        {
            for (int i = 0; i < transforms.Count; i++)
            {
                Transform target = transforms[i];
                if (target == null) continue;
                if (!target.localPosition.Equals(positions[i])) target.localPosition = positions[i];
                if (!target.localRotation.Equals(rotations[i])) target.localRotation = rotations[i];
                if (!target.localScale.Equals(scales[i])) target.localScale = scales[i];
            }
            for (int i = 0; i < transforms.Count; i++)
            {
                Transform target = transforms[i];
                if (target != null && target.gameObject.activeSelf != active[i])
                    target.gameObject.SetActive(active[i]);
            }
        }
    }

    /// <summary>
    /// Bake the prefab asset when the inspector is showing a scene instance. This
    /// keeps packing data in the distributable attraction rather than as a scene-only
    /// prefab override. Non-prefab objects still bake in place for test tooling.
    /// </summary>
    internal static bool BakeIntoAsset(AttractionTemplate attraction)
    {
        if (attraction == null) return false;
        // The root of a Prefab Mode stage IS the prefab being edited. For a variant it
        // is also an instance of the BASE prefab, and baking "into its asset" would
        // copy the variant's packing settings and layout frames over the base prefab.
        var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(attraction.gameObject);
        if (stage != null && stage.prefabContentsRoot == attraction.gameObject) return Bake(attraction);
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
            // been written to the prefab asset (especially while in Prefab Mode or
            // on a scene instance). Carry every packing input into the isolated
            // prefab copy so the bake reflects what the user currently sees.
            CopyPackingAuthoringSettings(attraction, prefabAttraction);
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

    private static void CopyPackingAuthoringSettings(
        AttractionTemplate source,
        AttractionTemplate destination)
    {
        destination.size = source.size;
        destination.customSize = source.customSize;
        destination.safeAreaInset = source.safeAreaInset;
        destination.maxGrowthScale = source.maxGrowthScale;
        destination.shrinkClearanceMeters = source.shrinkClearanceMeters;
        destination.growGroupGapMeters = source.growGroupGapMeters;
        destination.growAlignmentToleranceMeters = source.growAlignmentToleranceMeters;

        if (destination.essentialProps == null)
            destination.essentialProps = new List<Transform>();
        destination.essentialProps.Clear();
        if (source.essentialProps != null)
        {
            for (int i = 0; i < source.essentialProps.Count; i++)
            {
                Transform sourceProp = source.essentialProps[i];
                if (sourceProp == null) continue;
                string path = PathFrom(source.transform, sourceProp);
                Transform destinationProp = string.IsNullOrEmpty(path)
                    ? destination.transform
                    : destination.transform.Find(path);
                if (destinationProp != null)
                    destination.essentialProps.Add(destinationProp);
                else
                    Debug.LogWarning($"[AttractionPacking] Could not map essential prop '{path}' into the prefab asset.");
            }
        }

        foreach (AttractionPackingFollower sourceFollower in
                 source.GetComponentsInChildren<AttractionPackingFollower>(true))
        {
            string path = PathFrom(source.transform, sourceFollower.transform);
            Transform target = destination.transform.Find(path);
            if (target == null) continue;
            AttractionPackingFollower destinationFollower =
                target.GetComponent<AttractionPackingFollower>();
            if (destinationFollower == null)
                destinationFollower = target.gameObject.AddComponent<AttractionPackingFollower>();
            destinationFollower.enabled = sourceFollower.enabled;
            destinationFollower.followPacking = sourceFollower.followPacking;
            destinationFollower.scaleMode = sourceFollower.scaleMode;
            destinationFollower.avoidNewPropOverlaps = sourceFollower.avoidNewPropOverlaps;
        }

        destination.LegacyEssentialMigrated = source.LegacyEssentialMigrated;
        destination.SetLayoutFrames(CopyLayoutFrames(source, destination));
        destination.SetWallLayers(CopyWallLayers(source, destination));
    }

    /// <summary>Deep-copy wall layers into another hierarchy, re-resolving every override.</summary>
    private static List<AttractionWallLayer> CopyWallLayers(
        AttractionTemplate source,
        AttractionTemplate destination)
    {
        var copies = new List<AttractionWallLayer>();
        IReadOnlyList<AttractionWallLayer> layers = source.WallLayers;
        for (int i = 0; i < layers.Count; i++)
        {
            AttractionWallLayer layer = layers[i];
            if (layer == null) continue;
            copies.Add(new AttractionWallLayer
            {
                id = layer.id,
                side = layer.side,
                authoredFootprintMeters = layer.authoredFootprintMeters,
                overrides = CopyOverrides(source, destination, layer.overrides,
                    AttractionLayoutFrameEditSession.WallLabel(layer.side)),
            });
        }
        return copies;
    }

    /// <summary>
    /// Deep-copy layout frames into another hierarchy (the prefab contents). Each
    /// override is re-resolved by path in the destination; an unresolved one is
    /// kept with a null target so no authored data is lost. Bakes are not copied:
    /// the destination is re-baked immediately.
    /// </summary>
    private static List<AttractionLayoutFrame> CopyLayoutFrames(
        AttractionTemplate source,
        AttractionTemplate destination)
    {
        var copies = new List<AttractionLayoutFrame>();
        IReadOnlyList<AttractionLayoutFrame> frames = source.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame == null) continue;
            var copy = new AttractionLayoutFrame
            {
                id = frame.id,
                name = frame.name,
                kind = frame.kind,
                sizeFeet = frame.sizeFeet,
                overridePortals = frame.overridePortals,
                portals = AttractionPortal.CloneList(frame.portals),
            };
            copy.overrides = CopyOverrides(source, destination, frame.overrides, $"layout frame '{frame.name}'");
            copies.Add(copy);
        }
        return copies;
    }

    /// <summary>
    /// Copy overrides into another hierarchy. Each is re-resolved by its live source
    /// reference (else its path); an unresolved one keeps a null target so no
    /// authored data is lost.
    /// </summary>
    private static List<AttractionLayoutOverride> CopyOverrides(
        AttractionTemplate source,
        AttractionTemplate destination,
        List<AttractionLayoutOverride> overrides,
        string ownerLabel)
    {
        var copies = new List<AttractionLayoutOverride>();
        if (overrides == null) return copies;
        for (int j = 0; j < overrides.Count; j++)
        {
            AttractionLayoutOverride change = overrides[j];
            if (change == null) continue;
            // A live source reference knows its current path even after a rename,
            // and which of several same-named siblings it is (Transform.Find
            // would always take the first one).
            Transform sourceTarget = change.Resolve(source.transform);
            string path = sourceTarget != null ? PathFrom(source.transform, sourceTarget) : change.path;
            Transform target = sourceTarget != null
                ? FindCorresponding(source.transform, sourceTarget, destination.transform)
                : string.IsNullOrEmpty(path) ? null : destination.transform.Find(path);
            if (target == null)
                Debug.LogWarning($"[AttractionPacking] Could not map {ownerLabel} override '{path}' into the prefab asset.");
            copies.Add(new AttractionLayoutOverride
            {
                path = path,
                target = target,
                setActive = change.setActive,
                active = change.active,
                setPosition = change.setPosition,
                localPosition = change.localPosition,
                setRotation = change.setRotation,
                localRotation = change.localRotation,
                setScale = change.setScale,
                localScale = change.localScale,
            });
        }
        return copies;
    }

    /// <summary>
    /// The transform at the same place under destinationRoot as source is under
    /// sourceRoot: the same name AND the same position among same-named siblings at
    /// every level. Objects added on a prefab instance do not exist in its asset,
    /// so they are neither mapped nor counted. Null when there is no match.
    /// </summary>
    internal static Transform FindCorresponding(Transform sourceRoot, Transform source, Transform destinationRoot)
    {
        if (sourceRoot == null || source == null || destinationRoot == null || source == sourceRoot) return null;
        var chain = new List<Transform>();
        for (Transform cursor = source; cursor != sourceRoot; cursor = cursor.parent)
        {
            if (cursor == null) return null;
            chain.Add(cursor);
        }

        Transform current = destinationRoot;
        for (int i = chain.Count - 1; i >= 0 && current != null; i--)
        {
            Transform step = chain[i];
            if (PrefabUtility.IsAddedGameObjectOverride(step.gameObject)) return null;
            int occurrence = 0;
            Transform parent = step.parent;
            for (int sibling = 0; sibling < parent.childCount; sibling++)
            {
                Transform other = parent.GetChild(sibling);
                if (other == step) break;
                if (other.name == step.name && !PrefabUtility.IsAddedGameObjectOverride(other.gameObject))
                    occurrence++;
            }

            Transform match = null;
            for (int child = 0; child < current.childCount; child++)
            {
                Transform candidate = current.GetChild(child);
                if (candidate.name != step.name) continue;
                if (occurrence-- == 0)
                {
                    match = candidate;
                    break;
                }
            }
            current = match;
        }
        return current;
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
            "showPackingGizmos",
            "layoutFrames",
            "layoutBaseStates",
            "legacyEssentialMigrated",
            "layoutPreviewFootprintMeters",
            "layoutPreviewFrameId",
            "wallLayers",
            "layoutPreviewWalls"
        };
        for (int i = 0; i < fields.Length; i++)
        {
            SerializedProperty property = serialized.FindProperty(fields[i]);
            if (property != null && property.prefabOverride)
                PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
        }
        serialized.UpdateIfRequiredOrScript();
    }

    private static List<Item> CollectItems(
        AttractionTemplate attraction,
        bool includeLegacyEssential,
        bool skipInactive)
    {
        var essentialRoots = new HashSet<Transform>();
        if (includeLegacyEssential && attraction.essentialProps != null)
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
            if (HasPackingFollowerOnPath(prop.transform, attraction.transform)) continue;
            if (IsEntirePackingRootHidden(prop.transform, skipInactive)) continue;

            // Move only top-level nested props. A PropTemplate inside another prop moves
            // with its owner and recording both would apply the displacement twice.
            PropTemplate ancestor = prop.transform.parent != null
                ? prop.transform.parent.GetComponentInParent<PropTemplate>(true)
                : null;
            if (ancestor != null && ancestor.transform.IsChildOf(attraction.transform)) continue;

            Transform directChild = DirectChildOf(attraction.transform, prop.transform);
            if (directChild != null) claimedDirectChildren.Add(directChild);
            // The owning child stays claimed so an inactive prop never turns its
            // grouping transform into a legacy geometry item.
            if (skipInactive && !IsActiveUnder(prop.transform, attraction.transform)) continue;

            Vector2 size = prop.FootprintMeters;
            Vector3 relativeForward = attraction.transform.InverseTransformDirection(prop.transform.forward);

            Vector3 authoredInAttraction = attraction.transform.InverseTransformPoint(prop.transform.position);
            result.Add(new Item
            {
                template = prop,
                transform = prop.transform,
                authored = prop.transform.localPosition,
                authoredInAttraction = authoredInAttraction,
                shrunk = prop.transform.localPosition,
                grown = prop.transform.localPosition,
                essentialShrunk = prop.transform.localPosition,
                size = size,
                offset = prop.footprintOffsetMeters,
                yawRadians = Mathf.Atan2(relativeForward.x, relativeForward.z),
                footprintPolygon = GetFloorCutoutPolygon(
                    attraction.transform, prop.transform, authoredInAttraction),
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
            if (skipInactive && !child.gameObject.activeSelf) continue;
            if (HasPackingFollowerOnPath(child, attraction.transform)) continue;
            if (IsEntirePackingRootHidden(child, skipInactive)) continue;
            if (!TryMeasureLocalFootprint(child, out Vector2 size, out Vector2 offset,
                    false, skipInactive)) continue;

            Vector3 relativeForward = attraction.transform.InverseTransformDirection(child.forward);
            Vector3 authoredInAttraction = attraction.transform.InverseTransformPoint(child.position);
            result.Add(new Item
            {
                template = null,
                transform = child,
                authored = child.localPosition,
                authoredInAttraction = authoredInAttraction,
                shrunk = child.localPosition,
                grown = child.localPosition,
                essentialShrunk = child.localPosition,
                size = size,
                offset = offset,
                yawRadians = Mathf.Atan2(relativeForward.x, relativeForward.z),
                footprintPolygon = GetFloorCutoutPolygon(
                    attraction.transform, child, authoredInAttraction),
                essential = essentialRoots.Contains(child),
                group = -1,
            });
        }
        return result;
    }

    private static List<FollowerItem> CollectFollowers(AttractionTemplate attraction, bool skipInactive)
    {
        var result = new List<FollowerItem>();
        AttractionPackingFollower[] all = attraction.GetComponentsInChildren<AttractionPackingFollower>(true);
        for (int i = 0; i < all.Length; i++)
        {
            AttractionPackingFollower follower = all[i];
            if (follower == null || !follower.enabled || !follower.followPacking
                || follower.transform == attraction.transform) continue;
            if (skipInactive && !IsActiveUnder(follower.transform, attraction.transform)) continue;
            if (HasPackingFollowerOnPath(follower.transform.parent, attraction.transform))
            {
                Debug.LogWarning($"[AttractionPacking] Nested packing follower '{follower.name}' is ignored.");
                continue;
            }
            PropTemplate owner = follower.transform.parent != null
                ? follower.transform.parent.GetComponentInParent<PropTemplate>(true) : null;
            if (owner != null && owner.transform.IsChildOf(attraction.transform))
            {
                Debug.LogWarning($"[AttractionPacking] Put follower '{follower.name}' on its owning PropTemplate or a separate child root.");
                continue;
            }
            if (!TryMeasureLocalFootprint(follower.transform, out Vector2 size,
                    out Vector2 offset, true, skipInactive))
            {
                Debug.LogWarning($"[AttractionPacking] Follower '{follower.name}' has no measurable footprint.");
                continue;
            }
            Vector3 relativeForward = attraction.transform.InverseTransformDirection(follower.transform.forward);
            result.Add(new FollowerItem
            {
                component = follower,
                transform = follower.transform,
                authoredPosition = attraction.transform.InverseTransformPoint(follower.transform.position),
                authoredLocalScale = follower.transform.localScale,
                size = size,
                offset = offset,
                yawRadians = Mathf.Atan2(relativeForward.x, relativeForward.z),
            });
        }
        return result;
    }

    /// <summary>
    /// Active relative to the attraction root. activeInHierarchy is unusable here:
    /// it is false for every object of a prefab asset and for a disabled root.
    /// </summary>
    private static bool IsActiveUnder(Transform current, Transform attractionRoot)
    {
        for (; current != null && current != attractionRoot; current = current.parent)
            if (!current.gameObject.activeSelf) return false;
        return true;
    }

    private static bool HasPackingFollowerOnPath(Transform current, Transform attractionRoot)
    {
        for (; current != null && current != attractionRoot; current = current.parent)
        {
            AttractionPackingFollower follower = current.GetComponent<AttractionPackingFollower>();
            if (follower != null && follower.enabled && follower.followPacking) return true;
        }
        return false;
    }

    /// <summary>
    /// Legacy attractions often organize repeated gameplay objects below a plain
    /// top-level transform instead of giving every object a PropTemplate. Shrink
    /// deliberately treats that transform as one rigid item. Growth needs the
    /// individual repeated children so trails can distribute through longer rooms.
    /// Each child inherits the already-solved rigid shrink translation from its
    /// owner, keeping shrink behavior byte-for-byte equivalent at runtime.
    /// </summary>
    private static List<Item> ExpandLegacyRepeatedRunsForGrowth(
        Transform attractionRoot,
        List<Item> coarseItems,
        bool skipInactive = false)
    {
        var result = new List<Item>(coarseItems.Count);
        for (int i = 0; i < coarseItems.Count; i++)
        {
            Item owner = coarseItems[i];
            if (owner.template != null
                || !TryGetRepeatedLegacyChildren(owner.transform, out List<Transform> children, skipInactive))
            {
                result.Add(owner);
                continue;
            }

            Vector3 shrinkDelta = ToAttractionLocal(
                attractionRoot, owner.transform.parent, owner.shrunk) - owner.authoredInAttraction;
            Vector3 essentialShrinkDelta = ToAttractionLocal(
                attractionRoot, owner.transform.parent, owner.essentialShrunk) - owner.authoredInAttraction;

            for (int childIndex = 0; childIndex < children.Count; childIndex++)
            {
                Transform child = children[childIndex];
                if (!TryMeasureLocalFootprint(child, out Vector2 size, out Vector2 offset,
                        false, skipInactive)) continue;

                Vector3 authoredInAttraction = attractionRoot.InverseTransformPoint(child.position);
                Vector3 relativeForward = attractionRoot.InverseTransformDirection(child.forward);
                result.Add(new Item
                {
                    template = null,
                    transform = child,
                    authored = child.localPosition,
                    authoredInAttraction = authoredInAttraction,
                    shrunk = ToParentLocal(
                        attractionRoot, child.parent, authoredInAttraction + shrinkDelta),
                    grown = child.localPosition,
                    essentialShrunk = ToParentLocal(
                        attractionRoot, child.parent, authoredInAttraction + essentialShrinkDelta),
                    size = size,
                    offset = offset,
                    yawRadians = Mathf.Atan2(relativeForward.x, relativeForward.z),
                    essential = owner.essential,
                    group = -1,
                });
            }
        }
        return result;
    }

    private static bool TryGetRepeatedLegacyChildren(
        Transform owner,
        out List<Transform> children,
        bool skipInactive = false)
    {
        children = new List<Transform>();
        // Only plain grouping transforms may be expanded into independently growing
        // children. If the owner has geometry of its own, replacing its pose with
        // child poses leaves that geometry behind at runtime (for example, a
        // procedural lava pit whose platform children happen to share a name).
        if (HasOwnPackingGeometry(owner)) return false;

        string family = null;
        for (int i = 0; i < owner.childCount; i++)
        {
            Transform child = owner.GetChild(i);
            if (skipInactive && !child.gameObject.activeSelf) continue;
            if (IsEntirePackingRootHidden(child, skipInactive)) continue;
            if (!TryMeasureLocalFootprint(child, out _, out _, false, skipInactive)) continue;

            string childFamily = GrowthFamilyName(child.name);
            if (string.IsNullOrEmpty(childFamily)) return false;
            if (family == null) family = childFamily;
            else if (!string.Equals(family, childFamily, StringComparison.OrdinalIgnoreCase))
                return false;
            children.Add(child);
        }
        return children.Count >= 3;
    }

    private static bool HasOwnPackingGeometry(Transform owner)
    {
        if (owner == null || IsHiddenPackingGeometry(owner, owner)) return false;

        Collider collider = owner.GetComponent<Collider>();
        if (collider != null && collider.bounds.size.sqrMagnitude > Mathf.Epsilon)
            return true;

        MeshFilter mesh = owner.GetComponent<MeshFilter>();
        Renderer renderer = owner.GetComponent<Renderer>();
        if (mesh != null && mesh.sharedMesh != null && renderer != null
            && !RendererUsesOnlyHiddenMaterials(renderer))
            return true;

        SkinnedMeshRenderer skinned = owner.GetComponent<SkinnedMeshRenderer>();
        return skinned != null && skinned.sharedMesh != null
            && !RendererUsesOnlyHiddenMaterials(skinned);
    }

    private static List<Vector2> GetFloorCutoutPolygon(
        Transform attractionRoot,
        Transform packingRoot,
        Vector3 authoredInAttraction)
    {
        FloorCutout cutout = packingRoot.GetComponent<FloorCutout>();
        if (cutout == null || cutout.points == null || cutout.points.Count < 3)
            return null;

        var polygon = new List<Vector2>(cutout.points.Count);
        for (int i = 0; i < cutout.points.Count; i++)
        {
            Vector3 point = attractionRoot.InverseTransformPoint(
                cutout.transform.TransformPoint(cutout.points[i]));
            polygon.Add(new Vector2(
                point.x - authoredInAttraction.x,
                point.z - authoredInAttraction.z));
        }
        return polygon;
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

    /// <summary>
    /// Local XZ footprint of a packing root's geometry. skipInactive (frame bakes)
    /// ignores geometry that is not active under the root, so a frame that hides
    /// part of a plain group (fewer bricks) measures only what it keeps.
    /// </summary>
    private static bool TryMeasureLocalFootprint(Transform root, out Vector2 size,
        out Vector2 offset, bool includeFollowerGeometry = false, bool skipInactive = false)
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
            if (IsHiddenPackingGeometry(colliders[i].transform, root)
                || (skipInactive && !IsActiveUnder(colliders[i].transform, root))
                || (!includeFollowerGeometry && HasPackingFollowerOnPath(colliders[i].transform, root.parent))) continue;
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
                if (renderer == null || IsHiddenPackingGeometry(filter.transform, root)
                    || (skipInactive && !IsActiveUnder(filter.transform, root))
                    || (!includeFollowerGeometry && HasPackingFollowerOnPath(filter.transform, root.parent))) continue;
                EncapsulateBounds(root, filter.transform.localToWorldMatrix, filter.sharedMesh.bounds, ref minimum, ref maximum);
                any = true;
            }

            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinned[i];
                if (renderer.sharedMesh == null) continue;
                if (IsHiddenPackingGeometry(renderer.transform, root)
                    || (skipInactive && !IsActiveUnder(renderer.transform, root))
                    || (!includeFollowerGeometry && HasPackingFollowerOnPath(renderer.transform, root.parent))) continue;
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

    /// <summary>
    /// True when every renderer under the root is an occluder/invisible. With
    /// skipInactive only renderers active under the root count, matching
    /// TryMeasureLocalFootprint: a root is judged by what the frame keeps.
    /// </summary>
    private static bool IsEntirePackingRootHidden(Transform root, bool skipInactive = false)
    {
        if (NameSuggestsHiddenGeometry(root.name) || PackingIgnore.IsMarked(root)) return true;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        bool anyCounted = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (skipInactive && !IsActiveUnder(renderers[i].transform, root)) continue;
            anyCounted = true;
            if (!RendererUsesOnlyHiddenMaterials(renderers[i])) return false;
        }
        return anyCounted;
    }

    private static bool IsHiddenPackingGeometry(Transform geometry, Transform packingRoot)
    {
        for (Transform current = geometry; current != null; current = current.parent)
        {
            if (NameSuggestsHiddenGeometry(current.name) || PackingIgnore.IsMarked(current)) return true;
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
            || lower.Contains("occlus")
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

    private struct FollowerEndpoint
    {
        public Vector3 position;
        public Vector3 localScale;
    }

    private static List<AttractionPackingFollowerPose> BuildFollowerPoses(
        List<FollowerItem> followers, List<Item> props, Transform attractionRoot,
        Vector2 authoredFootprint, Vector2 shrinkFootprint, Vector2 essentialFootprint,
        Vector2 growFootprint, float clearance, bool includeLegacyEssential = true)
    {
        var poses = new List<AttractionPackingFollowerPose>(followers.Count);
        for (int i = 0; i < followers.Count; i++)
        {
            FollowerItem follower = followers[i];
            FollowerEndpoint shrunk = FitFollower(follower, props, attractionRoot,
                shrinkFootprint, Pose.Shrunk, false, clearance);
            // Without the legacy essential variant, its endpoint is the shrink endpoint.
            FollowerEndpoint essential = includeLegacyEssential
                ? FitFollower(follower, props, attractionRoot,
                    essentialFootprint, Pose.EssentialShrunk, true, clearance)
                : shrunk;
            FollowerEndpoint grown = GrowFollower(follower, authoredFootprint, growFootprint);
            poses.Add(new AttractionPackingFollowerPose(follower.transform,
                follower.authoredPosition, shrunk.position, grown.position, essential.position,
                follower.authoredLocalScale, shrunk.localScale, grown.localScale,
                essential.localScale, follower.component.scaleMode));
        }
        return poses;
    }

    private static FollowerEndpoint FitFollower(FollowerItem follower, List<Item> props,
        Transform attractionRoot, Vector2 targetFootprint, Pose pose,
        bool essentialOnly, float clearance)
    {
        Vector2 baseRatio = new Vector2(
            Mathf.Min(1f, targetFootprint.x / Mathf.Max(0.001f, follower.size.x)),
            Mathf.Min(1f, targetFootprint.y / Mathf.Max(0.001f, follower.size.y)));
        Rect authoredRect = FollowerBoundsAt(follower, follower.authoredPosition, Vector2.one);
        FollowerEndpoint fallback = new FollowerEndpoint
        {
            position = follower.authoredPosition,
            localScale = follower.authoredLocalScale,
        };

        // Try moving the full-size object into the baked room first. Only reduce
        // scale when it cannot fit there or the move would create a new overlap.
        for (int step = 0; step <= 80; step++)
        {
            float reduction = Mathf.Max(0.001f, 1f - step / 80f);
            Vector2 ratio = ScaleFollowerAxes(follower.component.scaleMode,
                baseRatio * reduction);
            Rect rect = FollowerBoundsAt(follower, follower.authoredPosition, ratio);
            Vector2 roomHalf = targetFootprint * 0.5f;
            if (rect.half.x > roomHalf.x + 0.0001f || rect.half.y > roomHalf.y + 0.0001f)
                continue;

            Vector2 desiredCenter = new Vector2(
                Mathf.Clamp(rect.center.x, -roomHalf.x + rect.half.x, roomHalf.x - rect.half.x),
                Mathf.Clamp(rect.center.y, -roomHalf.y + rect.half.y, roomHalf.y - rect.half.y));
            fallback.position = follower.authoredPosition +
                new Vector3(desiredCenter.x - rect.center.x, 0f, desiredCenter.y - rect.center.y);
            fallback.localScale = Vector3.Scale(follower.authoredLocalScale,
                new Vector3(ratio.x,
                    follower.component.scaleMode == AttractionPackingScaleMode.XYZUniform ? ratio.x : 1f,
                    ratio.y));
            if (follower.component.avoidNewPropOverlaps &&
                !TryFindClearFollowerCenter(authoredRect, rect, desiredCenter, roomHalf,
                    props, attractionRoot, pose, essentialOnly, clearance, out desiredCenter))
                continue;
            Vector3 position = follower.authoredPosition +
                new Vector3(desiredCenter.x - rect.center.x, 0f, desiredCenter.y - rect.center.y);
            fallback.position = position;
            return fallback;
        }

        Debug.LogWarning($"[AttractionPacking] Follower '{follower.transform.name}' cannot avoid every new prop overlap at the baked minimum. Keeping it inside the room bounds.");
        return fallback;
    }

    private static FollowerEndpoint GrowFollower(FollowerItem follower,
        Vector2 authoredFootprint, Vector2 growFootprint)
    {
        Vector2 roomRatio = new Vector2(
            growFootprint.x / Mathf.Max(0.001f, authoredFootprint.x),
            growFootprint.y / Mathf.Max(0.001f, authoredFootprint.y));
        Vector2 ratio = ScaleFollowerAxes(follower.component.scaleMode, roomRatio);
        Vector3 position = ScaleFromOrigin(follower.authoredPosition, roomRatio);
        Rect rect = FollowerBoundsAt(follower, position, ratio);
        Vector2 roomHalf = growFootprint * 0.5f;
        Vector2 center = new Vector2(
            Mathf.Clamp(rect.center.x, -roomHalf.x + rect.half.x, roomHalf.x - rect.half.x),
            Mathf.Clamp(rect.center.y, -roomHalf.y + rect.half.y, roomHalf.y - rect.half.y));
        position += new Vector3(center.x - rect.center.x, 0f, center.y - rect.center.y);
        return new FollowerEndpoint
        {
            position = position,
            localScale = Vector3.Scale(follower.authoredLocalScale,
                new Vector3(ratio.x,
                    follower.component.scaleMode == AttractionPackingScaleMode.XYZUniform ? ratio.x : 1f,
                    ratio.y)),
        };
    }

    private static Vector2 ScaleFollowerAxes(AttractionPackingScaleMode mode, Vector2 ratio)
    {
        if (mode == AttractionPackingScaleMode.XZIndependent) return ratio;
        float uniform = Mathf.Min(ratio.x, ratio.y);
        return new Vector2(uniform, uniform);
    }

    private static Rect FollowerBoundsAt(FollowerItem follower, Vector3 position, Vector2 ratio)
    {
        float c = Mathf.Abs(Mathf.Cos(follower.yawRadians));
        float s = Mathf.Abs(Mathf.Sin(follower.yawRadians));
        Vector2 halfLocal = Vector2.Scale(follower.size, ratio) * 0.5f;
        Vector2 offset = Vector2.Scale(follower.offset, ratio);
        float yaw = follower.yawRadians;
        return new Rect
        {
            center = new Vector2(position.x + Mathf.Cos(yaw) * offset.x - Mathf.Sin(yaw) * offset.y,
                position.z + Mathf.Sin(yaw) * offset.x + Mathf.Cos(yaw) * offset.y),
            half = new Vector2(c * halfLocal.x + s * halfLocal.y,
                s * halfLocal.x + c * halfLocal.y),
        };
    }

    private static bool TryFindClearFollowerCenter(Rect authored, Rect footprint,
        Vector2 desired, Vector2 roomHalf, List<Item> props, Transform attractionRoot,
        Pose pose, bool essentialOnly, float clearance, out Vector2 bestCenter)
    {
        var candidates = new List<Vector2> { desired, Vector2.zero };
        Vector2 min = -roomHalf + footprint.half;
        Vector2 max = roomHalf - footprint.half;
        for (int i = 0; i < props.Count; i++)
        {
            Item prop = props[i];
            if (essentialOnly && !prop.essential) continue;
            if (Overlaps(authored, BoundsAt(prop, prop.authoredInAttraction), clearance)) continue;
            Vector3 local = pose == Pose.EssentialShrunk ? prop.essentialShrunk
                : pose == Pose.Grown ? prop.grown : prop.shrunk;
            Vector3 position = ToAttractionLocal(attractionRoot, prop.transform.parent, local);
            Rect blocker = BoundsAt(prop, position);
            float xPad = blocker.half.x + footprint.half.x + clearance + 0.001f;
            float zPad = blocker.half.y + footprint.half.y + clearance + 0.001f;
            candidates.Add(new Vector2(blocker.center.x - xPad, desired.y));
            candidates.Add(new Vector2(blocker.center.x + xPad, desired.y));
            candidates.Add(new Vector2(desired.x, blocker.center.y - zPad));
            candidates.Add(new Vector2(desired.x, blocker.center.y + zPad));
        }
        bestCenter = desired;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            Vector2 center = new Vector2(
                Mathf.Clamp(candidates[i].x, min.x, max.x),
                Mathf.Clamp(candidates[i].y, min.y, max.y));
            Rect candidate = new Rect { center = center, half = footprint.half };
            bool overlaps = false;
            for (int j = 0; j < props.Count; j++)
            {
                Item prop = props[j];
                if (essentialOnly && !prop.essential) continue;
                if (Overlaps(authored, BoundsAt(prop, prop.authoredInAttraction), clearance)) continue;
                Vector3 local = pose == Pose.EssentialShrunk ? prop.essentialShrunk
                    : pose == Pose.Grown ? prop.grown : prop.shrunk;
                Vector3 position = ToAttractionLocal(attractionRoot, prop.transform.parent, local);
                if (!Overlaps(candidate, BoundsAt(prop, position), clearance)) continue;
                overlaps = true;
                break;
            }
            if (overlaps) continue;
            float distance = (center - authored.center).sqrMagnitude;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestCenter = center;
        }
        return !float.IsPositiveInfinity(bestDistance);
    }

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

    // ---------------------------------------------------------------------
    // Separable layout (layout-frame attractions only). The runtime resolves X
    // and Z independently (ResolveAxis per axis: width can be shrinking while
    // length grows), so EVERY (sx, sz) pair must be collision-free, not only the
    // (shrink, shrink) corner the joint compactor above checks. Each axis is
    // packed on its own, then relaxed back out so a prop moves only as far as the
    // footprint really shrinks.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Shrink pose that is collision-free for any independent X/Z scale pair.
    /// Tries X-then-Z and Z-then-X and keeps the smaller footprint area. shrink is
    /// that order's symmetric footprint (capped at authored per axis); the relax
    /// pass never moves a prop outside it.
    /// </summary>
    private static Vector3[] PackSeparable(List<Item> items, HashSet<long> authoredOverlaps,
        float clearance, Vector2 authored, out Vector2 shrink)
    {
        Vector3[] xFirst = PackSeparableOrder(items, authoredOverlaps, clearance, authored, true,
            out Vector2 xShrink);
        Vector3[] zFirst = PackSeparableOrder(items, authoredOverlaps, clearance, authored, false,
            out Vector2 zShrink);
        bool useXFirst = xShrink.x * xShrink.y <= zShrink.x * zShrink.y + 0.000001f;
        shrink = useXFirst ? xShrink : zShrink;
        return useXFirst ? xFirst : zFirst;
    }

    /// <summary>
    /// One axis order. First axis: compacted and relaxed with the other axis at its
    /// authored coordinates, so props sharing a row never pass through each other.
    /// Second axis: a pair that overlaps on the first axis at ANY point of the
    /// straight authored → shrunk interpolation (linear, so this is exact) is
    /// treated as overlapping there permanently, so the second axis keeps it apart
    /// whatever the first axis's scale is.
    /// </summary>
    private static Vector3[] PackSeparableOrder(List<Item> items, HashSet<long> authoredOverlaps,
        float clearance, Vector2 authored, bool xFirst, out Vector2 shrink)
    {
        int count = items.Count;
        var authoredPositions = new Vector3[count];
        var positions = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            authoredPositions[i] = items[i].authoredInAttraction;
            positions[i] = items[i].authoredInAttraction;
        }

        CompactAxisUntilStable(items, positions, authoredOverlaps, clearance, xFirst, null);
        float firstLimit = MeasureAxisFootprint(items, positions, xFirst, AxisOf(authored, xFirst));
        RelaxAxis(items, positions, authoredPositions, authoredOverlaps, clearance, xFirst, firstLimit, null);

        bool secondX = !xFirst;
        bool[] firstAxisEver = OverlapEverOnAxis(items, authoredPositions, positions, clearance, xFirst);
        float reachClearance = Mathf.Max(0f, clearance);
        PairOverlapRule rule = (i, a, j, b) =>
            firstAxisEver[i * count + j]
            && Mathf.Abs(AxisOf(a.center, secondX) - AxisOf(b.center, secondX))
               < AxisOf(a.half, secondX) + AxisOf(b.half, secondX) + reachClearance;
        CompactAxisUntilStable(items, positions, authoredOverlaps, clearance, secondX, rule);
        float secondLimit = MeasureAxisFootprint(items, positions, secondX, AxisOf(authored, secondX));
        RelaxAxis(items, positions, authoredPositions, authoredOverlaps, clearance, secondX, secondLimit,
            firstAxisEver);

        shrink = xFirst ? new Vector2(firstLimit, secondLimit) : new Vector2(secondLimit, firstLimit);
        return positions;
    }

    private static void CompactAxisUntilStable(List<Item> items, Vector3[] positions,
        HashSet<long> authoredOverlaps, float clearance, bool xAxis, PairOverlapRule rule)
    {
        var before = new Vector3[positions.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            Array.Copy(positions, before, positions.Length);
            CompactAxis(items, positions, authoredOverlaps, false, clearance, xAxis, rule);
            float moved = 0f;
            for (int i = 0; i < positions.Length; i++)
                moved = Mathf.Max(moved, Mathf.Abs(AxisOf(positions[i], xAxis) - AxisOf(before[i], xAxis)));
            if (moved < 0.0001f) break;
        }
    }

    /// <summary>
    /// Symmetric footprint on one axis with MeasureSymmetricFootprint's rule (geometry
    /// wider than the authored room cannot set a smaller limit), capped at authored.
    /// </summary>
    private static float MeasureAxisFootprint(List<Item> items, Vector3[] positions, bool xAxis, float authored)
    {
        float extent = 0f;
        bool any = false;
        for (int i = 0; i < items.Count; i++)
        {
            Rect rect = BoundsAt(items[i], positions[i]);
            float half = AxisOf(rect.half, xAxis);
            if (half * 2f > authored + 0.001f) continue;
            extent = Mathf.Max(extent, Mathf.Abs(AxisOf(rect.center, xAxis)) + half);
            any = true;
        }
        return Mathf.Min(authored, any ? extent * 2f : authored);
    }

    /// <summary>
    /// Per pair: does it overlap on this axis (with clearance) anywhere on the
    /// straight authored → current interpolation? Yes when the order flips or
    /// either end is within reach.
    /// </summary>
    private static bool[] OverlapEverOnAxis(List<Item> items, Vector3[] authoredPositions,
        Vector3[] positions, float clearance, bool xAxis)
    {
        int count = items.Count;
        var result = new bool[count * count];
        var authoredRects = new Rect[count];
        var currentRects = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            authoredRects[i] = BoundsAt(items[i], authoredPositions[i]);
            currentRects[i] = BoundsAt(items[i], positions[i]);
        }
        float reachClearance = Mathf.Max(0f, clearance);
        for (int i = 0; i < count; i++)
        for (int j = i + 1; j < count; j++)
        {
            float reach = AxisOf(authoredRects[i].half, xAxis) + AxisOf(authoredRects[j].half, xAxis)
                + reachClearance;
            bool ever = OverlapEver(
                AxisOf(authoredRects[i].center, xAxis) - AxisOf(authoredRects[j].center, xAxis),
                AxisOf(currentRects[i].center, xAxis) - AxisOf(currentRects[j].center, xAxis),
                reach);
            result[i * count + j] = ever;
            result[j * count + i] = ever;
        }
        return result;
    }

    // Two centres' separation moves linearly from 'from' to 'to'; is it ever inside
    // reach? Exactly touching (the compactor's stopping point) does not count.
    private const float TouchTolerance = 0.00001f;
    private static bool OverlapEver(float from, float to, float reach) =>
        from * to <= 0f || Mathf.Min(Mathf.Abs(from), Mathf.Abs(to)) < reach - TouchTolerance;

    /// <summary>
    /// Move every prop back out toward its authored coordinate on one axis, as far
    /// as it can while staying inside the symmetric limit and without a new
    /// overlap, outermost-authored first, until nothing moves. Maximal compaction
    /// pulls every prop to the centre even when a few edge props hold the
    /// footprint near full size; relaxed, the rest stay spread, so interpolating
    /// authored → shrunk moves each prop only as much as the footprint shrinks.
    /// crossAxisEver null: the other axis is static (compared as it is now);
    /// otherwise a pair collides on this axis alone exactly when it says so.
    /// Uses rect footprints (conservative for floor-cutout polygons).
    /// </summary>
    private static void RelaxAxis(List<Item> items, Vector3[] positions, Vector3[] authoredPositions,
        HashSet<long> authoredOverlaps, float clearance, bool xAxis, float limit, bool[] crossAxisEver)
    {
        int count = items.Count;
        if (count == 0 || limit <= 0f) return;
        clearance = Mathf.Max(0f, clearance);
        float halfLimit = limit * 0.5f;
        var targets = new float[count];
        var reachOut = new float[count];
        var order = new int[count];
        for (int i = 0; i < count; i++)
        {
            Rect authoredRect = BoundsAt(items[i], authoredPositions[i]);
            targets[i] = AxisOf(authoredRect.center, xAxis);
            reachOut[i] = Mathf.Abs(targets[i]) + AxisOf(authoredRect.half, xAxis);
            order[i] = i;
        }
        Array.Sort(order, (a, b) =>
        {
            int byReach = reachOut[b].CompareTo(reachOut[a]);
            return byReach != 0 ? byReach : a.CompareTo(b);
        });

        var rects = new Rect[count];
        for (int pass = 0; pass < 8; pass++)
        {
            for (int i = 0; i < count; i++) rects[i] = BoundsAt(items[i], positions[i]);
            float moved = 0f;
            for (int k = 0; k < count; k++)
            {
                int i = order[k];
                float half = AxisOf(rects[i].half, xAxis);
                float low = -halfLimit + half;
                float high = halfLimit - half;
                if (low > high + 0.0001f) continue;          // wider than the limit
                float current = AxisOf(rects[i].center, xAxis);
                if (current < low - 0.0001f || current > high + 0.0001f) continue; // already outside it
                float delta = Mathf.Clamp(targets[i], Mathf.Min(low, high), Mathf.Max(low, high)) - current;
                if (Mathf.Abs(delta) < 0.00001f) continue;
                float direction = Mathf.Sign(delta);
                float allowed = Mathf.Abs(delta);
                for (int j = 0; j < count && allowed > 0f; j++)
                {
                    if (j == i || authoredOverlaps.Contains(i < j ? PairKey(i, j) : PairKey(j, i))) continue;
                    bool crossBlocks = crossAxisEver != null
                        ? crossAxisEver[i * count + j]
                        : Mathf.Abs(AxisOf(rects[i].center, !xAxis) - AxisOf(rects[j].center, !xAxis))
                          < AxisOf(rects[i].half, !xAxis) + AxisOf(rects[j].half, !xAxis) + clearance;
                    if (!crossBlocks) continue;
                    float reach = half + AxisOf(rects[j].half, xAxis) + clearance;
                    float separation = current - AxisOf(rects[j].center, xAxis);
                    if (Mathf.Abs(separation) < reach - TouchTolerance)
                    {
                        // Already within clearance: moving apart is fine, closer is not.
                        if (separation != 0f && Mathf.Sign(separation) != direction) allowed = 0f;
                        continue;
                    }
                    if (Mathf.Sign(separation) == direction) continue;
                    allowed = Mathf.Min(allowed, Mathf.Max(0f, Mathf.Abs(separation) - reach - TouchTolerance));
                }
                if (allowed <= 0f) continue;
                float step = direction * allowed;
                if (xAxis)
                {
                    positions[i].x += step;
                    rects[i].center.x += step;
                }
                else
                {
                    positions[i].z += step;
                    rects[i].center.y += step;
                }
                moved = Mathf.Max(moved, allowed);
            }
            if (moved < 0.0001f) break;
        }
    }

    /// <summary>
    /// ApplyGroupedGrowth with one grouping per axis: grown X depends only on the X
    /// scale and grown Z only on the Z scale, exactly like ResolveAxis at runtime.
    /// Where growth would bring a pair together on one axis while the other axis
    /// can overlap at some scale, that pair's groups merge on the growing axis (it
    /// grows rigidly there) until no such pair is left. Without any such pair the
    /// result equals ApplyGroupedGrowth.
    /// </summary>
    private static void ApplySeparableGrowth(Transform attractionRoot, List<Item> items, Vector2 scale,
        float clearance)
    {
        int count = items.Count;
        var groupX = new int[count];
        var groupZ = new int[count];
        var firstOfGroup = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            if (!firstOfGroup.TryGetValue(items[i].group, out int first))
            {
                first = i;
                firstOfGroup[items[i].group] = i;
            }
            groupX[i] = first;
            groupZ[i] = first;
        }

        clearance = Mathf.Max(0f, clearance);
        HashSet<long> exempt = FindOverlaps(items, 1f, false, 0f);
        HashSet<long> close = FindOverlaps(items, 1f, false, clearance);
        var authoredRects = new Rect[count];
        var shrunkRects = new Rect[count];
        var offsets = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            authoredRects[i] = BoundsAt(items[i], items[i].authoredInAttraction);
            shrunkRects[i] = BoundsAt(items[i],
                ToAttractionLocal(attractionRoot, items[i].transform.parent, items[i].shrunk));
            offsets[i] = authoredRects[i].center - new Vector2(
                items[i].authoredInAttraction.x, items[i].authoredInAttraction.z);
        }

        var grownX = new float[count];
        var grownZ = new float[count];
        for (int iteration = 0; iteration <= count * 2; iteration++)
        {
            ComputeAxisGrowth(items, groupX, scale.x, true, grownX);
            ComputeAxisGrowth(items, groupZ, scale.y, false, grownZ);
            bool merged = false;
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                long key = PairKey(i, j);
                if (exempt.Contains(key)) continue;
                float pairClearance = close.Contains(key) ? 0f : clearance;
                float reachX = authoredRects[i].half.x + authoredRects[j].half.x + pairClearance;
                float reachZ = authoredRects[i].half.y + authoredRects[j].half.y + pairClearance;
                float authoredX = authoredRects[i].center.x - authoredRects[j].center.x;
                float authoredZ = authoredRects[i].center.y - authoredRects[j].center.y;
                bool xShrink = OverlapEver(shrunkRects[i].center.x - shrunkRects[j].center.x, authoredX, reachX);
                bool zShrink = OverlapEver(shrunkRects[i].center.y - shrunkRects[j].center.y, authoredZ, reachZ);
                bool xGrow = OverlapEver(authoredX,
                    grownX[i] + offsets[i].x - grownX[j] - offsets[j].x, reachX);
                bool zGrow = OverlapEver(authoredZ,
                    grownZ[i] + offsets[i].y - grownZ[j] - offsets[j].y, reachZ);
                if (!(xShrink || xGrow) || !(zShrink || zGrow)) continue;
                if (xGrow && !xShrink && Find(groupX, i) != Find(groupX, j))
                {
                    Union(groupX, i, j);
                    merged = true;
                }
                else if (zGrow && !zShrink && Find(groupZ, i) != Find(groupZ, j))
                {
                    Union(groupZ, i, j);
                    merged = true;
                }
            }
            if (!merged) break;
        }

        for (int i = 0; i < count; i++)
            items[i].grown = ToParentLocal(attractionRoot, items[i].transform.parent,
                new Vector3(grownX[i], items[i].authoredInAttraction.y, grownZ[i]));
    }

    // ApplyGroupedGrowth's rule on one axis (centroid delta, or elastic run scaling).
    private static void ComputeAxisGrowth(List<Item> items, int[] groups, float scale, bool xAxis, float[] grown)
    {
        var members = new Dictionary<int, List<int>>();
        var roots = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            int root = Find(groups, i);
            if (!members.TryGetValue(root, out List<int> list))
            {
                list = new List<int>();
                members[root] = list;
                roots.Add(root);
            }
            list.Add(i);
        }
        for (int r = 0; r < roots.Count; r++)
        {
            List<int> group = members[roots[r]];
            float sum = 0f;
            for (int k = 0; k < group.Count; k++) sum += AxisOf(items[group[k]].authoredInAttraction, xAxis);
            float centroid = sum / group.Count;
            bool elastic = IsElasticRepeatedRun(items, group, xAxis);
            for (int k = 0; k < group.Count; k++)
            {
                float position = AxisOf(items[group[k]].authoredInAttraction, xAxis);
                grown[group[k]] = elastic ? position * scale : position + centroid * (scale - 1f);
            }
        }
    }

    /// <summary>
    /// Bake-time check of the runtime contract: on a 5 × 5 grid of independent X/Z
    /// scales (shrink, halfway, authored, halfway, grow per axis — so the mixed
    /// shrink-X/grow-Z corners are included) resolve every prop with the runtime
    /// per-axis math and look for a new overlap. Pairs intersecting as authored are
    /// exempt; pairs authored closer than the clearance only may not intersect.
    /// Returns "" or the first collision, naming the pair and the footprint.
    /// </summary>
    private static string FindGridCollision(Transform attractionRoot, List<Item> items,
        Vector2 shrinkScale, Vector2 growScale, float clearance, Vector2 authoredFootprint)
    {
        int count = items.Count;
        if (count < 2) return "";
        clearance = Mathf.Max(0f, clearance);
        HashSet<long> exempt = FindOverlaps(items, 1f, false, 0f);
        HashSet<long> close = FindOverlaps(items, 1f, false, clearance);
        float[] xs = GridScales(shrinkScale.x, growScale.x);
        float[] zs = GridScales(shrinkScale.y, growScale.y);
        var rects = new Rect[count];
        for (int xi = 0; xi < xs.Length; xi++)
        for (int zi = 0; zi < zs.Length; zi++)
        {
            float sx = xs[xi];
            float sz = zs[zi];
            for (int i = 0; i < count; i++)
            {
                Item item = items[i];
                var local = new Vector3(
                    ResolvePackingAxis(item.authored.x, item.shrunk.x, item.grown.x, sx, shrinkScale.x, growScale.x),
                    item.authored.y,
                    ResolvePackingAxis(item.authored.z, item.shrunk.z, item.grown.z, sz, shrinkScale.y, growScale.y));
                rects[i] = BoundsAt(item, ToAttractionLocal(attractionRoot, item.transform.parent, local));
            }
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                long key = PairKey(i, j);
                if (exempt.Contains(key)) continue;
                if (!Collides(items[i], rects[i], items[j], rects[j], close.Contains(key) ? 0f : clearance))
                    continue;
                Vector2 feet = Vector2.Scale(authoredFootprint, new Vector2(sx, sz)) / FeetToMeters;
                return $"‘{items[i].transform.name}’ and ‘{items[j].transform.name}’ overlap at " +
                       $"{feet.x:0.##} × {feet.y:0.##} ft (width ×{sx:0.##}, length ×{sz:0.##})";
            }
        }
        return "";
    }

    private static float[] GridScales(float shrink, float grow) => new[]
    {
        shrink, Mathf.Lerp(shrink, 1f, 0.5f), 1f, Mathf.Lerp(1f, grow, 0.5f), grow,
    };

    // A real overlap; 1 mm of float noise is tolerated.
    private static bool Collides(Item a, Rect boundsA, Item b, Rect boundsB, float clearance)
    {
        const float tolerance = 0.001f;
        if (a.footprintPolygon != null && b.footprintPolygon != null)
            return PolygonsOverlap(a.footprintPolygon, boundsA.center - RotatedOffset(a),
                b.footprintPolygon, boundsB.center - RotatedOffset(b), Mathf.Max(0f, clearance - tolerance));
        return Mathf.Abs(boundsA.center.x - boundsB.center.x) < boundsA.half.x + boundsB.half.x + clearance - tolerance
            && Mathf.Abs(boundsA.center.y - boundsB.center.y) < boundsA.half.y + boundsB.half.y + clearance - tolerance;
    }

    // Same math as AttractionTemplate.ResolveAxis (the runtime per-axis pose).
    private static float ResolvePackingAxis(float authored, float shrunk, float grown,
        float scale, float minimumScale, float maximumScale)
    {
        if (scale < 1f && minimumScale < 1f)
            return Mathf.Lerp(authored, shrunk, Mathf.InverseLerp(1f, minimumScale, scale));
        if (scale > 1f && maximumScale > 1f)
            return Mathf.Lerp(authored, grown, Mathf.InverseLerp(1f, maximumScale, scale));
        return authored;
    }

    /// <summary>
    /// The bake-time grid check run against any bake (frame-less included), with
    /// each prop's footprint rebuilt from the live hierarchy. Diagnostics and tests.
    /// </summary>
    internal static string FindLayoutCollision(AttractionTemplate attraction, AttractionPackingBake bake)
    {
        if (attraction == null || bake == null) return "";
        Transform root = attraction.transform;
        var items = new List<Item>();
        for (int i = 0; i < bake.Props.Count; i++)
        {
            AttractionPropPackingPose pose = bake.Props[i];
            Transform target = pose?.Prop;
            if (target == null) continue;
            Vector2 size;
            Vector2 offset;
            PropTemplate template = target.GetComponent<PropTemplate>();
            if (template != null)
            {
                size = template.FootprintMeters;
                offset = template.footprintOffsetMeters;
            }
            else if (!TryMeasureLocalFootprint(target, out size, out offset, false, true))
            {
                continue;
            }
            Vector3 authoredInAttraction = ToAttractionLocal(root, target.parent, pose.AuthoredLocalPosition);
            Vector3 relativeForward = root.InverseTransformDirection(target.forward);
            items.Add(new Item
            {
                template = template,
                transform = target,
                authored = pose.AuthoredLocalPosition,
                authoredInAttraction = authoredInAttraction,
                shrunk = pose.ShrunkLocalPosition,
                grown = pose.GrownLocalPosition,
                essentialShrunk = pose.EssentialShrunkLocalPosition,
                size = size,
                offset = offset,
                yawRadians = Mathf.Atan2(relativeForward.x, relativeForward.z),
                footprintPolygon = GetFloorCutoutPolygon(root, target, authoredInAttraction),
                group = -1,
            });
        }
        return FindGridCollision(root, items, bake.ShrinkScale, bake.GrowScale,
            attraction.shrinkClearanceMeters, bake.AuthoredFootprintMeters);
    }

    private static float AxisOf(Vector2 value, bool xAxis) => xAxis ? value.x : value.y;
    private static float AxisOf(Vector3 value, bool xAxis) => xAxis ? value.x : value.z;

    private static void CompactAxis(
        List<Item> items,
        Vector3[] positions,
        HashSet<long> authoredOverlaps,
        bool essentialOnly,
        float clearance,
        bool xAxis,
        PairOverlapRule rule = null)
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
                essentialOnly, clearance, xAxis, rule);
            ApplyGroupTravel(items, positions, groups, translations, essentialOnly, xAxis, safeTravel);
            if (safeTravel >= 0.9999f) return;

            Dictionary<int, float> nextTranslations = BuildAxisTranslations(
                items, positions, groups, essentialOnly, xAxis);
            if (!MergeBlockingGroups(
                    items, positions, groups, nextTranslations, authoredOverlaps,
                    essentialOnly, clearance, xAxis, rule))
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
        bool xAxis,
        PairOverlapRule rule = null)
    {
        const int sweepSteps = 64;
        float previous = 0f;
        for (int step = 1; step <= sweepSteps; step++)
        {
            float candidate = (float)step / sweepSteps;
            if (!HasNewGroupedOverlap(
                    items, positions, groups, translations, authoredOverlaps,
                    essentialOnly, clearance, xAxis, candidate, rule))
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
                        essentialOnly, clearance, xAxis, mid, rule))
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
        bool xAxis,
        PairOverlapRule rule = null)
    {
        const float probeTravel = 0.01f;
        var merges = new List<Vector2Int>();
        // Groups and probe rects depend only on the item: computed once, not per pair.
        int count = items.Count;
        var itemGroups = new int[count];
        var rects = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            itemGroups[i] = Find(groups, i);
            rects[i] = BoundsAt(items[i], TravelPosition(
                positions[i], translations[itemGroups[i]], xAxis, probeTravel));
        }
        for (int i = 0; i < count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            int groupA = itemGroups[i];
            Rect a = rects[i];
            for (int j = i + 1; j < count; j++)
            {
                if (essentialOnly && !items[j].essential) continue;
                int groupB = itemGroups[j];
                if (groupA == groupB) continue;
                Rect b = rects[j];
                if (!(rule != null ? rule(i, a, j, b) : ItemsOverlap(items[i], a, items[j], b, clearance))) continue;
                if (authoredOverlaps.Contains(PairKey(i, j))) continue;
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
        float travel,
        PairOverlapRule rule = null)
    {
        // Each item's group and rect depend only on the item, so they are computed
        // once per call instead of once per pair (this runs O(sweep × merges) times).
        int count = items.Count;
        var itemGroups = new int[count];
        var rects = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            itemGroups[i] = Find(groups, i);
            rects[i] = BoundsAt(items[i], TravelPosition(
                positions[i], translations[itemGroups[i]], xAxis, travel));
        }
        for (int i = 0; i < count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            int groupA = itemGroups[i];
            Rect a = rects[i];
            for (int j = i + 1; j < count; j++)
            {
                if (essentialOnly && !items[j].essential) continue;
                if (groupA == itemGroups[j]) continue;
                Rect b = rects[j];
                // Geometry first: nearly every pair is apart, and the set lookup costs more.
                if (!(rule != null ? rule(i, a, j, b) : ItemsOverlap(items[i], a, items[j], b, clearance))) continue;
                if (!authoredOverlaps.Contains(PairKey(i, j))) return true;
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
                if (ItemsOverlap(items[i], a, items[j], b, clearance)) overlaps.Add(PairKey(i, j));
            }
        }
        return overlaps;
    }

    private static bool HasNewOverlap(List<Item> items, Vector2 scale, bool essentialOnly, float clearance, HashSet<long> ignored)
    {
        int count = items.Count;
        var rects = new Rect[count];
        for (int i = 0; i < count; i++)
            rects[i] = BoundsAt(items[i], ScaleFromOrigin(items[i].authoredInAttraction, scale));
        for (int i = 0; i < count; i++)
        {
            if (essentialOnly && !items[i].essential) continue;
            Rect a = rects[i];
            for (int j = i + 1; j < count; j++)
            {
                if (essentialOnly && !items[j].essential) continue;
                if (!ItemsOverlap(items[i], a, items[j], rects[j], clearance)) continue;
                if (!ignored.Contains(PairKey(i, j))) return true;
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

    private static void ApplyGroupedGrowth(
        Transform attractionRoot,
        List<Item> items,
        Vector2 scale)
    {
        var sums = new Dictionary<int, Vector2>();
        var counts = new Dictionary<int, int>();
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < items.Count; i++)
        {
            int group = items[i].group;
            Vector2 p = new Vector2(items[i].authoredInAttraction.x, items[i].authoredInAttraction.z);
            sums[group] = sums.TryGetValue(group, out Vector2 sum) ? sum + p : p;
            counts[group] = counts.TryGetValue(group, out int count) ? count + 1 : 1;
            if (!members.TryGetValue(group, out List<int> groupMembers))
            {
                groupMembers = new List<int>();
                members[group] = groupMembers;
            }
            groupMembers.Add(i);
        }

        var deltas = new Dictionary<int, Vector2>();
        var elasticAxes = new Dictionary<int, Vector2Int>();
        foreach (var pair in sums)
        {
            Vector2 centroid = pair.Value / counts[pair.Key];
            deltas[pair.Key] = new Vector2(
                centroid.x * (scale.x - 1f),
                centroid.y * (scale.y - 1f));
            List<int> groupMembers = members[pair.Key];
            elasticAxes[pair.Key] = new Vector2Int(
                IsElasticRepeatedRun(items, groupMembers, true) ? 1 : 0,
                IsElasticRepeatedRun(items, groupMembers, false) ? 1 : 0);
        }

        for (int i = 0; i < items.Count; i++)
        {
            Vector2 delta = deltas[items[i].group];
            Vector3 p = items[i].authoredInAttraction;
            // A compact mixed cluster moves rigidly by its centroid. A repeated,
            // aligned run (coins, lights, arrows, queue markers) is different:
            // keeping the entire run rigid preserves every internal gap and only
            // moves the trail as a block. Scale each centre on the run axis so
            // the individual props retain their size/alignment while their
            // spacing expands to occupy the requested hallway length.
            Vector2Int elastic = elasticAxes[items[i].group];
            float grownX = elastic.x != 0 ? p.x * scale.x : p.x + delta.x;
            float grownZ = elastic.y != 0 ? p.z * scale.y : p.z + delta.y;
            Vector3 grownInAttraction = new Vector3(grownX, p.y, grownZ);
            items[i].grown = ToParentLocal(attractionRoot, items[i].transform.parent, grownInAttraction);
        }
    }

    private static bool IsElasticRepeatedRun(
        List<Item> items,
        List<int> members,
        bool xAxis)
    {
        if (members == null || members.Count < 3) return false;

        string family = GrowthFamilyName(items[members[0]].transform.name);
        if (string.IsNullOrEmpty(family)) return false;

        float minAlong = float.PositiveInfinity;
        float maxAlong = float.NegativeInfinity;
        float minCross = float.PositiveInfinity;
        float maxCross = float.NegativeInfinity;
        for (int i = 0; i < members.Count; i++)
        {
            Item item = items[members[i]];
            if (!string.Equals(
                    family,
                    GrowthFamilyName(item.transform.name),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            Vector3 p = item.authoredInAttraction;
            float along = xAxis ? p.x : p.z;
            float cross = xAxis ? p.z : p.x;
            minAlong = Mathf.Min(minAlong, along);
            maxAlong = Mathf.Max(maxAlong, along);
            minCross = Mathf.Min(minCross, cross);
            maxCross = Mathf.Max(maxCross, cross);
        }

        float alongSpan = maxAlong - minAlong;
        float crossSpan = maxCross - minCross;
        return alongSpan > 0.001f
            && alongSpan > crossSpan * 2f;
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
            // Use the packed pose, even when the authored prop overhangs the
            // original room. Its shrink pose may have moved fully inside.
            // Geometry wider than the authored room cannot set a smaller limit.
            if (rect.half.x * 2f <= fallback.x + 0.001f)
            {
                extentX = Mathf.Max(extentX, Mathf.Abs(rect.center.x) + rect.half.x);
                anyX = true;
            }
            if (rect.half.y * 2f <= fallback.y + 0.001f)
            {
                extentZ = Mathf.Max(extentZ, Mathf.Abs(rect.center.y) + rect.half.y);
                anyZ = true;
            }
        }
        return new Vector2(anyX ? extentX * 2f : fallback.x, anyZ ? extentZ * 2f : fallback.y);
    }

    private static Rect BoundsAt(Item item, Vector3 localPosition)
    {
        return new Rect
        {
            center = new Vector2(localPosition.x, localPosition.z) + item.RotatedOffset,
            half = item.Half,
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

    private static bool ItemsOverlap(Item a, Rect boundsA, Item b, Rect boundsB, float clearance)
    {
        if (a.footprintPolygon != null && b.footprintPolygon != null)
            return PolygonsOverlap(
                a.footprintPolygon, boundsA.center - RotatedOffset(a),
                b.footprintPolygon, boundsB.center - RotatedOffset(b),
                clearance);
        return Overlaps(boundsA, boundsB, clearance);
    }

    private static Vector2 RotatedOffset(Item item) => item.RotatedOffset;

    private static bool PolygonsOverlap(
        List<Vector2> a,
        Vector2 positionA,
        List<Vector2> b,
        Vector2 positionB,
        float clearance)
    {
        for (int i = 0; i < a.Count; i++)
        {
            Vector2 a0 = a[i] + positionA;
            Vector2 a1 = a[(i + 1) % a.Count] + positionA;
            for (int j = 0; j < b.Count; j++)
            {
                Vector2 b0 = b[j] + positionB;
                Vector2 b1 = b[(j + 1) % b.Count] + positionB;
                if (SegmentsIntersect(a0, a1, b0, b1)) return true;
                if (clearance > 0f
                    && SegmentDistanceSquared(a0, a1, b0, b1) < clearance * clearance)
                    return true;
            }
        }
        return PointInPolygon(a[0] + positionA, b, positionB)
            || PointInPolygon(b[0] + positionB, a, positionA);
    }

    private static bool PointInPolygon(Vector2 point, List<Vector2> polygon, Vector2 position)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector2 a = polygon[i] + position;
            Vector2 b = polygon[j] + position;
            if ((a.y > point.y) != (b.y > point.y)
                && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    private static bool SegmentsIntersect(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        float d1 = Cross(a1 - a0, b0 - a0);
        float d2 = Cross(a1 - a0, b1 - a0);
        float d3 = Cross(b1 - b0, a0 - b0);
        float d4 = Cross(b1 - b0, a1 - b0);
        return d1 * d2 < -0.0000001f && d3 * d4 < -0.0000001f;
    }

    private static float SegmentDistanceSquared(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        return Mathf.Min(
            Mathf.Min(PointSegmentDistanceSquared(a0, b0, b1), PointSegmentDistanceSquared(a1, b0, b1)),
            Mathf.Min(PointSegmentDistanceSquared(b0, a0, a1), PointSegmentDistanceSquared(b1, a0, a1)));
    }

    private static float PointSegmentDistanceSquared(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= Mathf.Epsilon) return (point - start).sqrMagnitude;
        float t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
        return (point - (start + segment * t)).sqrMagnitude;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

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

    internal static string PathFrom(Transform root, Transform child)
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
    // Test seam: the same entry point Unity calls before writing assets.
    internal static string[] ProcessWillSaveForTests(string[] paths) => OnWillSaveAssets(paths);

    // Test seam: stands in for DreamParkAutomation.IsActive, so a batch-mode test can
    // exercise the interactive path.
    internal static bool? AutomationOverrideForTests;

    // Owners (Content Uploader panels) with a publish running. A destroyed owner drops out.
    private static readonly HashSet<UnityEngine.Object> publishOwners = new HashSet<UnityEngine.Object>();

    /// <summary>
    /// Marks a GUI publish as running (or finished) for this owner. While any is running,
    /// saves don't bake, exactly like an automated command.
    /// </summary>
    internal static void SetPublishing(UnityEngine.Object owner, bool publishing)
    {
        if (ReferenceEquals(owner, null)) return;
        if (publishing) publishOwners.Add(owner);
        else publishOwners.Remove(owner);
    }

    internal static bool PublishInProgress
    {
        get
        {
            publishOwners.RemoveWhere(owner => owner == null);
            return publishOwners.Count > 0;
        }
    }

    internal static bool SkipBakeForAutomation() =>
        (AutomationOverrideForTests ?? DreamParkAutomation.IsActive) || PublishInProgress;

    private static string[] OnWillSaveAssets(string[] paths)
    {
        // A layout-frame edit state must never be saved as Base. When the session's
        // prefab or scene is being saved, its overrides are written and Base is put
        // back BEFORE the bake below; the session resumes after the save.
        AttractionLayoutFrameEditSession.PrepareForSave(paths);
        if (AttractionPackingBaker.AutoBakeInProgress) return paths;

        // Automated commands (publish, preflight, batch mode) never bake from a save.
        // A publish bakes every attraction once, explicitly (BakeAllInContent), and
        // its other saves (scene saves, Addressables settings, the gameId/Beta stamping
        // pass and its restore, which re-save every prefab) would each re-bake every
        // dirty attraction here. On Zombiez (17 levels, 10 layouts and 4 wall layers
        // each) that held the main thread for over an hour at "Preparing release"
        // (Oct 10 2026). The same holds for a GUI Compile & Upload (PublishInProgress).
        // The author's own Prefab Mode saves outside a publish still bake.
        if (SkipBakeForAutomation()) return paths;

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
    private static readonly Dictionary<int, Mesh> packingGhostSkinnedMeshes = new Dictionary<int, Mesh>();

    private void OnDisable()
    {
        if (packingGhostMaterial != null)
        {
            DestroyImmediate(packingGhostMaterial);
            packingGhostMaterial = null;
        }
        foreach (Mesh mesh in packingGhostSkinnedMeshes.Values)
            if (mesh != null) DestroyImmediate(mesh);
        packingGhostSkinnedMeshes.Clear();
        AttractionLayoutFramesInspector.DisposeCoverageCache();
    }

    public override void OnInspectorGUI()
    {
        var attraction = (AttractionTemplate)target;
        // Sticky frame-edit panel first, so Save Frame / Cancel are always in reach.
        bool editingFrame = AttractionLayoutFramesInspector.DrawEditSessionPanel(attraction);
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Flexible Packing Bake", EditorStyles.boldLabel);

        // While a frame is edited the hierarchy IS that frame: nothing may bake it.
        using (new EditorGUI.DisabledScope(editingFrame))
            DrawPackingBakeSettings(attraction);

        AttractionLayoutFramesInspector.DrawLayoutFramesSection(attraction, this);
        AttractionWallLayersInspector.DrawSection(attraction);
        AttractionRectOpeningsInspector.DrawSection(attraction, serializedObject);
        AttractionPortalsEditor.DrawInspectorSection(attraction);
        if (editingFrame) return;

        if (attraction.HasPackingBake)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Editor Debug Preview", EditorStyles.boldLabel);
            // One preview for every attraction: an attraction without frames is simply
            // a ladder with only its Base frame.
            EditorGUILayout.HelpBox(
                Application.isPlaying
                    ? "Editor-only Play Mode override: the chosen frame is applied to the live props at this size. " +
                      "The changes are discarded when Play Mode ends."
                    : "Debug visualization only in Edit Mode: ghosts show the chosen frame at this size; the authored " +
                      "hierarchy is unchanged. Enter Play Mode to exercise it on the live props.",
                MessageType.None);
            AttractionLayoutFramesInspector.DrawFramesDebugPreview(attraction);
        }
        else
        {
            EditorGUILayout.HelpBox("No flexible packing data has been baked. Runtime falls back to the authored rectangle.", MessageType.Warning);
        }

        if (GUILayout.Button("Bake Packing Variants"))
        {
            AttractionPackingBaker.BakeIntoAsset(attraction);
            ResetDebugPreview(attraction);
            AssetDatabase.SaveAssets();
            SceneView.RepaintAll();
        }
        if (GUILayout.Button(attraction.HasLayoutFrames
                ? "Reset Debug Preview to Base (as authored)"
                : "Reset Debug Preview to 1.0 (Authored)"))
        {
            Undo.RecordObject(attraction, "Reset attraction packing preview");
            ResetDebugPreview(attraction);
            EditorUtility.SetDirty(attraction);
            SceneView.RepaintAll();
        }
    }

    private static void ResetDebugPreview(AttractionTemplate attraction)
    {
        if (attraction.HasLayoutFrames && !Application.isPlaying)
        {
            // Frame previews are ghosts in Edit Mode: clear the preview only, never
            // rewrite the authored hierarchy from the last bake's Base states.
            attraction.SetLayoutPreview(Vector2.zero, "");
            attraction.SetPackingPreview(Vector2.one, false);
            return;
        }
        attraction.ResetPackingVariant();
    }

    private void DrawPackingBakeSettings(AttractionTemplate attraction)
    {
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

        if (attraction.HasStalePackingBake)
        {
            AttractionPackingBake stale = attraction.PackingBake;
            EditorGUILayout.HelpBox(
                $"Packing bake is out of date. It was baked for {FormatFeet(stale.RequestedFootprintMeters)}, " +
                $"but the attraction is now {FormatFeet(attraction.DimensionsInFeet * 0.3048f)} " +
                $"and/or its maximum multipliers changed. Re-bake to refresh the layout and debug ranges.",
                MessageType.Warning);
        }

        if (attraction.HasPackingBake && attraction.HasLayoutFrames)
        {
            // The honest ladder: what the park can actually do with every frame.
            AttractionPackingBake bake = attraction.PackingBake;
            List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
            AttractionPackingBaker.GetSizeLine(ladder, out Vector2 smallest, out Vector2 largest);
            string smallestFrame = "";
            for (int i = 0; i < ladder.Count; i++)
                if (ladder[i].shrinkMeters == smallest) { smallestFrame = ladder[i].name; break; }
            EditorGUILayout.HelpBox(
                $"Base: {FormatFeet(bake.AuthoredFootprintMeters)} (as authored)\n" +
                $"Base range: {FormatFeet(bake.ShrinkFootprintMeters)} → {FormatFeet(bake.GrowFootprintMeters)}\n" +
                $"Smallest achievable: {FormatFeet(smallest)} ({smallestFrame})\n" +
                $"Largest: {FormatFeet(largest)}",
                MessageType.Info);
        }
        else if (attraction.HasPackingBake)
        {
            AttractionPackingBake bake = attraction.PackingBake;
            EditorGUILayout.HelpBox(
                $"Original: {FormatFeet(bake.AuthoredFootprintMeters)}\n" +
                $"Minimum Footprint: {FormatFeet(bake.ShrinkFootprintMeters)} " +
                $"{FormatPercentDifference(bake.ShrinkFootprintMeters, bake.AuthoredFootprintMeters)}\n" +
                $"Maximum Footprint: {FormatFeet(bake.GrowFootprintMeters)} " +
                $"{FormatPercentDifference(bake.GrowFootprintMeters, bake.AuthoredFootprintMeters)}",
                MessageType.Info);
        }
    }

    private void OnSceneGUI()
    {
        var attraction = (AttractionTemplate)target;
        if (attraction != null) AttractionPortalsEditor.DrawSceneHandles(attraction);
        if (attraction != null) AttractionWallLayersInspector.DrawSceneGhosts(attraction);
        if (attraction != null) AttractionRectOpeningsInspector.DrawSceneGizmo(attraction);
        if (attraction == null || !attraction.ShowPackingGizmos) return;
        // While a frame is edited the live hierarchy IS that frame; the edit session
        // draws its own frame rectangle instead of ghosts.
        if (AttractionLayoutFrameEditSession.IsEditing(attraction)) return;
        if (attraction.HasLayoutFrames)
        {
            DrawLayoutFrameGizmos(attraction);
            return;
        }

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

    /// <summary>
    /// Footprints of the frame the debug preview resolves to (its authored, safe,
    /// shrink and grow rectangles plus the previewed footprint), then its ghosts.
    /// </summary>
    private static void DrawLayoutFrameGizmos(AttractionTemplate attraction)
    {
        Matrix4x4 previous = Handles.matrix;
        Color previousColor = Handles.color;
        Handles.matrix = attraction.transform.localToWorldMatrix;

        attraction.TryResolveLayoutPreview(out string frameId, out Vector2 frameScale);
        if (attraction.TryGetFrameEnvelope(frameId, out AttractionFrameEnvelope envelope))
        {
            string name = string.IsNullOrEmpty(envelope.name) ? envelope.frameId : envelope.name;
            float remaining = 1f - Mathf.Clamp(attraction.safeAreaInset, 0f, 0.49f) * 2f;
            DrawFootprint(envelope.authoredMeters, 0.01f, new Color(1f, 1f, 1f, 0.55f), $"{name} / 1.0");
            if (remaining < 1f)
                DrawFootprint(envelope.authoredMeters * remaining, 0.03f, new Color(0.1f, 0.9f, 1f, 0.9f), "Safe");
            DrawFootprint(envelope.shrinkMeters, 0.05f, new Color(0.2f, 0.55f, 1f, 0.95f), $"{name} minimum shrink");
            DrawFootprint(envelope.growMeters, 0.07f, new Color(0.25f, 1f, 0.35f, 0.9f), $"{name} baked maximum");
            DrawFootprint(attraction.GetFrameFootprintMeters(frameId, frameScale), 0.12f,
                new Color(1f, 0.75f, 0.05f, 1f), "Debug preview");
        }
        else
        {
            DrawFootprint(attraction.DimensionsInFeet * 0.3048f, 0.01f, new Color(1f, 1f, 1f, 0.55f), "Authored / 1.0");
        }

        Handles.matrix = previous;
        DrawPackingGhosts(attraction);
        Handles.color = previousColor;
    }

    /// <summary>
    /// Ghosts of the frame the debug preview resolves to, placed from serialized data
    /// only (frame overrides + that frame's bake). Objects inactive in that frame are
    /// not drawn. Play Mode applies the preview to the live props instead.
    /// </summary>
    private static void DrawLayoutFrameGhosts(AttractionTemplate attraction)
    {
        if (Application.isPlaying || Event.current.type != EventType.Repaint) return;
        if (!AttractionLayoutFrameGhostState.TryCreate(attraction, out AttractionLayoutFrameGhostState ghost)) return;
        Material material = GetPackingGhostMaterial();
        if (material == null) return;

        Color color = ghost.IsBase
            ? new Color(1f, 0.72f, 0.05f, 0.2f)
            : new Color(0.2f, 0.85f, 1f, 0.22f);
        Func<Transform, bool> isActive = ghost.IsActive;
        IReadOnlyList<AttractionPropPackingPose> poses = ghost.Bake.Props;
        for (int i = 0; i < poses.Count; i++)
        {
            AttractionPropPackingPose pose = poses[i];
            Transform root = pose?.Prop;
            if (root == null || !pose.AuthoredActive || !ghost.IsActive(root)) continue;
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
                if (!ShouldDrawGhostRenderer(renderer, root, isActive)) continue;
                Matrix4x4 matrix = ghost.WorldMatrix(filter.transform);
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
                if (renderer.sharedMesh == null) continue;
                foundMesh = true;
                if (!ShouldDrawGhostRenderer(renderer, root, isActive)) continue;
                Mesh mesh = GetPackingGhostSkinnedMesh(renderer);
                if (mesh == null) continue;
                Matrix4x4 matrix = ghost.WorldMatrix(renderer.transform);
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
                Handles.matrix = ghost.WorldMatrix(root);
                Handles.color = new Color(color.r, color.g, color.b, 0.8f);
                Handles.DrawWireCube(Vector3.zero, new Vector3(pose.FootprintMeters.x, 0.05f, pose.FootprintMeters.y));
                Handles.matrix = previous;
            }
        }
    }

    private static void DrawPackingGhosts(AttractionTemplate attraction)
    {
        if (attraction.HasLayoutFrames)
        {
            DrawLayoutFrameGhosts(attraction);
            return;
        }
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
                if (renderer.sharedMesh == null) continue;
                foundMesh = true;
                if (!ShouldDrawGhostRenderer(renderer, root)) continue;
                Mesh mesh = GetPackingGhostSkinnedMesh(renderer);
                if (mesh == null) continue;
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

    private static Mesh GetPackingGhostSkinnedMesh(SkinnedMeshRenderer renderer)
    {
        int instanceId = renderer.GetInstanceID();
        if (!packingGhostSkinnedMeshes.TryGetValue(instanceId, out Mesh mesh) || mesh == null)
        {
            mesh = new Mesh
            {
                name = renderer.name + " Packing Ghost",
                hideFlags = HideFlags.HideAndDontSave,
            };
            packingGhostSkinnedMeshes[instanceId] = mesh;
        }

        // sharedMesh is the undeformed bind pose. Bake the current bone/blend-shape
        // result so a scale-1 ghost exactly overlays the live skinned character.
        // useScale=true compensates for the often-large scale on imported rigs;
        // renderer.localToWorldMatrix can then place it like an ordinary mesh.
        renderer.BakeMesh(mesh, true);
        return mesh;
    }

    private static bool ShouldDrawGhostRenderer(Renderer renderer, Transform propRoot,
        Func<Transform, bool> isActive = null)
    {
        if (renderer == null || !renderer.enabled || renderer.forceRenderingOff) return false;
        // Frame ghosts ask whether the renderer is active in THAT frame, not in Base.
        if (isActive != null ? !isActive(renderer.transform) : !renderer.gameObject.activeInHierarchy) return false;
        if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return false;

        for (Transform current = renderer.transform; current != null; current = current.parent)
        {
            if (NameSuggestsHiddenGeometry(current.name) || PackingIgnore.IsMarked(current)) return false;
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
            || lower.Contains("occlus")
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

/// <summary>
/// Objects the packing bake (footprint, shrink/grow, frame ghosts) must not measure:
/// debug overlays, gizmo meshes, helper visuals that draw the layout rather than BEING
/// the layout. Until Oct 2026 the only way to say so was to put "gizmo" in the name
/// (LayoutLab REPORT §5.6), which still works. Two explicit conventions, no component
/// (nothing for creators to add or configure):
///   • the GameObject's name contains "[nopack]" (any case), e.g. "Layout Overlay [nopack]";
///   • a LuaBehaviour on it runs a script whose source contains "-- @packing-ignore"
///     (the same annotation style as "-- @editor-only"), so a script-drawn overlay
///     marks itself.
/// Either marks the object AND everything under it.
/// </summary>
public static class PackingIgnore
{
    public const string NameToken = "[nopack]";
    public const string LuaMarker = "@packing-ignore";

    public static bool IsMarked(Transform t)
    {
        if (t == null) return false;
        if (t.name.IndexOf(NameToken, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var scripts = t.GetComponents<LuaBehaviour>();
        for (int i = 0; i < scripts.Length; i++)
        {
            var src = scripts[i] != null ? scripts[i].luaScript : null;
            if (src != null && ScriptHasMarker(src.text)) return true;
        }
        return false;
    }

    /// <summary>The marker must be in a Lua comment ("-- @packing-ignore"), not just any string.</summary>
    public static bool ScriptHasMarker(string source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        int at = source.IndexOf(LuaMarker, System.StringComparison.Ordinal);
        while (at >= 0)
        {
            int lineStart = source.LastIndexOf('\n', at) + 1;
            int dash = source.IndexOf("--", lineStart, at - lineStart, System.StringComparison.Ordinal);
            if (dash >= 0) return true;
            at = source.IndexOf(LuaMarker, at + LuaMarker.Length, System.StringComparison.Ordinal);
        }
        return false;
    }
}
