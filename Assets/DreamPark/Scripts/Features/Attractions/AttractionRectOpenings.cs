namespace DreamPark
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using UnityEngine;

    // ─────────────────────────────────────────────────────────────────────
    //  AttractionRectOpenings.cs — SDK-synced (RECT_EDGES_SPEC.md)
    //
    //  The rect an attraction is seated on, side by side, as SPANS (v3, Oct 15
    //  2026). Every side is an ordered list of spans that tile it end to end:
    //    wall     a real wall (RoomPlan) stands here — windows included
    //    opening  a RoomPlan door / opening (`type` "door" | "opening"), or where a
    //             neighbouring rect touches with no confirmed wall (`type` "adjacent")
    //    unknown  everything else, and the default: no wall, no opening.
    //             Passable. A rect with no wall and no RoomPlan door/opening on
    //             any side is an OPEN AREA (a gym floor, a courtyard): connect from
    //             any side.
    //  `touches` names the neighbouring rect sharing a stretch (adjacency is an
    //  attribute, never an opening). Read-only data core hands the attraction
    //  when it seats it (and again when the map changes).
    //
    //  FRAME: the attraction's own. Sides are its Front (+Z), Back (−Z),
    //  Right (+X) and Left (−X) — the wall-layer sides. Each side is the RECT's
    //  edge that side faces, and `t` runs along it in metres from that side's
    //  LEFT corner as seen from inside the attraction looking out through it.
    //
    //  NO DATA = ALL UNKNOWN (every side passable, no walls). Levels must
    //  always work that way.
    //
    //  OLD DATA (v1/v2: `kind` + `openings` = walkable stretches, everything
    //  else blocked) is still accepted on a side whose `spans` is empty and is
    //  converted on read: blocked → wall, door/opening → opening, "rect" →
    //  unknown with `touches`, "open" → unknown.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>One span of a rect side (v3). Spans of a side tile it from 0 to its length.</summary>
    [Serializable]
    public sealed class AttractionRectSpan
    {
        public const string Wall = "wall";
        public const string Opening = "opening";
        public const string Unknown = "unknown";

        /// <summary>Start, metres from the side's left corner.</summary>
        public float a;
        /// <summary>End, metres from the side's left corner (b &gt; a).</summary>
        public float b;
        /// <summary>"wall" | "opening" | "unknown".</summary>
        public string kind = Unknown;
        /// <summary>Openings: "door" | "opening" (RoomPlan) | "adjacent" (a neighbouring rect touches, no wall). Empty otherwise.</summary>
        public string type = "";
        /// <summary>Wall height (walls) or door/opening height (openings), metres; 0 = not measured.</summary>
        public float height;
        /// <summary>Walls: how far the real wall plane stands beyond the rect edge, metres (+ = beyond).</summary>
        public float offset;
        /// <summary>Walls: a window stands here (still a wall — it blocks walking).</summary>
        public bool window;
        /// <summary>The neighbouring rect sharing this stretch (or, on an opening, the rect beyond it); empty = none.</summary>
        public string touches = "";

        public AttractionRectSpan() { }

        public AttractionRectSpan(float a, float b, string kind, string type = null, float height = 0f, float offset = 0f,
            bool window = false, string touches = null)
        {
            this.a = a; this.b = b;
            this.kind = NormalizeKind(kind);
            this.type = this.kind == Opening
                ? (type == AttractionRectOpening.Door || type == AttractionRectOpening.Adjacent ? type : AttractionRectOpening.Opening) : "";
            this.height = height > 0f ? height : 0f;
            this.offset = this.kind == Wall ? offset : 0f;
            this.window = this.kind == Wall && window;
            this.touches = touches ?? "";
        }

        /// <summary>"wall"/"window" → wall, "opening"/"door" → opening, anything else → unknown.</summary>
        public static string NormalizeKind(string kind)
        {
            switch (kind)
            {
                case Wall:
                case "window": return Wall;
                case Opening:
                case "door": return Opening;
                default: return Unknown;
            }
        }

        /// <summary>Precedence where spans overlap: RoomPlan opening (3) &gt; wall (2) &gt; adjacent opening (1) &gt; unknown (0).</summary>
        public static int Rank(string kind, string type = null) =>
            NormalizeKind(kind) == Opening ? (type == AttractionRectOpening.Adjacent ? 1 : 3) : NormalizeKind(kind) == Wall ? 2 : 0;

        /// <summary>This span's precedence.</summary>
        public int Precedence => Rank(kind, type);

        public float Width => Mathf.Max(0f, b - a);
        public float Center => (a + b) * 0.5f;
        public bool IsWall => kind == Wall;
        public bool IsOpening => kind == Opening;
        /// <summary>A RoomPlan door / opening (not an adjacent-rect opening).</summary>
        public bool IsDoorway => kind == Opening && type != AttractionRectOpening.Adjacent;
        /// <summary>An opening where a neighbouring rect touches the side.</summary>
        public bool IsAdjacent => kind == Opening && type == AttractionRectOpening.Adjacent;
        public bool IsUnknown => kind != Wall && kind != Opening;
        /// <summary>Not a wall: guests may walk through (opening or unknown).</summary>
        public bool IsPassable => kind != Wall;
        public bool Touches => !string.IsNullOrEmpty(touches);

        public AttractionRectSpan Clone() => new AttractionRectSpan(a, b, kind, type, height, offset, window, touches);
        public AttractionRectSpan With(float na, float nb) { var c = Clone(); c.a = na; c.b = nb; return c; }

        internal bool SameAttributes(AttractionRectSpan o) =>
            o != null && kind == o.kind && (type ?? "") == (o.type ?? "") && window == o.window && (touches ?? "") == (o.touches ?? "")
            && Mathf.Abs(height - o.height) <= 0.005f && Mathf.Abs(offset - o.offset) <= 0.005f;

        public override string ToString() =>
            $"{kind}{(!string.IsNullOrEmpty(type) ? "/" + type : "")} {a:0.00}-{b:0.00}" + (window ? " window" : "")
            + (Touches ? $" ~{touches}" : "");
    }

    /// <summary>
    /// A stretch of a side derived from the spans: a RoomPlan door / opening
    /// (<see cref="AttractionRectOpenings.Openings"/>), any non-wall stretch
    /// (<see cref="AttractionRectOpenings.Passages"/>, type "unknown" too), or a stretch a
    /// neighbouring rect shares (<see cref="AttractionRectOpenings.Links"/>).
    /// </summary>
    [Serializable]
    public sealed class AttractionRectOpening
    {
        public const string Door = "door";
        public const string Opening = "opening";
        /// <summary>v3: an opening where a neighbouring rect touches the side with no confirmed wall.</summary>
        public const string Adjacent = "adjacent";
        /// <summary>v3: a passable stretch that is neither a RoomPlan door/opening nor adjacent.</summary>
        public const string Unknown = "unknown";
        /// <summary>v1/v2 data only (converted on read): a neighbouring rect touched here.</summary>
        public const string Rect = "rect";
        /// <summary>v1/v2 data only (converted on read): no wall here.</summary>
        public const string Open = "open";

        /// <summary>Start, metres from the side's left corner.</summary>
        public float a;
        /// <summary>End, metres from the side's left corner (b &gt; a).</summary>
        public float b;
        /// <summary>"door" | "opening" | "adjacent" | "unknown" (in Links: the span kind under it).</summary>
        public string type = Unknown;
        /// <summary>The rect id beyond this stretch (the span's `touches`), or null/empty.</summary>
        public string to;

        public AttractionRectOpening() { }

        public AttractionRectOpening(float a, float b, string type, string to)
        {
            this.a = a; this.b = b; this.type = string.IsNullOrEmpty(type) ? Unknown : type;
            this.to = string.IsNullOrEmpty(to) ? null : to;
        }

        public float Width => Mathf.Max(0f, b - a);
        public float Center => (a + b) * 0.5f;
        public bool LeadsToRect => !string.IsNullOrEmpty(to);
        public AttractionRectOpening Clone() => new AttractionRectOpening(a, b, type, to);
        public override string ToString() => $"{type} {a:0.00}-{b:0.00}" + (LeadsToRect ? $" → {to}" : "");
    }

    /// <summary>One side of the rect, seen from the attraction.</summary>
    [Serializable]
    public sealed class AttractionRectSide
    {
        /// <summary>A side is WALLED (wall layers apply when flush) when wall spans cover at least this share
        /// of it — core's RectEdgeBorder.WalledMinShare.</summary>
        public const float WalledMinShare = 0.5f;

        public AttractionWallSide side;
        /// <summary>Length of the rect's edge on this side, metres.</summary>
        public float length;
        /// <summary>v3: the side's spans, ordered, tiling [0, length]. Read through <see cref="Spans"/>
        /// (it also converts old data and fills gaps with unknown).</summary>
        public List<AttractionRectSpan> spans = new List<AttractionRectSpan>();

        /// <summary>LEGACY (v1/v2) input, read only while <see cref="spans"/> is empty: the old edge kind.</summary>
        public string kind = "";
        /// <summary>LEGACY (v1/v2) input, read only while <see cref="spans"/> is empty: the walkable
        /// stretches (door/opening/rect/open); everything else on the side was blocked.</summary>
        public List<AttractionRectOpening> openings = new List<AttractionRectOpening>();
        /// <summary>LEGACY (v1/v2) input: the wall height of the blocked stretches.</summary>
        public float wallHeight;

        /// <summary>The side's spans (v3), ordered and tiling [0, length]: stored spans (gaps read unknown,
        /// overlaps resolve opening &gt; wall &gt; unknown), else old data converted, else one unknown span.</summary>
        public List<AttractionRectSpan> Spans()
        {
            float len = Mathf.Max(0f, length);
            if (spans != null && spans.Count > 0) return Paint(spans, len);
            bool legacy = (openings != null && openings.Count > 0) || !string.IsNullOrEmpty(kind);
            if (!legacy)
                return len > 0f ? new List<AttractionRectSpan> { new AttractionRectSpan(0f, len, AttractionRectSpan.Unknown) }
                    : new List<AttractionRectSpan>();
            // v1/v2: the walkable list; everything else was blocked → wall.
            var sorted = new List<AttractionRectOpening>(openings ?? new List<AttractionRectOpening>());
            sorted.RemoveAll(o => o == null);
            sorted.Sort((p, q) => p.a.CompareTo(q.a));
            var result = new List<AttractionRectSpan>();
            float u = 0f;
            foreach (var o in sorted)
            {
                float a = Mathf.Clamp(o.a, 0f, len), b = Mathf.Clamp(o.b, 0f, len);
                if (a > u) result.Add(new AttractionRectSpan(u, a, AttractionRectSpan.Wall, height: wallHeight));
                a = Mathf.Max(a, u);
                if (b <= a) continue;
                bool door = o.type == AttractionRectOpening.Door || o.type == AttractionRectOpening.Opening;
                bool adjacent = o.type == AttractionRectOpening.Rect || (o.type == AttractionRectOpening.Adjacent);
                result.Add(new AttractionRectSpan(a, b, door || adjacent ? AttractionRectSpan.Opening : AttractionRectSpan.Unknown,
                    adjacent ? AttractionRectOpening.Adjacent : o.type, touches: o.to));
                u = b;
            }
            if (len > u) result.Add(new AttractionRectSpan(u, len, AttractionRectSpan.Wall, height: wallHeight));
            return Paint(result, len);
        }

        /// <summary>Spans painted over an unknown side (overlaps: opening &gt; wall &gt; unknown; first wins on a
        /// tie; `touches` on top, first wins), equal neighbours merged.</summary>
        internal static List<AttractionRectSpan> Paint(List<AttractionRectSpan> input, float len)
        {
            if (len <= 0f) return new List<AttractionRectSpan>();
            var list = new List<AttractionRectSpan> { new AttractionRectSpan(0f, len, AttractionRectSpan.Unknown) };
            for (int pass = 0; pass < 2; pass++)
                foreach (var s in input)
                {
                    if (s == null) continue;
                    float a = Mathf.Clamp(s.a, 0f, len), b = Mathf.Clamp(s.b, 0f, len);
                    if (b - a <= 1e-4f) continue;
                    Split(list, a); Split(list, b);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i];
                        if (c.b <= a + 1e-5f || c.a >= b - 1e-5f) continue;
                        if (pass == 0)
                        {
                            if (s.IsUnknown || s.Precedence <= c.Precedence) continue;
                            var n = s.With(c.a, c.b); n.touches = c.touches; list[i] = n;
                        }
                        else if (s.Touches && !c.Touches) { var n = c.Clone(); n.touches = s.touches; list[i] = n; }
                    }
                }
            for (int i = list.Count - 2; i >= 0; i--)
                if (list[i].SameAttributes(list[i + 1])) { list[i] = list[i].With(list[i].a, list[i + 1].b); list.RemoveAt(i + 1); }
            return list;
        }

        private static void Split(List<AttractionRectSpan> list, float x)
        {
            for (int i = 0; i < list.Count; i++)
                if (x > list[i].a + 1e-5f && x < list[i].b - 1e-5f)
                {
                    var s = list[i];
                    list[i] = s.With(s.a, x);
                    list.Insert(i + 1, s.With(x, s.b));
                    return;
                }
        }

        /// <summary>Share 0..1 of the side covered by `kind` spans.</summary>
        public float Share(string kind)
        {
            float sum = 0f, all = 0f;
            foreach (var s in Spans()) { all += s.Width; if (s.kind == kind) sum += s.Width; }
            return all > 0f ? sum / all : (kind == AttractionRectSpan.Unknown ? 1f : 0f);
        }

        /// <summary>Wall spans cover at least <see cref="WalledMinShare"/> of the side.</summary>
        public bool IsWalled => Share(AttractionRectSpan.Wall) >= WalledMinShare - 1e-4f;

        public AttractionRectSide Clone()
        {
            var copy = new AttractionRectSide { side = side, length = length, kind = kind ?? "", wallHeight = wallHeight };
            if (spans != null)
                for (int i = 0; i < spans.Count; i++)
                    if (spans[i] != null) copy.spans.Add(spans[i].Clone());
            if (openings != null)
                for (int i = 0; i < openings.Count; i++)
                    if (openings[i] != null) copy.openings.Add(openings[i].Clone());
            return copy;
        }
    }

    /// <summary>
    /// The seated rect's sides in the attraction's frame (v3 spans). Serializable so the
    /// same class is the authored test data of the Editor preview (AttractionTemplate's
    /// "Rect Openings Preview") and what Editor tools inject with
    /// <see cref="AttractionTemplate.SetRectOpenings"/>.
    /// </summary>
    [Serializable]
    public sealed class AttractionRectOpenings
    {
        /// <summary>Data format version: 3 = spans (wall / opening / unknown).</summary>
        public const int Version = 3;

        /// <summary>The rect the attraction is seated on, or null (no park data).</summary>
        public string rectId;
        /// <summary>Rect size in the attraction's axes: x = left↔right (along X), y = back↔front (along Z), metres.</summary>
        public Vector2 rectSize;
        /// <summary>Rect centre in the attraction's local XZ (metres; zero = centred on the attraction).</summary>
        public Vector2 rectCenter;
        /// <summary>Front, Back, Right, Left (missing sides read as all unknown).</summary>
        public List<AttractionRectSide> sides = new List<AttractionRectSide>();
        /// <summary>True when core (or the Editor preview / an injection) supplied this; false = the all-unknown default.</summary>
        public bool hasData;
        /// <summary>The side ("front" | "back" | "right" | "left") facing the rect's ENTRY edge, or "".</summary>
        public string entrySide = "";
        /// <summary>Why core chose that entry edge, or "".</summary>
        public string entryReason = "";

        public static readonly AttractionWallSide[] AllSides = AttractionWallSides.All;

        /// <summary>No park data: a rect the size of `footprint`, every side one unknown span.</summary>
        public static AttractionRectOpenings AllUnknown(Vector2 footprintMeters)
        {
            var o = new AttractionRectOpenings { rectSize = new Vector2(Mathf.Max(0f, footprintMeters.x), Mathf.Max(0f, footprintMeters.y)) };
            for (int i = 0; i < AllSides.Length; i++)
            {
                float len = LengthFor(AllSides[i], o.rectSize);
                var s = new AttractionRectSide { side = AllSides[i], length = len };
                if (len > 0f) s.spans.Add(new AttractionRectSpan(0f, len, AttractionRectSpan.Unknown));
                o.sides.Add(s);
            }
            return o;
        }

        /// <summary>Old name of <see cref="AllUnknown"/> (no data = no walls, passable everywhere).</summary>
        public static AttractionRectOpenings AllOpen(Vector2 footprintMeters) => AllUnknown(footprintMeters);

        /// <summary>Edge length of a side of a rect `size` (x along X, y along Z).</summary>
        public static float LengthFor(AttractionWallSide side, Vector2 size) =>
            side == AttractionWallSide.Front || side == AttractionWallSide.Back ? size.x : size.y;

        /// <summary>The four sides in Front, Back, Right, Left order.</summary>
        public IReadOnlyList<AttractionWallSide> Sides => AllSides;

        public AttractionRectSide Side(AttractionWallSide side)
        {
            if (sides != null)
                for (int i = 0; i < sides.Count; i++)
                    if (sides[i] != null && sides[i].side == side) return sides[i];
            return null;
        }

        public float Length(AttractionWallSide side)
        {
            var s = Side(side);
            return s != null && s.length > 0f ? s.length : LengthFor(side, rectSize);
        }

        /// <summary>The side's spans (v3): ordered, tiling [0, length]. A missing side is one unknown span.</summary>
        public IReadOnlyList<AttractionRectSpan> Spans(AttractionWallSide side)
        {
            var s = Side(side);
            if (s != null)
            {
                if (s.length <= 0f) s.length = LengthFor(side, rectSize);
                return s.Spans();
            }
            float len = LengthFor(side, rectSize);
            return len > 0f ? new[] { new AttractionRectSpan(0f, len, AttractionRectSpan.Unknown) } : Array.Empty<AttractionRectSpan>();
        }

        /// <summary>The span kind at metre `t` ("wall" | "opening" | "unknown"); a boundary reads the
        /// higher of the two (opening &gt; wall &gt; unknown).</summary>
        public string KindAt(AttractionWallSide side, float t)
        {
            AttractionRectSpan s = SpanAt(side, t);
            return s != null ? s.kind : AttractionRectSpan.Unknown;
        }

        /// <summary>The span at metre `t` (a boundary reads the higher-precedence span), or null without one.</summary>
        public AttractionRectSpan SpanAt(AttractionWallSide side, float t)
        {
            AttractionRectSpan best = null;
            foreach (var s in Spans(side))
                if (t >= s.a - 1e-4f && t <= s.b + 1e-4f && (best == null || s.Precedence > best.Precedence))
                    best = s;
            return best;
        }

        /// <summary>A real wall at metre `t` (from the side's left corner).</summary>
        public bool IsWall(AttractionWallSide side, float t) => KindAt(side, t) == AttractionRectSpan.Wall;
        /// <summary>Passable at metre `t`: an opening or unknown. The exact opposite of <see cref="IsWall"/>.</summary>
        public bool IsOpen(AttractionWallSide side, float t) => !IsWall(side, t);
        /// <summary>An opening at metre `t`: a RoomPlan door / opening or an adjacent-rect opening
        /// (<see cref="SpanAt"/>`.type` tells which).</summary>
        public bool IsOpening(AttractionWallSide side, float t) => KindAt(side, t) == AttractionRectSpan.Opening;
        /// <summary>Neither wall nor opening at metre `t`.</summary>
        public bool IsUnknown(AttractionWallSide side, float t) => KindAt(side, t) == AttractionRectSpan.Unknown;

        /// <summary>The whole side is wall (nothing passable).</summary>
        public bool IsFullWall(AttractionWallSide side)
        {
            foreach (var s in Spans(side)) if (!s.IsWall) return false;
            return Length(side) > 0f;
        }

        /// <summary>The side has no wall and no opening.</summary>
        public bool IsAllUnknown(AttractionWallSide side)
        {
            foreach (var s in Spans(side)) if (!s.IsUnknown) return false;
            return true;
        }

        /// <summary>No wall and no RoomPlan door/opening on the side (only unknown and adjacent openings).</summary>
        public bool IsOpenFloor(AttractionWallSide side)
        {
            foreach (var s in Spans(side)) if (s.IsWall || s.IsDoorway) return false;
            return true;
        }

        /// <summary>OPEN AREA: no side has a wall or a RoomPlan door/opening (or no data) — the rect stands
        /// in a big open space (gym floor, courtyard) and can be considered connected from any side.
        /// Neighbouring rects touching it (adjacent openings) do not make it less open.</summary>
        public bool IsOpenArea
        {
            get
            {
                for (int i = 0; i < AllSides.Length; i++) if (!IsOpenFloor(AllSides[i])) return false;
                return true;
            }
        }

        /// <summary>Wall spans cover ≥ half the side (<see cref="AttractionRectSide.WalledMinShare"/>).</summary>
        public bool IsWalled(AttractionWallSide side) => Share(side, AttractionRectSpan.Wall) >= AttractionRectSide.WalledMinShare - 1e-4f;

        public float Share(AttractionWallSide side, string kind)
        {
            float sum = 0f, all = 0f;
            foreach (var s in Spans(side)) { all += s.Width; if (s.kind == kind) sum += s.Width; }
            return all > 0f ? sum / all : (kind == AttractionRectSpan.Unknown ? 1f : 0f);
        }

        /// <summary>The side's kind for summaries: the kind covering the most length (ties: opening &gt; wall &gt;
        /// unknown), "" without data. Behaviour should read spans.</summary>
        public string Kind(AttractionWallSide side)
        {
            if (!hasData) return "";
            float w = Share(side, AttractionRectSpan.Wall), o = Share(side, AttractionRectSpan.Opening), u = Share(side, AttractionRectSpan.Unknown);
            if (o >= w && o >= u && o > 0f) return AttractionRectSpan.Opening;
            if (w >= u && w > 0f) return AttractionRectSpan.Wall;
            return AttractionRectSpan.Unknown;
        }

        /// <summary>Lowest measured wall height on a side (metres; windows aside), 0 = none / unknown.</summary>
        public float WallHeight(AttractionWallSide side)
        {
            float h = 0f;
            foreach (var s in Spans(side))
                if (s.IsWall && !s.window && s.height > 0f) h = h > 0f ? Mathf.Min(h, s.height) : s.height;
            return h;
        }

        /// <summary>Length-weighted distance of the real wall plane beyond the rect edge on a side (metres).</summary>
        public float WallOffset(AttractionWallSide side)
        {
            float sum = 0f, w = 0f;
            foreach (var s in Spans(side)) if (s.IsWall) { sum += s.offset * s.Width; w += s.Width; }
            return w > 0f ? sum / w : 0f;
        }

        /// <summary>The side core says guests arrive from, when it chose one.</summary>
        public bool TryEntrySide(out AttractionWallSide side)
        {
            side = AttractionWallSide.Back;
            return !string.IsNullOrEmpty(entrySide) && AttractionWallSides.TryParse(entrySide, out side);
        }

        /// <summary>Openings on a side, sorted: RoomPlan doors / openings (type "door" | "opening") and
        /// adjacent-rect openings (type "adjacent"); `to` = the rect beyond. Unknown stretches are not
        /// openings (see <see cref="Passages"/>).</summary>
        public IReadOnlyList<AttractionRectOpening> Openings(AttractionWallSide side)
        {
            var list = new List<AttractionRectOpening>();
            foreach (var s in Spans(side))
                if (s.IsOpening) list.Add(new AttractionRectOpening(s.a, s.b, string.IsNullOrEmpty(s.type) ? AttractionRectOpening.Opening : s.type, s.touches));
            return list;
        }

        /// <summary>Every passable stretch of a side (not wall): doors / openings and unknown stretches
        /// (type "unknown"), `to` = the rect beyond. With no data, the whole side.</summary>
        public IReadOnlyList<AttractionRectOpening> Passages(AttractionWallSide side)
        {
            var list = new List<AttractionRectOpening>();
            foreach (var s in Spans(side))
            {
                if (!s.IsPassable) continue;
                string type = s.IsOpening ? (string.IsNullOrEmpty(s.type) ? AttractionRectOpening.Opening : s.type) : AttractionRectOpening.Unknown;
                string to = s.Touches ? s.touches : null;
                var last = list.Count > 0 ? list[list.Count - 1] : null;
                if (last != null && last.type == type && last.to == to && s.a - last.b <= 1e-3f) last.b = s.b;
                else list.Add(new AttractionRectOpening(s.a, s.b, type, to));
            }
            return list;
        }

        /// <summary>Stretches of a side a neighbouring rect shares (`to`), whatever the span kind under them
        /// (`type` = "wall" | "opening" | "unknown") — rect-to-rect adjacency. Across a wall the rects touch
        /// but a guest cannot walk through.</summary>
        public IReadOnlyList<AttractionRectOpening> Links(AttractionWallSide side)
        {
            var list = new List<AttractionRectOpening>();
            foreach (var s in Spans(side))
            {
                if (!s.Touches) continue;
                var last = list.Count > 0 ? list[list.Count - 1] : null;
                if (last != null && last.to == s.touches && last.type == s.kind && s.a - last.b <= 1e-3f) last.b = s.b;
                else list.Add(new AttractionRectOpening(s.a, s.b, s.kind, s.touches));
            }
            return list;
        }

        /// <summary>The passable stretch nearest `t` on a side, or null when the side is all wall.</summary>
        public AttractionRectOpening Nearest(AttractionWallSide side, float t)
        {
            AttractionRectOpening best = null;
            float bestD = float.MaxValue;
            var list = Passages(side);
            for (int i = 0; i < list.Count; i++)
            {
                float d = t < list[i].a ? list[i].a - t : t > list[i].b ? t - list[i].b : 0f;
                if (d < bestD) { bestD = d; best = list[i]; }
            }
            return best;
        }

        /// <summary>The widest passable stretch on any side (ties: Front, Back, Right, Left), or null;
        /// `type` filters ("door", "opening", "unknown").</summary>
        public AttractionRectOpening Widest(out AttractionWallSide side, string type = null)
        {
            side = AttractionWallSide.Front;
            AttractionRectOpening best = null;
            for (int s = 0; s < AllSides.Length; s++)
            {
                var list = Passages(AllSides[s]);
                for (int i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (type != null && o.type != type) continue;
                    if (best == null || o.Width > best.Width + 1e-4f) { best = o; side = AllSides[s]; }
                }
            }
            return best;
        }

        /// <summary>The attraction-local point (y = 0) `t` metres along a side's rect edge.</summary>
        public Vector3 LocalPoint(AttractionWallSide side, float t)
        {
            float hx = rectSize.x * 0.5f, hz = rectSize.y * 0.5f;
            float cx = rectCenter.x, cz = rectCenter.y;
            switch (side)
            {
                case AttractionWallSide.Back: return new Vector3(cx + hx - t, 0f, cz - hz);
                case AttractionWallSide.Right: return new Vector3(cx + hx, 0f, cz + hz - t);
                case AttractionWallSide.Left: return new Vector3(cx - hx, 0f, cz - hz + t);
                default: return new Vector3(cx - hx + t, 0f, cz + hz);
            }
        }

        /// <summary>Attraction-local outward direction of a side.</summary>
        public static Vector3 Outward(AttractionWallSide side)
        {
            switch (side)
            {
                case AttractionWallSide.Back: return Vector3.back;
                case AttractionWallSide.Right: return Vector3.right;
                case AttractionWallSide.Left: return Vector3.left;
                default: return Vector3.forward;
            }
        }

        public AttractionRectOpenings Clone()
        {
            var copy = new AttractionRectOpenings { rectId = rectId, rectSize = rectSize, rectCenter = rectCenter, hasData = hasData,
                entrySide = entrySide ?? "", entryReason = entryReason ?? "" };
            if (sides != null)
                for (int i = 0; i < sides.Count; i++)
                    if (sides[i] != null) copy.sides.Add(sides[i].Clone());
            return copy;
        }

        /// <summary>Stable text of everything a consumer can read (change detection, logs).</summary>
        public string Signature()
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.Append(rectId ?? "-").Append('|').Append(hasData ? '1' : '0')
              .Append('|').Append(rectSize.x.ToString("0.00", ci)).Append('x').Append(rectSize.y.ToString("0.00", ci))
              .Append('@').Append(rectCenter.x.ToString("0.00", ci)).Append(',').Append(rectCenter.y.ToString("0.00", ci))
              .Append("|entry:").Append(entrySide ?? "");
            for (int s = 0; s < AllSides.Length; s++)
            {
                sb.Append('|').Append(AttractionWallSides.Name(AllSides[s])).Append(':')
                  .Append(Length(AllSides[s]).ToString("0.00", ci));
                foreach (var sp in Spans(AllSides[s]))
                    sb.Append(';').Append(sp.kind).Append('/').Append(sp.type ?? "").Append(' ')
                      .Append(sp.a.ToString("0.00", ci)).Append('-').Append(sp.b.ToString("0.00", ci))
                      .Append(" h").Append(sp.height.ToString("0.00", ci)).Append(" o").Append(sp.offset.ToString("0.00", ci))
                      .Append(sp.window ? " w" : "").Append('>').Append(sp.touches ?? "");
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(hasData ? $"rect {rectId ?? "?"} {rectSize.x:0.00}×{rectSize.y:0.00}" : "no park data (all unknown)");
            if (!string.IsNullOrEmpty(entrySide)) sb.Append(" entry ").Append(entrySide);
            for (int s = 0; s < AllSides.Length; s++)
            {
                sb.Append(" | ").Append(AttractionWallSides.Name(AllSides[s])).Append(": ");
                var list = Spans(AllSides[s]);
                for (int i = 0; i < list.Count; i++) sb.Append(i > 0 ? ", " : "").Append(list[i]);
            }
            return sb.ToString();
        }
    }
}
