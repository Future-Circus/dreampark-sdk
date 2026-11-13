using DreamPark;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class AttractionPackingTests
{
    private GameObject _root;
    private string _temporaryPrefabPath;

    [TearDown]
    public void TearDown()
    {
        if (_root != null) Object.DestroyImmediate(_root);
        if (!string.IsNullOrEmpty(_temporaryPrefabPath))
            AssetDatabase.DeleteAsset(_temporaryPrefabPath);
    }

    [Test]
    public void BakeIntoAsset_UsesUnsavedInspectorMaximumsForBakeAndPreviewRange()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        attraction.maxGrowthScale = new Vector2(1.25f, 1.25f);
        _temporaryPrefabPath = AssetDatabase.GenerateUniqueAssetPath(
            "Assets/__AttractionPackingBakeIntoAssetTest.prefab");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(_root, _temporaryPrefabPath);
        Object.DestroyImmediate(_root);
        _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        attraction = _root.GetComponent<AttractionTemplate>();
        attraction.maxGrowthScale = new Vector2(1.6f, 1.1f);

        Assert.That(AttractionPackingBaker.BakeIntoAsset(attraction), Is.True);

        AttractionTemplate saved = AssetDatabase.LoadAssetAtPath<GameObject>(_temporaryPrefabPath)
            .GetComponent<AttractionTemplate>();
        Assert.That(saved.maxGrowthScale.x, Is.EqualTo(1.6f).Within(0.001f));
        Assert.That(saved.maxGrowthScale.y, Is.EqualTo(1.1f).Within(0.001f));
        Assert.That(saved.PackingBake.GrowScale.x, Is.EqualTo(1.6f).Within(0.001f));
        Assert.That(saved.PackingBake.GrowScale.y, Is.EqualTo(1.1f).Within(0.001f));
    }

    [Test]
    public void BakeIntoAsset_RefreshesBakeAfterUnsavedCustomSizeChange()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        AttractionPackingBaker.Bake(attraction, false);
        _temporaryPrefabPath = AssetDatabase.GenerateUniqueAssetPath(
            "Assets/__AttractionPackingCustomSizeTest.prefab");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(_root, _temporaryPrefabPath);
        Object.DestroyImmediate(_root);
        _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        attraction = _root.GetComponent<AttractionTemplate>();
        attraction.customSize = new Vector2(30f, 12f);
        attraction.maxGrowthScale = new Vector2(1.4f, 1.2f);
        Assert.That(attraction.HasPackingBake, Is.False,
            "A bake made for the previous custom size must not drive the changed attraction.");
        Assert.That(attraction.HasStalePackingBake, Is.True);

        Assert.That(AttractionPackingBaker.BakeIntoAsset(attraction), Is.True);

        AttractionTemplate saved = AssetDatabase.LoadAssetAtPath<GameObject>(_temporaryPrefabPath)
            .GetComponent<AttractionTemplate>();
        Assert.That(saved.customSize, Is.EqualTo(new Vector2(30f, 12f)));
        Assert.That(saved.PackingBake.AuthoredFootprintMeters.x, Is.EqualTo(30f * 0.3048f).Within(0.001f));
        Assert.That(saved.PackingBake.AuthoredFootprintMeters.y, Is.EqualTo(12f * 0.3048f).Within(0.001f));
        Assert.That(saved.PackingBake.GrowScale.x, Is.EqualTo(1.4f).Within(0.001f));
        Assert.That(saved.PackingBake.GrowScale.y, Is.EqualTo(1.2f).Within(0.001f));
        Assert.That(saved.HasPackingBake, Is.True);
    }

    [Test]
    public void BakeIntoAsset_CopiesUnsavedFollowerModeIntoPrefab()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        CreateFollower("Border", AttractionPackingScaleMode.XZIndependent);
        _temporaryPrefabPath = AssetDatabase.GenerateUniqueAssetPath(
            "Assets/__AttractionPackingFollowerBakeTest.prefab");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(_root, _temporaryPrefabPath);
        Object.DestroyImmediate(_root);
        _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        attraction = _root.GetComponent<AttractionTemplate>();
        AttractionPackingFollower follower = _root.GetComponentInChildren<AttractionPackingFollower>();
        follower.scaleMode = AttractionPackingScaleMode.XYZUniform;
        Assert.That(AttractionPackingBaker.BakeIntoAsset(attraction), Is.True);

        GameObject saved = AssetDatabase.LoadAssetAtPath<GameObject>(_temporaryPrefabPath);
        Assert.That(saved.GetComponentInChildren<AttractionPackingFollower>().scaleMode,
            Is.EqualTo(AttractionPackingScaleMode.XYZUniform));
        Assert.That(saved.GetComponent<AttractionTemplate>().PackingBake.Followers.Count,
            Is.EqualTo(1));
    }

    [Test]
    public void Bake_ShrinksWithoutOverlap_AndBuildsEssentialVariant()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.essentialProps.Add(left.transform);
        attraction.shrinkClearanceMeters = 0.1f;

        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
        Assert.That(attraction.HasPackingBake, Is.True);
        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(2));

        AttractionPropPackingPose leftPose = attraction.PackingBake.Props[0];
        AttractionPropPackingPose rightPose = attraction.PackingBake.Props[1];
        float centreGap = Mathf.Abs(leftPose.ShrunkLocalPosition.x - rightPose.ShrunkLocalPosition.x);
        Assert.That(centreGap, Is.GreaterThanOrEqualTo(1.099f));
        Assert.That(attraction.PackingBake.EssentialShrinkFootprintMeters.x, Is.EqualTo(1f).Within(0.01f));

        Assert.That(attraction.ApplyPackingVariant(attraction.PackingBake.EssentialShrinkScale, true), Is.True);
        Assert.That(left.gameObject.activeSelf, Is.True);
        Assert.That(right.gameObject.activeSelf, Is.False);
        Assert.That(left.transform.localPosition.x, Is.EqualTo(0f).Within(0.01f));

        attraction.ResetPackingVariant();
        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(left.transform.localPosition.x, Is.EqualTo(-1f).Within(0.01f));
    }

    [Test]
    public void Bake_MovesOverhangingPropInwardAndShrinksFloorFollowerOnBothAxes()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.customSize = new Vector2(12f, 18f);
        PropTemplate control = CreatePropAt("Overlay Controls", -1.72f, 0f);
        control.customFootprintMeters = new Vector2(0.36f, 0.75f);
        Transform floor = CreateFollower("Sequence Floor Border",
            AttractionPackingScaleMode.XZIndependent);
        floor.localScale = new Vector3(0.36576f, 1f, 0.54864f);
        floor.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;

        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
        Assert.That(attraction.PackingBake.Props[0].ShrunkLocalPosition.x,
            Is.EqualTo(0f).Within(0.001f));
        Assert.That(attraction.PackingBake.ShrinkFootprintMeters.x,
            Is.EqualTo(0.36f).Within(0.01f));
        Assert.That(attraction.PackingBake.ShrinkFootprintMeters.y,
            Is.EqualTo(0.75f).Within(0.01f));
        Assert.That(attraction.ApplyPackingVariant(attraction.PackingBake.ShrinkScale, false), Is.True);
        // Never scaled to fit (Aidan, Oct 19 2026): a follower keeps its authored size; shrink only moves things.
        Assert.That(floor.localScale, Is.EqualTo(new Vector3(0.36576f, 1f, 0.54864f)));
    }

    [Test]
    public void Bake_GrowsNearbyPropsAsOneRigidGroup()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate first = CreateProp("First", 1f);
        PropTemplate second = CreateProp("Second", 2.05f);
        attraction.maxGrowthScale = new Vector2(1.5f, 1.2f);
        attraction.growGroupGapMeters = 0.1f;

        AttractionPackingBaker.Bake(attraction, false);
        AttractionPropPackingPose firstPose = attraction.PackingBake.Props[0];
        AttractionPropPackingPose secondPose = attraction.PackingBake.Props[1];

        Assert.That(firstPose.GrowGroup, Is.EqualTo(secondPose.GrowGroup));
        float firstDelta = firstPose.GrownLocalPosition.x - firstPose.AuthoredLocalPosition.x;
        float secondDelta = secondPose.GrownLocalPosition.x - secondPose.AuthoredLocalPosition.x;
        Assert.That(firstDelta, Is.EqualTo(secondDelta).Within(0.001f));
        Assert.That(attraction.PackingBake.GrowScale.x, Is.EqualTo(1.5f).Within(0.001f));
        Assert.That(attraction.PackingBake.GrowScale.y, Is.EqualTo(1.2f).Within(0.001f));
    }

    [Test]
    public void Bake_StretchesRepeatedAlignedTrailAlongGrowthAxis()
    {
        AttractionTemplate attraction = CreateAttraction();
        for (int i = -2; i <= 2; i++) CreatePropAt("Coin (" + (i + 2) + ")", 0f, i);
        attraction.maxGrowthScale = new Vector2(1f, 5f);

        AttractionPackingBaker.Bake(attraction, false);

        var poses = attraction.PackingBake.Props;
        Assert.That(poses.Count, Is.EqualTo(5));
        Assert.That(poses[0].GrowGroup, Is.EqualTo(poses[4].GrowGroup));
        Assert.That(poses[0].GrownLocalPosition.z, Is.EqualTo(-10f).Within(0.001f));
        Assert.That(poses[4].GrownLocalPosition.z, Is.EqualTo(10f).Within(0.001f));
        Assert.That(poses[0].GrownLocalPosition.x, Is.EqualTo(0f).Within(0.001f));

        float authoredGap = poses[1].AuthoredLocalPosition.z - poses[0].AuthoredLocalPosition.z;
        float grownGap = poses[1].GrownLocalPosition.z - poses[0].GrownLocalPosition.z;
        Assert.That(grownGap, Is.EqualTo(authoredGap * 5f).Within(0.001f));
    }

    [Test]
    public void Bake_ExpandsRepeatedChildrenInsideLegacyGroupWithoutChangingShrinkSpacing()
    {
        AttractionTemplate attraction = CreateAttraction();
        var zone = new GameObject("A_CoinZone");
        zone.transform.SetParent(_root.transform, false);
        zone.transform.localPosition = new Vector3(0f, 0f, 2f);
        for (int i = -2; i <= 2; i++)
        {
            GameObject coin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            coin.name = "E_COIN (" + (i + 2) + ")";
            coin.transform.SetParent(zone.transform, false);
            coin.transform.localPosition = new Vector3(i * 0.1f, 0f, i);
        }
        attraction.maxGrowthScale = new Vector2(1f, 5f);

        AttractionPackingBaker.Bake(attraction, false);

        var poses = attraction.PackingBake.Props;
        Assert.That(poses.Count, Is.EqualTo(5));
        Assert.That(poses[0].DisplayName, Does.StartWith("E_COIN"));
        float authoredGap = poses[1].AuthoredLocalPosition.z - poses[0].AuthoredLocalPosition.z;
        float shrunkGap = poses[1].ShrunkLocalPosition.z - poses[0].ShrunkLocalPosition.z;
        float grownGap = poses[1].GrownLocalPosition.z - poses[0].GrownLocalPosition.z;
        Assert.That(shrunkGap, Is.EqualTo(authoredGap).Within(0.001f));
        Assert.That(grownGap, Is.EqualTo(authoredGap * 5f).Within(0.001f));
    }

    [Test]
    public void Bake_DoesNotExpandRepeatedChildrenWhenLegacyOwnerHasOwnGeometry()
    {
        AttractionTemplate attraction = CreateAttraction();
        GameObject pit = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pit.name = "L_LAVAPIT_3";
        pit.transform.SetParent(_root.transform, false);
        pit.transform.localPosition = new Vector3(1.5f, 0f, 0f);
        for (int i = 0; i < 3; i++)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "L_METALBLOCK (" + i + ")";
            block.transform.SetParent(pit.transform, false);
            block.transform.localPosition = new Vector3(0f, 0f, i + 1f);
        }
        CreateLegacyCube("Neighbor", -1.5f);
        attraction.maxGrowthScale = new Vector2(2f, 2f);

        AttractionPackingBaker.Bake(attraction, false);

        var poses = attraction.PackingBake.Props;
        Assert.That(poses.Count, Is.EqualTo(2));
        Assert.That(poses[0].Prop, Is.EqualTo(pit.transform));
        Assert.That(poses[0].HierarchyPath, Is.EqualTo("L_LAVAPIT_3"));
        Assert.That(poses[0].Prop.GetComponent<MeshFilter>(), Is.Not.Null);
    }

    [Test]
    public void Bake_DoesNotLetOversizedInfrastructureMergeEveryGrowthGroup()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate oversized = CreateProp("Oversized Backdrop", 0f);
        oversized.customFootprintMeters = new Vector2(20f, 20f);
        CreateProp("Visible Left", -1f);
        CreateProp("Visible Right", 1f);

        AttractionPackingBaker.Bake(attraction, false);

        int oversizedGroup = attraction.PackingBake.Props[0].GrowGroup;
        Assert.That(attraction.PackingBake.Props[1].GrowGroup, Is.Not.EqualTo(oversizedGroup));
        Assert.That(attraction.PackingBake.Props[2].GrowGroup, Is.Not.EqualTo(oversizedGroup));
    }

    [Test]
    public void Bake_LocalBlockerDoesNotFreezeUnrelatedShrinkSpace()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Near Outer", -2f);
        CreateProp("Near Inner", -0.9f);
        CreateProp("Far Opposite", 3f);

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.PackingBake.ShrinkScale.x, Is.LessThan(0.7f));
        float finalGap = Mathf.Abs(
            attraction.PackingBake.Props[0].ShrunkLocalPosition.x
            - attraction.PackingBake.Props[1].ShrunkLocalPosition.x);
        Assert.That(finalGap, Is.GreaterThanOrEqualTo(1.079f));
    }

    [Test]
    public void Bake_DoesNotCollapseCloselyAuthoredBlockRowsOntoEachOther()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.shrinkClearanceMeters = 0.08f;
        CreatePropAt("L_BRICKBLOCK", -0.25f, -0.25f).customFootprintMeters = Vector2.one * 0.49491882f;
        CreatePropAt("L_BRICKBLOCK (1)", 0.25f, -0.25f).customFootprintMeters = Vector2.one * 0.49491882f;
        CreatePropAt("L_BRICKBLOCK (2)", -0.25f, 0.25f).customFootprintMeters = Vector2.one * 0.49491882f;
        CreatePropAt("L_BRICKBLOCK (3)", 0.25f, 0.25f).customFootprintMeters = Vector2.one * 0.49491882f;

        AttractionPackingBaker.Bake(attraction, false);

        var poses = attraction.PackingBake.Props;
        Assert.That(poses.Count, Is.EqualTo(4));
        float leftRowSeparation = Mathf.Abs(
            poses[0].ShrunkLocalPosition.z - poses[2].ShrunkLocalPosition.z);
        float rightRowSeparation = Mathf.Abs(
            poses[1].ShrunkLocalPosition.z - poses[3].ShrunkLocalPosition.z);
        Assert.That(leftRowSeparation, Is.GreaterThanOrEqualTo(0.494f));
        Assert.That(rightRowSeparation, Is.GreaterThanOrEqualTo(0.494f));
    }

    [Test]
    public void Bake_UsesFloorCutoutPolygonsWhenRectangularBoundsAlreadyOverlap()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate first = CreateProp("Irregular First", -1f);
        PropTemplate second = CreateProp("Irregular Second", 1f);
        first.customFootprintMeters = new Vector2(3f, 1f);
        second.customFootprintMeters = new Vector2(3f, 1f);
        AddSquareCutout(first.gameObject, 0.5f);
        AddSquareCutout(second.gameObject, 0.5f);

        AttractionPackingBaker.Bake(attraction, false);

        AttractionPropPackingPose firstPose = attraction.PackingBake.Props[0];
        AttractionPropPackingPose secondPose = attraction.PackingBake.Props[1];
        float separation = secondPose.ShrunkLocalPosition.x - firstPose.ShrunkLocalPosition.x;
        Assert.That(separation, Is.GreaterThanOrEqualTo(0.499f));
        Assert.That(separation, Is.LessThan(2f));
    }

    [Test]
    public void PackingScale_IsIndependentAndDirectlyMultipliesAuthoredDimensions()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        attraction.maxGrowthScale = new Vector2(1.1f, 1.35f);

        AttractionPackingBaker.Bake(attraction, false);
        Vector2 requested = new Vector2(1.05f, 1.2f);
        Vector2 footprint = attraction.GetPackingFootprintMeters(requested, false);
        Vector2 authored = attraction.PackingBake.AuthoredFootprintMeters;

        Assert.That(footprint.x, Is.EqualTo(authored.x * requested.x).Within(0.001f));
        Assert.That(footprint.y, Is.EqualTo(authored.y * requested.y).Within(0.001f));
        Assert.That(attraction.PackingBake.GrowScale.x, Is.EqualTo(1.1f).Within(0.001f));
        Assert.That(attraction.PackingBake.GrowScale.y, Is.EqualTo(1.35f).Within(0.001f));

        Assert.That(attraction.ApplyPackingVariant(requested, false), Is.True);
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(footprint.x).Within(0.001f));
        Assert.That(attraction.RuntimeFootprintMeters.y, Is.EqualTo(footprint.y).Within(0.001f));
        GameArea gameArea = attraction.GetComponent<GameArea>();
        Assert.That(gameArea.unpaddedHalfExtents.x, Is.EqualTo(footprint.x * 0.5f).Within(0.001f));
        Assert.That(gameArea.unpaddedHalfExtents.z, Is.EqualTo(footprint.y * 0.5f).Within(0.001f));
    }

    [Test]
    public void PackingScale_OneExactlyMatchesAuthoredLayout()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        AttractionPackingBaker.Bake(attraction, false);

        AttractionPropPackingPose leftPose = attraction.PackingBake.Props[0];
        Vector3 atOne = attraction.GetPackingPoseLocalPosition(leftPose, Vector2.one, false);
        Vector2 footprintAtOne = attraction.GetPackingFootprintMeters(Vector2.one, false);

        Assert.That(atOne, Is.EqualTo(left.transform.localPosition));
        Assert.That(footprintAtOne, Is.EqualTo(attraction.PackingBake.AuthoredFootprintMeters));
    }

    [Test]
    public void Bake_WithNoPropTemplates_DoesNotCollapseAttractionToZero()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.maxGrowthScale = new Vector2(1.1f, 1.2f);

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.PackingBake.ShrinkScale, Is.EqualTo(Vector2.one));
        Assert.That(attraction.PackingBake.EssentialShrinkScale, Is.EqualTo(Vector2.one));
        Assert.That(attraction.PackingBake.GrowScale.x, Is.EqualTo(1.1f).Within(0.001f));
        Assert.That(attraction.PackingBake.GrowScale.y, Is.EqualTo(1.2f).Within(0.001f));
    }

    [Test]
    public void SafeArea_IsAnInset_AndDoesNotClampShrink()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        attraction.safeArea = 0.1f;

        AttractionPackingBaker.Bake(attraction, false);
        Vector2 authored = attraction.PackingBake.AuthoredFootprintMeters;

        Assert.That(attraction.PackingBake.SafeFootprintMeters.x, Is.EqualTo(authored.x * 0.8f).Within(0.001f));
        Assert.That(attraction.PackingBake.SafeFootprintMeters.y, Is.EqualTo(authored.y * 0.8f).Within(0.001f));
        Assert.That(attraction.PackingBake.ShrinkScale.x, Is.LessThan(1f));

        attraction.safeArea = 0f;
        Assert.That(attraction.GetSafeFootprintMeters(), Is.EqualTo(authored));
    }

    [Test]
    public void Bake_UsesLegacyChildGeometryWithoutPropTemplates()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateLegacyCube("Left", -1f);
        CreateLegacyCube("Right", 1f);

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(2));
        Assert.That(attraction.PackingBake.ShrinkScale.x, Is.LessThan(1f));
        Assert.That(attraction.PackingBake.Props[0].ResourceName, Is.Empty);
    }

    [Test]
    public void Bake_IgnoresOccluderGeometryInPackingCalculations()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateLegacyCube("Occlusion Volume", 0f);
        CreateLegacyCube("Visible Prop", 1f);

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(1));
        Assert.That(attraction.PackingBake.Props[0].DisplayName, Is.EqualTo("Visible Prop"));
    }

    [Test]
    public void PackingPreview_DoesNotMoveAuthoredProps()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        AttractionPackingBaker.Bake(attraction, false);
        Vector3 authoredPosition = left.transform.localPosition;

        attraction.SetPackingPreview(attraction.PackingBake.ShrinkScale, false);

        Assert.That(left.transform.localPosition, Is.EqualTo(authoredPosition));
        float previewX = attraction.GetPackingPoseLocalPosition(
            attraction.PackingBake.Props[0],
            attraction.PackingBake.ShrinkScale,
            false).x;
        Assert.That(Mathf.Abs(previewX - authoredPosition.x), Is.GreaterThan(0.001f));
    }

    [Test]
    public void Bake_FollowerDoesNotLimitShrinkAndFollowsEachFloorAxis()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        Transform border = CreateFollower("Border", AttractionPackingScaleMode.XZIndependent);
        border.localScale = new Vector3(0.6096f, 1f, 0.6096f);
        border.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;
        FloorAnchor anchor = border.gameObject.AddComponent<FloorAnchor>();
        anchor.matchGrade = true;
        anchor.PrecalculateBounds();

        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(2));
        Assert.That(attraction.PackingBake.Followers.Count, Is.EqualTo(1));
        Assert.That(attraction.PackingBake.ShrinkScale.x, Is.LessThan(0.6f));
        Assert.That(attraction.PackingBake.ShrinkScale.y, Is.LessThan(0.3f));

        attraction.ApplyPackingVariant(attraction.PackingBake.ShrinkScale, false);
        // Never scaled to fit (Aidan, Oct 19 2026): the follower keeps its authored size at any shrink.
        Assert.That(border.localScale, Is.EqualTo(new Vector3(0.6096f, 1f, 0.6096f)));
        Vector3[] gradeCorners = anchor.GetPrecalculatedCornersWorld();
        Assert.That(Vector3.Distance(gradeCorners[0], gradeCorners[1]), Is.EqualTo(6.096f).Within(0.01f));
        attraction.ResetPackingVariant();
        Assert.That(border.localScale.x, Is.EqualTo(0.6096f).Within(0.001f));
    }

    [Test]
    public void Bake_FollowerMovesInsideBeforeItScales()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Center", 0f);
        Transform follower = CreateFollower("Edge Marker", AttractionPackingScaleMode.XZIndependent);
        follower.localPosition = new Vector3(2f, 0f, 0f);
        follower.localScale = new Vector3(0.02f, 1f, 0.02f);
        follower.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;

        AttractionPackingBaker.Bake(attraction, false);
        attraction.ApplyPackingVariant(attraction.PackingBake.ShrinkScale, false);

        Assert.That(follower.localScale.x, Is.EqualTo(0.02f).Within(0.001f));
        Assert.That(follower.localPosition.x, Is.LessThan(0.5f));
    }

    [Test]
    public void Bake_UniformModesPreserveOrScaleHeight()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        Transform planar = CreateFollower("Planar", AttractionPackingScaleMode.XZUniform);
        Transform spatial = CreateFollower("Spatial", AttractionPackingScaleMode.XYZUniform);
        planar.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;
        spatial.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;

        AttractionPackingBaker.Bake(attraction, false);
        attraction.ApplyPackingVariant(attraction.PackingBake.ShrinkScale, false);

        // Never scaled to fit, whatever the follower's mode (Aidan, Oct 19 2026).
        Assert.That(planar.localScale, Is.EqualTo(Vector3.one));
        Assert.That(spatial.localScale, Is.EqualTo(Vector3.one));
    }

    [Test]
    public void Bake_CollisionAwareFollowerAvoidsNewOverlapAtShrinkEndpoint()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate[] props =
        {
            CreatePropAt("East", 1f, 0f),
            CreatePropAt("West", -1f, 0f),
            CreatePropAt("North", 0f, 1f),
            CreatePropAt("South", 0f, -1f),
        };
        Transform follower = CreateFollower("Off-Centre", AttractionPackingScaleMode.XZIndependent);
        follower.localPosition = new Vector3(2f, 0f, 1.8f);
        follower.localScale = new Vector3(0.05f, 1f, 0.05f);

        AttractionPackingBaker.Bake(attraction, false);
        attraction.ApplyPackingVariant(attraction.PackingBake.ShrinkScale, false);

        float halfFollowerX = follower.localScale.x * 5f;
        float halfFollowerZ = follower.localScale.z * 5f;
        foreach (PropTemplate prop in props)
        {
            bool separatedX = Mathf.Abs(follower.localPosition.x - prop.transform.localPosition.x)
                >= halfFollowerX + 0.5f + attraction.shrinkClearanceMeters - 0.001f;
            bool separatedZ = Mathf.Abs(follower.localPosition.z - prop.transform.localPosition.z)
                >= halfFollowerZ + 0.5f + attraction.shrinkClearanceMeters - 0.001f;
            Assert.That(separatedX || separatedZ, Is.True,
                $"follower={follower.localPosition} scale={follower.localScale}, prop={prop.transform.localPosition}, shrink={attraction.PackingBake.ShrinkFootprintMeters}");
        }
    }

    [Test]
    public void Bake_FollowerUsesGrowthAndEssentialShrinkEndpoints()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate essential = CreateProp("Essential", 0f);
        CreateProp("Optional", 2f);
        attraction.essentialProps.Add(essential.transform);
        attraction.maxGrowthScale = new Vector2(1.5f, 1.2f);
        Transform border = CreateFollower("Border", AttractionPackingScaleMode.XZIndependent);
        border.localScale = new Vector3(0.6096f, 1f, 0.6096f);
        border.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;

        AttractionPackingBaker.Bake(attraction, false);
        // Never scaled to fit — neither grown nor shrunk (Aidan, Oct 19 2026).
        attraction.ApplyPackingVariant(attraction.PackingBake.GrowScale, false);
        Assert.That(border.localScale, Is.EqualTo(new Vector3(0.6096f, 1f, 0.6096f)));
        attraction.ApplyPackingVariant(attraction.PackingBake.EssentialShrinkScale, true);
        Assert.That(border.localScale, Is.EqualTo(new Vector3(0.6096f, 1f, 0.6096f)));
    }

    /// Oct 19 2026: the bake records each layout's TRUE ground bounds (renderers whose bottom is ≤ 0.5 m, at 1×
    /// and at shrink) so the planner can reserve floor content that overhangs the declared footprint. Airborne
    /// and [nopack] renderers do not count, and measuring leaves the attraction exactly as it was.
    [Test]
    public void Bake_RecordsTrueGroundBoundsPastTheDeclaredFootprint()
    {
        AttractionTemplate attraction = CreateAttraction();   // declared 20 x 20 ft (6.096 m)
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        AddSlab("LavaRim", new Vector3(0f, 0f, 0f), new Vector3(9f, 0.1f, 3f));       // ground, overhangs on X
        AddSlab("Banner", new Vector3(0f, 3f, 0f), new Vector3(20f, 0.1f, 1f));       // airborne: may overhang
        AddSlab("Overlay [nopack]", Vector3.zero, new Vector3(30f, 0.01f, 30f));      // debug overlay
        int children = _root.transform.childCount;

        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

        AttractionPackingBake bake = attraction.PackingBake;
        Assert.That(bake.GroundFootprintMeters.x, Is.EqualTo(9f).Within(0.01f), "the rim overhangs the declared 6.1 m");
        Assert.That(bake.GroundFootprintMeters.y, Is.EqualTo(3f).Within(0.01f));
        Assert.That(bake.GroundShrinkFootprintMeters.x, Is.GreaterThan(0.01f), "measured at the shrink layout too");
        Assert.That(_root.transform.childCount, Is.EqualTo(children), "measuring leaves the hierarchy as it was");
        Assert.That(_root.transform.Find("LavaRim").localPosition, Is.EqualTo(Vector3.zero));

        // The manifest/planner envelope carries it.
        var envelopes = attraction.GetFrameEnvelopes();
        if (envelopes.Count > 0) Assert.That(envelopes[0].groundMeters.x, Is.EqualTo(9f).Within(0.01f));
    }

    /// Oct 19 2026 (A_LittleLava Minimum: declared 1.60 m at shrink, rigid content 1.85 m): a layout's runtime footprint
    /// is never smaller than its baked TRUE ground at that scale — interpolated between the ground at shrink and at 1×,
    /// growing past 1× — so its floor, bounds and seat cover what it draws. No ground baked = the declared size.
    [Test]
    public void RuntimeFootprint_NeverShrinksBelowTheBakedGround()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
        AttractionPackingBake bake = attraction.PackingBake;
        Vector2 shrink = bake.ShrinkFootprintMeters, authored = bake.AuthoredFootprintMeters;

        // No ground past the declared sizes (two 1 m props): the declared shrink stands.
        bake.SetGroundFootprints(Vector2.zero, Vector2.zero);
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, bake.ShrinkScale);
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(shrink.x).Within(0.01f));

        // Rigid ground that does not shrink as far: the footprint stops at it, per axis.
        Vector2 groundShrink = new Vector2(shrink.x + 1f, shrink.y);
        bake.SetGroundFootprints(new Vector2(authored.x, authored.y), groundShrink);
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, bake.ShrinkScale);
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(groundShrink.x).Within(0.01f));
        Assert.That(attraction.RuntimeFootprintMeters.y, Is.EqualTo(shrink.y).Within(0.01f));
        Assert.That(AttractionTemplate.GroundAtScale(bake, Vector2.one).x, Is.EqualTo(authored.x).Within(0.01f));
        Assert.That(AttractionTemplate.GroundAtScale(bake, new Vector2(2f, 2f)).x, Is.EqualTo(authored.x * 2f).Within(0.01f));
    }

    /// Oct 19 2026 (A_LavaCoinField's LevelFloor +0.3/+0.2 m past every frame): a generated floor built for another
    /// footprint than the applied one is stale, and the next layout refresh rebuilds it.
    [Test]
    public void GeneratedFloor_IsStaleWhenBuiltForAnotherFootprint()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.generateFloor = true;
        attraction.runtimePlane = new GameObject("LevelFloor");
        attraction.runtimePlane.transform.SetParent(_root.transform, false);
        Vector2 dims = attraction.RuntimeFootprintMeters;
        attraction.gridWidth = dims.x; attraction.gridHeight = dims.y;
        Assert.That(attraction.GeneratedFloorIsStale, Is.False);
        attraction.gridWidth = dims.x + 0.3f;
        Assert.That(attraction.GeneratedFloorIsStale, Is.True);
    }

    /// WE NEVER SCALE OUR ASSETS TO FIT (Aidan, Oct 19 2026): after ApplyLayout / ApplyPackingVariant at ANY shrink or
    /// grow, every authored renderer's lossyScale is exactly what it was — props and followers only move.
    [Test]
    public void Layout_NeverScalesAnyAuthoredRenderer()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        AddSlab("Rock", new Vector3(1.5f, 0f, 1.5f), new Vector3(0.5f, 0.6f, 0.5f));
        Transform decal = CreateFollower("Path Decal", AttractionPackingScaleMode.XZIndependent);
        decal.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.transform.SetParent(decal, false); quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        Transform plant = CreateFollower("Plant", AttractionPackingScaleMode.XYZUniform);
        var leaf = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(leaf.GetComponent<Collider>());
        leaf.transform.SetParent(plant, false);
        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

        var before = new Dictionary<Renderer, Vector3>();
        foreach (Renderer r in _root.GetComponentsInChildren<Renderer>(true)) before[r] = r.transform.lossyScale;
        AttractionPackingBake bake = attraction.PackingBake;
        foreach (Vector2 s in new[] { bake.ShrinkScale, (bake.ShrinkScale + Vector2.one) * 0.5f, bake.GrowScale, Vector2.one })
        {
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, s);
            foreach (var kv in before)
                Assert.That((kv.Key.transform.lossyScale - kv.Value).sqrMagnitude, Is.LessThan(1e-8f),
                    $"{kv.Key.name} rescaled at layout scale {s}");
        }
    }

    private void AddSlab(string name, Vector3 position, Vector3 scale)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = position;
        go.transform.localScale = scale;
    }

    private AttractionTemplate CreateAttraction()
    {
        _root = new GameObject("Attraction");
        AttractionTemplate attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.safeArea = 0f;
        return attraction;
    }

    private PropTemplate CreateProp(string name, float x)
        => CreatePropAt(name, x, 0f);

    private PropTemplate CreatePropAt(string name, float x, float z)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = new Vector3(x, 0f, z);
        PropTemplate prop = go.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;
        return prop;
    }

    private void CreateLegacyCube(string name, float x)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = new Vector3(x, 0f, 0f);
    }

    private Transform CreateFollower(string name, AttractionPackingScaleMode mode)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = name;
        go.transform.SetParent(_root.transform, false);
        go.AddComponent<AttractionPackingFollower>().scaleMode = mode;
        return go.transform;
    }

    private static void AddSquareCutout(GameObject target, float size)
    {
        float half = size * 0.5f;
        FloorCutout cutout = target.AddComponent<FloorCutout>();
        cutout.points.Add(new Vector3(-half, 0f, -half));
        cutout.points.Add(new Vector3(half, 0f, -half));
        cutout.points.Add(new Vector3(half, 0f, half));
        cutout.points.Add(new Vector3(-half, 0f, half));
    }
}
