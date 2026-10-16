// ─────────────────────────────────────────────────────────────────────
//  ParkSimRect.cs — a simulated scan rect for the Park Simulator (wave 2,
//  Oct 8 2026; LayoutLab finding 9: the Arena test never applied walls or
//  rect openings, so wall layers and on_rect_changed could not be tried in
//  the Editor without hand-written preview data).
//
//  A ParkSimRect is what core's SpaceMap hands a seat: four edges (minX /
//  maxX / minZ / maxZ, rect frame), each with a kind ("wall", "open", …), a
//  wall height and its openings (a, b in metres from the edge's min-tangent
//  corner: minX/maxX from z, minZ/maxZ from x — RECT_EDGES_SPEC §2).
//  Apply() seats an attraction on it the way core does at runtime:
//    • SetWalls(mask)  — a side is walled when its footprint edge is within
//                        0.10 m of a "wall" edge (core SeatWalls.Resolve);
//    • SetRectOpenings — the rect re-expressed in the attraction's frame,
//                        t from each side's left corner seen from inside
//                        (core SeatOpenings.Resolve), plus the entry side.
//  Edges keep the v1/v2 vocabulary (kind + walkable openings, everything
//  else blocked); AttractionRectSide converts them to v3 spans on read
//  (blocked → wall, door/opening → opening, rect/open → unknown).
//  Pure maths for axis-aligned seats (quarter turns), so it can be tested
//  offline against core's harness (Tools/openings-e2e-offline).
//
//  Park Simulator: DreamPark → Park Simulator → Arena Rect → None | Four
//  Walls | Front Door | Mixed. "None" (default) keeps the old behaviour (no
//  park data: every side open, no walls).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    [Serializable]
    public sealed class ParkSimRectEdge
    {
        public string kind = "open";
        public float wallHeight;
        /// <summary>Metres from the edge's min-tangent corner (core's convention), sorted, disjoint.</summary>
        public List<AttractionRectOpening> openings = new List<AttractionRectOpening>();

        public static ParkSimRectEdge Open(float length) => new ParkSimRectEdge
        {
            kind = "open",
            openings = new List<AttractionRectOpening> { new AttractionRectOpening(0f, length, AttractionRectOpening.Open, null) },
        };

        public static ParkSimRectEdge Wall(float height = 2.4f) => new ParkSimRectEdge { kind = "wall", wallHeight = height };

        /// <summary>A wall with doors/openings cut out of it (kind "door" when any is a door).</summary>
        public static ParkSimRectEdge WallWith(float height, params AttractionRectOpening[] gaps)
        {
            var e = new ParkSimRectEdge { kind = "wall", wallHeight = height };
            foreach (var g in gaps) { e.openings.Add(g); if (g.type == AttractionRectOpening.Door || g.type == AttractionRectOpening.Opening) e.kind = g.type; }
            e.openings.Sort((p, q) => p.a.CompareTo(q.a));
            return e;
        }
    }

    [Serializable]
    public sealed class ParkSimRect
    {
        public string id = "sim_rect";
        /// <summary>x = along X, y = along Z (metres). The rect is centred on its origin.</summary>
        public Vector2 size;
        public ParkSimRectEdge minX, maxX, minZ, maxZ;
        /// <summary>The Arena entry edge ("minX" … "maxZ"), or null.</summary>
        public string entryEdge;
        public string entryReason = "Park Simulator";

        public ParkSimRectEdge Edge(string e)
        {
            switch (e)
            {
                case "minX": return minX;
                case "maxX": return maxX;
                case "minZ": return minZ;
                default: return maxZ;
            }
        }

        public float Length(string e) => e == "minX" || e == "maxX" ? size.y : size.x;

        public static readonly string[] Presets = { "None", "Four Walls", "Front Door", "Mixed" };

        public static ParkSimRect AllOpen(Vector2 size) => new ParkSimRect
        {
            size = size, minX = ParkSimRectEdge.Open(size.y), maxX = ParkSimRectEdge.Open(size.y),
            minZ = ParkSimRectEdge.Open(size.x), maxZ = ParkSimRectEdge.Open(size.x),
        };

        /// <summary>A preset scan rect of `size`; null for "None".</summary>
        public static ParkSimRect Preset(string name, Vector2 size)
        {
            float w = size.x, d = size.y;
            switch (name)
            {
                case "Four Walls":
                    return new ParkSimRect { id = "sim_walls", size = size, minX = ParkSimRectEdge.Wall(), maxX = ParkSimRectEdge.Wall(),
                        minZ = ParkSimRectEdge.Wall(), maxZ = ParkSimRectEdge.Wall() };
                case "Front Door":
                {
                    // Walls all round, a 0.9 m door in the middle of the -Z edge (where guests enter).
                    float dw = Mathf.Min(0.9f, w * 0.8f);
                    return new ParkSimRect { id = "sim_door", size = size, minX = ParkSimRectEdge.Wall(), maxX = ParkSimRectEdge.Wall(),
                        maxZ = ParkSimRectEdge.Wall(),
                        minZ = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening((w - dw) * 0.5f, (w + dw) * 0.5f, AttractionRectOpening.Door, "sim_hall")),
                        entryEdge = "minZ", entryReason = "Park Simulator: front door" };
                }
                case "Mixed":
                {
                    // LayoutLab's WM case: maxZ has a corner opening, a door and a gap to another rect;
                    // maxX a door and an opening; minZ a back door; minX a full wall.
                    var maxZ = new ParkSimRectEdge { kind = "door", wallHeight = 2.4f };
                    float u = w / 10f;
                    maxZ.openings.Add(new AttractionRectOpening(0f, Mathf.Max(0.3f, u * 1.2f), AttractionRectOpening.Opening, null));
                    if (w >= 3f) maxZ.openings.Add(new AttractionRectOpening(u * 3f, u * 3f + Mathf.Min(0.9f, u * 2f), AttractionRectOpening.Door, "room_B"));
                    if (w >= 2f) maxZ.openings.Add(new AttractionRectOpening(u * 6.5f, u * 9f, AttractionRectOpening.Rect, "room_C"));
                    var maxX = new ParkSimRectEdge { kind = "door", wallHeight = 2.4f };
                    float v = d / 10f;
                    maxX.openings.Add(new AttractionRectOpening(v * 2f, v * 2f + Mathf.Min(0.9f, v * 2f), AttractionRectOpening.Door, "room_D"));
                    if (d >= 2f) maxX.openings.Add(new AttractionRectOpening(v * 6f, v * 8f, AttractionRectOpening.Opening, "room_D"));
                    var minZ = ParkSimRectEdge.WallWith(2.4f, new AttractionRectOpening(w * 0.5f - Mathf.Min(0.45f, w * 0.4f), w * 0.5f + Mathf.Min(0.45f, w * 0.4f), AttractionRectOpening.Door, "room_E"));
                    return new ParkSimRect { id = "sim_mixed", size = size, minX = ParkSimRectEdge.Wall(), maxX = maxX, maxZ = maxZ, minZ = minZ,
                        entryEdge = "maxZ", entryReason = "Park Simulator: widest opening" };
                }
                default: return null;
            }
        }

        // ── seating maths (mirrors core SeatOpenings / SeatWalls for quarter turns) ──
        static readonly string[] EdgeNames = { "minX", "maxX", "minZ", "maxZ" };

        static Vector2 Rotate(Vector2 v, int quarterTurns)
        {
            float rad = 90f * quarterTurns * Mathf.Deg2Rad, c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            return new Vector2(c * v.x + s * v.y, -s * v.x + c * v.y);
        }

        static Vector2 OutwardLocal(AttractionWallSide side)
        {
            switch (side)
            {
                case AttractionWallSide.Back: return new Vector2(0f, -1f);
                case AttractionWallSide.Right: return new Vector2(1f, 0f);
                case AttractionWallSide.Left: return new Vector2(-1f, 0f);
                default: return new Vector2(0f, 1f);
            }
        }

        static Vector2 TangentLocal(AttractionWallSide side)
        {
            switch (side)
            {
                case AttractionWallSide.Back: return new Vector2(-1f, 0f);
                case AttractionWallSide.Right: return new Vector2(0f, -1f);
                case AttractionWallSide.Left: return new Vector2(0f, 1f);
                default: return new Vector2(1f, 0f);
            }
        }

        /// <summary>The rect edge an attraction side faces at `quarterTurns` (attraction yaw = 90° × turns in the rect).</summary>
        public static string EdgeFor(AttractionWallSide side, int quarterTurns)
        {
            Vector2 d = Rotate(OutwardLocal(side), quarterTurns);
            if (d.x > 0.5f) return "maxX";
            if (d.x < -0.5f) return "minX";
            return d.y > 0.5f ? "maxZ" : "minZ";
        }

        /// <summary>
        /// The rect's openings in the frame of an attraction whose origin stands at
        /// `posInRect` (rect-centred metres, x/z) turned `quarterTurns` × 90°.
        /// </summary>
        public AttractionRectOpenings Resolve(Vector2 posInRect, int quarterTurns)
        {
            quarterTurns = ((quarterTurns % 4) + 4) % 4;
            var result = new AttractionRectOpenings { rectId = id, hasData = true };
            foreach (AttractionWallSide side in AttractionRectOpenings.AllSides)
            {
                string edge = EdgeFor(side, quarterTurns);
                bool xEdge = edge == "minX" || edge == "maxX";
                float length = Length(edge);
                Vector2 tan = Rotate(TangentLocal(side), quarterTurns);
                bool same = xEdge ? tan.y > 0f : tan.x > 0f;
                ParkSimRectEdge e = Edge(edge) ?? ParkSimRectEdge.Open(length);
                var s = new AttractionRectSide { side = side, length = length, kind = e.kind ?? "", wallHeight = e.wallHeight };
                var list = e.openings ?? new List<AttractionRectOpening>();
                for (int k = 0; k < list.Count; k++)
                {
                    AttractionRectOpening o = same ? list[k] : list[list.Count - 1 - k];
                    float a = same ? o.a : length - o.b, b = same ? o.b : length - o.a;
                    s.openings.Add(new AttractionRectOpening(Mathf.Clamp(a, 0f, length), Mathf.Clamp(b, 0f, length), o.type, o.to));
                }
                if (edge == entryEdge) { result.entrySide = AttractionWallSides.Name(side); result.entryReason = entryReason ?? ""; }
                result.sides.Add(s);
            }
            result.rectSize = new Vector2(result.Length(AttractionWallSide.Front), result.Length(AttractionWallSide.Right));
            result.rectCenter = Rotate(-posInRect, -quarterTurns);
            return result;
        }

        /// <summary>Walled sides: footprint edge within 0.10 m of a walled edge (≥ half wall, core SeatWalls).</summary>
        public AttractionWallMask Walls(Vector2 posInRect, Vector2 footprintLocal, int quarterTurns)
        {
            quarterTurns = ((quarterTurns % 4) + 4) % 4;
            Vector2 ext = (quarterTurns & 1) == 0 ? footprintLocal : new Vector2(footprintLocal.y, footprintLocal.x);
            var mask = AttractionWallMask.None;
            foreach (AttractionWallSide side in AttractionWallSides.All)
            {
                string edge = EdgeFor(side, quarterTurns);
                ParkSimRectEdge e = Edge(edge);
                // v3 (RECT_EDGES_SPEC.md): walled = wall spans cover ≥ half the side, as core's SeatWalls.
                if (e == null || !new AttractionRectSide { length = Length(edge), kind = e.kind ?? "",
                        openings = e.openings ?? new List<AttractionRectOpening>(), wallHeight = e.wallHeight }.IsWalled) continue;
                float gap;
                switch (edge)
                {
                    case "minX": gap = (posInRect.x - ext.x * 0.5f) - (-size.x * 0.5f); break;
                    case "maxX": gap = size.x * 0.5f - (posInRect.x + ext.x * 0.5f); break;
                    case "minZ": gap = (posInRect.y - ext.y * 0.5f) - (-size.y * 0.5f); break;
                    default: gap = size.y * 0.5f - (posInRect.y + ext.y * 0.5f); break;
                }
                if (gap <= 0.10f) mask |= AttractionWallSides.Mask(side);
            }
            return mask;
        }

        /// <summary>Seat `attraction` on this rect as core would: walls, then openings (+ entry side).</summary>
        public void Apply(AttractionTemplate attraction, Vector2 posInRect, int quarterTurns)
        {
            if (attraction == null) return;
            attraction.SetWalls(Walls(posInRect, attraction.RuntimeFootprintMeters, quarterTurns));
            attraction.SetRectOpenings(Resolve(posInRect, quarterTurns));
        }
    }

#if !DREAMPARKCORE   // the Park Simulator itself is SDK-only (core has the real park)
    /// <summary>Park Simulator menu: which simulated rect the Arena test seats on.</summary>
    public static class ParkSimArenaRect
    {
        const string Pref = "DreamPark.ParkSim.ArenaRect";
        const string Menu = "DreamPark/Park Simulator/Arena Rect/";

        public static string Current
        {
            get => EditorPrefs.GetString(Pref, "None");
            set => EditorPrefs.SetString(Pref, value ?? "None");
        }

        /// <summary>Scripting override (LayoutLab sweeps): when set, used instead of the menu preset.</summary>
        public static Func<Vector2, ParkSimRect> Override;

        public static ParkSimRect For(Vector2 floorMeters) =>
            Override != null ? Override(floorMeters) : ParkSimRect.Preset(Current, floorMeters);

        [MenuItem(Menu + "None", false, 300)] static void None() => Set("None");
        [MenuItem(Menu + "Four Walls", false, 301)] static void FourWalls() => Set("Four Walls");
        [MenuItem(Menu + "Front Door", false, 302)] static void FrontDoor() => Set("Front Door");
        [MenuItem(Menu + "Mixed", false, 303)] static void Mixed() => Set("Mixed");
        [MenuItem(Menu + "None", true)] static bool VNone() { Menu_Checked("None"); return true; }
        [MenuItem(Menu + "Four Walls", true)] static bool VFourWalls() { Menu_Checked("Four Walls"); return true; }
        [MenuItem(Menu + "Front Door", true)] static bool VFrontDoor() { Menu_Checked("Front Door"); return true; }
        [MenuItem(Menu + "Mixed", true)] static bool VMixed() { Menu_Checked("Mixed"); return true; }

        static void Menu_Checked(string name) => UnityEditor.Menu.SetChecked(Menu + name, Current == name);

        static void Set(string name)
        {
            Current = name;
            Debug.Log("[ParkSim] Arena rect: " + name + " (applies on the next Arena spawn)");
            if (ParkSimPackageTest.IsArenaPreview) ParkSimulator.RegenerateWhenIdle();
        }
    }
#endif
}
