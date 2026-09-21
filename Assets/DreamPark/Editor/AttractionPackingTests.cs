using DreamPark;
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
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = new Vector3(x, 0f, 0f);
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
}
