// ─────────────────────────────────────────────────────────────────────
//  RunnerBoardRules.cs — the BOARD a way to play runs on (ways-to-play
//  contract §2, Oct 25 2026). PURE: no UnityEngine calls, so
//  Tools/runner-offline runs it as is.
//
//  A park is a board of 12 × 18 ft TILES: 12 × 18 = 1 tile, 24 × 18 = 2 (side
//  by side, left / right), 24 × 36 = 4 (2 × 2). A level's FOOTPRINT (1, 2 or 4
//  tiles) is read from its own bounds; a bigger level shrinks into a smaller
//  board (its layout frames fit it). Tiles are 0-based here, 1-based in Lua.
//
//  Board frame = the Location frame of the web setup's play rect: origin at the
//  rect centre, +X right when standing at the portal looking in, +Z into the
//  park (back). Tile order: front row left → right, then the back row.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Defective.JSON;

namespace DreamPark
{
    /// One 12 × 18 ft tile of the board (metres, board frame).
    public sealed class RunnerTile
    {
        public int index, col, row;
        public float cx, cz, w, d;
        public override string ToString() => $"tile{index + 1}(c{col},r{row} @ {cx:0.##},{cz:0.##} {w:0.##}x{d:0.##}m)";
    }

    /// A level the title ships (an AttractionTemplate of the release).
    public sealed class RunnerLevel
    {
        public string id, name, address;
        /// Authored footprint in feet (width = local X, depth = local Z). 0 = unknown (counts as one tile).
        public float widthFt, depthFt;
        public int Footprint => RunnerBoardRules.Footprint(widthFt, depthFt);
        public override string ToString() => $"{id}({Footprint}t)";
    }

    /// One placed level on the board: which tiles it covers. Every headset renders exactly these.
    public sealed class RunnerLoad
    {
        public int id;
        public string level;
        public int[] tiles = new int[0];
        public bool done;
        public string result;
        public double at;
        /// The ZONE this load belongs to (dp.zones, Oct 26 2026): null / "" = the main board every player not in a zone
        /// sees. A zone's loads render only on the headsets of that zone's players (same physical tiles, another level).
        public string zone;
        /// The director's per-load options (dp.flow.load(level, {options = {...}}) → gameplay dp.level.options(go)), JSON.
        public string options;
        public bool InMain => string.IsNullOrEmpty(zone);
        public bool Covers(int tile) { foreach (int t in tiles) if (t == tile) return true; return false; }
        public string Key => id + ":" + level + ":" + string.Join(",", tiles);
        public RunnerLoad Clone() => new RunnerLoad { id = id, level = level, tiles = (int[])tiles.Clone(), done = done, result = result, at = at, zone = zone, options = options };
    }

    /// A deferred board change the leader applies once its gate opens.
    public sealed class RunnerPending
    {
        public const string Swap = "sw", Transform = "xf";
        public string kind, level, nuTitle, nuSub;
        public int tile = -1;
        public double at;
    }

    public sealed class RunnerBoardGeometry
    {
        public const float TileWidthFt = 12f, TileDepthFt = 18f, FtToM = 0.3048f;
        public float widthFt, depthFt;
        public int cols, rows;
        public readonly List<RunnerTile> tiles = new List<RunnerTile>();
        public int Count => tiles.Count;
        public float WidthM => widthFt * FtToM;
        public float DepthM => depthFt * FtToM;

        /// The board of a play rect (feet). Anything ≥ 23 ft wide splits into 2 columns, ≥ 35 ft deep into 2 rows
        /// (a little slack for measured rects); smaller rects are one tile (the level shrinks into it).
        public static RunnerBoardGeometry FromFeet(float widthFt, float depthFt)
        {
            var g = new RunnerBoardGeometry { widthFt = Math.Max(1f, widthFt), depthFt = Math.Max(1f, depthFt) };
            g.cols = g.widthFt >= 2 * TileWidthFt - 1f ? 2 : 1;
            g.rows = g.depthFt >= 2 * TileDepthFt - 1f ? 2 : 1;
            float tw = g.WidthM / g.cols, td = g.DepthM / g.rows;
            int i = 0;
            for (int r = 0; r < g.rows; r++)
                for (int c = 0; c < g.cols; c++)
                    g.tiles.Add(new RunnerTile { index = i++, col = c, row = r, w = tw, d = td,
                        cx = -g.WidthM / 2f + tw * (c + 0.5f), cz = -g.DepthM / 2f + td * (r + 0.5f) });
            return g;
        }

        public static RunnerBoardGeometry FromMeters(float widthM, float depthM) => FromFeet(widthM / FtToM, depthM / FtToM);

        public RunnerTile Tile(int i) => i >= 0 && i < tiles.Count ? tiles[i] : null;

        /// The rectangle a set of tiles covers (metres, board frame): centre + size. Empty = the whole board.
        public (float cx, float cz, float w, float d) Frame(int[] set)
        {
            if (set == null || set.Length == 0) return (0, 0, WidthM, DepthM);
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (int i in set)
            {
                var t = Tile(i); if (t == null) continue;
                x0 = Math.Min(x0, t.cx - t.w / 2); x1 = Math.Max(x1, t.cx + t.w / 2);
                z0 = Math.Min(z0, t.cz - t.d / 2); z1 = Math.Max(z1, t.cz + t.d / 2);
            }
            if (x0 > x1) return (0, 0, WidthM, DepthM);
            return ((x0 + x1) / 2, (z0 + z1) / 2, x1 - x0, z1 - z0);
        }

        /// The tile under a board-frame point, -1 outside the board.
        public int TileAt(float x, float z)
        {
            foreach (var t in tiles)
                if (Math.Abs(x - t.cx) <= t.w / 2 + 1e-4f && Math.Abs(z - t.cz) <= t.d / 2 + 1e-4f) return t.index;
            return -1;
        }

        /// The tile an edge socket of a placed level belongs to (runtime-fixes, Oct 26 2026). A socket sits ON its edge, so
        /// on a seam between two tiles a plain TileAt answers the FIRST tile for both levels — the right/lower level's
        /// inner socket then never opened. Probe `inset` metres back inside the level (opposite the side it faces, in the
        /// board frame) and keep the probe inside the level's own frame, so each level's socket finds its own tile.
        /// -1 when the point is not on this level.
        public int SocketTile(int[] levelTiles, float x, float z, string boardSide, float inset = 0.15f)
        {
            switch (RunnerBoardRules.NormSide(boardSide))
            {
                case "left": x += inset; break;
                case "right": x -= inset; break;
                case "front": z += inset; break;
                case "back": z -= inset; break;
            }
            var f = Frame(levelTiles);
            const float pad = 0.05f;
            x = Math.Max(f.cx - f.w / 2 + pad, Math.Min(f.cx + f.w / 2 - pad, x));
            z = Math.Max(f.cz - f.d / 2 + pad, Math.Min(f.cz + f.d / 2 - pad, z));
            int t = TileAt(x, z);
            if (levelTiles == null || levelTiles.Length == 0) return t;
            foreach (int lt in levelTiles) if (lt == t) return t;
            return -1;
        }

        /// The neighbour of a tile across a side ("left" −X, "right" +X, "front" −Z, "back" +Z), -1 at the board edge.
        public int Neighbour(int tile, string side)
        {
            var t = Tile(tile); if (t == null) return -1;
            int c = t.col, r = t.row;
            switch (RunnerBoardRules.NormSide(side))
            {
                case "left": c--; break;
                case "right": c++; break;
                case "front": r--; break;
                case "back": r++; break;
                default: return -1;
            }
            if (c < 0 || r < 0 || c >= cols || r >= rows) return -1;
            return r * cols + c;
        }

        /// Every inner edge: (tile, side, neighbour, its side) once per pair.
        public List<(int a, string sa, int b, string sb)> InnerEdges()
        {
            var l = new List<(int, string, int, string)>();
            foreach (var t in tiles)
            {
                int r = Neighbour(t.index, "right"); if (r >= 0) l.Add((t.index, "right", r, "left"));
                int b = Neighbour(t.index, "back"); if (b >= 0) l.Add((t.index, "back", b, "front"));
            }
            return l;
        }
    }

    public static class RunnerBoardRules
    {
        public const double NextUpSeconds = 6.0;
        public const double TransformRevealSeconds = 6.0;
        public const double VoteSecondsDefault = 10.0;
        public const double EndingSecondsDefault = 60.0;
        public const int MaxStateChars = 3000;

        /// 1 tile for anything that fits 12 × 18 (either way round), 2 for 24 × 18 / 12 × 36, 4 above. Unknown = 1.
        public static int Footprint(float widthFt, float depthFt)
        {
            if (widthFt <= 0 || depthFt <= 0) return 1;
            float a = Math.Min(widthFt, depthFt), b = Math.Max(widthFt, depthFt);
            const float slack = 1f;
            if (a <= RunnerBoardGeometry.TileWidthFt + slack && b <= RunnerBoardGeometry.TileDepthFt + slack) return 1;
            if (a <= RunnerBoardGeometry.TileDepthFt + slack && b <= 2 * RunnerBoardGeometry.TileWidthFt + slack) return 2;   // 24 × 18
            if (a <= RunnerBoardGeometry.TileWidthFt + slack && b <= 2 * RunnerBoardGeometry.TileDepthFt + slack) return 2;   // 12 × 36
            return 4;
        }

        /// The yaw (degrees, Unity y-up: +90 turns the level's front −Z to the board's left −X) a level gets in its frame
        /// (runtime-fixes, Oct 26 2026). 0 when its long side already runs along the frame's long side (or either is
        /// square / unknown). Turned the other way round (18 × 12 authored, 12 × 18 tile) it turns a quarter, and the
        /// turn keeps its PORTAL side (front) on the board's outside rather than against a neighbour: a frame in the
        /// left column turns +90 (front → left wall), the right column −90 (front → right wall); a frame spanning the
        /// width (one column, or the whole board) turns +90.
        public static float FitYaw(float levelWidthFt, float levelDepthFt, float frameW, float frameD, float frameCx = 0f, float boardW = 0f)
        {
            if (levelWidthFt <= 0 || levelDepthFt <= 0 || frameW <= 0 || frameD <= 0) return 0f;
            const float squareSlack = 0.02f;   // within 2 % = square: never turn
            if (Math.Abs(levelWidthFt - levelDepthFt) <= squareSlack * Math.Max(levelWidthFt, levelDepthFt)) return 0f;
            if (Math.Abs(frameW - frameD) <= squareSlack * Math.Max(frameW, frameD)) return 0f;
            if ((levelWidthFt > levelDepthFt) == (frameW > frameD)) return 0f;
            bool rightColumn = boardW > 0 && frameW < boardW - 1e-3f && frameCx > 1e-3f;
            return rightColumn ? -90f : 90f;
        }

        /// The board size (feet) nowPlaying.board carries ({widthFt, depthFt}, numbers or numeric strings; ways-web may
        /// send it). (0, 0) = not there / unusable → the caller falls back (SpaceMap 'play' rect, else 12 × 18).
        public static (float widthFt, float depthFt) BoardFeetFrom(JSONObject board)
        {
            if (board == null || board.type != JSONObject.Type.Object) return (0, 0);
            float w = (float)Num(board, "widthFt"), d = (float)Num(board, "depthFt");
            if (!(w >= 4f && w <= 400f) || !(d >= 4f && d <= 400f)) return (0, 0);
            return (w, d);
        }

        public static string NormSide(string side)
        {
            switch ((side ?? "").Trim().ToLowerInvariant())
            {
                case "left": case "l": case "w": case "west": case "-x": return "left";
                case "right": case "r": case "e": case "east": case "+x": return "right";
                case "front": case "f": case "s": case "south": case "-z": return "front";
                case "back": case "b": case "n": case "north": case "+z": return "back";
                default: return "";
            }
        }

        /// The tiles a load takes. tile ≥ 0: that tile (a 2/4-tile level grows from it when the board has room, else it
        /// shrinks into the tile). spanAll: every tile. Neither: the level's footprint starting at the first tile not
        /// standing a level (else tile 0); a footprint the board can't hold → the whole board.
        public static int[] Place(RunnerBoardGeometry g, int footprint, int tile, bool spanAll, ICollection<int> occupied = null)
        {
            int n = g?.Count ?? 0;
            if (n == 0) return new int[0];
            if (spanAll || footprint >= n && tile < 0) return All(n);
            if (tile >= n) tile = n - 1;
            if (tile < 0)
            {
                tile = 0;
                for (int i = 0; i < n; i++) if (occupied == null || !occupied.Contains(i)) { tile = i; break; }
            }
            if (footprint <= 1) return new[] { tile };
            if (footprint >= 4 || footprint >= n) return n <= footprint ? All(n) : new[] { tile };
            // footprint 2 on a 4-tile board: the pair in the tile's row (side by side), else its column.
            int right = g.Neighbour(tile, "right"), left = g.Neighbour(tile, "left");
            if (right >= 0) return new[] { tile, right };
            if (left >= 0) return new[] { left, tile };
            int back = g.Neighbour(tile, "back"), front = g.Neighbour(tile, "front");
            if (back >= 0) return new[] { tile, back };
            if (front >= 0) return new[] { front, tile };
            return new[] { tile };
        }

        public static int[] All(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = i; return a; }

        /// A swap may happen once the tile's level is done AND nobody stands in the tile (or the tile stands nothing).
        public static bool SwapReady(RunnerLoad standing, int playersInTile) =>
            playersInTile <= 0 && (standing == null || standing.done);

        /// The whole-board change may happen once every standing level is done (players are always on the board; the
        /// Next-up reveal covers the change).
        public static bool TransformReady(IEnumerable<RunnerLoad> standing)
        {
            if (standing == null) return true;
            foreach (var l in standing) if (l != null && !l.done) return false;
            return true;
        }

        /// The vote: the option with the most votes; a tie or no votes → the default (clamped to a valid option).
        public static int Tally(IEnumerable<int> votes, int options, int defaultIndex)
        {
            int def = defaultIndex >= 0 && defaultIndex < options ? defaultIndex : 0;
            if (options <= 0) return -1;
            var n = new int[options];
            foreach (int v in votes ?? new int[0]) if (v >= 0 && v < options) n[v]++;
            int best = -1, bestN = 0; bool tie = false;
            for (int i = 0; i < options; i++)
            {
                if (n[i] > bestN) { best = i; bestN = n[i]; tie = false; }
                else if (n[i] == bestN && bestN > 0) tie = true;
            }
            return best < 0 || tie ? def : best;
        }

        /// The vote in full: counts per option and the options tied for the most votes (empty when nobody voted).
        public static int[] Counts(IEnumerable<int> votes, int options)
        {
            var n = new int[Math.Max(0, options)];
            foreach (int v in votes ?? new int[0]) if (v >= 0 && v < options) n[v]++;
            return n;
        }

        public static List<int> Tied(int[] counts)
        {
            var l = new List<int>();
            int best = 0;
            foreach (int c in counts) best = Math.Max(best, c);
            if (best <= 0) return l;
            for (int i = 0; i < counts.Length; i++) if (counts[i] == best) l.Add(i);
            return l;
        }

        /// A tie broken the same way on every device: 'random' picks among the tied by a hash of the vote id.
        public static int TieBreak(string mode, string voteId, List<int> tied, int defaultIndex)
        {
            if (tied == null || tied.Count == 0) return defaultIndex;
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "random":
                {
                    uint h = 2166136261;
                    foreach (char c in voteId ?? "") h = (h ^ c) * 16777619;
                    return tied[(int)(h % (uint)tied.Count)];
                }
                case "first": return tied[0];
                case "last": return tied[tied.Count - 1];
                default: return defaultIndex;   // 'default' (and a lost Lua tiebreak): the default option, as before
            }
        }

        /// The level for a name: exact id, the address leaf, the display name, then a slug match. Null = unknown.
        public static RunnerLevel MatchLevel(IList<RunnerLevel> levels, string name)
        {
            if (levels == null || string.IsNullOrWhiteSpace(name)) return null;
            string want = name.Trim();
            // a full address ("DreamSports/Runners/Levels/A_FloorZones"): that address, else its leaf
            if (want.IndexOf('/') >= 0)
            {
                foreach (var l in levels) if (l != null && string.Equals(l.address, want, StringComparison.OrdinalIgnoreCase)) return l;
                foreach (var l in levels) if (l != null && string.Equals(l.id, want, StringComparison.OrdinalIgnoreCase)) return l;
                want = Leaf(want);
            }
            foreach (var l in levels) if (l != null && string.Equals(l.id, want, StringComparison.Ordinal)) return l;
            foreach (var l in levels) if (l != null && string.Equals(l.id, want, StringComparison.OrdinalIgnoreCase)) return l;
            foreach (var l in levels) if (l != null && string.Equals(Leaf(l.address), want, StringComparison.OrdinalIgnoreCase)) return l;
            foreach (var l in levels) if (l != null && string.Equals(l.name, want, StringComparison.OrdinalIgnoreCase)) return l;
            string s = Slug(want);
            foreach (var l in levels) if (l != null && (Slug(l.id) == s || Slug(l.name) == s || Slug(Leaf(l.address)) == s)) return l;
            return null;
        }

        public static string Leaf(string address)
        {
            if (string.IsNullOrEmpty(address)) return "";
            int i = address.LastIndexOfAny(new[] { '/', '\\' });
            string leaf = i >= 0 ? address.Substring(i + 1) : address;
            int dot = leaf.LastIndexOf('.');
            return dot > 0 ? leaf.Substring(0, dot) : leaf;
        }

        public static string Slug(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = new StringBuilder();
            string x = s.StartsWith("A_", StringComparison.Ordinal) || s.StartsWith("L_", StringComparison.Ordinal) ? s.Substring(2) : s;
            foreach (char c in x.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) b.Append(c);
            return b.ToString();
        }

        /// The standing load of a tile (newest wins), null = nothing there.
        public static RunnerLoad At(IList<RunnerLoad> loads, int tile)
        {
            RunnerLoad hit = null;
            if (loads != null) foreach (var l in loads) if (l != null && l.Covers(tile) && (hit == null || l.id > hit.id)) hit = l;
            return hit;
        }

        /// Same zone ("" / null = the main board).
        public static bool SameZone(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

        /// The loads of one zone ("" / null = the main board).
        public static List<RunnerLoad> InZone(IList<RunnerLoad> loads, string zone)
        {
            var l = new List<RunnerLoad>();
            if (loads != null) foreach (var x in loads) if (x != null && SameZone(x.zone, zone)) l.Add(x);
            return l;
        }

        /// Put a new load on the board: every load OF THE SAME ZONE sharing a tile with it leaves (the rest keep playing).
        public static List<RunnerLoad> Apply(IList<RunnerLoad> loads, RunnerLoad add)
        {
            var next = new List<RunnerLoad>();
            if (loads != null)
                foreach (var l in loads)
                {
                    if (l == null) continue;
                    if (!SameZone(l.zone, add.zone)) { next.Add(l); continue; }
                    bool overlap = false;
                    foreach (int t in add.tiles) if (l.Covers(t)) { overlap = true; break; }
                    if (!overlap) next.Add(l);
                }
            next.Add(add);
            next.Sort((a, b) => a.id.CompareTo(b.id));
            return next;
        }

        /// An edge between two tiles is INTERNAL (no wall) when one load covers both.
        public static bool Joined(IList<RunnerLoad> loads, int a, int b)
        {
            var la = At(loads, a); var lb = At(loads, b);
            return la != null && la == lb;
        }

        /// The look an inner edge wears: the director's look when it set one, "open" when one level spans both tiles,
        /// else "closed" (a standalone level shows its closed look).
        public static string EdgeLook(IList<RunnerLoad> loads, int a, int b, string directorLook)
        {
            if (!string.IsNullOrEmpty(directorLook)) return directorLook;
            return Joined(loads, a, b) ? "open" : "closed";
        }

        // ── JSON (the shared table's values) ─────────────────────────

        public static string EncodeLoads(IList<RunnerLoad> loads)
        {
            var arr = new JSONObject(JSONObject.Type.Array);
            if (loads != null)
                foreach (var l in loads)
                {
                    if (l == null) continue;
                    var o = new JSONObject();
                    o.AddField("i", l.id);
                    o.AddField("lv", l.level ?? "");
                    var t = new JSONObject(JSONObject.Type.Array); foreach (int x in l.tiles) t.Add(x);
                    o.AddField("t", t);
                    if (l.done) o.AddField("dn", true);
                    if (!string.IsNullOrEmpty(l.result)) o.AddField("rs", l.result);
                    o.AddField("at", Math.Round(l.at, 3));
                    if (!string.IsNullOrEmpty(l.zone)) o.AddField("zn", l.zone);
                    var opj = Parse(l.options);
                    if (opj != null && opj.type == JSONObject.Type.Object) o.AddField("op", opj);
                    arr.Add(o);
                }
            return arr.Print();
        }

        public static List<RunnerLoad> DecodeLoads(string json)
        {
            var l = new List<RunnerLoad>();
            var j = Parse(json);
            if (j == null || j.type != JSONObject.Type.Array || j.list == null) return l;
            foreach (var o in j.list)
            {
                if (o == null || o.type != JSONObject.Type.Object) continue;
                var r = new RunnerLoad { id = (int)Num(o, "i"), level = Str(o, "lv"), done = Bool(o, "dn"), result = Str(o, "rs"), at = Num(o, "at"), zone = Str(o, "zn"), options = o.GetField("op")?.Print() };
                var t = o.GetField("t");
                if (t?.list != null) { var ts = new List<int>(); foreach (var x in t.list) if (x != null && x.isNumber) ts.Add((int)x.doubleValue); r.tiles = ts.ToArray(); }
                l.Add(r);
            }
            return l;
        }

        public static string EncodePending(IList<RunnerPending> pend)
        {
            var arr = new JSONObject(JSONObject.Type.Array);
            if (pend != null)
                foreach (var p in pend)
                {
                    if (p == null) continue;
                    var o = new JSONObject();
                    o.AddField("k", p.kind ?? "");
                    o.AddField("lv", p.level ?? "");
                    if (p.tile >= 0) o.AddField("t", p.tile);
                    if (p.nuTitle != null) o.AddField("nt", p.nuTitle);
                    if (p.nuSub != null) o.AddField("ns", p.nuSub);
                    o.AddField("at", Math.Round(p.at, 3));
                    arr.Add(o);
                }
            return arr.Print();
        }

        public static List<RunnerPending> DecodePending(string json)
        {
            var l = new List<RunnerPending>();
            var j = Parse(json);
            if (j?.list == null || j.type != JSONObject.Type.Array) return l;
            foreach (var o in j.list)
            {
                if (o == null || o.type != JSONObject.Type.Object) continue;
                var t = o.GetField("t");
                l.Add(new RunnerPending { kind = Str(o, "k"), level = Str(o, "lv"), tile = t != null && t.isNumber ? (int)t.doubleValue : -1,
                    nuTitle = o.GetField("nt") != null ? Str(o, "nt") : null, nuSub = o.GetField("ns") != null ? Str(o, "ns") : null, at = Num(o, "at") });
            }
            return l;
        }

        public static JSONObject Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return null;
            try { return new JSONObject(json); } catch (Exception) { return null; }
        }

        public static string Str(JSONObject o, string k, string fallback = null)
        {
            var v = o?.GetField(k);
            if (v == null || v.type == JSONObject.Type.Null) return fallback;
            return v.type == JSONObject.Type.String ? v.stringValue : v.Print();
        }

        public static double Num(JSONObject o, string k, double fallback = 0)
        {
            var v = o?.GetField(k);
            if (v == null) return fallback;
            if (v.isNumber) return v.doubleValue;
            if (v.type == JSONObject.Type.String && double.TryParse(v.stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            return fallback;
        }

        public static bool Bool(JSONObject o, string k)
        {
            var v = o?.GetField(k);
            if (v == null) return false;
            if (v.type == JSONObject.Type.Bool) return v.boolValue;
            if (v.isNumber) return v.doubleValue != 0;
            return false;
        }

        public static string Q(string s) => s == null ? "null" : JSONObject.CreateStringObject(s).Print();
    }
}
