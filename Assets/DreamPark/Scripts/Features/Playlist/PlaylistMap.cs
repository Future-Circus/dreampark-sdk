// ─────────────────────────────────────────────────────────────────────
//  PlaylistMap.cs — THE MAP IS GAME- AND PLAYLIST-CONTROLLED (pl-announce, Oct 22 2026; Aidan: "showing the map should
//  be entirely up to the game AND playlist"). The platform never pops the map or marks it on its own; Lua asks:
//
//    game Lua (any headset)                     playlist Lua (the leader)
//    dp.map.mark(target, { done = b })          run.map.mark(ref, { done = b })
//    dp.map.unmark(target) / dp.map.clear()     run.map.unmark(ref) / run.map.clear()
//    dp.map.show{ seconds = n, mark = {…} }     run.map.show{ seconds = n, mark = {…} }   run.show{ …, map = true | n }
//    dp.map.hide()                              run.map.hide()
//
//  target: nil = the calling attraction · a string = an attraction (pool id, stop id, attraction name or occurrence id) ·
//  an object (GameObject / component / script self) · a position {x, y, z} (world; kept local to its attraction).
//  Marks and show / hide live in the run's shared state (every headset in the run, late joiners included); a non-leader's
//  call is a request the leader applies. The map draws not done = an empty circle + down arrow, done = a white filled
//  circle + check (pl-ux, PlaylistRoundDiorama). Attraction marks also show in PlaylistRun.Waypoints (run.waypoint is the
//  playlist's older sugar for the same marks on attractions).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    /// One mark on the map, as THIS device resolves it.
    public sealed class PlaylistMapMark
    {
        public string id;
        /// attraction | object | spot
        public string kind;
        /// attraction marks: the ref (pool id / attraction leaf name).
        public string reference;
        /// The stop it is in on this device (null when its attraction is not out now).
        public string occurrenceId;
        /// The attraction's leaf name (A_CoinGarden), when known.
        public string attraction;
        /// object marks: the object's key under its stop root (PlaylistAnnounce.KeyOf).
        public string path;
        /// spot marks: the position, local to the stop root.
        public Vector3 local;
        public bool done;
    }

    public sealed partial class PlaylistRunWorld
    {
        /// dp.map.show reached THIS device (seconds ≤ 0 = until hidden). Core: PlaylistMap.Shown → the round diorama.
        public Action<PlaylistRun, double> MapShow = (r, s) => PlaylistMap.RaiseShow(r, s);
        /// dp.map.hide reached THIS device.
        public Action<PlaylistRun> MapHide = r => PlaylistMap.RaiseHide(r);

        /// Every live run's map marks on this device.
        public List<PlaylistMapMark> MapMarks()
        {
            var l = new List<PlaylistMapMark>();
            foreach (var r in runs.Values) if (r != null && !r.Disposed) l.AddRange(r.MapMarks);
            return l;
        }
    }

    public sealed partial class PlaylistRun
    {
        public const double MapDefaultSeconds = 4.0;
        public const int MapMaxMarks = 48;
        /// The same show / hide asked again within this long is one (every headset's game calls it at once).
        public const double MapCoalesceSeconds = 1.0;

        int _mapSeq = -1;
        long _mapVersion = -1;

        /// run op 'map' (playlist Lua on the leader) and the game side (any device: a non-leader's call is a request).
        /// json: {op = mark|unmark|clear|show|hide, target = {ref | occ [, path | at {x,y,z}]}, done, seconds, mark}.
        public string MapOp(string json)
        {
            if (State == null) return "false";
            if (!IsLeader) { State.Request("map", PlaylistEvents.Clamp(json ?? "{}", 2048)); return "true"; }
            return MapApply(json) ? "true" : "false";
        }

        /// The leader applies a map op.
        bool MapApply(string json)
        {
            var o = SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json);
            if (o == null || o.type != JSONObject.Type.Object || State == null) return false;
            string op = PlaylistRunPlan.Str(o, "op", "mark");
            var rows = MapRows();
            bool changed = false, ok = true;
            switch (op)
            {
                case "mark": ok = MapMark(rows, o.GetField("target"), PlaylistRunPlan.Bool(o, "done", false), ref changed); break;
                case "unmark":
                {
                    string id = MapId(o.GetField("target"), out _, out _);
                    if (id == null) { ok = false; break; }
                    int n = rows.RemoveAll(r => PlaylistRunPlan.Str(r, "id", "") == id);
                    changed = n > 0;
                    break;
                }
                case "clear":
                {
                    // a game clears its own attraction's marks (target.occ); the playlist clears every mark
                    var t = o.GetField("target");
                    string occ = PlaylistRunPlan.Str(t, "occ", null);
                    string key = occ != null ? KeyOfStop(occ) : null;
                    int n = occ == null ? rows.Count
                        : rows.RemoveAll(r => PlaylistRunPlan.Str(r, "occ", null) == occ || (key != null && string.Equals(PlaylistRunPlan.Str(r, "ref", null), key, StringComparison.OrdinalIgnoreCase)));
                    if (occ == null) rows.Clear();
                    changed = n > 0;
                    break;
                }
                case "show":
                {
                    var m = o.GetField("mark");
                    if (m != null && m.type == JSONObject.Type.Array && m.list != null)
                        foreach (var x in m.list) MapMark(rows, x?.GetField("target") ?? x, PlaylistRunPlan.Bool(x, "done", false), ref changed);
                    else if (m != null && m.type == JSONObject.Type.Object)
                        MapMark(rows, m.GetField("target") ?? m, PlaylistRunPlan.Bool(m, "done", false), ref changed);
                    double s = o.GetField("seconds") != null ? PlaylistRunPlan.Num(o, "seconds") : MapDefaultSeconds;
                    MapSignal("show", s);
                    break;
                }
                case "hide": MapSignal("hide", 0); break;
                default: Log($"dp.map: unknown op '{op}'"); return false;
            }
            if (changed) MapSave(rows);
            return ok;
        }

        bool MapMark(List<JSONObject> rows, JSONObject target, bool done, ref bool changed)
        {
            string id = MapId(target, out string kind, out JSONObject row);
            if (id == null) { Log($"dp.map.mark: unknown target {target?.Print() ?? "nil"} — ignored"); return false; }
            var cur = rows.Find(r => PlaylistRunPlan.Str(r, "id", "") == id);
            if (cur != null)
            {
                if (PlaylistRunPlan.Bool(cur, "done", false) == done) return true;
                cur.SetField("done", done); changed = true;
                return true;
            }
            if (rows.Count >= MapMaxMarks) { Log($"dp.map.mark: {MapMaxMarks} marks already — '{id}' ignored"); return false; }
            row.SetField("id", id); row.SetField("kind", kind); row.SetField("done", done);
            rows.Add(row); changed = true;
            return true;
        }

        /// A target → its row id (+ kind and a fresh row with its fields). Null = unknown.
        string MapId(JSONObject t, out string kind, out JSONObject row)
        {
            kind = null; row = new JSONObject();
            if (t == null || t.type != JSONObject.Type.Object) return null;
            string occ = PlaylistRunPlan.Str(t, "occ", null);
            string path = PlaylistRunPlan.Str(t, "path", null);
            var at = t.GetField("at");
            if (occ != null && path != null)
            {
                kind = "object"; row.AddField("occ", occ); row.AddField("path", path);
                return "o:" + occ + "/" + path;
            }
            if (occ != null && at != null && at.type == JSONObject.Type.Object)
            {
                float x = (float)PlaylistRunPlan.Num(at, "x"), y = (float)PlaylistRunPlan.Num(at, "y"), z = (float)PlaylistRunPlan.Num(at, "z");
                var a = new JSONObject(); a.AddField("x", R2(x)); a.AddField("y", R2(y)); a.AddField("z", R2(z));
                kind = "spot"; row.AddField("occ", occ); row.AddField("at", a);
                return "p:" + occ + ":" + R2(x).ToString(CultureInfo.InvariantCulture) + "," + R2(z).ToString(CultureInfo.InvariantCulture);
            }
            string reference = PlaylistRunPlan.Str(t, "ref", null) ?? occ;
            if (reference == null) return null;
            string key = WaypointKey(reference) ?? KeyOfStop(reference);
            if (key == null) return null;
            kind = "attraction"; row.AddField("ref", key);
            return "a:" + key.ToLowerInvariant();
        }

        static float R2(float v) => Mathf.Round(v * 100f) / 100f;

        /// An occurrence id → its attraction's ref (the pool id / stop id, else its attraction leaf). Null = unknown.
        string KeyOfStop(string occ)
        {
            var plan = Plan;
            if (plan == null || string.IsNullOrEmpty(occ)) return null;
            foreach (var l in plan.legs ?? new List<PlaylistRunLeg>())
                foreach (var s in l?.stops ?? new List<PlaylistRunStop>())
                    if (s != null && s.occurrenceId == occ)
                        return WaypointKey(!string.IsNullOrEmpty(s.stopId) ? s.stopId : PlaylistAutoRules.Leaf(s.attraction)) ?? PlaylistAutoRules.Leaf(s.attraction);
            return null;
        }

        List<JSONObject> MapRows()
        {
            var j = SharedState.ParseFragment(State?.GetJson("map"));
            var l = new List<JSONObject>();
            foreach (var x in j?.list ?? new List<JSONObject>()) if (x != null && x.type == JSONObject.Type.Object) l.Add(x);
            return l;
        }

        void MapSave(List<JSONObject> rows)
        {
            var arr = new JSONObject(JSONObject.Type.Array);
            foreach (var r in rows) arr.Add(r);
            string j = arr.Print();
            if (j.Length > SharedState.MaxValueChars) { Log("dp.map: the marks no longer fit the shared state — not saved"); return; }
            if (State.GetJson("map") != j) State.Set("map", j);
        }

        /// show / hide to every headset in the run (a counter: each device acts once per change).
        void MapSignal(string op, double seconds)
        {
            var cur = SharedState.ParseFragment(State.GetJson("mapshow"));
            // the same call from every headset at once (challenge_won reaches each headset's copy of the attraction): one pop
            if (cur != null && PlaylistRunPlan.Str(cur, "op", "") == op && _w.Clock() - PlaylistRunPlan.Num(cur, "t") < MapCoalesceSeconds) return;
            int seq = cur != null ? (int)PlaylistRunPlan.Num(cur, "seq") + 1 : 1;
            var j = new JSONObject();
            j.AddField("seq", seq); j.AddField("op", op); j.AddField("s", seconds); j.AddField("t", _w.Clock());
            State.Set("mapshow", j.Print());
            Log($"dp.map.{op}{(op == "show" ? $"{{seconds = {PlaylistRunRules.Num(seconds)}}}" : "")} (#{seq})");
            AddLog("map_" + op, null, PlaylistRunRules.Num(seconds));
        }

        /// The built-in rule for run.show{map = 'multi_room'}: the map pops only when the new field spans more than one room.
        public const string MultiRoom = "multi_room";

        /// run.show{…, map = true | seconds | 'multi_room' | {seconds, mark, when = 'multi_room'}}: the map pops once that
        /// field is out (leader).
        void MapAfterField(string json)
        {
            var req = SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json);
            var m = req?.GetField("map");
            if (m == null || State == null) return;
            if (m.type == JSONObject.Type.Bool && !m.boolValue) return;
            if (m.type == JSONObject.Type.Null) return;
            var pend = new JSONObject();
            pend.AddField("after", Current?.Plan?.Current?.id ?? "");
            if (m.type == JSONObject.Type.String)
            {
                if (m.stringValue != MultiRoom) { Log($"run.show{{map = '{m.stringValue}'}}: unknown rule (use true, seconds or '{MultiRoom}') — no map"); return; }
                pend.AddField("when", MultiRoom);
            }
            if (m.type == JSONObject.Type.Number) pend.AddField("seconds", m.doubleValue);
            else if (m.type == JSONObject.Type.Object)
            {
                if (m.GetField("seconds") != null) pend.AddField("seconds", PlaylistRunPlan.Num(m, "seconds"));
                if (m.GetField("mark") != null) pend.AddField("mark", m.GetField("mark"));
                if (PlaylistRunPlan.Str(m, "when", null) == MultiRoom) pend.AddField("when", MultiRoom);
            }
            // a set that is already the one out (the first set, previewed before anything was packed): its field is out now
            var plan = Current?.Plan;
            var set = SharedState.ParseFragment(State.GetJson("set"));
            if (plan != null && plan.sets && set != null && (int)PlaylistRunPlan.Num(set, "n") == PlaylistSets.SetNumber(plan.Current?.id))
                pend.SetField("after", "");
            State.Set("mapnext", pend.Print());
        }

        /// Every device, every tick: act on a new show / hide; the leader fires a run.show{map} once its field is out.
        void MapPoll(double now, bool leader, IPlaylistLeg leg)
        {
            if (State == null) return;
            // not synced yet: the run's state is not here (a first look now would miss the shows and replay them later)
            if (State.Version != _mapVersion && (leader || Synced)) { _mapVersion = State.Version; MapShowPoll(now); }
            if (!leader || leg == null || !State.Has("mapnext")) return;
            var pend = SharedState.ParseFragment(State.GetJson("mapnext"));
            if (pend == null) return;
            string after = PlaylistRunPlan.Str(pend, "after", "");
            if (after.Length > 0 && (leg.Plan?.Current?.id ?? "") == after) return;
            State.Delete("mapnext");
            if (PlaylistRunPlan.Str(pend, "when", null) == MultiRoom)
            {
                var pl = Placed(leg);
                if (pl.rooms <= 1) { Log($"run.show{{map = '{MultiRoom}'}}: the field spans {pl.rooms} room — no map"); return; }
            }
            var show = new JSONObject();
            show.AddField("op", "show");
            if (pend.GetField("seconds") != null) show.AddField("seconds", PlaylistRunPlan.Num(pend, "seconds"));
            if (pend.GetField("mark") != null) show.AddField("mark", pend.GetField("mark"));
            MapApply(show.Print());
        }

        void MapShowPoll(double now)
        {
            var ms = SharedState.ParseFragment(State.GetJson("mapshow"));
            int seq = ms != null ? (int)PlaylistRunPlan.Num(ms, "seq") : 0;
            if (_mapSeq < 0)
            {
                // first look (a late joiner): no replay — unless a show is still on, for what is left of it
                _mapSeq = seq;
                if (ms != null && PlaylistRunPlan.Str(ms, "op", "") == "show")
                {
                    double s = PlaylistRunPlan.Num(ms, "s"), left = s - (now - PlaylistRunPlan.Num(ms, "t"));
                    if (s > 0 && left > 0.5) Fire("show", left);
                }
            }
            else if (seq != _mapSeq)
            {
                _mapSeq = seq;
                Fire(PlaylistRunPlan.Str(ms, "op", "show"), PlaylistRunPlan.Num(ms, "s"));
            }
            void Fire(string op, double s)
            {
                try { if (op == "hide") _w.MapHide?.Invoke(this); else _w.MapShow?.Invoke(this, s); }
                catch (Exception e) { Log("map show / hide threw: " + e.Message); }
            }
        }

        /// What the field that is out now spans: its rooms (distinct rooms its attractions stand in) and attractions.
        /// run.placed() in playlist Lua.
        public (int rooms, int attractions) Placed(IPlaylistLeg leg)
        {
            var rooms = new HashSet<string>(StringComparer.Ordinal);
            int n = 0;
            foreach (var s in leg?.Plan?.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null || s.bookend) continue;
                n++;
                rooms.Add(s.roomId ?? "");
            }
            return (n == 0 ? 0 : Math.Max(1, rooms.Count), n);
        }

        string PlacedJson()
        {
            var p = Placed(Current);
            return "{\"rooms\":" + p.rooms + ",\"attractions\":" + p.attractions + "}";
        }

        /// The map marks on THIS device (assignment order), each with its standing stop when out.
        public List<PlaylistMapMark> MapMarks
        {
            get
            {
                var l = new List<PlaylistMapMark>();
                if (State == null) return l;
                var plan = Plan;
                foreach (var r in MapRows())
                {
                    var m = new PlaylistMapMark
                    {
                        id = PlaylistRunPlan.Str(r, "id", ""), kind = PlaylistRunPlan.Str(r, "kind", "attraction"),
                        reference = PlaylistRunPlan.Str(r, "ref", null), occurrenceId = PlaylistRunPlan.Str(r, "occ", null),
                        path = PlaylistRunPlan.Str(r, "path", null), done = PlaylistRunPlan.Bool(r, "done", false),
                    };
                    var at = r.GetField("at");
                    if (at != null) m.local = new Vector3((float)PlaylistRunPlan.Num(at, "x"), (float)PlaylistRunPlan.Num(at, "y"), (float)PlaylistRunPlan.Num(at, "z"));
                    foreach (var st in plan?.Current?.stops ?? new List<PlaylistRunStop>())
                    {
                        if (st == null) continue;
                        bool hit = m.reference != null ? PlaylistSets.Matches(st, m.reference) : st.occurrenceId == m.occurrenceId;
                        if (!hit) continue;
                        m.occurrenceId = st.occurrenceId; m.attraction = PlaylistAutoRules.Leaf(st.attraction);
                        break;
                    }
                    if (m.attraction == null && m.reference != null)
                        foreach (var st in plan?.pool ?? new List<PlaylistRunStop>())
                            if (PlaylistSets.Matches(st, m.reference)) { m.attraction = PlaylistAutoRules.Leaf(st.attraction); break; }
                    l.Add(m);
                }
                return l;
            }
        }

        /// Attraction marks as waypoints (PlaylistRun.Waypoints merges them: the map's done / not-done attraction marks).
        List<PlaylistWaypoint> MapWaypoints()
        {
            var l = new List<PlaylistWaypoint>();
            foreach (var m in MapMarks)
                if (m.kind == "attraction" && m.reference != null)
                    l.Add(new PlaylistWaypoint { reference = m.reference, attraction = m.attraction, occurrenceId = m.occurrenceId, done = m.done, how = m.done ? "map" : null });
            return l;
        }
    }

    /// The device side for the map's drawer (pl-ux, PlaylistRoundDiorama) and for game Lua (dp.map, DreamParkLuaMap).
    public static class PlaylistMap
    {
        /// The map was asked to show (playlist id, seconds; ≤ 0 = until hidden) / to hide — on THIS device.
        public static event Action<string, double> Shown;
        public static event Action<string> Hidden;

        internal static void RaiseShow(PlaylistRun r, double seconds)
        {
            Debug.Log($"[pl-map] show {r?.Plan?.playlistId} {seconds:0.#} s");
            Shown?.Invoke(r?.Plan?.playlistId, seconds);
        }

        internal static void RaiseHide(PlaylistRun r)
        {
            Debug.Log($"[pl-map] hide {r?.Plan?.playlistId}");
            Hidden?.Invoke(r?.Plan?.playlistId);
        }

        /// The marks of this playlist's run on this device (null / empty id = every run).
        public static List<PlaylistMapMark> Marks(string playlistId)
        {
            var l = new List<PlaylistMapMark>();
            foreach (var r in PlaylistRunWorld.Main.Runs)
                if (r != null && !r.Disposed && (string.IsNullOrEmpty(playlistId) || r.Plan?.playlistId == playlistId)) l.AddRange(r.MapMarks);
            return l;
        }

        /// Where a mark is in the world on this device (object / spot / the attraction's root), or null.
        public static Vector3? WorldOf(PlaylistMapMark m)
        {
            if (m == null || string.IsNullOrEmpty(m.occurrenceId)) return null;
            var root = RootOf(m.occurrenceId, out _);
            if (root == null) return null;
            if (m.kind == "spot") return root.transform.TransformPoint(m.local);
            if (m.kind == "object") { var t = Find(root.transform, m.path); return t != null ? t.position : (Vector3?)null; }
            return root.transform.position;
        }

        /// The live root of a standing stop on this device.
        public static GameObject RootOf(string occ, out PlaylistRun run)
        {
            run = null;
            foreach (var r in PlaylistRunWorld.Main.Runs)
            {
                var leg = r?.Current;
                if (r == null || r.Disposed || leg == null) continue;
                foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
                    if (s != null && s.occurrenceId == occ) { run = r; return leg.StopRoot(s); }
            }
            return null;
        }

        /// The stop an object stands in (its run, occurrence and root), or false.
        public static bool Locate(GameObject go, out PlaylistRun run, out string occ, out GameObject root)
        {
            run = null; occ = null; root = null;
            if (go == null) return false;
            foreach (var r in PlaylistRunWorld.Main.Runs)
            {
                var leg = r?.Current;
                if (r == null || r.Disposed || leg == null) continue;
                foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
                {
                    if (s == null) continue;
                    var sr = leg.StopRoot(s);
                    if (sr != null && go.transform.IsChildOf(sr.transform)) { run = r; occ = s.occurrenceId; root = sr; return true; }
                }
            }
            return false;
        }

        /// The only / first live run (a call from outside any stop).
        static PlaylistRun AnyRun()
        {
            foreach (var r in PlaylistRunWorld.Main.Runs) if (r != null && !r.Disposed && r.Current != null) return r;
            return null;
        }

        /// An object's key under its stop root → the object (PlaylistAnnounce.KeyOf segments: "Name" / "Name~k").
        public static Transform Find(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            if (path == ".") return root;
            var t = root;
            foreach (var seg in path.Split('/'))
            {
                string name = seg; int k = 0;
                int tilde = seg.LastIndexOf('~');
                if (tilde > 0 && int.TryParse(seg.Substring(tilde + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kk)) { name = seg.Substring(0, tilde); k = kk; }
                Transform next = null;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name.Replace("(Clone)", "").Trim() != name) continue;
                    if (k-- == 0) { next = c; break; }
                }
                if (next == null) return null;
                t = next;
            }
            return t;
        }

        // ── game Lua (dp.map, DreamParkLuaMap) ──────────────────────────
        /// targetKind: self (the caller's attraction) | ref (an attraction by name / id) | object (obj) | world (a position).
        /// show: the target + done are the mark that goes with it when withMark.
        public static bool GameOp(GameObject caller, string op, string targetKind, string reference, GameObject obj, Vector3 world, bool done, double seconds, bool withMark)
        {
            bool inStop = Locate(targetKind == "object" && obj != null ? obj : caller, out var run, out string occ, out var root);
            if (run == null) run = AnyRun();
            if (run == null) { Debug.Log($"[pl-map] dp.map.{op}: no playlist run here — ignored"); return false; }
            bool needsTarget = op == "mark" || op == "unmark" || (op == "show" && withMark);
            JSONObject t = null;
            if (needsTarget || op == "clear")
            {
                t = new JSONObject();
                switch (targetKind)
                {
                    case "ref": t.AddField("ref", reference ?? ""); break;
                    case "object":
                        if (!inStop || obj == null) { Debug.LogWarning($"[pl-map] dp.map.{op}: {(obj != null ? obj.name : "?")} is in no attraction of the run — ignored"); return false; }
                        t.AddField("occ", occ); t.AddField("path", PlaylistAnnounce.KeyOf(obj, root)); break;
                    case "world":
                    {
                        if (!inStop) { Debug.LogWarning($"[pl-map] dp.map.{op}: a position needs the calling script to be in an attraction of the run — ignored"); return false; }
                        Vector3 l = root.transform.InverseTransformPoint(world);
                        var a = new JSONObject(); a.AddField("x", l.x); a.AddField("y", l.y); a.AddField("z", l.z);
                        t.AddField("occ", occ); t.AddField("at", a); break;
                    }
                    default:   // self: the calling attraction
                        if (!inStop) { Debug.LogWarning($"[pl-map] dp.map.{op}: the calling script is in no attraction of the run — ignored"); return false; }
                        if (op == "clear") t.AddField("occ", occ); else t.AddField("ref", occ);
                        break;
                }
            }
            var j = new JSONObject();
            j.AddField("op", op);
            if (op == "show")
            {
                j.AddField("seconds", seconds);
                if (withMark) { var m = new JSONObject(); m.AddField("target", t); m.AddField("done", done); j.AddField("mark", m); }
            }
            else
            {
                if (t != null) j.AddField("target", t);
                j.AddField("done", done);
            }
            return run.MapOp(j.Print()) == "true";
        }
    }
}
