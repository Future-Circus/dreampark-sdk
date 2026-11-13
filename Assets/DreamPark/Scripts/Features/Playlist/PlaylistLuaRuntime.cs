// ─────────────────────────────────────────────────────────────────────
//  PlaylistLuaRuntime.cs — runs GOAL BLOCKS and the PLAYLIST LUA in XLua
//  (repair-oct20 CONTRACT § 2 / § 3). Leader only; PlaylistRun drives it.
//
//  A goal block is Lua returning function(stop):
//    stop.id, stop.attraction, stop.kind, stop.options, stop.state
//    stop.on(source, filter, fn)  stop.count{kind} → {total, remaining, done, by}   stop.progress(n, of, unit)
//    stop.mark{kind} (announced objects, § 4.2); stop.census{script|marker|name} and stop.mark{script|marker|name}:
//    DEPRECATED, kept until the titles announce (stop.census{kind} reads the announce list too).
//    stop.won{by=, ...} (= stop.done)   stop.lost{why=, player=} (stop.timed_out() = lost{why='time'}, stop.failed = lost)
//    stop.skip() (= won, only when on offer)   stop.time(sec) (each attempt's limit)   stop.attempt() → {n, e, l, p, so…}
//    stop.timer(sec, fn) / stop.every(sec, fn)   stop.mark{...} / stop.unmark()
//    stop.edge('start'|'far') → {x,y,z} (stop-local)   stop.crossing{...}   stop.jump{pads=...}   stop.reach{at=...}   stop.request(table, key, value)
//    stop.hint(text)   stop.log(...)
//  Playlist Lua returns { on_start, on_stop_result, on_set_done, on_finale } with a `run` object (PLAYLIST-PLAN § 3):
//    run.show{attractions, rooms, place, lock} / run.add{…} / run.remove{…} (sets, all at once), run.finish{result},
//    run.badge(id) | run.badge(player, id), run.space() → {rooms}, run.pool(), run.set(), run.skip(ref),
//    stop.send(name, data) → the stop's attraction objects on every headset (onevent / dp.on, the challenge-event channel),
//    run.waypoint(ref, true|false) (§ 6.7: which attractions count on the map), run.batches{…, waypoints = true, map = true},
//    run.map.mark(ref, {done}) / unmark / clear / show{seconds, mark} / hide (PlaylistMap.cs; run.show{…, map = true}),
//    run.can_play(playlistId) → {ok, need} (also stop.can_play), run.started_from (the playlist whose finish{next} started it),
//    run.state, run.lock(ref, {needs, sign}), run.unlock, run.locked, run.flag, run.result, run.players(), run.leader(),
//    run.batches{challenges, boss, rooms, badge} (the world pattern), run.rounds({ids}, own) (arc[] records, converted).
//    Kept for records from before sets: choose_next, run.skip_round(), run.next_round(legId).
//
//  Sandbox: each block's chunk loads with its own environment (stop, dp.dreamie, dp.event, math,
//  string, table, pairs …) — no CS, require, load, os, io, debug, _G, setmetatable (the server's
//  allow-list mirrors this). State lives in stop.state / run.state only; after every call the runtime
//  reads it back as JSON so PlaylistRun can sync it (a handover resumes mid-goal).
//
//  The C# side is engine-free (no UnityEngine calls) so Tools/playlist-run-offline runs the REAL
//  bootstrap in a real XLua VM per fake device.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;
using UnityEngine;
using XLua;

namespace DreamPark
{
    /// What a goal block / the playlist Lua can do (PlaylistRun implements it).
    public interface IPlaylistGoalHost
    {
        void GoalProgress(string occ, double n, double of, string unit);
        void GoalOutcome(string occ, string kind, string fieldsJson);
        int GoalTimer(string occ, double seconds, bool every);
        double GoalCensus(string occ, string filterJson);
        /// stop.count{kind}: the announce list's {total, remaining, done, by} under the stop root (JSON) — pl-announce, § 4.2.
        string GoalCount(string occ, string kind);
        void GoalMark(string occ, string specJson);
        bool GoalEdge(string occ, string which, out Vector3 local);
        void GoalJump(string occ, string specJson);
        /// stop.jump{} with no pads: how many pads the attraction authored for `chain` ("" = its default chain) — pl-announce.
        int GoalJumpAuthored(string occ, string chain);
        /// Reset a crossing player to the start (why: forced | hit; extraJson e.g. {cause}).
        void GoalJumpReset(string occ, string player, string why, string extraJson);
        void GoalRequest(string occ, string table, string key, string valueJson);
        void GoalHint(string occ, string text);
        void GoalSubscribe(string occ, string source, string name);
        void GoalEmit(string occ, string name, string dataJson);
        void GoalLog(string occ, string line);
        /// run.* ops: lock / unlock / locked / flag / badge / result / players / leader / skip_round / next_round.
        string RunOp(string op, string a, string json);
    }

    public sealed class PlaylistLuaRuntime
    {
        public static Action<string> Log = m => Debug.Log(m);

        readonly LuaEnv _env;
        readonly Dictionary<string, (IPlaylistGoalHost host, string occ)> _blocks = new Dictionary<string, (IPlaylistGoalHost, string)>(StringComparer.Ordinal);
        LuaFunction _prepare, _start, _event, _timer, _end, _runPrepare, _runStart, _runCall, _runEnd;

        // The shipping app's Lua sandbox removes `load` (LuaSecuritySandbox): blocks compile from C# into their OWN
        // environment table (no CS, no load, no io / os / debug — only what the sandbox below hands them).
        LuaFunction Compile(string source, string name, LuaTable env) => _env.LoadString(source ?? "", name, env);

        static readonly Dictionary<LuaEnv, PlaylistLuaRuntime> s_byEnv = new Dictionary<LuaEnv, PlaylistLuaRuntime>();
        /// The env Unity runs (LuaBehaviour's); the offline sim passes its own per fake device.
        public static Func<LuaEnv> DefaultEnv = () => LuaBehaviour.GetLuaEnv();

        public static PlaylistLuaRuntime For(LuaEnv env)
        {
            if (env == null) return null;
            lock (s_byEnv)
            {
                if (s_byEnv.TryGetValue(env, out var r)) return r;
                r = new PlaylistLuaRuntime(env);
                s_byEnv[env] = r;
                return r;
            }
        }

        PlaylistLuaRuntime(LuaEnv env)
        {
            _env = env;
            Register();
        }

        public LuaEnv Env => _env;

        static string Sid(string runId, string occ) => (runId ?? "") + "|" + (occ ?? "");

        (IPlaylistGoalHost host, string occ) H(string sid) => _blocks.TryGetValue(sid ?? "", out var h) ? h : (null, null);

        void Register()
        {
            var g = _env.Global;
            g.Set("dp_plr_progress", new Action<string, double, double, string>((sid, n, of, u) => { var h = H(sid); h.host?.GoalProgress(h.occ, n, of, u); }));
            g.Set("dp_plr_outcome", new Action<string, string, string>((sid, k, j) => { var h = H(sid); h.host?.GoalOutcome(h.occ, k, j); }));
            g.Set("dp_plr_timer", new Func<string, double, bool, int>((sid, sec, every) => { var h = H(sid); return h.host != null ? h.host.GoalTimer(h.occ, sec, every) : 0; }));
            g.Set("dp_plr_census", new Func<string, string, double>((sid, f) => { var h = H(sid); return h.host != null ? h.host.GoalCensus(h.occ, f) : 0; }));
            g.Set("dp_plr_count", new Func<string, string, LuaTable>((sid, kind) =>
            {
                var h = H(sid);
                var j = SharedState.ParseFragment(h.host?.GoalCount(h.occ, kind) ?? "{\"total\":0,\"remaining\":0,\"done\":0,\"by\":{}}");
                return j != null ? DreamParkLuaNet.JsonToLua(_env, j) as LuaTable : null;
            }));
            g.Set("dp_plr_mark", new Action<string, string>((sid, j) => { var h = H(sid); h.host?.GoalMark(h.occ, j); }));
            g.Set("dp_plr_edge", new Func<string, string, LuaTable>((sid, which) =>
            {
                var h = H(sid);
                if (h.host == null || !h.host.GoalEdge(h.occ, which, out Vector3 p)) return null;
                var t = _env.NewTable();
                t.Set("x", (double)p.x); t.Set("y", (double)p.y); t.Set("z", (double)p.z);
                return t;
            }));
            g.Set("dp_plr_jump", new Action<string, string>((sid, j) => { var h = H(sid); h.host?.GoalJump(h.occ, j); }));
            g.Set("dp_plr_jump_authored", new Func<string, string, int>((sid, chain) => { var h = H(sid); return h.host != null ? h.host.GoalJumpAuthored(h.occ, chain) : 0; }));
            g.Set("dp_plr_jump_reset", new Action<string, string, string, string>((sid, p, why, x) => { var h = H(sid); h.host?.GoalJumpReset(h.occ, p, why, x); }));
            g.Set("dp_plr_request", new Action<string, string, string, string>((sid, t, k, v) => { var h = H(sid); h.host?.GoalRequest(h.occ, t, k, v); }));
            g.Set("dp_plr_hint", new Action<string, string>((sid, t) => { var h = H(sid); h.host?.GoalHint(h.occ, t); }));
            g.Set("dp_plr_sub", new Action<string, string, string>((sid, s, n) => { var h = H(sid); h.host?.GoalSubscribe(h.occ, s, n); }));
            g.Set("dp_plr_emit", new Action<string, string, string>((sid, n, d) => { var h = H(sid); h.host?.GoalEmit(h.occ, n, d); }));
            g.Set("dp_plr_log", new Action<string, string>((sid, line) => { var h = H(sid); if (h.host != null) h.host.GoalLog(h.occ, line); else Log?.Invoke("[pl-runtime] " + sid + ": " + line); }));
            g.Set("dp_plr_run", new Func<string, string, string, string, LuaTable>(RunOp));
            _env.DoString(Bootstrap, "dp.playlist.goals");
            _prepare = g.Get<LuaFunction>("__plr_prepare");
            _start = g.Get<LuaFunction>("__plr_start");
            _runPrepare = g.Get<LuaFunction>("__plr_run_prepare");
            _event = g.Get<LuaFunction>("__plr_event");
            _timer = g.Get<LuaFunction>("__plr_timer");
            _end = g.Get<LuaFunction>("__plr_end");
            _runStart = g.Get<LuaFunction>("__plr_run_start");
            _runCall = g.Get<LuaFunction>("__plr_run_call");
            _runEnd = g.Get<LuaFunction>("__plr_run_end");
        }

        // run ops come back to Lua as a table {v = <decoded JSON>} (a nil-safe box).
        LuaTable RunOp(string rid, string op, string a, string json)
        {
            var h = H(rid);
            string res = h.host?.RunOp(op, a, json);
            var box = _env.NewTable();
            if (res != null)
            {
                var j = SharedState.ParseFragment(res);
                object v = j != null ? DreamParkLuaNet.JsonToLua(_env, j) : null;
                if (v != null) box.Set("v", v);
            }
            return box;
        }

        object Table(string json)
        {
            var j = SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json);
            return j != null ? DreamParkLuaNet.JsonToLua(_env, j) : null;
        }

        /// Start a goal block for a stop. ctxJson = {id, attraction, kind, options, key}; stateJson = the synced stop.state
        /// (a handover / restart resumes it). Returns null + the state JSON on success, else the error.
        public string StartBlock(IPlaylistGoalHost host, string runId, string occ, string source, string ctxJson, string stateJson, out string stateOut)
        {
            stateOut = null;
            string sid = Sid(runId, occ);
            _blocks[sid] = (host, occ);
            try
            {
                var envT = _prepare.Call(sid, Table(ctxJson), string.IsNullOrEmpty(stateJson) ? null : Table(stateJson));
                var env = envT != null && envT.Length > 0 ? envT[0] as LuaTable : null;
                if (env == null) { _blocks.Remove(sid); return "goal block: no sandbox"; }
                LuaFunction chunk;
                try { chunk = Compile(source, "goal:" + (SharedState.ParseFragment(ctxJson)?.GetField("key")?.stringValue ?? occ), env); }
                catch (Exception ce) { _blocks.Remove(sid); try { _end.Call(sid); } catch (Exception) { } return "compile: " + ce.Message; }
                var r = _start.Call(sid, chunk);
                bool ok = r != null && r.Length > 0 && r[0] is bool b && b;
                if (!ok) { _blocks.Remove(sid); return r != null && r.Length > 1 ? Convert.ToString(r[1]) : "goal block failed"; }
                stateOut = r.Length > 1 ? r[1] as string : null;
                return null;
            }
            catch (Exception e) { _blocks.Remove(sid); return e.Message; }
        }

        public bool Running(string runId, string occ) => _blocks.ContainsKey(Sid(runId, occ));

        /// Deliver an event; returns the block's state JSON after it (null = the block is not running / threw).
        public string Dispatch(string runId, string occ, PlaylistEvent e)
        {
            string sid = Sid(runId, occ);
            if (!_blocks.ContainsKey(sid) || e == null) return null;
            try
            {
                var r = _event.Call(sid, Table(e.ToLuaJson()));
                return r != null && r.Length > 0 ? r[0] as string : null;
            }
            catch (Exception ex) { Log?.Invoke($"[pl-runtime] {occ}: event {e} threw: {ex.Message}"); return null; }
        }

        public string FireTimer(string runId, string occ, int id)
        {
            string sid = Sid(runId, occ);
            if (!_blocks.ContainsKey(sid)) return null;
            try
            {
                var r = _timer.Call(sid, id);
                return r != null && r.Length > 0 ? r[0] as string : null;
            }
            catch (Exception ex) { Log?.Invoke($"[pl-runtime] {occ}: timer {id} threw: {ex.Message}"); return null; }
        }

        public void EndBlock(string runId, string occ)
        {
            string sid = Sid(runId, occ);
            if (!_blocks.Remove(sid)) return;
            try { _end.Call(sid); } catch (Exception) { }
        }

        public void EndAll(string runId)
        {
            var dead = new List<string>();
            foreach (var kv in _blocks) if (kv.Key.StartsWith((runId ?? "") + "|", StringComparison.Ordinal)) dead.Add(kv.Key);
            foreach (var sid in dead)
            {
                _blocks.Remove(sid);
                try { _end.Call(sid); } catch (Exception) { }
            }
        }

        // ── Playlist Lua ──────────────────────────────────────────────

        static string Rid(string runId) => "run:" + (runId ?? "");

        /// Load the playlist's Lua (return {on_start, on_stop_result, choose_next, on_finale}). Null = ok, else the error.
        public string StartRun(IPlaylistGoalHost host, string runId, string lua, string stateJson, string ctxJson, out string stateOut)
        {
            stateOut = null;
            string rid = Rid(runId);
            _blocks[rid] = (host, null);
            try
            {
                var envT = _runPrepare.Call(rid, Table(ctxJson), string.IsNullOrEmpty(stateJson) ? null : Table(stateJson));
                var env = envT != null && envT.Length > 0 ? envT[0] as LuaTable : null;
                if (env == null) { _blocks.Remove(rid); return "playlist lua: no sandbox"; }
                LuaFunction chunk;
                try { chunk = Compile(lua, "playlist:" + runId, env); }
                catch (Exception ce) { _blocks.Remove(rid); try { _runEnd.Call(rid); } catch (Exception) { } return "compile: " + ce.Message; }
                var r = _runStart.Call(rid, chunk);
                bool ok = r != null && r.Length > 0 && r[0] is bool b && b;
                if (!ok) { _blocks.Remove(rid); return r != null && r.Length > 1 ? Convert.ToString(r[1]) : "playlist lua failed"; }
                stateOut = r.Length > 1 ? r[1] as string : null;
                return null;
            }
            catch (Exception e) { _blocks.Remove(rid); return e.Message; }
        }

        public bool RunLoaded(string runId) => _blocks.ContainsKey(Rid(runId));

        /// Call a hook (on_start / on_stop_result / choose_next / on_finale) with JSON args.
        /// Returns (state JSON, the hook's return value as a string or null).
        public (string state, string ret) CallRun(string runId, string hook, string arg1Json = null, string arg2Json = null)
        {
            string rid = Rid(runId);
            if (!_blocks.ContainsKey(rid)) return (null, null);
            try
            {
                var r = _runCall.Call(rid, hook, arg1Json == null ? null : Table(arg1Json), arg2Json == null ? null : Table(arg2Json));
                string st = r != null && r.Length > 0 ? r[0] as string : null;
                string ret = r != null && r.Length > 1 && r[1] != null ? Convert.ToString(r[1], System.Globalization.CultureInfo.InvariantCulture) : null;
                return (st, ret);
            }
            catch (Exception ex) { Log?.Invoke($"[pl-runtime] playlist lua {hook} threw: {ex.Message}"); return (null, null); }
        }

        /// The FIRST set a playlist's Lua shows (pl-sets): its on_start in a throwaway sandbox whose run records run.show and
        /// answers run.space() with `rooms`. Same inputs → the same set the leader's real on_start puts out. Null = none.
        /// Oct 23 (pl-loading, The Academy): the run.add calls on_start makes AFTER that show ride along as `"added": [request…]`
        /// — they join set 1 the moment the run starts, so the pack places them WITH the set (every room's load card at once)
        /// instead of streaming them in ~20 s later when the real on_start runs.
        public string PreviewFirstSet(string lua, string ctxJson, int rooms, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(lua)) { error = "no lua"; return null; }
            string rid = "preview:" + Guid.NewGuid().ToString("N");
            _blocks[rid] = (new PreviewHost(rooms), null);
            try
            {
                var envT = _runPrepare.Call(rid, Table(ctxJson), null);
                var env = envT != null && envT.Length > 0 ? envT[0] as LuaTable : null;
                if (env == null) { error = "no sandbox"; return null; }
                var chunk = Compile(lua, "playlist-preview", env);
                var r = _runStart.Call(rid, chunk);
                if (!(r != null && r.Length > 0 && r[0] is bool ok && ok)) { error = r != null && r.Length > 1 ? Convert.ToString(r[1]) : "load failed"; return null; }
                _runCall.Call(rid, "on_start", null, null);
                return ((PreviewHost)_blocks[rid].host).Request();
            }
            catch (Exception e) { error = e.Message; return null; }
            finally { _blocks.Remove(rid); try { _runEnd.Call(rid); } catch (Exception) { } }
        }

        /// The preview's run: records the first run.show, answers the space, ignores everything else.
        sealed class PreviewHost : IPlaylistGoalHost
        {
            readonly int _rooms;
            public string shown;
            /// run.add requests on_start made after its first run.show (in order).
            public readonly List<string> added = new List<string>();
            public PreviewHost(int rooms) { _rooms = Math.Max(1, rooms); }
            /// The first show, with the adds that followed it as `added` (null = nothing shown).
            public string Request()
            {
                if (shown == null || added.Count == 0) return shown;
                JSONObject req;
                try { req = new JSONObject(shown); } catch (Exception) { return shown; }
                if (req.type != JSONObject.Type.Object) return shown;
                var arr = new JSONObject(JSONObject.Type.Array);
                foreach (string a in added) { try { var j = new JSONObject(a); if (j.type == JSONObject.Type.Object) arr.Add(j); } catch (Exception) { } }
                if (arr.count > 0) req.SetField("added", arr);
                return req.Print();
            }
            public void GoalProgress(string occ, double n, double of, string unit) { }
            public void GoalOutcome(string occ, string kind, string fieldsJson) { }
            public int GoalTimer(string occ, double seconds, bool every) => 0;
            public double GoalCensus(string occ, string filterJson) => 0;
            public string GoalCount(string occ, string kind) => null;
            public void GoalMark(string occ, string specJson) { }
            public bool GoalEdge(string occ, string which, out Vector3 local) { local = default; return false; }
            public void GoalJump(string occ, string specJson) { }
            public int GoalJumpAuthored(string occ, string chain) => 0;
            public void GoalJumpReset(string occ, string player, string why, string extraJson) { }
            public void GoalRequest(string occ, string table, string key, string valueJson) { }
            public void GoalHint(string occ, string text) { }
            public void GoalSubscribe(string occ, string source, string name) { }
            public void GoalEmit(string occ, string name, string dataJson) { }
            public void GoalLog(string occ, string line) { }
            public string RunOp(string op, string a, string json)
            {
                switch (op)
                {
                    case "show": if (shown == null) shown = json; return "true";
                    case "add": if (shown != null && !string.IsNullOrEmpty(json)) added.Add(json); return "true";
                    case "space": return "{\"rooms\":" + _rooms + "}";
                    case "players": case "pool": return "[]";
                    default: return null;
                }
            }
        }

        public void EndRun(string runId)
        {
            string rid = Rid(runId);
            if (!_blocks.Remove(rid)) return;
            try { _runEnd.Call(rid); } catch (Exception) { }
        }

        // ── The Lua side ──────────────────────────────────────────────
        // No double quotes inside this verbatim block (C#). Lua 5.3, no CS.* (runs in the offline sim's VM too).
        internal const string Bootstrap = @"
__plr = __plr or { blocks = {}, runs = {} }
local R = __plr

local Q = string.char(34)
local function esc(s)
    s = string.gsub(s, '[%c\\' .. Q .. ']', function(c)
        if c == '\\' then return '\\\\' end
        if c == Q then return '\\' .. Q end
        if c == '\n' then return '\\n' end
        if c == '\r' then return '\\r' end
        if c == '\t' then return '\\t' end
        return string.format('\\u%04x', string.byte(c))
    end)
    return Q .. s .. Q
end
local function json(v, depth)
    depth = depth or 0
    local tv = type(v)
    if depth > 8 or tv == 'nil' then return 'null' end
    if tv == 'boolean' then return v and 'true' or 'false' end
    if tv == 'number' then
        if v ~= v or v == math.huge or v == -math.huge then return '0' end
        if math.type and math.type(v) == 'integer' then return tostring(v) end
        if v == math.floor(v) and math.abs(v) < 1e15 then return string.format('%d', v) end
        return string.format('%.6g', v)
    end
    if tv == 'string' then return esc(v) end
    if tv == 'table' then
        local n = #v
        if n > 0 then
            local parts = {}
            for i = 1, n do parts[i] = json(v[i], depth + 1) end
            return '[' .. table.concat(parts, ',') .. ']'
        end
        local parts = {}
        for k, val in pairs(v) do
            local tk, tval = type(k), type(val)
            if (tk == 'string' or tk == 'number') and tval ~= 'function' and tval ~= 'userdata' and tval ~= 'thread' then
                parts[#parts + 1] = esc(tostring(k)) .. ':' .. json(val, depth + 1)
            end
        end
        return '{' .. table.concat(parts, ',') .. '}'
    end
    if tv == 'userdata' then
        local ok, x, y, z = pcall(function() return v.x, v.y, v.z end)
        if ok and type(x) == 'number' then return '{' .. esc('x') .. ':' .. json(x) .. ',' .. esc('y') .. ':' .. json(y) .. ',' .. esc('z') .. ':' .. json(z) .. '}' end
    end
    return 'null'
end
R.json = json

local SAFE = { 'math', 'string', 'table', 'pairs', 'ipairs', 'next', 'select', 'type', 'tostring', 'tonumber',
               'pcall', 'error', 'assert', 'utf8' }
local function sandbox(sid, extra)
    local e = {}
    for _, k in ipairs(SAFE) do e[k] = _G[k] end
    e.unpack = table.unpack
    e.print = function(...)
        local t = {}
        for i = 1, select('#', ...) do t[i] = tostring((select(i, ...))) end
        dp_plr_log(sid, table.concat(t, ' '))
    end
    for k, v in pairs(extra) do e[k] = v end
    return e
end

local function normscript(s)
    if type(s) ~= 'string' then return s end
    s = string.lower(s)
    s = string.gsub(s, '^.*[/\\]', '')
    s = string.gsub(s, '%.txt$', '')
    s = string.gsub(s, '%.lua$', '')
    return s
end

local function eq(want, have, key)
    if key == 'script' then return normscript(want) == normscript(have) end
    if type(want) == 'number' and type(have) == 'number' then return math.abs(want - have) < 1e-9 end
    if type(want) == 'number' and type(have) == 'string' then return tostring(want) == have end
    return want == have
end

local function field(e, k)
    if k == 'name' then return e.name end
    if k == 'source' then return e.source end
    if k == 'stop' then return e.stop end
    if k == 'player' then return e.player end
    if k == 'from' then return e.from end
    local d = e.data
    if type(d) == 'table' then return d[k] end
    return nil
end

local function match(filter, e)
    if type(filter) ~= 'table' then return true end
    for k, want in pairs(filter) do
        local have = field(e, k)
        if type(have) == 'table' and k == 'script' then
            local ok = false
            for _, h in ipairs(have) do if eq(want, h, k) then ok = true break end end
            if not ok then return false end
        elseif type(want) == 'table' then
            local ok = false
            for _, w in ipairs(want) do if eq(w, have, k) then ok = true break end end
            if not ok then return false end
        elseif not eq(want, have, k) then return false end
    end
    return true
end
R.match = match

-- An event table passed straight through as a result (stop.on(..., stop.done)) is not a result.
local function fields(t)
    if type(t) ~= 'table' then return '{}' end
    if t.source ~= nil and t.name ~= nil and t.data ~= nil then return '{}' end
    return json(t)
end

local function filter_name(filter)
    if type(filter) ~= 'table' then return '' end
    local n = filter.name
    if type(n) == 'string' then return n end
    return ''
end

local function make_stop(b, ctx)
    local sid = b.sid
    local S = {}
    S.id = ctx.id
    S.occurrence = ctx.occurrence
    S.attraction = ctx.attraction
    S.kind = ctx.kind
    S.role = ctx.role
    S.options = ctx.options or {}
    S.state = b.state
    S.on = function(source, filter, fn)
        if type(filter) == 'function' and fn == nil then fn, filter = filter, nil end
        if type(source) ~= 'string' or type(fn) ~= 'function' then return nil end
        local h = { source = source, filter = filter, fn = fn }
        b.handlers[#b.handlers + 1] = h
        dp_plr_sub(sid, source, filter_name(filter))
        return h
    end
    S.off = function(h)
        for i = #b.handlers, 1, -1 do if b.handlers[i] == h or b.handlers[i].fn == h then table.remove(b.handlers, i) end end
    end
    S.census = function(f) return dp_plr_census(sid, json(type(f) == 'table' and f or {})) end
    -- stop.count{kind='collect'} → {total, remaining, done, by = {[player] = n}} (announcements, PLAYLIST-PLAN § 4.2)
    S.count = function(t)
        local k = type(t) == 'table' and t.kind or t
        return dp_plr_count(sid, k ~= nil and tostring(k) or '') or { total = 0, remaining = 0, done = 0, by = {} }
    end
    S.progress = function(n, of, unit)
        dp_plr_progress(sid, tonumber(n) or 0, tonumber(of) or 0, unit ~= nil and tostring(unit) or '')
    end
    -- § 3.2 challenge results: the runtime applies the multiplayer attempt rules (alone a hit loses; in a group each
    -- failed player is out and the attempt is lost when everyone failed or time is up; one winner does not end it for
    -- the others; the skip after 10+ lost attempts, only as the set's last open challenge). The block keeps running
    -- across attempts (stop.on('attempt', {name='start'}, fn) hears each new one); it ends when the challenge is won
    -- and everyone inside is done.
    local function result(t)
        if type(t) == 'table' and t.source ~= nil and t.name ~= nil and t.data ~= nil then
            return { by = t.player }   -- an event passed straight through: who did it
        end
        return type(t) == 'table' and t or {}
    end
    S.won = function(t) if b.over then return end dp_plr_outcome(sid, 'won', json(result(t))) end
    S.lost = function(t)
        if b.over then return end
        t = result(t)
        if t.player == nil and t.by ~= nil then t.player = t.by end
        dp_plr_outcome(sid, 'lost', json(t))
    end
    S.done = S.won
    S.timed_out = function() S.lost({ why = 'time' }) end
    S.failed = function(t) t = result(t); t.why = t.why or 'failed'; S.lost(t) end
    S.skip = function(t) if b.over then return end dp_plr_outcome(sid, 'skip', json(result(t))) end
    S.over = function() return b.over end
    S.attempt = function() local box = dp_plr_run(sid, 'attempt', S.occurrence, '') return box and box.v end
    -- stop.send(name, data): this stop's own event to its attraction's objects (onevent / dp.on) on EVERY headset (the
    -- challenge-event channel, like challenge_won). Not after the stop is over.
    S.send = function(name, data)
        if b.over then return false end
        local box = dp_plr_run(sid, 'send', S.occurrence, json({ name = tostring(name or ''), data = type(data) == 'table' and data or {} }))
        return (box and box.v) == true
    end
    -- stop.can_play(playlistId) → {ok, need}: run.can_play from a goal block (decide before stop.won).
    S.can_play = function(id) local box = dp_plr_run(sid, 'can_play', id ~= nil and tostring(id) or '', '') return (box and box.v) or { ok = false, need = {}, unknown = true } end
    S.skip_available = function() local a = S.attempt() return type(a) == 'table' and a.so == true end
    S.time = function(sec) dp_plr_run(sid, 'attempt_time', S.occurrence, json({ sec = tonumber(sec) or 0 })) end
    local function timer(sec, fn, every)
        if type(fn) ~= 'function' then return 0 end
        local id = dp_plr_timer(sid, tonumber(sec) or 0, every == true)
        if id > 0 then b.timers[id] = fn end
        return id
    end
    S.timer = function(sec, fn, opts)
        return timer(sec, fn, type(opts) == 'table' and opts.every == true)
    end
    S.every = function(sec, fn) return timer(sec, fn, true) end
    S.cancel = function(id) b.timers[id] = nil end
    S.mark = function(t) dp_plr_mark(sid, json(type(t) == 'table' and t or {})) end
    S.unmark = function() dp_plr_mark(sid, '') end
    S.edge = function(which) return dp_plr_edge(sid, tostring(which or 'start')) end
    -- CROSSING (start line → finish line; CONTRACT Changes Oct 20): each wearer's device runs its attempt and emits
    -- crossing_start / crossing_fail{why, cause, fails} / crossing_done{fails}; the done here (the block's own handlers
    -- still see every event). Its counter is private (`_crossing_fails`): a block may keep its own.
    S.crossing = function(t)
        t = type(t) == 'table' and t or {}
        t.kind = 'crossing'
        table.insert(b.handlers, 1, { source = 'event', filter = { name = 'crossing_fail' }, fn = function(e)
            S.state._crossing_fails = (S.state._crossing_fails or 0) + 1
        end })
        table.insert(b.handlers, 1, { source = 'event', filter = { name = 'crossing_done' }, fn = function(e)
            local d = e.data or {}
            -- win = false (ww-playlists): the crossing is one part of a longer challenge — the block decides when to win
            if t.win ~= false then S.done({ player = e.player, fails = d.fails or S.state._crossing_fails or 0 }) end
        end })
        dp_plr_sub(sid, 'event', 'crossing_start')
        dp_plr_sub(sid, 'event', 'crossing_fail')
        dp_plr_sub(sid, 'event', 'crossing_done')
        dp_plr_jump(sid, json(t))
    end
    -- JUMP PADS: stop.jump{pads = {posA, posB, ...}, window = 2.5, fail_on = {...}, win = true}: jump_hop{from, to} /
    -- jump_fail{why, cause, fails} / jump_done{fails} → done{player, fails} (win = false: no done — the block wins it itself).
    S.jump = function(t)
        t = type(t) == 'table' and t or {}
        -- the LIVE chain (sal-jumppads): the given pads, else the attraction's authored chain (`chain` / `id`, else its default);
        -- returned as {pads = n, fell_back = bool, why}. Fewer than 2 pads → it runs as a crossing ({pads = 0, fell_back = true}).
        local cbox = dp_plr_run(sid, 'jump_chain', S.occurrence, json(t))
        local live = (cbox and type(cbox.v) == 'table') and cbox.v or { pads = 0, fell_back = true, why = 'no jump chain here' }
        if live.fell_back or (tonumber(live.pads) or 0) < 2 then
            dp_plr_log(sid, 'stop.jump: ' .. tostring(live.why or 'no pads') .. ': running it as a crossing')
            S.crossing(t)
            return { pads = 0, fell_back = true, why = live.why }
        end
        t.kind = 'jump'
        table.insert(b.handlers, 1, { source = 'event', filter = { name = 'jump_fail' }, fn = function(e)
            S.state._jump_fails = (S.state._jump_fails or 0) + 1
        end })
        table.insert(b.handlers, 1, { source = 'event', filter = { name = 'jump_done' }, fn = function(e)
            local d = e.data or {}
            -- win = false (ww-playlists): the jump is one part of a longer challenge — jump_done still reaches the block's own
            -- handlers (and the game), the block decides when to win
            if t.win ~= false then S.done({ player = e.player, fails = d.fails or S.state._jump_fails or 0 }) end
        end })
        dp_plr_sub(sid, 'event', 'jump_hop')
        dp_plr_sub(sid, 'event', 'jump_fail')
        dp_plr_sub(sid, 'event', 'jump_done')
        dp_plr_jump(sid, json(t))
        return live
    end
    -- REACH: stop.reach{at = pos | stop.edge('far'), radius = 0.5}: the TARGET CIRCLE at the destination (no crossing
    -- lines); a wearer stepping into it → reach_done{player} → done{player}.
    S.reach = function(t)
        t = type(t) == 'table' and t or {}
        t.at = t.at or S.edge('far')
        t.radius = tonumber(t.radius) or 0.5
        t.kind = 'reach'
        if t.at ~= nil then S.mark({ at = t.at, icon = 'target', radius = t.radius }) end
        table.insert(b.handlers, 1, { source = 'event', filter = { name = 'reach_done' }, fn = function(e)
            S.done({ player = e.player })
        end })
        dp_plr_sub(sid, 'event', 'reach_done')
        dp_plr_jump(sid, json(t))
    end
    S.crossing_reset = function(player, why) dp_plr_jump_reset(sid, player ~= nil and tostring(player) or '', why ~= nil and tostring(why) or 'forced', '{}') end
    S.jump_reset = S.crossing_reset
    S.request = function(tbl, key, value) dp_plr_request(sid, tostring(tbl or ''), tostring(key or ''), json(value)) end
    S.hint = function(text) dp_plr_hint(sid, text ~= nil and tostring(text) or '') end
    S.log = function(...)
        local t = {}
        for i = 1, select('#', ...) do t[i] = tostring((select(i, ...))) end
        dp_plr_log(sid, table.concat(t, ' '))
    end
    return S
end

local function make_dp(sid, occ)
    local base = dp and dp.dreamie
    local DD = {}
    if base ~= nil then
        DD.moment = function(name, data, opts)
            opts = type(opts) == 'table' and opts or {}
            if opts.stop == nil then opts.stop = occ end
            return base.moment(name, data, opts)
        end
        DD.say = function(text, opts) return base.say(text, opts) end
        DD.busy = function() return base.busy() end
        DD.title_speaking = function(on) return base.title_speaking(on) end
    else
        DD.moment = function() return false end
        DD.say = function() return false end
        DD.busy = function() return false end
        DD.title_speaking = function() end
    end
    return { dreamie = DD, event = function(name, data) dp_plr_emit(sid, tostring(name or ''), json(type(data) == 'table' and data or {})) end }
end

-- 1) the block's sandbox (C# compiles the source into it: the app's sandbox has no `load`), 2) run it.
function __plr_prepare(sid, ctx, state)
    ctx = ctx or {}
    local b = { sid = sid, handlers = {}, timers = {}, state = state or {}, over = false }
    R.blocks[sid] = b
    local stop = make_stop(b, ctx)
    b.stop = stop
    return sandbox(sid, { stop = stop, dp = make_dp(sid, ctx.occurrence) })
end

function __plr_start(sid, chunk)
    local b = R.blocks[sid]
    if b == nil then return false, 'no block' end
    local stop = b.stop
    local ok, fn = pcall(chunk)
    if not ok then R.blocks[sid] = nil return false, 'load: ' .. tostring(fn) end
    if type(fn) ~= 'function' then R.blocks[sid] = nil return false, 'a goal block must return function(stop)' end
    local ok2, err2 = pcall(fn, stop)
    if not ok2 then R.blocks[sid] = nil return false, 'run: ' .. tostring(err2) end
    return true, json(stop.state)
end

function __plr_event(sid, e)
    local b = R.blocks[sid]
    if b == nil or b.over then return b and json(b.stop.state) or nil end
    if type(e.data) ~= 'table' then e.data = {} end
    -- Convenience: data fields also ride at the top level (e.n, e.of, e.script) unless they collide.
    for k, v in pairs(e.data) do if e[k] == nil then e[k] = v end end
    local snap = {}
    for i = 1, #b.handlers do snap[i] = b.handlers[i] end
    for i = 1, #snap do
        local h = snap[i]
        if b.over then break end
        if (h.source == e.source or h.source == '*') and match(h.filter, e) then
            local ok, err = pcall(h.fn, e)
            if not ok then dp_plr_log(sid, 'handler for ' .. tostring(e.source) .. ':' .. tostring(e.name) .. ' threw: ' .. tostring(err)) end
        end
    end
    return json(b.stop.state)
end

function __plr_timer(sid, id)
    local b = R.blocks[sid]
    if b == nil then return nil end
    local fn = b.timers[id]
    if fn ~= nil and not b.over then
        local ok, err = pcall(fn)
        if not ok then dp_plr_log(sid, 'timer ' .. tostring(id) .. ' threw: ' .. tostring(err)) end
    end
    return json(b.stop.state)
end

function __plr_end(sid) R.blocks[sid] = nil end

-- ── Playlist Lua ─────────────────────────────────────────────
local function make_run(r, ctx)
    local rid = r.rid
    local function op(name, a, t)
        local box = dp_plr_run(rid, name, a ~= nil and tostring(a) or '', t ~= nil and json(t) or '')
        return box and box.v
    end
    local run = {}
    run.id = ctx.runId
    run.playlist = ctx.playlistId
    -- run.started_from: the playlist whose run.finish{next} started this run (nil when started any other way).
    run.started_from = ctx.startedFrom
    run.state = r.state
    run.lock = function(stopId, opts) return op('lock', stopId, type(opts) == 'table' and opts or {}) end
    run.unlock = function(stopId) return op('unlock', stopId) end
    run.locked = function(stopId) return op('locked', stopId) == true end
    run.flag = function(name, v) return op('flag', name, { v = v == nil and true or v }) end
    -- run.badge(id): every player present now (they earned it); run.badge(player, id): that player only.
    run.badge = function(who, id)
        if id == nil then id, who = who, 'all' end
        return op('badge', who or 'all', { id = id })
    end
    run.show = function(t) return op('show', '', type(t) == 'table' and t or { attractions = { t } }) == true end
    run.add = function(t) return op('add', '', type(t) == 'table' and t or { attractions = { t } }) == true end
    run.remove = function(t) return op('remove', '', type(t) == 'table' and t or { attractions = { t } }) == true end
    run.finish = function(t)
        if type(t) ~= 'table' then t = { result = t ~= nil and tostring(t) or 'won' } end
        return op('finish', '', t) == true
    end
    -- run.waypoint(ref, true|false): does this pool entry / attraction COUNT as a waypoint (the map's done / not-done marks,
    -- Dreamie's guidance)? Nothing counts by default. Done when its challenge is won / skipped or the game announced it done.
    run.waypoint = function(ref, on) return op('waypoint', ref, { on = on ~= false }) == true end
    -- run.map (PlaylistMap.cs, pl-announce): the map is the playlist's and the game's to show — the platform never pops it.
    -- run.map.mark(ref, {done}) / unmark(ref) / clear() / show{seconds, mark = {ref = …, done} | {…}} / hide().
    -- The usual one-liner: run.show{…, map = 'multi_room'} pops the map once that field is out, only when it spans more than
    -- one room (a 1-room apartment: no map); map = true | seconds | {seconds, mark, when} for the other cases.
    local function mtarget(ref) return { ref = ref ~= nil and tostring(ref) or nil } end
    -- run.placed() → {rooms, attractions}: what the field that is out now spans (its own rooms, not the space's). The set-1
    -- preview has no field ({rooms = 1, attractions = 0}): to CHOOSE a set in on_start use run.space(), which both agree on.
    run.placed = function() return op('placed') or { rooms = 1, attractions = 0 } end
    run.map = {
        mark = function(ref, o) return op('map', '', { op = 'mark', target = mtarget(ref), done = type(o) == 'table' and o.done == true }) == true end,
        unmark = function(ref) return op('map', '', { op = 'unmark', target = mtarget(ref) }) == true end,
        clear = function() return op('map', '', { op = 'clear' }) == true end,
        show = function(o)
            o = type(o) == 'table' and o or { seconds = tonumber(o) }
            local t = { op = 'show', seconds = o.seconds }
            if type(o.mark) == 'table' then
                local m = o.mark
                if m.ref ~= nil or m.done ~= nil then m = { m } end
                local l = {}
                for _, x in ipairs(m) do l[#l + 1] = { target = mtarget(x.ref), done = x.done == true } end
                t.mark = l
            end
            return op('map', '', t) == true
        end,
        hide = function() return op('map', '', { op = 'hide' }) == true end,
    }
    -- run.can_play(playlistId) → {ok, need = {badges…}}: may this group play it now (its requires.badges, the start gate's rule)?
    run.can_play = function(id) return op('can_play', id) or { ok = false, need = {}, unknown = true } end
    run.space = function() return op('space') or { rooms = 1 } end
    run.pool = function() return op('pool') or {} end
    run.set = function() return op('set') or {} end
    run.skip = function(ref) return op('skip', ref) == true end
    run.attempt = function(ref) return op('attempt', ref) end
    run.result = function(stopId) return op('result', stopId) end
    run.players = function() return op('players') or {} end
    run.leader = function() return op('leader') end
    run.skip_round = function() return op('skip_round') end
    run.next_round = function(legId) return op('next_round', legId) end
    run.log = function(...)
        local t = {}
        for i = 1, select('#', ...) do t[i] = tostring((select(i, ...))) end
        dp_plr_log(rid, table.concat(t, ' '))
    end
    run.rounds = function(ids, own) return R.rounds(run, ids, own) end
    run.batches = function(o) return R.batches(run, o) end
    return run
end

-- ── The library (pl-sets) ─────────────────────────────────────
local function merged(own, H)
    own = type(own) == 'table' and own or {}
    for k, v in pairs(own) do if H[k] == nil then H[k] = v end end
    return H, own
end

-- arc[] records, converted on load: one run.show per old round, in order (the record's own choose_next still picks).
R.rounds = function(run, ids, own)
    local H = {}
    H, own = merged(own, H)
    local function idx(id) for i, x in ipairs(ids) do if x == id then return i end end return nil end
    H.on_start = function(r)
        if type(own.on_start) == 'function' then own.on_start(r) end
        if ids[1] ~= nil then r.show({ round = ids[1] }) end
    end
    H.on_set_done = function(r, set)
        local nx = nil
        if type(own.choose_next) == 'function' then nx = own.choose_next(r, set) end
        if nx == nil then local i = idx(set and set.id) if i ~= nil then nx = ids[i + 1] end end
        if nx ~= nil then r.show({ round = nx }) end
        return nx
    end
    H.choose_next = nil
    return H
end

-- THE WORLD PATTERN (§ 3.1 batches): with N rooms put out N challenges; when all are won (or skipped) the next N; the
-- last set is the final challenges plus the boss, locked until they are won. Beating the boss awards `badge` (if any) and
-- finishes the run. o = { challenges = {refs…}, boss = ref, rooms = N (default run.space().rooms), place = {ref = wish},
-- boss_place = 'biggest', sign = 'Win … first!', badge = id, result = 'won', waypoints = true (the challenges + boss count as
-- waypoints), map = true | seconds (pop the map after each set comes out — run.show{map}) }. Own hooks in o.hooks still run.
R.batches = function(run, o)
    o = type(o) == 'table' and o or {}
    local list = o.challenges or {}
    local H = {}
    local own
    H, own = merged(o.hooks, H)
    local function N(r)
        local n = tonumber(o.rooms)
        if n == nil then local sp = r.space() n = sp and tonumber(sp.rooms) or 1 end
        if n < 1 then n = 1 end
        return math.floor(n)
    end
    local function show_next(r)
        local st = r.state
        st._b = st._b or { i = 0, last = false }
        if st._b.last then return false end
        local n = N(r)
        local from = st._b.i + 1
        local set, place = {}, {}
        local to = math.min(#list, from + n - 1)
        for k = from, to do
            set[#set + 1] = list[k]
            if type(o.place) == 'table' and o.place[list[k]] ~= nil then place[list[k]] = o.place[list[k]] end
        end
        st._b.i = to
        local lock = nil
        if to >= #list then
            st._b.last = true
            if o.boss ~= nil then
                set[#set + 1] = o.boss
                place[o.boss] = o.boss_place or (type(o.place) == 'table' and o.place[o.boss]) or 'biggest'
                local needs = {}
                for k = 1, #set - 1 do needs[#needs + 1] = set[k] end
                if #needs > 0 then
                    lock = {}
                    lock[o.boss] = { needs = needs, sign = o.sign }
                end
            end
        end
        st._b.out = set
        return r.show({ attractions = set, rooms = n, place = place, lock = lock, map = o.map })
    end
    H.on_start = function(r)
        if o.waypoints == true then
            for k = 1, #list do r.waypoint(list[k], true) end
            if o.boss ~= nil then r.waypoint(o.boss, true) end
        end
        if type(own.on_start) == 'function' then own.on_start(r) end
        show_next(r)
    end
    H.on_stop_result = function(r, stop, res)
        if type(own.on_stop_result) == 'function' then own.on_stop_result(r, stop, res) end
        local st = r.state
        if not (res and res.done) then return end
        local id = stop and (stop.ref or stop.id)
        if o.boss ~= nil and (id == o.boss or (stop and (stop.id == o.boss or stop.attraction == o.boss))) then
            st._b.boss = true
            if o.badge ~= nil then r.badge(o.badge) end
            r.finish({ result = o.result or 'won' })
            return
        end
        -- the boss opens when everything else in the last set is won
        if st._b and st._b.last and o.boss ~= nil then
            local set = r.set()
            local open = 0
            for _, ref in ipairs(set.open or {}) do if ref ~= o.boss and ref ~= id then open = open + 1 end end
            if open == 0 and r.locked(o.boss) then r.unlock(o.boss) end
        end
    end
    H.on_set_done = function(r, set)
        if type(own.on_set_done) == 'function' then own.on_set_done(r, set) end
        local st = r.state
        if st._b and st._b.last then
            if o.boss == nil and not st._b.done then st._b.done = true r.finish({ result = o.result or 'won' }) end
            return
        end
        show_next(r)
    end
    return H
end

function __plr_run_prepare(rid, ctx, state)
    ctx = ctx or {}
    local r = { rid = rid, state = state or {}, hooks = {} }
    R.runs[rid] = r
    r.run = make_run(r, ctx)
    return sandbox(rid, { run = r.run, dp = make_dp(rid, nil) })
end

function __plr_run_start(rid, chunk)
    local r = R.runs[rid]
    if r == nil then return false, 'no run' end
    local ok, hooks = pcall(chunk)
    if not ok then R.runs[rid] = nil return false, 'load: ' .. tostring(hooks) end
    if type(hooks) ~= 'table' then R.runs[rid] = nil return false, 'playlist lua must return { on_start, on_stop_result, on_set_done, on_finale }' end
    r.hooks = hooks
    return true, json(r.run.state)
end

function __plr_run_call(rid, hook, a1, a2)
    local r = R.runs[rid]
    if r == nil then return nil, nil end
    local fn = r.hooks[hook]
    local ret = nil
    if type(fn) == 'function' then
        local ok, v = pcall(fn, r.run, a1, a2)
        if not ok then dp_plr_log(rid, hook .. ' threw: ' .. tostring(v)) else ret = v end
    end
    if type(ret) == 'table' then ret = json(ret) elseif ret ~= nil then ret = tostring(ret) end
    return json(r.run.state), ret
end

function __plr_run_end(rid) R.runs[rid] = nil end
";
    }
}
