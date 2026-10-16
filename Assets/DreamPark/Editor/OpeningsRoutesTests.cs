using System.Collections.Generic;
using System.Reflection;
using DreamPark;
using DreamPark.ParkSim;
using NUnit.Framework;
using UnityEngine;
using XLua;
using Object = UnityEngine.Object;

// Wave 2 (Oct 8 2026): walls + rect openings end to end on the attraction, and the
// Routes Lua surface (dp.ports / dp.route / dp.tour). The core side (scan → borders →
// SeatOpenings / SeatWalls / ArenaFacing on real scans) is dreampark-core
// Tools/openings-e2e-offline; the pure Lua planner is Tools/routes-offline.
public sealed class OpeningsRoutesTests
{
    private const float Ft = 0.3048f;
    private readonly List<GameObject> _roots = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        foreach (var r in _roots) if (r != null) Object.DestroyImmediate(r);
        _roots.Clear();
    }

    private AttractionTemplate Attraction(string name, Vector2 feet, Vector3 position, bool frames = false)
    {
        var root = new GameObject(name);
        _roots.Add(root);
        root.transform.position = position;
        var a = root.AddComponent<AttractionTemplate>();
        a.size = GameLevelSize.Custom;
        a.customSize = feet;
        a.safeArea = 0f;
        var go = new GameObject("Game");
        go.transform.SetParent(root.transform, false);
        var prop = go.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;
        if (frames)
            a.SetLayoutFrames(new List<AttractionLayoutFrame>
            {
                new AttractionLayoutFrame { id = "fsmall001", name = "Small", kind = AttractionLayoutFrameKind.Custom, sizeFeet = feet * 0.5f },
            });
        AttractionPackingBaker.Bake(a, false);
        a.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);
        return a;
    }

    private static LuaEnv Env()
    {
        typeof(DreamParkLuaAPI).GetMethod("RegisterLua", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        return LuaBehaviour.GetLuaEnv();
    }

    // ── LayoutLab finding 3: the all-open default follows the layout ─────────
    [Test]
    public void NoParkData_LayoutChangeFiresRectChanged_ParkDataDoesNot()
    {
        AttractionTemplate a = Attraction("A", new Vector2(20f, 20f), Vector3.zero, frames: true);
        var sizes = new List<float>();
        a.RectChanged += t => sizes.Add(t.RectOpenings.rectSize.x);
        a.ApplyLayout("fsmall001", Vector2.one);
        Assert.That(sizes.Count, Is.EqualTo(1), "the default rect is the footprint: it changed");
        Assert.That(sizes[0], Is.EqualTo(10f * Ft).Within(1e-3f));
        a.ApplyLayout("fsmall001", Vector2.one);
        Assert.That(sizes.Count, Is.EqualTo(1), "no change, no event");

        a.SetRectOpenings(ParkSimRect.Preset("Four Walls", new Vector2(4f, 4f)).Resolve(Vector2.zero, 0));
        Assert.That(sizes.Count, Is.EqualTo(2), "seated");
        a.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);
        Assert.That(sizes.Count, Is.EqualTo(2), "park data: the rect does not follow the footprint");
        Assert.That(a.RectOpeningsSource, Is.EqualTo("park"));
    }

    // ── a/b, turns, entry side on the simulated rect (mirrors core SeatOpenings) ──
    [Test]
    public void ParkSimRect_DoorLandsOnTheSameRectPointsAtEveryTurn_AndEntrySideFacesIt()
    {
        ParkSimRect rect = ParkSimRect.Preset("Front Door", new Vector2(4f, 3f));   // door on minZ, x −0.45…0.45
        var pos = new Vector2(0.3f, -0.2f);
        for (int turns = 0; turns < 4; turns++)
        {
            AttractionRectOpenings o = rect.Resolve(pos, turns);
            int doors = 0;
            foreach (AttractionWallSide side in o.Sides)
                foreach (AttractionRectOpening op in o.Openings(side))
                {
                    if (op.type != "door") continue;
                    doors++;
                    Vector2 p0 = ToRect(o.LocalPoint(side, op.a), pos, turns), p1 = ToRect(o.LocalPoint(side, op.b), pos, turns);
                    Assert.That(Mathf.Abs(p0.y + 1.5f) < 1e-3f && Mathf.Abs(p1.y + 1.5f) < 1e-3f, $"turns {turns}: on the minZ edge ({p0}, {p1})");
                    Assert.That(Mathf.Min(p0.x, p1.x), Is.EqualTo(-0.45f).Within(1e-3f));
                    Assert.That(Mathf.Max(p0.x, p1.x), Is.EqualTo(0.45f).Within(1e-3f));
                    Vector3 right = Vector3.Cross(Vector3.up, AttractionRectOpenings.Outward(side));
                    Assert.That(Vector3.Dot(o.LocalPoint(side, op.b) - o.LocalPoint(side, op.a), right), Is.GreaterThan(0f), "t grows left → right looking out");
                    Assert.That(o.entrySide, Is.EqualTo(AttractionWallSides.Name(side)), "the entry side is the side facing the door edge");
                }
            Assert.That(doors, Is.EqualTo(1), "turns " + turns);
        }
    }

    private static Vector2 ToRect(Vector3 local, Vector2 pos, int turns)
    {
        float rad = 90f * turns * Mathf.Deg2Rad, c = Mathf.Cos(rad), s = Mathf.Sin(rad);
        return new Vector2(pos.x + c * local.x + s * local.z, pos.y - s * local.x + c * local.z);
    }

    // ── a side is walled iff its wall layer applies ─────────────────────────
    [Test]
    public void WalledSide_IffItsWallLayerApplies_AtEveryTurn()
    {
        AttractionTemplate a = Attraction("A", new Vector2(20f, 20f), Vector3.zero);
        var extra = new GameObject("FrontExtra");
        extra.transform.SetParent(a.transform, false);
        extra.SetActive(false);
        a.SetWallLayers(new List<AttractionWallLayer>
        {
            new AttractionWallLayer
            {
                id = "wfront001", side = AttractionWallSide.Front, authoredFootprintMeters = new Vector2(20f, 20f) * Ft,
                overrides = { new AttractionLayoutOverride { path = AttractionPackingBaker.PathFrom(a.transform, extra.transform), target = extra.transform, setActive = true, active = true } },
            },
        });
        AttractionPackingBaker.Bake(a, false);
        Vector2 fp = a.RuntimeFootprintMeters;
        // Mixed: minX is a full wall; the other edges are walls with doors/openings/a rect gap. v3: a side
        // is walled when ≥ half of it is wall, so check against the data. The Front faces minX at 3 turns.
        ParkSimRect rect = ParkSimRect.Preset("Mixed", fp);
        for (int turns = 0; turns < 4; turns++)
        {
            rect.Apply(a, Vector2.zero, turns);
            bool frontWalled = a.HasWall(AttractionWallSide.Front);
            Assert.That(frontWalled, Is.EqualTo(a.RectOpenings.IsWalled(AttractionWallSide.Front)), "turns " + turns + ": " + a.WallsWire);
            if (turns == 3) Assert.That(frontWalled, Is.True, "the full minX wall");
            Assert.That(extra.activeSelf, Is.EqualTo(frontWalled), "the Front layer applies iff Front is walled");
            foreach (AttractionWallSide side in AttractionWallSides.All)
                Assert.That(a.HasWall(side), Is.EqualTo(a.RectOpenings.IsWalled(side)), $"turns {turns} {side}: walled ⇔ its rect edge is ≥ half wall (flush)");
        }
        // A smaller footprint in the middle of the same rect is against no wall.
        rect = ParkSimRect.Preset("Four Walls", fp * 2f);
        rect.Apply(a, Vector2.zero, 0);
        Assert.That(a.Walls, Is.EqualTo(AttractionWallMask.None));
        Assert.That(extra.activeSelf, Is.False);
    }

    // ── dp.rect_openings(): entry_side, wall_height, source, walled ──────────
    [Test]
    public void RectOpeningsLua_CarriesEntryWallHeightSourceAndWalled()
    {
        AttractionTemplate a = Attraction("A", new Vector2(10f, 10f), Vector3.zero);
        LuaEnv env = Env();
        LuaTable none = DreamParkLuaAPI.RectOpeningsToLua(env, a);
        Assert.That(none.Get<string>("source"), Is.EqualTo("default"));
        Assert.That(none.Get<string>("entry_side"), Is.EqualTo("back"), "no data: the attraction front (default Entry portal, −Z)");
        ParkSimRect.Preset("Front Door", a.RuntimeFootprintMeters).Apply(a, Vector2.zero, 2);   // turned 180°: Front faces minZ
        LuaTable t = DreamParkLuaAPI.RectOpeningsToLua(env, a);
        Assert.That(t.Get<string>("source"), Is.EqualTo("park"));
        Assert.That(t.Get<string>("entry_side"), Is.EqualTo("front"));
        Assert.That(t.Get<LuaTable>("left").Get<float>("wall_height"), Is.EqualTo(2.4f).Within(1e-4f));
        Assert.That(t.Get<LuaTable>("left").Get<bool>("walled"), Is.True);
        // v3: walled = wall spans cover ≥ half the side, so a wall with a 0.9 m door in it is walled.
        Assert.That(t.Get<LuaTable>("front").Get<bool>("walled"), Is.True, "a mostly-wall edge with a door is walled");
        Assert.That(t.Get<int>("version"), Is.EqualTo(3));
    }

    // ── dp.ports(): portals resolved against the openings ────────────────────
    [Test]
    public void LuaPorts_SitOnTheirOpeningOrAreBlocked()
    {
        AttractionTemplate a = Attraction("A", new Vector2(10f, 10f), Vector3.zero);
        a.SetPortals(new List<AttractionPortal>
        {
            new AttractionPortal(AttractionPortalKind.Entry, 0f),                     // middle of −Z (Back)
            new AttractionPortal(AttractionPortalKind.Both, 0.5f) { width = 0.8f, tag = "track" },   // middle of +Z (Front)
            // 3 cm past the door's far end (Back side t ≈ 2.004 m, door 1.074–1.974): a wall, not the door.
            new AttractionPortal(AttractionPortalKind.Exit, 0.48f / (10f * Ft * 0.5f) / 8f),
        });
        ParkSimRect.Preset("Front Door", a.RuntimeFootprintMeters).Apply(a, Vector2.zero, 0);   // door on minZ = Back
        LuaEnv env = Env();
        env.Global.Set("__t_go", a.gameObject);
        env.DoString("__t_ports = dp.ports(__t_go)");
        var ports = env.Global.Get<LuaTable>("__t_ports");
        Assert.That(ports.Length, Is.EqualTo(3));
        LuaTable entry = ports.Get<int, LuaTable>(1), both = ports.Get<int, LuaTable>(2);
        Assert.That(entry.Get<string>("side"), Is.EqualTo("back"));
        Assert.That(entry.Get<bool>("blocked"), Is.False);
        Assert.That(entry.Get<LuaTable>("opening").Get<string>("type"), Is.EqualTo("door"));
        Assert.That(entry.Get<LuaTable>("opening").Get<string>("to"), Is.EqualTo("sim_hall"));
        Assert.That(both.Get<string>("kind"), Is.EqualTo("both"));
        Assert.That(both.Get<string>("tag"), Is.EqualTo("track"));
        Assert.That(both.Get<bool>("blocked"), Is.True, "the front side is a full wall");
        Assert.That(both.Get<LuaTable>("opening"), Is.Null);
        LuaTable past = ports.Get<int, LuaTable>(3);
        Assert.That(past.Get<float>("t"), Is.EqualTo(2.004f).Within(0.01f));
        Assert.That(past.Get<bool>("blocked"), Is.True, "a portal just past an opening's end faces the wall (LayoutLab 2×3_WM)");
    }

    // ── dp.route / dp.tour / dp.neighbors across two seated attractions ───────
    [Test]
    public void LuaRouteAndTour_GoThroughTheSharedDoor()
    {
        // Two 3 × 3 m rooms side by side (10 cm wall), a 0.9 m door between them.
        AttractionTemplate a = Attraction("RoomA", new Vector2(9.84f, 9.84f), new Vector3(0f, 0f, 0f));
        AttractionTemplate b = Attraction("RoomB", new Vector2(9.84f, 9.84f), new Vector3(3.1f, 0f, 0f));
        var ra = ParkSimRect.Preset("Four Walls", new Vector2(3f, 3f)); ra.id = "rA";
        ra.maxX = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening(1.1f, 2.0f, "door", "rB"));
        var rb = ParkSimRect.Preset("Four Walls", new Vector2(3f, 3f)); rb.id = "rB";
        rb.minX = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening(1.1f, 2.0f, "door", "rA"));
        ra.Apply(a, Vector2.zero, 0);
        rb.Apply(b, Vector2.zero, 0);
        LuaEnv env = Env();
        env.Global.Set("__t_a", a.gameObject);
        env.Global.Set("__t_b", b.gameObject);
        env.DoString(@"
__t_r = dp.route(__t_a, __t_b)
__t_t = dp.tour({ __t_a, __t_b })
__t_n = dp.neighbors(__t_a)");
        var r = env.Global.Get<LuaTable>("__t_r");
        Assert.That(r.Get<bool>("ok"), Is.True, r.Get<string>("why"));
        Assert.That(r.Get<int>("hops"), Is.EqualTo(1), "one doorway");
        var pts = r.Get<LuaTable>("pts");
        Vector3 door = pts.Get<int, Vector3>(2);
        Assert.That(door.x, Is.InRange(1.45f, 1.65f), "via the door, on the wall line");
        Assert.That(door.z, Is.InRange(-0.4f, 0.5f));
        var t = env.Global.Get<LuaTable>("__t_t");
        Assert.That(t.Get<bool>("ok"), Is.True, t.Get<string>("why"));
        Assert.That(t.Get<LuaTable>("links").Length, Is.EqualTo(2), "there and back (loop)");
        var n = env.Global.Get<LuaTable>("__t_n");
        Assert.That(n.Length, Is.EqualTo(1));
        Assert.That(n.Get<int, LuaTable>(1).Get<string>("rect_id"), Is.EqualTo("rB"));
        Assert.That(n.Get<int, LuaTable>(1).Get<GameObject>("attraction"), Is.EqualTo(b.gameObject));
    }

    // ── the park graph: a hall with no attraction joins two rooms (core sets DreamParkRoutes.ParkGraph;
    //    the SDK alone leaves it null and sees only the seated attractions) ──
    [Test]
    public void ParkGraph_LetsRoutesCrossAnEmptyHall_AndTheSdkFallsBackWithoutIt()
    {
        // Room A (x −3.05…−0.05) | hall h1 (…0.5) | hall h2 (…1.05) | Room B (1.05…4.05). The rooms' doors lead
        // into DIFFERENT empty halls, so only the park graph knows h1 ↔ h2.
        AttractionTemplate a = Attraction("RoomA", new Vector2(9.84f, 9.84f), new Vector3(-1.55f, 0f, 0f));
        AttractionTemplate b = Attraction("RoomB", new Vector2(9.84f, 9.84f), new Vector3(2.55f, 0f, 0f));
        var ra = ParkSimRect.Preset("Four Walls", new Vector2(3f, 3f)); ra.id = "rA";
        ra.maxX = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening(1.1f, 2.0f, "door", "h1"));
        var rb = ParkSimRect.Preset("Four Walls", new Vector2(3f, 3f)); rb.id = "rB";
        rb.minX = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening(1.1f, 2.0f, "door", "h2"));
        ra.Apply(a, Vector2.zero, 0);
        rb.Apply(b, Vector2.zero, 0);
        var h1 = ParkSimRect.Preset("Four Walls", new Vector2(0.55f, 3f)); h1.id = "h1";
        h1.minX = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening(1.1f, 2.0f, "door", "rA"));
        h1.maxX = new ParkSimRectEdge { kind = "open", openings = { new AttractionRectOpening(0f, 3f, "rect", "h2") } };
        var h2 = ParkSimRect.Preset("Four Walls", new Vector2(0.55f, 3f)); h2.id = "h2";
        h2.minX = new ParkSimRectEdge { kind = "open", openings = { new AttractionRectOpening(0f, 3f, "rect", "h1") } };
        h2.maxX = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening(1.1f, 2.0f, "door", "rB"));
        LuaEnv env = Env();
        env.Global.Set("__t_a", a.gameObject);
        env.Global.Set("__t_b", b.gameObject);
        DreamParkRoutes.ParkGraph = null;
        try
        {
            env.DoString("__t_r0 = dp.route(__t_a, __t_b)");
            Assert.That(env.Global.Get<LuaTable>("__t_r0").Get<bool>("ok"), Is.False, "SDK alone: the halls are unknown, no doorway path");
            DreamParkRoutes.ParkGraph = () => new List<DreamParkRouteRect>
            {
                new DreamParkRouteRect { id = "h1", center = new Vector3(0.225f, 0f, 0f), yawDeg = 0f, openings = h1.Resolve(Vector2.zero, 0) },
                new DreamParkRouteRect { id = "h2", center = new Vector3(0.775f, 0f, 0f), yawDeg = 0f, openings = h2.Resolve(Vector2.zero, 0) },
            };
            env.DoString("__t_r1 = dp.route(__t_a, __t_b) __t_t1 = dp.tour({ __t_a, __t_b })");
            var r = env.Global.Get<LuaTable>("__t_r1");
            Assert.That(r.Get<bool>("ok"), Is.True, r.Get<string>("why"));
            Assert.That(r.Get<int>("hops"), Is.GreaterThanOrEqualTo(3), "room door, the hall-to-hall gap, room door");
            Assert.That(env.Global.Get<LuaTable>("__t_t1").Get<bool>("ok"), Is.True);
            env.DoString("__t_g = #dp.park_rects()");
            Assert.That(env.Global.Get<int>("__t_g"), Is.EqualTo(2));
        }
        finally { DreamParkRoutes.ParkGraph = null; }
    }

    // ── v3 Lua semantics: wall / opening / unknown, spans, links, open area (RECT_EDGES_SPEC.md) ──
    [Test]
    public void LuaRectApi_ThreeKinds_SpansLinksAndOpenArea()
    {
        AttractionTemplate a = Attraction("A", new Vector2(10f, 10f), Vector3.zero);
        float L = a.RuntimeFootprintMeters.x;
        var o = AttractionRectOpenings.AllUnknown(a.RuntimeFootprintMeters);
        o.rectId = "rA";
        o.hasData = true;
        var front = o.Side(AttractionWallSide.Front).spans;
        front.Clear();
        front.Add(new AttractionRectSpan(0f, 1f, "wall", height: 2.4f, offset: 0.2f));
        front.Add(new AttractionRectSpan(1f, 2f, "opening", "door", 2.0f, touches: "rB"));
        front.Add(new AttractionRectSpan(2f, 3f, "opening", "adjacent", touches: "rC"));
        front.Add(new AttractionRectSpan(3f, L, "unknown"));
        a.SetRectOpenings(o);
        LuaEnv env = Env();
        env.Global.Set("__t_go", a.gameObject);
        env.DoString(@"
local go = __t_go
__t_wall = { dp.rect_is_wall(go, 'front', 0.5), dp.rect_is_open(go, 'front', 0.5), dp.rect_is_opening(go, 'front', 0.5), dp.rect_is_unknown(go, 'front', 0.5) }
__t_door = { dp.rect_is_wall(go, 'front', 1.5), dp.rect_is_open(go, 'front', 1.5), dp.rect_is_opening(go, 'front', 1.5), dp.rect_is_unknown(go, 'front', 1.5) }
__t_adj  = { dp.rect_is_wall(go, 'front', 2.5), dp.rect_is_open(go, 'front', 2.5), dp.rect_is_opening(go, 'front', 2.5), dp.rect_is_unknown(go, 'front', 2.5) }
__t_unk  = { dp.rect_is_wall(go, 'front', 3.2), dp.rect_is_open(go, 'front', 3.2), dp.rect_is_opening(go, 'front', 3.2), dp.rect_is_unknown(go, 'front', 3.2) }
__t_adjtype = dp.rect_span_at(go, 'front', 2.5).type
__t_spans = dp.rect_spans(go, 'front')
__t_area = dp.rect_is_open_area(go)
local t = dp.rect_openings(go)
__t_openings = #t.front.openings
__t_links = t.front.links
__t_passages = #t.front.passages
__t_full = t.back.full_wall");
        bool[] B(string name) { var t = env.Global.Get<LuaTable>(name); return new[] { t.Get<int, bool>(1), t.Get<int, bool>(2), t.Get<int, bool>(3), t.Get<int, bool>(4) }; }
        Assert.That(B("__t_wall"), Is.EqualTo(new[] { true, false, false, false }), "wall: is_wall only");
        Assert.That(B("__t_door"), Is.EqualTo(new[] { false, true, true, false }), "door: open + opening");
        Assert.That(B("__t_adj"), Is.EqualTo(new[] { false, true, true, false }), "adjacent: open + opening");
        Assert.That(env.Global.Get<string>("__t_adjtype"), Is.EqualTo("adjacent"));
        Assert.That(B("__t_unk"), Is.EqualTo(new[] { false, true, false, true }), "unknown: open + unknown");
        var spans = env.Global.Get<LuaTable>("__t_spans");
        Assert.That(spans.Length, Is.EqualTo(4));
        Assert.That(spans.Get<int, LuaTable>(1).Get<string>("kind"), Is.EqualTo("wall"));
        Assert.That(spans.Get<int, LuaTable>(1).Get<float>("offset"), Is.EqualTo(0.2f).Within(1e-4f));
        Assert.That(spans.Get<int, LuaTable>(2).Get<string>("type"), Is.EqualTo("door"));
        Assert.That(spans.Get<int, LuaTable>(2).Get<string>("touches"), Is.EqualTo("rB"));
        Assert.That(spans.Get<int, LuaTable>(3).Get<string>("type"), Is.EqualTo("adjacent"));
        Assert.That(spans.Get<int, LuaTable>(4).Get<string>("kind"), Is.EqualTo("unknown"));
        Assert.That(env.Global.Get<bool>("__t_area"), Is.False);
        Assert.That(env.Global.Get<int>("__t_openings"), Is.EqualTo(2), "the door and the adjacent opening");
        Assert.That(env.Global.Get<int>("__t_passages"), Is.EqualTo(3), "door + adjacent + unknown are passable");
        var links = env.Global.Get<LuaTable>("__t_links");
        Assert.That(links.Length, Is.EqualTo(2), "rB through the door, rC along the adjacent opening");
        LuaTable adjLink = links.Get<int, LuaTable>(2);
        Assert.That(adjLink.Get<string>("to"), Is.EqualTo("rC"));
        Assert.That(adjLink.Get<string>("kind"), Is.EqualTo("opening"));
        Assert.That(adjLink.Get<float>("a"), Is.EqualTo(2f).Within(1e-4f));
        Assert.That(adjLink.Get<Vector3>("p0"), Is.EqualTo(o.LocalPoint(AttractionWallSide.Front, 2f)));
        Assert.That(env.Global.Get<bool>("__t_full"), Is.False, "back has no data: all unknown, not a full wall");

        // No data at all: an open area, nothing walled, nothing an opening.
        a.SetRectOpenings(null);
        env.DoString("__t_area2 = dp.rect_is_open_area(__t_go) __t_w2 = dp.rect_is_wall(__t_go, 'left', 1) __t_o2 = dp.rect_is_opening(__t_go, 'left', 1)");
        Assert.That(env.Global.Get<bool>("__t_area2"), Is.True);
        Assert.That(env.Global.Get<bool>("__t_w2"), Is.False);
        Assert.That(env.Global.Get<bool>("__t_o2"), Is.False);
    }

    // ── all-unknown rects connect from any side, to each other (founder: a gym floor / courtyard) ──
    [Test]
    public void OpenAreaRects_RouteToEachOtherWithoutTouching()
    {
        AttractionTemplate a = Attraction("A", new Vector2(6.56f, 6.56f), new Vector3(0f, 0f, 0f));
        AttractionTemplate b = Attraction("B", new Vector2(6.56f, 6.56f), new Vector3(4f, 0f, 0f));   // 2 m of floor between
        a.SetRectOpenings(new AttractionRectOpenings { rectId = "gA", hasData = true, rectSize = a.RuntimeFootprintMeters,
            sides = AttractionRectOpenings.AllUnknown(a.RuntimeFootprintMeters).sides });
        b.SetRectOpenings(new AttractionRectOpenings { rectId = "gB", hasData = true, rectSize = b.RuntimeFootprintMeters,
            sides = AttractionRectOpenings.AllUnknown(b.RuntimeFootprintMeters).sides });
        LuaEnv env = Env();
        env.Global.Set("__t_a", a.gameObject);
        env.Global.Set("__t_b", b.gameObject);
        DreamParkRoutes.ParkGraph = null;
        env.DoString("__t_r = dp.route(__t_a, __t_b)");
        var r = env.Global.Get<LuaTable>("__t_r");
        Assert.That(r.Get<bool>("ok"), Is.True, r.Get<string>("why"));
    }
}
