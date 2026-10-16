using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// Entries & exits: the 0-1 perimeter coordinate, per-frame inheritance and the
// catalog packing payload. In-memory only.
public sealed class AttractionPortalTests
{
    private const float Ft = 0.3048f;
    private GameObject _root;

    [TearDown]
    public void TearDown()
    {
        if (_root != null) Object.DestroyImmediate(_root);
    }

    [Test]
    public void PerimeterCoordinateStartsAtMiddleOfMinusZAndRunsClockwise()
    {
        var footprint = new Vector2(4f, 6f);
        AssertNear(AttractionPortalGeometry.LocalPoint(0f, footprint), new Vector3(0f, 0f, -3f));
        AssertNear(AttractionPortalGeometry.LocalPoint(0.125f, footprint), new Vector3(-2f, 0f, -3f));
        AssertNear(AttractionPortalGeometry.LocalPoint(0.25f, footprint), new Vector3(-2f, 0f, 0f));
        AssertNear(AttractionPortalGeometry.LocalPoint(0.5f, footprint), new Vector3(0f, 0f, 3f));
        AssertNear(AttractionPortalGeometry.LocalPoint(0.75f, footprint), new Vector3(2f, 0f, 0f));
        AssertNear(AttractionPortalGeometry.LocalPoint(1f, footprint), new Vector3(0f, 0f, -3f));

        // Same sense as Unity yaw: t × 360° rotates -Z to the portal (mid-edges and corners).
        for (int i = 0; i < 8; i++)
        {
            float t = i / 8f;
            Vector3 onSquare = AttractionPortalGeometry.LocalPoint(t, Vector2.one * 2f).normalized;
            AssertNear(onSquare, Quaternion.Euler(0f, t * 360f, 0f) * Vector3.back);
        }
    }

    [Test]
    public void LocalPointRoundTripsAndKeepsItsPlaceWhenTheLayoutScales()
    {
        var footprint = new Vector2(4f, 6f);
        for (int i = 0; i < 400; i++)
        {
            float t = i / 400f;
            Assert.That(AttractionPortalGeometry.FromLocalPoint(
                AttractionPortalGeometry.LocalPoint(t, footprint), footprint), Is.EqualTo(t).Within(1e-5f));
        }
        Vector3 a = AttractionPortalGeometry.LocalPoint(0.06f, new Vector2(4f, 6f));
        Vector3 b = AttractionPortalGeometry.LocalPoint(0.06f, new Vector2(2f, 9f));
        Assert.That(a.x / 2f, Is.EqualTo(b.x / 1f).Within(1e-5f), "same relative place on its edge");
        Assert.That(b.z, Is.EqualTo(-4.5f).Within(1e-5f), "still on the -Z edge");
    }

    [Test]
    public void EntriesWalkInAndExitsWalkOut()
    {
        Assert.That(AttractionPortalGeometry.WalkDirection(
            new AttractionPortal(AttractionPortalKind.Entry, 0f)), Is.EqualTo(Vector3.forward));
        Assert.That(AttractionPortalGeometry.WalkDirection(
            new AttractionPortal(AttractionPortalKind.Exit, 0.5f)), Is.EqualTo(Vector3.forward));
        Assert.That(AttractionPortalGeometry.WalkDirection(
            new AttractionPortal(AttractionPortalKind.Exit, 0.75f)), Is.EqualTo(Vector3.right));
    }

    [Test]
    public void EveryAttractionStartsWithOneEntryAtMiddleOfMinusZ()
    {
        AttractionTemplate attraction = NewAttraction();
        Assert.That(attraction.Portals.Count, Is.EqualTo(1));
        Assert.That(attraction.Portals[0].IsEntry, Is.True);
        Assert.That(attraction.Portals[0].position, Is.EqualTo(0f));
        Assert.That(attraction.HasEntry(), Is.True);
    }

    [Test]
    public void FramesFollowBaseUntilTheyOverride()
    {
        AttractionTemplate attraction = NewAttraction();
        AttractionLayoutFrame frame = Custom("fsmall001", "Small", 10f, 10f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { frame });

        Assert.That(attraction.GetPortals(frame.id), Is.SameAs(attraction.Portals));
        frame.overridePortals = true;
        frame.portals = new List<AttractionPortal> { new AttractionPortal(AttractionPortalKind.Exit, 0.25f) };
        Assert.That(attraction.GetPortals(frame.id).Single().IsExit, Is.True);
        Assert.That(attraction.HasEntry(frame.id), Is.False);
        Assert.That(attraction.GetPortals("unknown"), Is.SameAs(attraction.Portals));
        Assert.That(attraction.CurrentPortals, Is.SameAs(attraction.Portals), "Base until a layout is applied");
    }

#if !DREAMPARKCORE
    // The Content Uploader is SDK-only (compiled out of dreampark-core).
    [Test]
    public void CatalogPackingCarriesEffectivePortalsForBaseAndEveryFrame()
    {
        AttractionTemplate attraction = NewAttraction();
        attraction.SetPortals(new List<AttractionPortal>
        {
            new AttractionPortal(AttractionPortalKind.Entry, 0f),
            new AttractionPortal(AttractionPortalKind.Exit, 0.5f),
        });
        AttractionLayoutFrame follows = Custom("ffollow01", "Follows", 12f, 12f);
        AttractionLayoutFrame own = Custom("fown00001", "Own", 10f, 10f);
        own.overridePortals = true;
        own.portals = new List<AttractionPortal> { new AttractionPortal(AttractionPortalKind.Entry, 0.25f) };
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { follows, own });

        JSONObject packing = new JSONObject(ContentUploaderPanel.BuildPackingUpload(attraction).ToString());
        Assert.That(packing.GetField("schemaVersion").intValue, Is.EqualTo(AttractionPackingBake.CatalogSchemaVersion));
        List<JSONObject> top = packing.GetField("portals").list;
        Assert.That(top.Select(p => p.GetField("kind").stringValue), Is.EqualTo(new[] { "entry", "exit" }));
        Assert.That(top[1].GetField("position").floatValue, Is.EqualTo(0.5f).Within(1e-4f));

        Dictionary<string, JSONObject> frames = packing.GetField("frames").list
            .ToDictionary(f => f.GetField("id").stringValue);
        Assert.That(frames["base"].GetField("portals").count, Is.EqualTo(2));
        Assert.That(frames["ffollow01"].GetField("portals").count, Is.EqualTo(2), "a following frame ships Base's list");
        JSONObject ownPortal = frames["fown00001"].GetField("portals").list.Single();
        Assert.That(ownPortal.GetField("position").floatValue, Is.EqualTo(0.25f).Within(1e-4f));
    }
#endif

    private AttractionTemplate NewAttraction()
    {
        _root = new GameObject("A_Portals");
        var attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.SetPackingBake(SyntheticBake(new Vector2(20f, 20f) * Ft));
        return attraction;
    }

    private static AttractionLayoutFrame Custom(string id, string name, float widthFeet, float lengthFeet)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = name,
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = new Vector2(widthFeet, lengthFeet),
        };
        frame.SetBake(SyntheticBake(frame.SizeMeters));
        return frame;
    }

    private static AttractionPackingBake SyntheticBake(Vector2 authored) =>
        new AttractionPackingBake(authored, authored, authored * 0.5f, authored * 1.25f, authored * 0.5f,
            new List<AttractionPropPackingPose>());

    private static void AssertNear(Vector3 actual, Vector3 expected) =>
        Assert.That((actual - expected).magnitude, Is.LessThan(1e-4f), $"{actual} vs {expected}");
}
