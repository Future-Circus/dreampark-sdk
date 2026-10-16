using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DreamPark;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class AttractionLayoutFrameTests
{
    private const float Ft = 0.3048f;
    private GameObject _root;
    private string _temporaryPrefabPath;

    [TearDown]
    public void TearDown()
    {
        AttractionPackingBaker.FrameBakeHookForTests = null;
        if (_root != null) Object.DestroyImmediate(_root);
        if (!string.IsNullOrEmpty(_temporaryPrefabPath))
            AssetDatabase.DeleteAsset(_temporaryPrefabPath);
    }

    [Test]
    public void Bake_FrameHonoursOverridesAndRestoresHierarchy()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        PropTemplate extra = CreateProp("Extra", 3f);
        extra.gameObject.SetActive(false);
        AttractionLayoutFrame small = Frame("fsmall001", "Small", 10f, 10f,
            Hide(right.transform), Move(left.transform, Vector3.zero));
        AttractionLayoutFrame big = Frame("fbig00001", "Big", 30f, 20f, Show(extra.transform));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { small, big });

        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(extra.gameObject.activeSelf, Is.False);

        Assert.That(attraction.FrameHasValidBake(small), Is.True);
        Assert.That(small.Bake.AuthoredFootprintMeters.x, Is.EqualTo(10f * Ft).Within(0.001f));
        Assert.That(small.Bake.AuthoredFootprintMeters.y, Is.EqualTo(10f * Ft).Within(0.001f));
        Assert.That(small.Bake.Props.Count, Is.EqualTo(1));
        Assert.That(small.Bake.Props[0].Prop, Is.SameAs(left.transform));
        Assert.That(small.Bake.Props[0].AuthoredLocalPosition, Is.EqualTo(Vector3.zero));

        Assert.That(attraction.FrameHasValidBake(big), Is.True);
        Assert.That(big.Bake.Props.Count, Is.EqualTo(3));
        Assert.That(big.Bake.AuthoredFootprintMeters.x, Is.EqualTo(30f * Ft).Within(0.001f));

        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(2),
            "Base ignores the extra that only exists in the big frame.");
        Assert.That(attraction.PackingBake.MirrorsMinimumFrame, Is.False);
        Assert.That(attraction.PackingBake.EssentialShrinkFootprintMeters,
            Is.EqualTo(attraction.PackingBake.ShrinkFootprintMeters));
        Assert.That(attraction.customSize, Is.EqualTo(new Vector2(20f, 20f)),
            "Frame bakes never mutate the attraction size.");
        Assert.That(attraction.LayoutBaseStates.Count, Is.EqualTo(3));
    }

    [Test]
    public void Bake_RestoresHierarchyWhenFrameBakeThrows()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsmall001", "Small", 10f, 10f,
                Hide(right.transform), Move(left.transform, new Vector3(0.25f, 0f, 0f))),
        });
        bool sawFrameState = false;
        AttractionPackingBaker.FrameBakeHookForTests = (a, frame) =>
        {
            sawFrameState = !right.gameObject.activeSelf && left.transform.localPosition.x > 0f;
            throw new InvalidOperationException("simulated bake failure");
        };

        Assert.Throws<InvalidOperationException>(() => AttractionPackingBaker.Bake(attraction, false));

        Assert.That(sawFrameState, Is.True, "The failure happened while the frame was applied.");
        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
    }

    [Test]
    public void Bake_IgnoresInactiveItemsOnlyWhenFramesExist()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        PropTemplate hidden = CreateProp("Hidden", 3f);
        hidden.gameObject.SetActive(false);

        AttractionPackingBaker.Bake(attraction, false);
        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(3), "Frame-less bakes are unchanged.");
        Vector2 legacyShrink = attraction.PackingBake.ShrinkFootprintMeters;

        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fbig00001", "Big", 30f, 20f, Show(hidden.transform)),
        });
        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(2));
        foreach (AttractionPropPackingPose pose in attraction.PackingBake.Props)
            Assert.That(pose.Prop, Is.Not.SameAs(hidden.transform));
        Assert.That(attraction.PackingBake.ShrinkFootprintMeters.x, Is.LessThan(legacyShrink.x - 0.5f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Bake_FrameThatHidesPartOfAPlainGroupMeasuresOnlyWhatItKeeps(bool withColliders)
    {
        AttractionTemplate attraction = CreateAttraction();
        Transform[] row = CreateBrickRow(withColliders);

        AttractionPackingBaker.Bake(attraction, false);
        Vector2 framelessShrink = attraction.PackingBake.ShrinkFootprintMeters;
        Vector2 framelessLayout = attraction.PackingBake.LayoutFootprintMeters;
        Assert.That(framelessLayout.x, Is.EqualTo(6f).Within(0.01f));

        // 12 ft (3.66 m) holds the three inner bricks (3.5 m) but not all five (6 m).
        AttractionLayoutFrame few = Frame("ffew00001", "Few", 12f, 20f, Hide(row[0]), Hide(row[4]));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { few });
        AttractionPackingBaker.Bake(attraction, false);

        AttractionPackingBake baseBake = attraction.PackingBake;
        AssertNear(baseBake.ShrinkFootprintMeters, framelessShrink, "Base (all active) is unchanged.");
        AssertNear(baseBake.LayoutFootprintMeters, framelessLayout, "Base (all active) is unchanged.");

        Assert.That(attraction.FrameHasValidBake(few), Is.True);
        Assert.That(few.Bake.LayoutFootprintMeters.x, Is.EqualTo(3.5f).Within(0.01f),
            "Bricks the frame hides do not widen the group.");
        Assert.That(few.Bake.ShrinkFootprintMeters.x, Is.EqualTo(3.5f).Within(0.01f));
        Assert.That(few.Bake.ShrinkFootprintMeters.x, Is.LessThan(baseBake.ShrinkFootprintMeters.x - 1f));
        foreach (AttractionPropPackingPose pose in few.Bake.Props)
        {
            Assert.That(pose.Prop, Is.Not.SameAs(row[0]));
            Assert.That(pose.Prop, Is.Not.SameAs(row[4]));
        }
        Assert.That(AttractionPackingBaker.GetFrameWarnings(attraction), Has.None.Contains("overflows"));
        Assert.That(row[0].gameObject.activeSelf && row[4].gameObject.activeSelf, Is.True);
    }

    [Test]
    public void Bake_FramelessBakeStillMeasuresInactiveDescendants()
    {
        AttractionTemplate attraction = CreateAttraction();
        Transform[] row = CreateBrickRow(false);
        row[0].gameObject.SetActive(false);
        row[4].gameObject.SetActive(false);

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.PackingBake.LayoutFootprintMeters.x, Is.EqualTo(6f).Within(0.01f),
            "Frame-less bakes keep the original measurement exactly.");
        Assert.That(attraction.PackingBake.ShrinkFootprintMeters.x, Is.EqualTo(6f).Within(0.01f));
    }

    [Test]
    public void Bake_FrameThatHidesPartOfAFollowerMeasuresOnlyWhatItKeeps()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Center", 0f);
        var rail = new GameObject("Rail");
        rail.transform.SetParent(_root.transform, false);
        AttractionPackingFollower follower = rail.AddComponent<AttractionPackingFollower>();
        follower.scaleMode = AttractionPackingScaleMode.XZIndependent;
        follower.avoidNewPropOverlaps = false;
        CreateCube(rail.transform, "Core", 0f, false);
        Transform wing = CreateCube(rail.transform, "Wing", 2.5f, false);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fnowing01", "No Wing", 16f, 16f, Hide(wing)),
        });

        AttractionPackingBaker.Bake(attraction, false);

        // Shrink is the 1 m prop, so the 3.5 m rail (core + wing) scales down in Base...
        Assert.That(attraction.PackingBake.Followers.Count, Is.EqualTo(1));
        Assert.That(attraction.PackingBake.Followers[0].ShrunkLocalScale.x, Is.LessThan(0.5f));
        // ...but without its wing the rail is the 1 m core and fits at full scale.
        AttractionLayoutFrame frame = attraction.FindFrame("fnowing01");
        Assert.That(attraction.FrameHasValidBake(frame), Is.True);
        Assert.That(frame.Bake.Followers.Count, Is.EqualTo(1));
        Assert.That(frame.Bake.Followers[0].Follower, Is.SameAs(rail.transform));
        Assert.That(frame.Bake.Followers[0].ShrunkLocalScale.x, Is.EqualTo(1f).Within(0.01f));
        Assert.That(wing.gameObject.activeSelf, Is.True);
    }

    [Test]
    public void Bake_MigratesEssentialPropsIntoMinimumFrame()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.essentialProps.Add(left.transform);

        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

        Assert.That(attraction.LegacyEssentialMigrated, Is.True);
        AttractionLayoutFrame minimum = attraction.MinimumFrame;
        Assert.That(minimum, Is.Not.Null);
        Assert.That(minimum.id, Is.EqualTo(AttractionTemplate.MinimumFrameId));
        Assert.That(minimum.name, Is.EqualTo("Minimum"));
        Assert.That(minimum.kind, Is.EqualTo(AttractionLayoutFrameKind.Minimum));
        Assert.That(minimum.sizeFeet, Is.EqualTo(new Vector2(20f, 20f)));
        Assert.That(minimum.overrides.Count, Is.EqualTo(1));
        Assert.That(minimum.overrides[0].path, Is.EqualTo("Right"));
        Assert.That(minimum.overrides[0].target, Is.SameAs(right.transform));
        Assert.That(minimum.overrides[0].setActive, Is.True);
        Assert.That(minimum.overrides[0].active, Is.False);
        Assert.That(attraction.essentialProps, Has.Count.EqualTo(1), "Legacy data is kept.");

        Assert.That(attraction.FrameHasValidBake(minimum), Is.True);
        Assert.That(minimum.Bake.Props.Count, Is.EqualTo(1));
        AttractionPackingBake bake = attraction.PackingBake;
        Assert.That(bake.MirrorsMinimumFrame, Is.True);
        Assert.That(bake.HasEssentialProps, Is.True);
        Assert.That(bake.EssentialShrinkFootprintMeters, Is.EqualTo(minimum.Bake.ShrinkFootprintMeters));
        Assert.That(bake.EssentialShrinkScale.x,
            Is.EqualTo(minimum.Bake.ShrinkFootprintMeters.x / bake.AuthoredFootprintMeters.x).Within(0.0001f));
        Assert.That(right.gameObject.activeSelf, Is.True);

        AttractionPackingBaker.Bake(attraction, false);
        Assert.That(attraction.LayoutFrames.Count, Is.EqualTo(1), "Migration runs once.");
    }

    [Test]
    public void Bake_WithoutEssentialPropsDoesNotMigrate()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.HasLayoutFrames, Is.False);
        Assert.That(attraction.LegacyEssentialMigrated, Is.False);
        Assert.That(attraction.PackingBake.MirrorsMinimumFrame, Is.False);
        Assert.That(attraction.PackingBake.Version, Is.EqualTo(AttractionPackingBake.CurrentVersion));
        Assert.That(attraction.LayoutBaseStates.Count, Is.EqualTo(0));
    }

    [Test]
    public void LegacyEssentialOnly_MapsToMinimumFrame()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.essentialProps.Add(left.transform);
        AttractionPackingBaker.Bake(attraction, false);
        AttractionPackingBake bake = attraction.PackingBake;
        Vector2 minimumShrink = attraction.MinimumFrame.Bake.ShrinkFootprintMeters;

        Vector2 footprint = attraction.GetPackingFootprintMeters(bake.EssentialShrinkScale, true);
        Assert.That(footprint.x, Is.EqualTo(minimumShrink.x).Within(0.001f));
        Assert.That(footprint.y, Is.EqualTo(minimumShrink.y).Within(0.001f));

        Assert.That(attraction.ApplyPackingVariant(bake.EssentialShrinkScale, true), Is.True);
        Assert.That(attraction.CurrentFrameId, Is.EqualTo(AttractionTemplate.MinimumFrameId));
        Assert.That(right.gameObject.activeSelf, Is.False);
        Assert.That(left.transform.localPosition.x, Is.EqualTo(0f).Within(0.01f));
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(minimumShrink.x).Within(0.001f));

        Assert.That(attraction.ApplyPackingVariant(Vector2.one, false), Is.True);
        Assert.That(attraction.CurrentFrameId, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(attraction.MinimumFootprintMeters, Is.EqualTo(minimumShrink));
    }

    [Test]
    public void TrySelectFrame_PicksLargestFittingFrameClampsScaleAndFallsBack()
    {
        AttractionTemplate attraction = CreateSyntheticLadder(
            out AttractionLayoutFrame custom, out AttractionLayoutFrame minimum);
        Vector2 baseAuthored = new Vector2(20f, 20f) * Ft;

        Assert.That(attraction.TrySelectFrame(new Vector2(7f, 7f), out string id, out Vector2 scale), Is.True);
        Assert.That(id, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(scale.x, Is.EqualTo(7f / baseAuthored.x).Within(0.0001f));

        Assert.That(attraction.TrySelectFrame(new Vector2(10f, 10f), out id, out scale), Is.True);
        Assert.That(id, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(scale.x, Is.EqualTo(1.25f).Within(0.0001f), "Clamped to the frame's grow scale.");

        Assert.That(attraction.TrySelectFrame(new Vector2(3f, 5f), out id, out scale), Is.True);
        Assert.That(id, Is.EqualTo(custom.id));
        Assert.That(scale.x, Is.EqualTo(3f / custom.SizeMeters.x).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(1.25f).Within(0.0001f));

        Assert.That(attraction.TrySelectFrame(new Vector2(1f, 1f), out id, out scale), Is.True);
        Assert.That(id, Is.EqualTo(AttractionTemplate.MinimumFrameId));
        Assert.That(scale.x, Is.EqualTo(1f / minimum.SizeMeters.x).Within(0.0001f));

        Assert.That(attraction.TrySelectFrame(new Vector2(0.3f, 5f), out id, out scale), Is.False);
        Assert.That(id, Is.EqualTo(AttractionTemplate.MinimumFrameId), "Fallback is the smallest shrink.");
        Assert.That(scale, Is.EqualTo(minimum.Bake.ShrinkScale));

        Assert.That(attraction.MinimumFootprintMeters, Is.EqualTo(new Vector2(0.6f, 0.6f)));
        Assert.That(attraction.ApplyFootprint(new Vector2(3f, 5f)), Is.True);
        Assert.That(attraction.CurrentFrameId, Is.EqualTo(custom.id));
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(3f).Within(0.001f));
        Assert.That(attraction.RuntimeFootprintMeters.y, Is.EqualTo(custom.SizeMeters.y * 1.25f).Within(0.001f));
        Assert.That(attraction.GetFrameFootprintMeters(custom.id, new Vector2(0.01f, 9f)),
            Is.EqualTo(Vector2.Scale(custom.Bake.AuthoredFootprintMeters,
                new Vector2(custom.Bake.ShrinkScale.x, custom.Bake.GrowScale.y))));
    }

    [Test]
    public void GetFrameEnvelopes_OrdersLadderLargestToSmallest()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.SetPackingBake(SyntheticBake(new Vector2(20f, 20f) * Ft, new Vector2(4f, 4f)));
        var frames = new List<AttractionLayoutFrame>
        {
            SyntheticFrame(MinimumFrameOf(4f, 4f), new Vector2(0.6f, 0.6f)),
            SyntheticFrame(Frame("flong0001", "Long", 10f, 40f), new Vector2(1f, 3f)),
            SyntheticFrame(Frame("fsquare01", "Square", 20f, 20f), new Vector2(3f, 3f)),
            SyntheticFrame(Frame("fsmall001", "Small", 10f, 10f), new Vector2(2f, 2f)),
            SyntheticFrame(Frame("Bad Id", "Invalid", 12f, 12f), new Vector2(2f, 2f)),
            SyntheticFrame(Frame("fsmall001", "Duplicate", 11f, 11f), new Vector2(2f, 2f)),
            Frame("fstale001", "Unbaked", 14f, 14f),
        };
        attraction.SetLayoutFrames(frames);

        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();

        CollectionAssert.AreEqual(
            new[] { "base", "fsquare01", "flong0001", "fsmall001", "minimum" },
            ladder.ConvertAll(e => e.frameId));
        CollectionAssert.AreEqual(
            new[] { "base", "custom", "custom", "custom", "minimum" },
            ladder.ConvertAll(e => e.kind));
        Assert.That(ladder[3].name, Is.EqualTo("Small"));
        Assert.That(ladder[4].shrinkMeters, Is.EqualTo(new Vector2(0.6f, 0.6f)));
        Assert.That(attraction.TryGetFrameEnvelope("fstale001", out _), Is.False);
        Assert.That(attraction.TryGetFrameEnvelope("minimum", out AttractionFrameEnvelope min), Is.True);
        Assert.That(min.growScale.x, Is.EqualTo(1.25f).Within(0.0001f));

        attraction.SetPackingBake(null);
        Assert.That(attraction.GetFrameEnvelopes(), Is.Empty, "No ladder without a valid Base bake.");
    }

    [Test]
    public void ApplyLayout_SwitchingFramesOnLiveObjectIsExact()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        PropTemplate extra = CreateProp("Extra", 3f);
        extra.gameObject.SetActive(false);
        Transform border = CreateFollower("Border", AttractionPackingScaleMode.XZIndependent);
        border.localScale = new Vector3(0.6096f, 1f, 0.6096f);
        border.GetComponent<AttractionPackingFollower>().avoidNewPropOverlaps = false;
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsmall001", "Small", 10f, 10f,
                Hide(right.transform), Move(left.transform, Vector3.zero)),
            Frame("fbig00001", "Big", 30f, 20f,
                Show(extra.transform), Move(extra.transform, new Vector3(4f, 0f, 0f))),
        });
        AttractionPackingBaker.Bake(attraction, false);
        AttractionLayoutFrame small = attraction.FindFrame("fsmall001");
        Vector2 smallShrink = small.Bake.ShrinkScale;

        Assert.That(attraction.ApplyLayout("fsmall001", smallShrink), Is.True);
        Assert.That(attraction.CurrentFrameId, Is.EqualTo("fsmall001"));
        Assert.That(attraction.CurrentFrameScale, Is.EqualTo(smallShrink));
        Assert.That(right.gameObject.activeSelf, Is.False);
        Assert.That(extra.gameObject.activeSelf, Is.False);
        Assert.That(left.transform.localPosition.x, Is.EqualTo(0f).Within(0.0001f));
        Vector3 smallBorderScale = border.localScale;
        Vector3 smallBorderPosition = border.localPosition;
        Assert.That(smallBorderScale.x, Is.LessThan(0.6f));
        Assert.That(attraction.RuntimeFootprintMeters,
            Is.EqualTo(Vector2.Scale(small.Bake.AuthoredFootprintMeters, smallShrink)));

        Assert.That(attraction.ApplyLayout("fbig00001", Vector2.one), Is.True);
        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(extra.gameObject.activeSelf, Is.True);
        Assert.That(extra.transform.localPosition, Is.EqualTo(new Vector3(4f, 0f, 0f)));
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(border.localScale.x, Is.EqualTo(0.6096f).Within(0.0001f));
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(30f * Ft).Within(0.001f));

        Assert.That(attraction.ApplyLayout("fsmall001", smallShrink), Is.True);
        Assert.That(right.gameObject.activeSelf, Is.False);
        Assert.That(extra.gameObject.activeSelf, Is.False);
        Assert.That(extra.transform.localPosition, Is.EqualTo(new Vector3(3f, 0f, 0f)));
        Assert.That(left.transform.localPosition.x, Is.EqualTo(0f).Within(0.0001f));
        Assert.That((border.localScale - smallBorderScale).sqrMagnitude, Is.LessThan(0.0000001f));
        Assert.That((border.localPosition - smallBorderPosition).sqrMagnitude, Is.LessThan(0.0000001f));

        Assert.That(attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one), Is.True);
        Assert.That(attraction.CurrentFrameId, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(extra.gameObject.activeSelf, Is.False);
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(border.localScale.x, Is.EqualTo(0.6096f).Within(0.0001f));

        LogAssert.Expect(LogType.Warning, new Regex("no baked layout frame 'fmissing1'"));
        Assert.That(attraction.ApplyLayout("fmissing1", Vector2.one), Is.True);
        Assert.That(attraction.CurrentFrameId, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(right.gameObject.activeSelf, Is.True);
    }

    [Test]
    public void ApplyLayout_WithoutFramesUsesLegacyVariant()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        AttractionPackingBaker.Bake(attraction, false);
        Vector2 shrinkScale = attraction.PackingBake.ShrinkScale;

        Assert.That(attraction.HasLayoutFrames, Is.False);
        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        Assert.That(ladder.Count, Is.EqualTo(1));
        Assert.That(ladder[0].frameId, Is.EqualTo(AttractionTemplate.BaseFrameId));

        attraction.ApplyPackingVariant(shrinkScale, false);
        Vector3 legacyPosition = left.transform.localPosition;
        Vector2 legacyFootprint = attraction.RuntimeFootprintMeters;
        attraction.ApplyPackingVariant(Vector2.one, false);

        Assert.That(attraction.ApplyLayout(AttractionTemplate.BaseFrameId, shrinkScale), Is.True);
        Assert.That(left.transform.localPosition, Is.EqualTo(legacyPosition));
        Assert.That(attraction.RuntimeFootprintMeters, Is.EqualTo(legacyFootprint));

        Assert.That(attraction.TrySelectFrame(attraction.PackingBake.ShrinkFootprintMeters,
            out string id, out Vector2 scale), Is.True);
        Assert.That(id, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(scale.x, Is.EqualTo(shrinkScale.x).Within(0.0001f));

        LogAssert.Expect(LogType.Warning, new Regex("no baked layout frame 'fmissing1'"));
        Assert.That(attraction.ApplyLayout("fmissing1", Vector2.one), Is.True);
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
    }

    [Test]
    public void BakeIntoAsset_CopiesFramesWithPathRemapping()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        var group = new GameObject("Group");
        group.transform.SetParent(_root.transform, false);
        PropTemplate right = CreateProp("Right", 1f);
        right.transform.SetParent(group.transform, false);
        _temporaryPrefabPath = AssetDatabase.GenerateUniqueAssetPath(
            "Assets/__AttractionLayoutFrameBakeIntoAssetTest.prefab");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(_root, _temporaryPrefabPath);
        Object.DestroyImmediate(_root);
        _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        attraction = _root.GetComponent<AttractionTemplate>();
        Transform instanceRight = _root.transform.Find("Group/Right");
        AttractionLayoutFrame frame = Frame("fsmall001", "Small", 10f, 10f, Hide(instanceRight));
        frame.overrides[0].path = "Stale/Path";
        frame.overrides.Add(new AttractionLayoutOverride { path = "Gone/Away", setActive = true });
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { frame });
        attraction.LegacyEssentialMigrated = true;

        LogAssert.Expect(LogType.Warning, new Regex("Gone/Away"));
        Assert.That(AttractionPackingBaker.BakeIntoAsset(attraction), Is.True);

        AttractionTemplate saved = AssetDatabase.LoadAssetAtPath<GameObject>(_temporaryPrefabPath)
            .GetComponent<AttractionTemplate>();
        Transform savedRight = saved.transform.Find("Group/Right");
        Assert.That(saved.LayoutFrames.Count, Is.EqualTo(1));
        AttractionLayoutFrame savedFrame = saved.LayoutFrames[0];
        Assert.That(savedFrame.id, Is.EqualTo("fsmall001"));
        Assert.That(savedFrame.name, Is.EqualTo("Small"));
        Assert.That(savedFrame.sizeFeet, Is.EqualTo(new Vector2(10f, 10f)));
        Assert.That(savedFrame.overrides.Count, Is.EqualTo(2));
        Assert.That(savedFrame.overrides[0].path, Is.EqualTo("Group/Right"));
        Assert.That(savedFrame.overrides[0].target, Is.SameAs(savedRight));
        Assert.That(savedFrame.overrides[0].setActive, Is.True);
        Assert.That(savedFrame.overrides[0].active, Is.False);
        Assert.That(savedFrame.overrides[1].path, Is.EqualTo("Gone/Away"));
        Assert.That(savedFrame.overrides[1].target, Is.Null);
        Assert.That(saved.LegacyEssentialMigrated, Is.True);
        Assert.That(saved.FrameHasValidBake(savedFrame), Is.True);
        Assert.That(savedFrame.Bake.Props.Count, Is.EqualTo(1));
        Assert.That(savedRight.gameObject.activeSelf, Is.True, "The asset is saved in its Base state.");
        Assert.That(saved.PackingBake.Props.Count, Is.EqualTo(2));
        Assert.That(saved.LayoutBaseStates.Count, Is.EqualTo(2));
    }

    [Test]
    public void BakeIntoAsset_MapsSameNamedSiblingsToTheRightObject()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Brick", -2f);
        CreateProp("Brick", 0f);
        CreateProp("Brick", 2f);
        _temporaryPrefabPath = AssetDatabase.GenerateUniqueAssetPath(
            "Assets/__AttractionLayoutFrameSameNameTest.prefab");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(_root, _temporaryPrefabPath);
        Object.DestroyImmediate(_root);
        _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        attraction = _root.GetComponent<AttractionTemplate>();
        Transform thirdBrick = _root.transform.GetChild(2);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsmall001", "Small", 10f, 10f, Hide(thirdBrick)),
        });
        attraction.LegacyEssentialMigrated = true;

        Assert.That(AttractionPackingBaker.BakeIntoAsset(attraction), Is.True);

        AttractionTemplate saved = AssetDatabase.LoadAssetAtPath<GameObject>(_temporaryPrefabPath)
            .GetComponent<AttractionTemplate>();
        AttractionLayoutOverride hide = saved.LayoutFrames[0].overrides[0];
        Assert.That(hide.target, Is.SameAs(saved.transform.GetChild(2)),
            "The third 'Brick' stays the third one, not the first same-named sibling.");
        Assert.That(saved.LayoutFrames[0].Bake.Props.Count, Is.EqualTo(2));
        Assert.That(saved.transform.GetChild(2).gameObject.activeSelf, Is.True, "Base is untouched.");
    }

    [Test]
    public void Migration_IsMarkedDoneWhenAMinimumFrameAlreadyExists()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        attraction.essentialProps.Add(left.transform);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { MinimumFrameOf(20f, 20f) });

        AttractionPackingBaker.Bake(attraction, false);
        Assert.That(attraction.LegacyEssentialMigrated, Is.True);
        Assert.That(attraction.MinimumFrame.overrides, Is.Empty, "The creator's own Minimum frame is kept.");

        // Deleting that frame must not bring a migrated one back on the next bake.
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>());
        AttractionPackingBaker.Bake(attraction, false);
        Assert.That(attraction.HasLayoutFrames, Is.False);
        Assert.That(attraction.PackingBake.HasEssentialProps, Is.False,
            "The superseded essentialProps list does not come back as a hidden essential variant.");
        Assert.That(attraction.PackingBake.EssentialShrinkFootprintMeters,
            Is.EqualTo(attraction.PackingBake.ShrinkFootprintMeters));
    }

    [Test]
    public void CreateMinimumFrame_MigratesUnmigratedEssentialProps()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.essentialProps.Add(left.transform);

        AttractionLayoutFrame minimum = AttractionLayoutFrameAuthoring.CreateMinimumFrame(attraction);

        Assert.That(minimum, Is.SameAs(attraction.MinimumFrame));
        Assert.That(attraction.LegacyEssentialMigrated, Is.True);
        Assert.That(minimum.overrides.Count, Is.EqualTo(1));
        Assert.That(minimum.overrides[0].target, Is.SameAs(right.transform));
        Assert.That(minimum.overrides[0].setActive && !minimum.overrides[0].active, Is.True);
        Assert.That(right.gameObject.activeSelf, Is.True);
    }

    [Test]
    public void GetFrameWarnings_BaseSizedMinimumFrameIsNotADuplicateSize()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { MinimumFrameOf(20f, 20f, Hide(right.transform)) });
        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(AttractionPackingBaker.GetFrameWarnings(attraction), Has.None.Contains("same size"));
    }

    [Test]
    public void GetFrameWarnings_ReportsAuthoringProblems()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsame0001", "Same", 20f, 20f,
                new AttractionLayoutOverride { path = "Missing", setActive = true }),
            Frame("fover0001", "Overflow", 4f, 4f),
            MinimumFrameOf(18f, 18f),
        });
        AttractionPackingBaker.Bake(attraction, false);

        List<string> warnings = AttractionPackingBaker.GetFrameWarnings(attraction);
        Assert.That(warnings, Has.Some.Contains("'Missing' was not found"));
        Assert.That(warnings, Has.Some.Contains("overflows"));
        Assert.That(warnings, Has.Some.Contains("no smaller than Base"));
        Assert.That(warnings, Has.Some.Contains("same size"));
        Assert.That(warnings, Has.None.Contains("out of date"));

        attraction.maxGrowthScale = new Vector2(1.5f, 1.5f);
        Assert.That(AttractionPackingBaker.GetFrameWarnings(attraction), Has.Some.Contains("out of date"));
    }

    [Test]
    public void LayoutPreview_ResolvesWithoutMovingAuthoredPropsInEditMode()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsmall001", "Small", 10f, 10f, Hide(right.transform)),
        });
        AttractionPackingBaker.Bake(attraction, false);

        attraction.SetLayoutPreview(new Vector2(1.5f, 1.5f), "");

        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(attraction.HasLayoutPreview, Is.True);
        Assert.That(attraction.TryResolveLayoutPreview(out string id, out Vector2 scale), Is.True);
        Assert.That(id, Is.EqualTo("fsmall001"));
        Assert.That(scale.x, Is.EqualTo(1.5f / (10f * Ft)).Within(0.0001f));

        attraction.SetLayoutPreview(Vector2.zero, AttractionTemplate.BaseFrameId);
        attraction.TryResolveLayoutPreview(out id, out scale);
        Assert.That(id, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(scale, Is.EqualTo(Vector2.one));

        attraction.ResetPackingVariant();
        Assert.That(attraction.HasLayoutPreview, Is.False);
    }

    [Test]
    public void PreviewPaths_RoundTrip()
    {
        Assert.That(AttractionLayoutFramePreviewPaths.FolderFor("Game"),
            Is.EqualTo("Assets/Content/Game/Previews/Frames"));
        Assert.That(AttractionLayoutFramePreviewPaths.PathFor("Game", "A_Breakout", "f1a2b3c4d"),
            Is.EqualTo("Assets/Content/Game/Previews/Frames/A_Breakout@f1a2b3c4d.png"));
        Assert.That(AttractionLayoutFramePreviewPaths.MetadataKey("A_Breakout", "minimum"),
            Is.EqualTo("A_Breakout@minimum"));

        Assert.That(AttractionLayoutFramePreviewPaths.TryParse(
            "Assets/Content/Game/Previews/Frames/A_Breakout@minimum.png", out string prefab, out string frame), Is.True);
        Assert.That(prefab, Is.EqualTo("A_Breakout"));
        Assert.That(frame, Is.EqualTo("minimum"));
        Assert.That(AttractionLayoutFramePreviewPaths.TryParse("A@B@f1", out prefab, out frame), Is.True);
        Assert.That(prefab, Is.EqualTo("A@B"));
        Assert.That(frame, Is.EqualTo("f1"));
        Assert.That(AttractionLayoutFramePreviewPaths.TryParse("A_Breakout.png", out _, out _), Is.False);
        Assert.That(AttractionLayoutFramePreviewPaths.TryParse("A_Breakout@Bad Id.png", out _, out _), Is.False);
        Assert.That(AttractionLayoutFramePreviewPaths.TryParse("@minimum.png", out _, out _), Is.False);

        Assert.That(AttractionLayoutFramePreviewPaths.IsFramePreviewPath(
            "Assets/Content/Game/Previews/Frames/A_Breakout@minimum.png"), Is.True);
        Assert.That(AttractionLayoutFramePreviewPaths.IsFramePreviewPath(
            "Assets/Content/Game/Previews/A_Breakout.png"), Is.False);
    }

    [Test]
    public void IsValidFrameId_MatchesSharedRegex()
    {
        Assert.That(AttractionTemplate.IsValidFrameId("f1a2b3c4d"), Is.True);
        Assert.That(AttractionTemplate.IsValidFrameId("minimum"), Is.True);
        Assert.That(AttractionTemplate.IsValidFrameId("a-b"), Is.True);
        Assert.That(AttractionTemplate.IsValidFrameId(new string('a', 32)), Is.True);
        Assert.That(AttractionTemplate.IsValidFrameId(new string('a', 33)), Is.False);
        Assert.That(AttractionTemplate.IsValidFrameId("-ab"), Is.False);
        Assert.That(AttractionTemplate.IsValidFrameId("Ab"), Is.False);
        Assert.That(AttractionTemplate.IsValidFrameId("a b"), Is.False);
        Assert.That(AttractionTemplate.IsValidFrameId(""), Is.False);
        Assert.That(AttractionTemplate.IsValidFrameId(null), Is.False);
    }

    [Test]
    public void Bake_FrameShrinkPoseStaysSpreadWhenAnEdgeLimitsTheFootprint()
    {
        AttractionTemplate attraction = CreateAttraction();
        // A tight spine along Z barely compresses. Maximal compaction would still pull
        // the loose crates to the centre; the relaxed shrink pose keeps them where the
        // (almost unchanged) length allows.
        for (int k = -2; k <= 2; k++) CreateSizedProp($"Spine ({k + 2})", 0f, 1.2f * k, 1f, 1.1f);
        PropTemplate crateA = CreateSizedProp("Crate", -2.2f, 2f, 0.6f, 0.6f);
        PropTemplate crateB = CreateSizedProp("Crate (1)", 2.2f, -2f, 0.6f, 0.6f);
        PropTemplate crateC = CreateSizedProp("Crate (2)", -2.2f, -1f, 0.6f, 0.6f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { Frame("fbig00001", "Big", 30f, 30f) });

        AttractionPackingBaker.Bake(attraction, false);

        AttractionPackingBake bake = attraction.PackingBake;
        Vector2 shrink = bake.ShrinkFootprintMeters;
        Assert.That(shrink.y, Is.GreaterThan(5.7f), "The spine limits the length.");
        Assert.That(bake.LayoutIssue, Is.Empty);
        var crates = new HashSet<Transform> { crateA.transform, crateB.transform, crateC.transform };
        foreach (AttractionPropPackingPose pose in bake.Props)
        {
            Vector2 half = pose.FootprintMeters * 0.5f;
            Vector3 authored = pose.AuthoredLocalPosition;
            Vector3 shrunk = pose.ShrunkLocalPosition;
            Assert.That(Mathf.Abs(shrunk.x) + half.x, Is.LessThanOrEqualTo(shrink.x * 0.5f + 0.001f), pose.DisplayName);
            Assert.That(Mathf.Abs(shrunk.z) + half.y, Is.LessThanOrEqualTo(shrink.y * 0.5f + 0.001f), pose.DisplayName);
            if (!crates.Contains(pose.Prop)) continue;
            Assert.That(Mathf.Abs(shrunk.x),
                Is.GreaterThanOrEqualTo(Mathf.Min(Mathf.Abs(authored.x), shrink.x * 0.5f - half.x) - 0.01f),
                pose.DisplayName + " stays as wide as the shrunk width allows");
            Assert.That(Mathf.Abs(shrunk.z),
                Is.GreaterThanOrEqualTo(Mathf.Min(Mathf.Abs(authored.z), shrink.y * 0.5f - half.y) - 0.01f),
                pose.DisplayName + " stays as long as the shrunk length allows");
        }
        AssertNear(ShrunkExtent(bake), shrink, 0.01f, "The relax pass never changes the shrink footprint.");
        AssertNoNewOverlapAtShrink(bake, attraction.shrinkClearanceMeters);
        Assert.That(crateA.transform.localPosition, Is.EqualTo(new Vector3(-2.2f, 0f, 2f)), "The hierarchy is untouched.");
    }

    [Test]
    public void Bake_FrameLayoutHasNoCollisionAtAnyWidthAndLengthPair()
    {
        AttractionTemplate attraction = CreateAttraction();
        // Three coins share a row; blocks above and below pull on them. The old joint
        // compaction separated same-row coins by Z and then stacked them at one X, so
        // shrinking the width while the length grew slid them through each other.
        CreateSizedProp("E_COIN", -0.68f, -1.39f, 0.3f, 0.3f);
        CreateSizedProp("E_COIN (1)", 0.32f, -1.39f, 0.3f, 0.3f);
        CreateSizedProp("E_COIN (2)", 1.32f, -1.39f, 0.3f, 0.3f);
        CreateSizedProp("Block", -0.68f, 0.2f, 0.8f, 0.8f);
        CreateSizedProp("Block (1)", 1.32f, -2.6f, 0.8f, 0.8f);
        CreateSizedProp("Block (2)", -2.3f, 1.9f, 1f, 1f);
        CreateSizedProp("Block (3)", 2.2f, 2.3f, 0.6f, 1.2f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { Frame("fwide0001", "Wide", 24f, 20f) });

        AttractionPackingBaker.Bake(attraction, false);

        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        Assert.That(ladder.Count, Is.EqualTo(2));
        foreach (AttractionFrameEnvelope envelope in ladder)
        {
            Assert.That(BakeOf(attraction, envelope.frameId).LayoutIssue, Is.Empty, envelope.name);
            Assert.That(envelope.shrinkScale.x, Is.LessThan(0.9f), envelope.name + " shrinks in width");
            AssertNoCollisionOnGrid(attraction, envelope);
        }
    }

    [Test]
    public void FindLayoutCollision_ReportsAPairThatOnlyMeetsAtAMixedScale()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate a = CreateSizedProp("Coin A", -0.9f, 0f, 1f, 1f);
        PropTemplate b = CreateSizedProp("Coin B", 0.9f, 0f, 1f, 1f);
        Vector2 authored = new Vector2(3f, 3f);
        Vector2 shrink = new Vector2(1f, 2.2f);
        // Joint-compaction style: one shrunk X for both, separated only by shrunk Z.
        // Width shrunk + length as authored puts them on top of each other.
        var poses = new List<AttractionPropPackingPose>
        {
            Pose(a.transform, new Vector3(-0.9f, 0f, 0f), new Vector3(0f, 0f, -0.6f)),
            Pose(b.transform, new Vector3(0.9f, 0f, 0f), new Vector3(0f, 0f, 0.6f)),
        };
        var bake = new AttractionPackingBake(authored, authored, shrink, authored * 1.25f, shrink, poses);

        string issue = AttractionPackingBaker.FindLayoutCollision(attraction, bake);

        Assert.That(issue, Does.Contain("Coin A").And.Contain("Coin B").And.Contain("ft"));
        var separated = new AttractionPackingBake(authored, authored, shrink, authored * 1.25f, shrink,
            new List<AttractionPropPackingPose>
            {
                Pose(a.transform, new Vector3(-0.9f, 0f, 0f), new Vector3(-0.54f, 0f, 0f)),
                Pose(b.transform, new Vector3(0.9f, 0f, 0f), new Vector3(0.54f, 0f, 0f)),
            });
        Assert.That(AttractionPackingBaker.FindLayoutCollision(attraction, separated), Is.Empty);
    }

    [Test]
    public void Bake_OverhangingFrameKeepsItsDeclaredSizeAndScalesFromIt()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateSizedProp("Block", -2.5f, 0f, 1f, 1f);
        CreateSizedProp("Block (1)", 0f, 0f, 1f, 1f);
        PropTemplate right = CreateSizedProp("Block (2)", 2.5f, 0f, 1f, 1f);
        // The 6 m row overhangs a 12 ft (3.66 m) frame; it packs to ~3.16 m.
        AttractionLayoutFrame tight = Frame("ftight001", "Tight", 12f, 20f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { tight });

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-2.5f, 0f, 0f)), "Hierarchy restored.");
        Assert.That(attraction.FrameHasValidBake(tight), Is.True);
        AttractionPackingBake bake = tight.Bake;
        AssertNear(bake.AuthoredFootprintMeters, tight.SizeMeters, 0.0001f, "The declared size is the authored footprint.");
        AssertNear(bake.RequestedFootprintMeters, tight.SizeMeters, 0.0001f, "Requested = declared.");
        Assert.That(bake.LayoutFootprintMeters.x, Is.EqualTo(6f).Within(0.01f), "Content is not moved to fit.");
        foreach (AttractionPropPackingPose pose in bake.Props)
            Assert.That(pose.AuthoredLocalPosition, Is.EqualTo(pose.Prop.localPosition), pose.DisplayName);
        // Shrink = declared × packed / as placed: content and footprint keep their proportion.
        float packed = ShrunkExtent(bake).x;
        Assert.That(packed, Is.LessThan(3.3f));
        Assert.That(bake.ShrinkFootprintMeters.x, Is.EqualTo(tight.SizeMeters.x * packed / 6f).Within(0.01f));
        Assert.That(bake.ShrinkFootprintMeters.x, Is.LessThanOrEqualTo(tight.SizeMeters.x));
        AssertContentScalesWithEnvelope(attraction, tight.id);
        Assert.That(bake.LayoutIssue, Is.Empty);
        List<string> warnings = AttractionPackingBaker.GetFrameWarnings(attraction);
        Assert.That(warnings, Has.Some.Contains("‘Tight’: content overhangs its 12 × 20 ft frame").And.Contains("allowed"));
        Assert.That(warnings, Has.None.Contains("can't fit"));
    }

    [Test]
    public void Bake_OversizedObjectNeverChangesTheFrameSize()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateSizedProp("Left", -1f, 0f, 1f, 1f);
        PropTemplate pit = CreateSizedProp("Lava Pit", 1f, 0f, 1.5f, 1.5f);
        pit.gameObject.SetActive(false);
        // A 1.5 m pit in a 2 × 2 ft (0.61 m) Minimum: allowed, and the frame stays 2 × 2 ft.
        AttractionLayoutFrame minimum = MinimumFrameOf(2f, 2f, Hide(left.transform), Show(pit.transform));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { minimum });

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(attraction.FrameHasValidBake(minimum), Is.True);
        AttractionPackingBake bake = minimum.Bake;
        AssertNear(bake.AuthoredFootprintMeters, minimum.SizeMeters, 0.0001f, "Authored = declared.");
        AssertNear(bake.RequestedFootprintMeters, minimum.SizeMeters, 0.0001f, "Requested = declared.");
        Assert.That(bake.ShrinkFootprintMeters.x, Is.LessThanOrEqualTo(minimum.SizeMeters.x + 0.0001f));
        Assert.That(bake.ShrinkFootprintMeters.y, Is.LessThanOrEqualTo(minimum.SizeMeters.y + 0.0001f));
        Assert.That(bake.GrowScale.x, Is.GreaterThanOrEqualTo(1f));
        AssertContentScalesWithEnvelope(attraction, AttractionTemplate.MinimumFrameId);
        Assert.That(pit.gameObject.activeSelf, Is.False, "Hierarchy restored.");
        List<string> warnings = AttractionPackingBaker.GetFrameWarnings(attraction);
        Assert.That(warnings, Has.Some.Contains("‘Minimum’: content overhangs its 2 × 2 ft frame"));
        Assert.That(warnings, Has.None.Contains("can't fit"));
        Assert.That(warnings, Has.None.Contains("out of date"));
    }

    [Test]
    public void Bake_ChainedGrowthTracksEverySizeOnTheSizeLine()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.customSize = new Vector2(20f, 30f);
        var crates = new Transform[3, 5];
        for (int ix = 0; ix < 3; ix++)
        for (int iz = 0; iz < 5; iz++)
            crates[ix, iz] = CreateSizedProp($"Crate {ix}{iz}", -2f + 2f * ix, -3.6f + 1.8f * iz, 1f, 1f).transform;
        var midHides = new List<AttractionLayoutOverride>();
        for (int ix = 0; ix < 3; ix++)
        {
            midHides.Add(Hide(crates[ix, 0]));
            midHides.Add(Hide(crates[ix, 4]));
        }
        var minimumHides = new List<AttractionLayoutOverride>();
        for (int ix = 0; ix < 3; ix++)
        for (int iz = 0; iz < 5; iz++)
            if (ix != 1 || iz == 0 || iz == 4) minimumHides.Add(Hide(crates[ix, iz]));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fmid00001", "Mid", 14f, 20f, midHides.ToArray()),
            MinimumFrameOf(8f, 8f, minimumHides.ToArray()),
        });

        AttractionPackingBaker.Bake(attraction, false);

        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        CollectionAssert.AreEqual(new[] { "base", "fmid00001", "minimum" }, ladder.ConvertAll(e => e.frameId));
        AssertSizeLineTracksExactly(attraction, 30);
        foreach (AttractionFrameEnvelope envelope in ladder)
            Assert.That(BakeOf(attraction, envelope.frameId).LayoutIssue, Is.Empty, envelope.name);
    }

    [Test]
    public void Bake_BlockPartyShapedFramesAreHonestContinuousAndCollisionFree()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.customSize = new Vector2(20f, 30f);
        // Base as placed spans 21.4 × 31 ft (declared 20 × 30): a tight spine along Z
        // that barely compresses, blocks either side, wet-floor signs at the ends.
        var spine = new Transform[7];
        for (int k = 0; k < 7; k++)
            spine[k] = CreateSizedProp($"L_BLOCK_PRESET ({k})", 0f, (k - 3) * 1.375f, 1f, 1.1f).transform;
        var sides = new List<Transform>();
        foreach (float z in new[] { -2.8f, 0f, 2.8f })
        {
            sides.Add(CreateSizedProp($"L_ITEMBLOCK ({sides.Count})", -2.86f, z, 0.8f, 0.8f).transform);
            sides.Add(CreateSizedProp($"L_ITEMBLOCK ({sides.Count})", 2.86f, z, 0.8f, 0.8f).transform);
        }
        Transform wetA = CreateSizedProp("E_WETFLOOR", -1.6f, 4.425f, 0.6f, 0.6f).transform;
        Transform wetB = CreateSizedProp("E_WETFLOOR (1)", 1.6f, -4.425f, 0.6f, 0.6f).transform;
        // The Minimum's lava pit alone is 11.9 × 11.8 ft; it only exists there.
        PropTemplate pit = CreateSizedProp("L_LAVAPIT_TINY", 0f, 0f, 11.9f * Ft, 11.8f * Ft);
        pit.gameObject.SetActive(false);

        // Frame 1: declared 20 × 20 ft, but its content spans 23.1 × 24.3 ft.
        var frameOne = new List<AttractionLayoutOverride> { Hide(spine[0]), Hide(spine[6]) };
        foreach (Transform side in sides)
            frameOne.Add(Move(side, new Vector3(Mathf.Sign(side.localPosition.x) * 3.12f, 0f, side.localPosition.z)));
        frameOne.Add(Move(wetA, new Vector3(-1.6f, 0f, 3.4f)));
        frameOne.Add(Move(wetB, new Vector3(1.6f, 0f, -3.4f)));
        // Minimum: declared 7 × 10 ft, holding only the pit.
        var minimum = new List<AttractionLayoutOverride> { Show(pit.transform), Hide(wetA), Hide(wetB) };
        foreach (Transform block in spine) minimum.Add(Hide(block));
        foreach (Transform side in sides) minimum.Add(Hide(side));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fframe001", "Frame 1", 20f, 20f, frameOne.ToArray()),
            MinimumFrameOf(7f, 10f, minimum.ToArray()),
        });

        AttractionPackingBaker.Bake(attraction, false);

        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        CollectionAssert.AreEqual(new[] { "base", "fframe001", "minimum" }, ladder.ConvertAll(e => e.frameId));
        AssertNear(attraction.PackingBake.AuthoredFootprintMeters, new Vector2(20f, 30f) * Ft, 0.0001f,
            "Base keeps its declared 20 × 30 ft.");
        AssertNear(ladder[1].authoredMeters, new Vector2(20f, 20f) * Ft, 0.0001f,
            "Frame 1 keeps its declared 20 × 20 ft.");
        AttractionFrameEnvelope min = ladder[2];
        AssertNear(min.authoredMeters, new Vector2(7f, 10f) * Ft, 0.0001f, "The Minimum keeps its declared 7 × 10 ft.");
        Assert.That(attraction.MinimumFootprintMeters, Is.EqualTo(min.shrinkMeters), "The Minimum is the smallest.");
        List<string> notes = AttractionPackingBaker.GetFrameWarnings(attraction);
        Assert.That(notes, Has.Some.Contains("‘Minimum’: content overhangs its 7 × 10 ft frame"));
        Assert.That(notes, Has.None.Contains("can't fit"));

        foreach (AttractionFrameEnvelope envelope in ladder)
        {
            Assert.That(BakeOf(attraction, envelope.frameId).LayoutIssue, Is.Empty, envelope.name);
            AssertContentScalesWithEnvelope(attraction, envelope.frameId);
            AssertNoCollisionOnGrid(attraction, envelope);
        }
        AssertSizeLineTracksExactly(attraction, 40);

        // Where the Size line switches Base ↔ Frame 1, the content just above and just
        // below must be about as big relative to each frame's declared size (the old bake
        // snapped to a visibly larger frame). Frame 1's content overhangs its declared
        // 20 ft by ~21% on purpose, so raw content differs by that authored overhang.
        AttractionPackingBaker.GetSizeLine(ladder, out Vector2 start, out Vector2 end);
        float low = 0f;
        float high = 1f;
        Assert.That(SelectedFrame(attraction, start, end, high), Is.EqualTo(AttractionTemplate.BaseFrameId));
        for (int i = 0; i < 24; i++)
        {
            float mid = (low + high) * 0.5f;
            if (SelectedFrame(attraction, start, end, mid) == AttractionTemplate.BaseFrameId) high = mid;
            else low = mid;
        }
        Assert.That(SelectedFrame(attraction, start, end, low - 0.002f), Is.EqualTo("fframe001"));
        Vector2 above = Vector2.Scale(ContentAt(attraction, start, end, high + 0.002f, out Vector2 spaceAbove),
            InverseOverhang(attraction, AttractionTemplate.BaseFrameId));
        Vector2 below = Vector2.Scale(ContentAt(attraction, start, end, low - 0.002f, out _),
            InverseOverhang(attraction, "fframe001"));
        Assert.That(Mathf.Abs(above.x - below.x), Is.LessThan(0.1f * spaceAbove.x), $"width {above} vs {below}");
        Assert.That(Mathf.Abs(above.y - below.y), Is.LessThan(0.1f * spaceAbove.y), $"length {above} vs {below}");
    }

    [Test]
    public void CoverageMapSampler_MatchesTrySelectFrame()
    {
        AttractionTemplate attraction = CreateSyntheticLadder(out _, out _);
        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        int margins = 0;
        int empty = 0;
        for (float width = 0.1f; width <= 9f; width += 0.37f)
        for (float length = 0.1f; length <= 9f; length += 0.41f)
        {
            Vector2 size = new Vector2(width, length);
            int index = AttractionLayoutFramesInspector.SelectFrameForSize(ladder, size, out bool margin);
            bool fits = attraction.TrySelectFrame(size, out string frameId, out Vector2 scale);
            Assert.That(index >= 0, Is.EqualTo(fits), $"{size}");
            if (!fits)
            {
                empty++;
                continue;
            }
            Assert.That(ladder[index].frameId, Is.EqualTo(frameId), $"{size}");
            Vector2 footprint = attraction.GetFrameFootprintMeters(frameId, scale);
            bool expectedMargin = footprint.x < size.x - 0.001f || footprint.y < size.y - 0.001f;
            Assert.That(margin, Is.EqualTo(expectedMargin), $"{size}");
            if (margin) margins++;
        }
        Assert.That(margins, Is.GreaterThan(0), "The sample covers sizes beyond every grow.");
        Assert.That(empty, Is.GreaterThan(0), "The sample covers sizes where nothing fits.");
    }

    [Test]
    public void Bake_OffCentreFrameKeepsItsDeclaredSize()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.customSize = new Vector2(30f, 52f);
        CreateSizedProp("Centre", 0f, 0f, 1f, 1f);
        PropTemplate ship = CreateSizedProp("E_SHIP", 0f, 7f, 6f, 5.8f);
        CreateSizedProp("Cannon", 1f, 3f, 1f, 1f);
        // Cannonsnout-like: the frame keeps only content on +Z, far bigger than 12 × 16 ft.
        AttractionLayoutFrame frame = Frame("fframe001", "Frame 1", 12f, 16f,
            Hide(attraction.transform.Find("Centre")));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { frame });

        AttractionPackingBaker.Bake(attraction, false);

        Assert.That(ship.transform.localPosition, Is.EqualTo(new Vector3(0f, 0f, 7f)), "Nothing is moved or re-centred.");
        AttractionPackingBake bake = frame.Bake;
        AssertNear(bake.AuthoredFootprintMeters, new Vector2(12f, 16f) * Ft, 0.0001f, "Authored = declared 12 × 16 ft.");
        Assert.That(bake.ShrinkFootprintMeters.x, Is.LessThanOrEqualTo(12f * Ft + 0.0001f));
        Assert.That(bake.ShrinkFootprintMeters.y, Is.LessThanOrEqualTo(16f * Ft + 0.0001f));
        AssertNear(attraction.PackingBake.AuthoredFootprintMeters, new Vector2(30f, 52f) * Ft, 0.0001f, "Base = Size.");
        AssertContentScalesWithEnvelope(attraction, frame.id);
        Assert.That(AttractionPackingBaker.GetFrameWarnings(attraction),
            Has.Some.Contains("‘Frame 1’: content overhangs its 12 × 16 ft frame"));
    }

    [Test]
    public void Bake_FramelessOffCentreAttractionIsUnchanged()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.customSize = new Vector2(10f, 12f);
        CreateSizedProp("E_SHIP", 0f, 3.5f, 2f, 1f);
        CreateSizedProp("Cannon", 0f, 2f, 1f, 1f);

        AttractionPackingBaker.Bake(attraction, false);

        AttractionPackingBake bake = attraction.PackingBake;
        Assert.That(bake.LayoutFootprintMeters.y, Is.EqualTo(8f).Within(0.01f), "Frame-less keeps the symmetric measure.");
        foreach (AttractionPropPackingPose pose in bake.Props)
            Assert.That(pose.AuthoredLocalPosition, Is.EqualTo(pose.Prop.localPosition), pose.DisplayName);
        AssertNear(bake.AuthoredFootprintMeters, new Vector2(10f, 12f) * Ft, 0.0001f, "Frame-less: authored = Size.");
    }

    // ------------------------------------------------------------------ helpers (frame layout checks)

    // 1 / (as-placed content ÷ declared size), per axis, for content that overhangs.
    private static Vector2 InverseOverhang(AttractionTemplate attraction, string frameId)
    {
        AttractionPackingBake bake = BakeOf(attraction, frameId);
        Vector2 ratio = Vector2.Max(Vector2.one, new Vector2(
            bake.LayoutFootprintMeters.x / bake.AuthoredFootprintMeters.x,
            bake.LayoutFootprintMeters.y / bake.AuthoredFootprintMeters.y));
        return new Vector2(1f / ratio.x, 1f / ratio.y);
    }

    private static string SelectedFrame(AttractionTemplate attraction, Vector2 start, Vector2 end, float t)
    {
        attraction.TrySelectFrame(Vector2.Lerp(start, end, t), out string frameId, out _);
        return frameId;
    }

    // The live content extent of the frame the attraction picks for a Size-line point.
    private Vector2 ContentAt(AttractionTemplate attraction, Vector2 start, Vector2 end, float t, out Vector2 space)
    {
        space = Vector2.Lerp(start, end, t);
        try
        {
            attraction.ApplyFootprint(space);
            return Extent(ActiveBoxes());
        }
        finally
        {
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);
        }
    }

    // Along the Size line (smallest shrink → largest grow) the chosen footprint equals
    // the space everywhere (1 cm) and never shrinks as the space grows.
    private static void AssertSizeLineTracksExactly(AttractionTemplate attraction, int samples)
    {
        AttractionPackingBaker.GetSizeLine(attraction.GetFrameEnvelopes(), out Vector2 start, out Vector2 end);
        Assert.That(start, Is.EqualTo(attraction.MinimumFootprintMeters));
        Vector2 previous = Vector2.zero;
        for (int i = 0; i <= samples; i++)
        {
            Vector2 target = Vector2.Lerp(start, end, (float)i / samples);
            Assert.That(attraction.TrySelectFrame(target, out string frameId, out Vector2 scale), Is.True, $"{target}");
            Vector2 footprint = attraction.GetFrameFootprintMeters(frameId, scale);
            Assert.That(footprint.x, Is.EqualTo(target.x).Within(0.01f), $"width at {i}/{samples} ({frameId})");
            Assert.That(footprint.y, Is.EqualTo(target.y).Within(0.01f), $"length at {i}/{samples} ({frameId})");
            Assert.That(footprint.x, Is.GreaterThanOrEqualTo(previous.x - 0.0001f), "monotonic width");
            Assert.That(footprint.y, Is.GreaterThanOrEqualTo(previous.y - 0.0001f), "monotonic length");
            previous = footprint;
        }
    }

    // Declared-relative honesty: at any width/length scale the live content is never
    // bigger, relative to the reserved footprint, than it is as authored (content that
    // fits keeps fitting; content that overhangs overhangs by no more than authored).
    private void AssertContentScalesWithEnvelope(AttractionTemplate attraction, string frameId)
    {
        Assert.That(attraction.TryGetFrameEnvelope(frameId, out AttractionFrameEnvelope envelope), Is.True, frameId);
        Vector2 layout = BakeOf(attraction, frameId).LayoutFootprintMeters;
        try
        {
            foreach (float sx in new[] { envelope.shrinkScale.x, 1f, envelope.growScale.x })
            foreach (float sz in new[] { envelope.shrinkScale.y, 1f, envelope.growScale.y })
            {
                Vector2 scale = new Vector2(sx, sz);
                attraction.ApplyLayout(frameId, scale);
                Vector2 reserved = attraction.GetFrameFootprintMeters(frameId, scale);
                Vector2 content = Extent(ActiveBoxes());
                Vector2 ratio = Vector2.Max(Vector2.one, new Vector2(
                    layout.x / envelope.authoredMeters.x, layout.y / envelope.authoredMeters.y));
                Assert.That(content.x, Is.LessThanOrEqualTo(reserved.x * ratio.x + 0.01f), $"{envelope.name} width at {scale}");
                Assert.That(content.y, Is.LessThanOrEqualTo(reserved.y * ratio.y + 0.01f), $"{envelope.name} length at {scale}");
            }
        }
        finally
        {
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);
        }
    }

    private struct Box
    {
        public string name;
        public Vector2 center;
        public Vector2 half;
    }

    private PropTemplate CreateSizedProp(string name, float x, float z, float width, float length)
    {
        PropTemplate prop = CreateProp(name, x);
        prop.transform.localPosition = new Vector3(x, 0f, z);
        prop.customFootprintMeters = new Vector2(width, length);
        return prop;
    }

    private static AttractionPropPackingPose Pose(Transform prop, Vector3 authored, Vector3 shrunk) =>
        new AttractionPropPackingPose(prop, prop.name, prop.name, "", false, true, 0,
            prop.GetComponent<PropTemplate>().FootprintMeters, authored, shrunk, authored, shrunk);

    private static AttractionPackingBake BakeOf(AttractionTemplate attraction, string frameId) =>
        frameId == AttractionTemplate.BaseFrameId ? attraction.PackingBake : attraction.FindFrame(frameId).Bake;

    // Symmetric footprint of a bake's props at their shrunk poses (props are root children).
    private static Vector2 ShrunkExtent(AttractionPackingBake bake)
    {
        Vector2 extent = Vector2.zero;
        foreach (AttractionPropPackingPose pose in bake.Props)
            extent = Vector2.Max(extent, new Vector2(
                Mathf.Abs(pose.ShrunkLocalPosition.x) + pose.FootprintMeters.x * 0.5f,
                Mathf.Abs(pose.ShrunkLocalPosition.z) + pose.FootprintMeters.y * 0.5f));
        return extent * 2f;
    }

    private static void AssertNoNewOverlapAtShrink(AttractionPackingBake bake, float clearance)
    {
        IReadOnlyList<AttractionPropPackingPose> poses = bake.Props;
        for (int i = 0; i < poses.Count; i++)
        for (int j = i + 1; j < poses.Count; j++)
        {
            Vector2 reach = (poses[i].FootprintMeters + poses[j].FootprintMeters) * 0.5f;
            Vector3 authored = poses[i].AuthoredLocalPosition - poses[j].AuthoredLocalPosition;
            bool closeAsAuthored = Mathf.Abs(authored.x) < reach.x + clearance && Mathf.Abs(authored.z) < reach.y + clearance;
            float allowed = closeAsAuthored ? 0f : clearance;
            Vector3 shrunk = poses[i].ShrunkLocalPosition - poses[j].ShrunkLocalPosition;
            bool overlap = Mathf.Abs(shrunk.x) < reach.x + allowed - 0.001f
                           && Mathf.Abs(shrunk.z) < reach.y + allowed - 0.001f;
            Assert.That(overlap, Is.False, $"{poses[i].DisplayName} and {poses[j].DisplayName} overlap when shrunk");
        }
    }

    // Every active PropTemplate's footprint in attraction space (axis-aligned props).
    private List<Box> ActiveBoxes()
    {
        var boxes = new List<Box>();
        foreach (PropTemplate prop in _root.GetComponentsInChildren<PropTemplate>(false))
        {
            if (prop.transform == _root.transform) continue;
            Vector3 position = _root.transform.InverseTransformPoint(prop.transform.position);
            boxes.Add(new Box
            {
                name = prop.name,
                center = new Vector2(position.x, position.z),
                half = prop.FootprintMeters * 0.5f,
            });
        }
        return boxes;
    }

    private static Vector2 Extent(List<Box> boxes)
    {
        Vector2 extent = Vector2.zero;
        foreach (Box box in boxes)
            extent = Vector2.Max(extent, new Vector2(
                Mathf.Abs(box.center.x) + box.half.x, Mathf.Abs(box.center.y) + box.half.y));
        return extent * 2f;
    }

    // Applies the frame on the live object at a 5 × 5 grid of independent width/length
    // scales (shrink, halfway, authored, halfway, grow) and checks every pair.
    private void AssertNoCollisionOnGrid(AttractionTemplate attraction, AttractionFrameEnvelope envelope)
    {
        float clearance = attraction.shrinkClearanceMeters;
        float[] xs = GridScales(envelope.shrinkScale.x, envelope.growScale.x);
        float[] zs = GridScales(envelope.shrinkScale.y, envelope.growScale.y);
        attraction.ApplyLayout(envelope.frameId, Vector2.one);
        List<Box> authored = ActiveBoxes();
        var close = new HashSet<string>();
        for (int i = 0; i < authored.Count; i++)
        for (int j = i + 1; j < authored.Count; j++)
            if (BoxesOverlap(authored[i], authored[j], clearance, 0f))
                close.Add(authored[i].name + "|" + authored[j].name);
        try
        {
            foreach (float sx in xs)
            foreach (float sz in zs)
            {
                attraction.ApplyLayout(envelope.frameId, new Vector2(sx, sz));
                List<Box> boxes = ActiveBoxes();
                for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    bool wasClose = close.Contains(boxes[i].name + "|" + boxes[j].name);
                    Assert.That(BoxesOverlap(boxes[i], boxes[j], wasClose ? 0f : clearance, 0.001f), Is.False,
                        $"{envelope.name}: {boxes[i].name} and {boxes[j].name} overlap at width ×{sx:0.###}, length ×{sz:0.###}");
                }
            }
        }
        finally
        {
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);
        }
    }

    private static float[] GridScales(float shrink, float grow) => new[]
    {
        shrink, Mathf.Lerp(shrink, 1f, 0.5f), 1f, Mathf.Lerp(1f, grow, 0.5f), grow,
    };

    private static bool BoxesOverlap(Box a, Box b, float clearance, float tolerance) =>
        Mathf.Abs(a.center.x - b.center.x) < a.half.x + b.half.x + clearance - tolerance
        && Mathf.Abs(a.center.y - b.center.y) < a.half.y + b.half.y + clearance - tolerance;

    private static void AssertNear(Vector2 actual, Vector2 expected, float tolerance, string message)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), message);
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), message);
    }

    private AttractionTemplate CreateSyntheticLadder(
        out AttractionLayoutFrame custom,
        out AttractionLayoutFrame minimum)
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.SetPackingBake(SyntheticBake(new Vector2(20f, 20f) * Ft, new Vector2(4f, 4f)));
        custom = SyntheticFrame(Frame("fcustom01", "Mid", 10f, 10f), new Vector2(2f, 2f));
        minimum = SyntheticFrame(MinimumFrameOf(4f, 4f), new Vector2(0.6f, 0.6f));
        // List order differs from ladder order on purpose.
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { minimum, custom });
        return attraction;
    }

    private static AttractionLayoutFrame SyntheticFrame(AttractionLayoutFrame frame, Vector2 shrink)
    {
        frame.SetBake(SyntheticBake(frame.SizeMeters, shrink));
        return frame;
    }

    private static AttractionPackingBake SyntheticBake(Vector2 authored, Vector2 shrink) =>
        new AttractionPackingBake(authored, authored, shrink, authored * 1.25f, shrink,
            new List<AttractionPropPackingPose>());

    private static AttractionLayoutFrame Frame(string id, string name, float widthFeet, float lengthFeet,
        params AttractionLayoutOverride[] overrides)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = name,
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = new Vector2(widthFeet, lengthFeet),
        };
        frame.overrides.AddRange(overrides);
        return frame;
    }

    private static AttractionLayoutFrame MinimumFrameOf(float widthFeet, float lengthFeet,
        params AttractionLayoutOverride[] overrides)
    {
        AttractionLayoutFrame frame = Frame(AttractionTemplate.MinimumFrameId, "Minimum",
            widthFeet, lengthFeet, overrides);
        frame.kind = AttractionLayoutFrameKind.Minimum;
        return frame;
    }

    private AttractionLayoutOverride Hide(Transform target) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setActive = true,
        active = false,
    };

    private AttractionLayoutOverride Show(Transform target) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setActive = true,
        active = true,
    };

    private AttractionLayoutOverride Move(Transform target, Vector3 localPosition) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setPosition = true,
        localPosition = localPosition,
    };

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

    private Transform CreateFollower(string name, AttractionPackingScaleMode mode)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = name;
        go.transform.SetParent(_root.transform, false);
        go.AddComponent<AttractionPackingFollower>().scaleMode = mode;
        return go.transform;
    }

    // A plain "Bricks" group (no PropTemplate) of five 1 m cubes spanning -3..3 m.
    private Transform[] CreateBrickRow(bool withColliders)
    {
        var bricks = new GameObject("Bricks");
        bricks.transform.SetParent(_root.transform, false);
        var row = new Transform[5];
        for (int i = 0; i < row.Length; i++)
            row[i] = CreateCube(bricks.transform, $"Brick ({i})", -2.5f + 1.25f * i, withColliders);
        return row;
    }

    // Without a collider the measurement takes the mesh/renderer path.
    private static Transform CreateCube(Transform parent, string name, float x, bool withCollider)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (!withCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(x, 0f, 0f);
        return go.transform;
    }

    private static void AssertNear(Vector2 actual, Vector2 expected, string message)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f), message);
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f), message);
    }
}
