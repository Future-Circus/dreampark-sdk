using DreamPark;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// RECT_EDGES_SPEC.md (v3): the seated rect's sides as spans (wall / opening /
// unknown), in the attraction's frame; no data = every side all unknown.
public sealed class AttractionRectOpeningsTests
{
    private GameObject _root;

    [TearDown]
    public void TearDown()
    {
        if (_root != null) Object.DestroyImmediate(_root);
    }

    private AttractionTemplate CreateAttraction()
    {
        _root = new GameObject("Attraction");
        AttractionTemplate attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(10f, 6f);
        attraction.safeArea = 0f;
        return attraction;
    }

    /// v3: Front (4 m) = wall 0–1.0, door 1.0–1.9 → room_5_r0, wall 1.9–2.5, opening 2.5–3.6, unknown
    /// 3.6–4.0; Back = wall end to end; Right / Left = no spans (all unknown).
    private static AttractionRectOpenings FrontDoorRect()
    {
        var o = AttractionRectOpenings.AllUnknown(new Vector2(4f, 3f));
        o.rectId = "room_7_r0";
        o.hasData = true;
        var front = o.Side(AttractionWallSide.Front).spans;
        front.Clear();
        front.Add(new AttractionRectSpan(0f, 1.0f, "wall", height: 2.5f, offset: 0.2f));
        front.Add(new AttractionRectSpan(1.0f, 1.9f, "opening", "door", 2.0f, touches: "room_5_r0"));
        front.Add(new AttractionRectSpan(1.9f, 2.5f, "wall", height: 2.5f, offset: 0.2f));
        front.Add(new AttractionRectSpan(2.5f, 3.6f, "opening", "opening"));
        front.Add(new AttractionRectSpan(3.6f, 4.0f, "unknown"));
        var back = o.Side(AttractionWallSide.Back).spans;
        back.Clear();
        back.Add(new AttractionRectSpan(0f, 4f, "wall", height: 2.4f));
        return o;
    }

    [Test]
    public void NoData_EverySideIsAllUnknown_PassableNotAnOpening()
    {
        AttractionTemplate attraction = CreateAttraction();
        AttractionRectOpenings o = attraction.RectOpenings;
        Assert.That(o.hasData, Is.False);
        Assert.That(attraction.HasParkRectOpenings, Is.False);
        Assert.That(o.IsOpenArea, Is.True);
        foreach (AttractionWallSide side in o.Sides)
        {
            Assert.That(o.Spans(side).Count, Is.EqualTo(1), side.ToString());
            Assert.That(o.Spans(side)[0].kind, Is.EqualTo("unknown"));
            Assert.That(o.Spans(side)[0].Width, Is.EqualTo(o.Length(side)).Within(1e-4f));
            Assert.That(o.Openings(side).Count, Is.EqualTo(0), "unknown is not an opening");
            Assert.That(o.Passages(side).Count, Is.EqualTo(1), "but it is passable");
            Assert.That(o.IsWall(side, o.Length(side) * 0.5f), Is.False);
            Assert.That(o.IsOpen(side, o.Length(side) * 0.5f), Is.True);
            Assert.That(o.IsUnknown(side, o.Length(side) * 0.5f), Is.True);
        }
    }

    [Test]
    public void Queries_FollowTheSpans()
    {
        AttractionRectOpenings o = FrontDoorRect();
        Assert.That(o.IsOpening(AttractionWallSide.Front, 1.5f), Is.True);
        Assert.That(o.IsOpen(AttractionWallSide.Front, 1.5f), Is.True);
        Assert.That(o.IsWall(AttractionWallSide.Front, 2.2f), Is.True);
        Assert.That(o.IsOpen(AttractionWallSide.Front, 2.2f), Is.False, "IsOpen is the exact opposite of IsWall");
        Assert.That(o.IsUnknown(AttractionWallSide.Front, 3.8f), Is.True);
        Assert.That(o.IsOpen(AttractionWallSide.Front, 3.8f), Is.True, "unknown is passable");
        Assert.That(o.IsFullWall(AttractionWallSide.Back), Is.True);
        Assert.That(o.IsOpenArea, Is.False);
        Assert.That(o.Openings(AttractionWallSide.Front).Count, Is.EqualTo(2), "door + opening only");
        Assert.That(o.Passages(AttractionWallSide.Front).Count, Is.EqualTo(3), "door, opening, unknown");
        Assert.That(o.Nearest(AttractionWallSide.Front, 2.3f).type, Is.EqualTo("opening"));
        Assert.That(o.WallOffset(AttractionWallSide.Front), Is.EqualTo(0.2f).Within(1e-4f));
        Assert.That(o.WallHeight(AttractionWallSide.Front), Is.EqualTo(2.5f).Within(1e-4f));
        Assert.That(o.IsWalled(AttractionWallSide.Front), Is.False, "1.6 m wall of 4 m < half");
        Assert.That(o.IsWalled(AttractionWallSide.Back), Is.True);
        Assert.That(o.Kind(AttractionWallSide.Front), Is.EqualTo("opening"), "summary kind = most length: 2.0 m of openings beat 1.6 m of wall");
        AttractionRectOpening widest = o.Widest(out AttractionWallSide side);
        Assert.That(side, Is.EqualTo(AttractionWallSide.Right), "the unknown 3 m side beats the 0.9 m door");
        Assert.That(widest.Width, Is.EqualTo(3f).Within(1e-4f));
        Assert.That(o.Widest(out side, "door").to, Is.EqualTo("room_5_r0"));
    }

    [Test]
    public void Links_ListTouches_WhateverTheKind()
    {
        var o = AttractionRectOpenings.AllUnknown(new Vector2(4f, 3f));
        o.hasData = true;
        var left = o.Side(AttractionWallSide.Left).spans;
        left.Clear();
        left.Add(new AttractionRectSpan(0f, 1f, "unknown"));
        left.Add(new AttractionRectSpan(1f, 3f, "unknown", touches: "room_9_r0"));
        Assert.That(o.Openings(AttractionWallSide.Left).Count, Is.EqualTo(0), "injected data is taken as given: unknown + touches");
        var links = o.Links(AttractionWallSide.Left);
        Assert.That(links.Count, Is.EqualTo(1));
        Assert.That(links[0].to, Is.EqualTo("room_9_r0"));
        Assert.That(links[0].type, Is.EqualTo("unknown"), "the span kind under the link");
        Assert.That(links[0].a, Is.EqualTo(1f).Within(1e-4f));
        Assert.That(links[0].b, Is.EqualTo(3f).Within(1e-4f));
        Assert.That(o.IsOpenArea, Is.True, "touches is an attribute: the rect is still all unknown");
    }

    [Test]
    public void OldData_ConvertsOnRead()
    {
        var o = new AttractionRectOpenings { rectId = "old", rectSize = new Vector2(4f, 3f), hasData = true };
        var front = new AttractionRectSide { side = AttractionWallSide.Front, length = 4f, kind = "door", wallHeight = 2.4f };
        front.openings.Add(new AttractionRectOpening(1.0f, 1.9f, "door", "room_5_r0"));
        front.openings.Add(new AttractionRectOpening(1.9f, 4.0f, "rect", "room_6_r0"));
        o.sides.Add(front);
        o.sides.Add(new AttractionRectSide { side = AttractionWallSide.Back, length = 4f, kind = "wall" });
        o.sides.Add(new AttractionRectSide { side = AttractionWallSide.Right, length = 3f, kind = "open",
            openings = { new AttractionRectOpening(0f, 3f, "open", null) } });
        var spans = o.Spans(AttractionWallSide.Front);
        Assert.That(spans.Count, Is.EqualTo(3), string.Join(", ", spans));
        Assert.That(spans[0].kind, Is.EqualTo("wall"), "blocked → wall");
        Assert.That(spans[0].height, Is.EqualTo(2.4f).Within(1e-4f));
        Assert.That(spans[1].kind, Is.EqualTo("opening"));
        Assert.That(spans[1].type, Is.EqualTo("door"));
        Assert.That(spans[1].touches, Is.EqualTo("room_5_r0"));
        Assert.That(spans[2].kind, Is.EqualTo("opening"), "rect → an adjacent opening");
        Assert.That(spans[2].type, Is.EqualTo("adjacent"));
        Assert.That(spans[2].touches, Is.EqualTo("room_6_r0"), "… with touches");
        Assert.That(o.Openings(AttractionWallSide.Front).Count, Is.EqualTo(2), "door + adjacent");
        Assert.That(o.SpanAt(AttractionWallSide.Front, 3f).IsDoorway, Is.False);
        Assert.That(o.IsFullWall(AttractionWallSide.Back), Is.True, "old full wall (no openings)");
        Assert.That(o.IsAllUnknown(AttractionWallSide.Right), Is.True, "open → unknown");
        Assert.That(o.IsAllUnknown(AttractionWallSide.Left), Is.True, "missing side → unknown");
    }

    [Test]
    public void Spans_FillGapsAndResolveOverlaps()
    {
        var side = new AttractionRectSide { side = AttractionWallSide.Front, length = 5f };
        side.spans.Add(new AttractionRectSpan(0f, 3f, "wall"));
        side.spans.Add(new AttractionRectSpan(2f, 2.9f, "opening", "door"));     // opening over wall
        side.spans.Add(new AttractionRectSpan(4f, 5f, "wall"));                  // gap 3–4 → unknown
        var s = side.Spans();
        Assert.That(s.Count, Is.EqualTo(5), string.Join(", ", s));
        Assert.That(s[1].kind, Is.EqualTo("opening"));
        Assert.That(s[2].kind, Is.EqualTo("wall"));
        Assert.That(s[3].kind, Is.EqualTo("unknown"));
        Assert.That(s[3].a, Is.EqualTo(3f).Within(1e-4f));
        Assert.That(s[4].b, Is.EqualTo(5f).Within(1e-4f));
    }

    [Test]
    public void LocalPoint_RunsFromEachSidesLeftCornerSeenFromInside()
    {
        var o = AttractionRectOpenings.AllUnknown(new Vector2(4f, 2f));
        o.rectCenter = new Vector2(1f, 0f);
        Assert.That(o.LocalPoint(AttractionWallSide.Front, 0f), Is.EqualTo(new Vector3(-1f, 0f, 1f)));   // left = −X
        Assert.That(o.LocalPoint(AttractionWallSide.Back, 0f), Is.EqualTo(new Vector3(3f, 0f, -1f)));    // left = +X
        Assert.That(o.LocalPoint(AttractionWallSide.Right, 0f), Is.EqualTo(new Vector3(3f, 0f, 1f)));    // left = +Z
        Assert.That(o.LocalPoint(AttractionWallSide.Left, 0f), Is.EqualTo(new Vector3(-1f, 0f, -1f)));   // left = −Z
        Assert.That(o.Length(AttractionWallSide.Right), Is.EqualTo(2f));
    }

    [Test]
    public void SetRectOpenings_FiresOnlyWhenSomethingChanges()
    {
        AttractionTemplate attraction = CreateAttraction();
        int fired = 0;
        attraction.RectChanged += _ => fired++;
        attraction.SetRectOpenings(FrontDoorRect());
        Assert.That(fired, Is.EqualTo(1));
        Assert.That(attraction.RectOpenings.hasData, Is.True);
        Assert.That(attraction.RectOpenings.rectId, Is.EqualTo("room_7_r0"));
        attraction.SetRectOpenings(FrontDoorRect());
        Assert.That(fired, Is.EqualTo(1), "identical data does not re-fire");
        var moved = FrontDoorRect();
        moved.rectCenter = new Vector2(0.5f, 0f);
        attraction.SetRectOpenings(moved);
        Assert.That(fired, Is.EqualTo(2), "a re-seat that moves the attraction in its rect fires");
        attraction.SetRectOpenings(null);
        Assert.That(fired, Is.EqualTo(3));
        Assert.That(attraction.RectOpenings.hasData, Is.False, "null reverts to all unknown");
    }

    [Test]
    public void EditorPreview_IsUsedOnlyWithoutParkData()
    {
        AttractionTemplate attraction = CreateAttraction();
        attraction.SetRectOpeningsPreview(FrontDoorRect(), true);
        Assert.That(attraction.RectOpenings.rectId, Is.EqualTo("room_7_r0"));
        Assert.That(attraction.RectOpenings.hasData, Is.True);
        var park = FrontDoorRect();
        park.rectId = "park_rect";
        attraction.SetRectOpenings(park);
        Assert.That(attraction.RectOpenings.rectId, Is.EqualTo("park_rect"), "core's data wins");
        attraction.SetRectOpenings(null);
        attraction.SetRectOpeningsPreview(FrontDoorRect(), false);
        Assert.That(attraction.RectOpenings.hasData, Is.False);
    }

    [Test]
    public void SampleData_HasAFrontDoor()
    {
        AttractionRectOpenings sample = AttractionRectOpeningsInspector.Sample(new Vector2(3f, 2f));
        Assert.That(sample.Openings(AttractionWallSide.Front).Count, Is.EqualTo(1));
        Assert.That(sample.Openings(AttractionWallSide.Front)[0].type, Is.EqualTo("door"));
        Assert.That(sample.IsFullWall(AttractionWallSide.Back), Is.True);
        Assert.That(sample.Links(AttractionWallSide.Left)[0].to, Is.EqualTo("preview_neighbour"));
        Assert.That(sample.IsAllUnknown(AttractionWallSide.Left), Is.True);
    }
}
