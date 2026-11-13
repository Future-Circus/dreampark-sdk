// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaPlaylist.cs — dp.playlist, the standard objective hook
//  (Docs/PLAYLISTS.md § 7.2). Titles say "this happened" at a stop; the
//  platform's PlaylistRunner turns it into legs, swaps and the ending.
//
//  Same bridge shape as DreamParkLuaNet / DreamParkLuaRoutes: flat internal
//  dp_pl_* delegates of primitives, GameObject and LuaTable, so nothing
//  enters the generated XLua wrappers (no Gen/ drift, no codegen). Older
//  apps have no dp.playlist: content guards with `if dp.playlist then`.
//
//  Registered from DreamParkLuaAPI.RegisterLua after DreamParkLuaNet (it
//  reuses its JSON → Lua conversion). Pumped from __dp_pump_globals.
// ─────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;
using XLua;

namespace DreamPark
{
    internal static class DreamParkLuaPlaylist
    {
        static PlaylistRunner R(GameObject caller, GameObject target) =>
            PlaylistRunner.For(caller != null ? caller : target) ?? (target != null ? PlaylistRunner.For(target) : null);

        static bool Active(GameObject go) => PlaylistRunner.For(go) != null;

        static int Version(GameObject go)
        {
            var r = PlaylistRunner.For(go);
            return r != null ? r.Version : -1;
        }

        static LuaTable State(GameObject go)
        {
            var r = PlaylistRunner.For(go);
            if (r == null) return null;
            var env = LuaBehaviour.GetLuaEnv();
            var t = DreamParkLuaNet.JsonToLua(env, SharedState.ParseFragment(r.StateJson())) as LuaTable;
            if (t == null) return null;
            t.Set("id", r.Plan.playlistId ?? "");
            t.Set("kind", r.Plan.kindWord ?? "Adventure");
            t.Set("leg_name", r.Plan.Current?.name ?? "");
            t.Set("leader", r.IsLeader);
            t.Set("standing", r.LegStanding);
            // Stops as Lua rows built here (with their live roots), replacing the JSON copy.
            var stops = env.NewTable();
            var leg = r.Plan.Current;
            if (leg != null)
                for (int i = 0; i < leg.stops.Count; i++)
                    if (leg.stops[i] != null) stops.Set(i + 1, StopRow(r, leg.stops[i]));
            t.Set("stops", stops);
            return t;
        }

        static LuaTable StopRow(PlaylistRunner r, PlaylistRunStop s)
        {
            var env = LuaBehaviour.GetLuaEnv();
            var t = env.NewTable();
            bool current = r.Plan.Current?.stops != null && r.Plan.Current.stops.Contains(s);
            t.Set("occurrence", s.occurrenceId ?? "");
            t.Set("occurrenceId", s.occurrenceId ?? "");
            t.Set("attraction", s.attraction ?? "");
            t.Set("role", s.role ?? "");
            t.Set("room", s.roomLabel ?? "");
            t.Set("rect", s.rectId ?? "");
            t.Set("current", current);
            // The stop being played now (the round's target, or one whose intro ran / that is under way). false = a later
            // stop the wearer is only passing: don't start its clock. nil on older cores (Oct 20, meadow-host).
            t.Set("target", r.IsTarget(s));
            t.Set("seconds", (double)PlaylistRunRules.StopTimer(r.Plan, s));
            var p = r.Progress.TryGetValue(s.occurrenceId ?? "", out var pr) ? pr : null;
            t.Set("done", p != null && (p.done || p.timedOut));
            t.Set("n", p?.n ?? 0.0);
            t.Set("result", p?.Result ?? "");
            t.Set("name", PlaylistObjectiveText.Name(s));
            t.Set("goal_text", PlaylistObjectiveText.StopGoal(r.Plan.Current, s, p));
            t.Set("of", p?.goal ?? 0.0);
            t.Set("unit", p?.unit ?? "");
            t.Set("streamed", !s.standing && !string.IsNullOrEmpty(s.streamAfter));
            if (!string.IsNullOrEmpty(s.options))
            {
                object o = DreamParkLuaNet.JsonToLua(env, SharedState.ParseFragment(s.options));
                if (o != null) t.Set("options", o);
            }
            var root = PlaylistRunner.Stage?.StopRoot(r, s);
            if (root != null) t.Set("root", root);
            return t;
        }

        static PlaylistRunStop Which(PlaylistRunner r, GameObject caller, GameObject target, string occ)
        {
            if (!string.IsNullOrEmpty(occ)) return r.Plan.FindStop(occ, out _);
            var s = r.StopOf(target != null ? target : caller);
            if (s != null) return s;
            return target == null || target == caller ? r.ResolveStop(null) : null;
        }

        static LuaTable Stop(GameObject caller, GameObject target, string occ)
        {
            var r = R(caller, target);
            if (r == null) return null;
            PlaylistRunStop s = !string.IsNullOrEmpty(occ) ? r.Plan.FindStop(occ, out _)
                              : r.StopOf(target != null ? target : caller);
            return s != null ? StopRow(r, s) : null;
        }

        // Titles emit events; playlists decide goals (repair-oct20 CONTRACT § 1–§ 2). Every title call is an EVENT
        // (source signal / progress) on the bus; it is applied to the runner only when no goal block owns the stop
        // (precedence per stop: inline block → title default block → these calls → PlaylistAutoRules.Generic).
        static string J(string k, double v) => "{\"" + k + "\":" + PlaylistRunRules.Num(v) + "}";

        static bool Owned(PlaylistRunner r, PlaylistRunStop s)
        {
            if (r == null || s == null) return false;
            if (r.GoalDriven(s.occurrenceId)) return true;
            r.NoteTitleDriven(s.occurrenceId);
            return false;
        }

        static bool Signal(GameObject caller, GameObject target, string occ, string name, double n, bool hasN)
        {
            var r = R(caller, target);
            var s = r != null ? Which(r, caller, target, occ) : null;
            PlaylistEvents.Emit(PlaylistEvent.Signal, name ?? "", hasN ? J("n", n) : "{}", (s ?? r?.ResolveStop(null))?.occurrenceId);
            if (r == null) return false;
            if (s == null && PlaylistRunRules.Normalize(name, null) != null)
                Debug.Log($"[Playlist] signal '{name}' from {(caller != null ? caller.name : "?")}: not a stop of the running playlist (a declared title word still resolves)");
            var at = s ?? (PlaylistRunRules.Normalize(name, null) != null ? r.ResolveStop(null) : null);
            if (Owned(r, at)) return true;   // a goal block decides: the event above is all this call does
            return r.Signal(s?.occurrenceId, name, hasN ? (double?)n : null);
        }

        static bool Result(GameObject caller, GameObject target, string occ, double score, bool completed, double timeSec)
        {
            var r = R(caller, target);
            var s = r != null ? Which(r, caller, target, occ) : null;
            PlaylistEvents.Emit(PlaylistEvent.Signal, "result", "{\"score\":" + PlaylistRunRules.Num(score) + ",\"completed\":" + (completed ? "true" : "false")
                + ",\"timeSec\":" + PlaylistRunRules.Num(timeSec) + "}", s?.occurrenceId);
            if (r == null) return false;
            if (Owned(r, s)) return true;
            return s != null && r.Result(s.occurrenceId, score, completed, timeSec);
        }

        static bool Progress(GameObject caller, GameObject target, string occ, double n, double goal, string unit)
        {
            var r = R(caller, target);
            var s = r != null ? Which(r, caller, target, occ) : null;
            PlaylistEvents.Emit(PlaylistEvent.Progress, "progress", "{\"n\":" + PlaylistRunRules.Num(n) + ",\"of\":" + PlaylistRunRules.Num(goal)
                + ",\"unit\":\"" + SharedState.JsonEscape(unit ?? "") + "\"}", s?.occurrenceId);
            if (r == null) return false;
            if (Owned(r, s)) return true;
            return s != null && r.ReportProgress(s.occurrenceId, n, goal, unit);
        }

        static bool TimeLimit(GameObject caller, GameObject target, string occ, double seconds)
        {
            var r = R(caller, target);
            var s = r != null ? Which(r, caller, target, occ) : null;
            PlaylistEvents.Emit(PlaylistEvent.Signal, "time_limit", J("seconds", seconds), s?.occurrenceId);
            if (r == null) return false;
            if (Owned(r, s)) return true;
            return s != null && r.ReportTimeLimit(s.occurrenceId, seconds);
        }

        /// dp.event(name, data[, stop]) (CONTRACT § 1): a title event — no effect of its own; goal blocks listen to it.
        static bool Event(GameObject caller, GameObject target, string occ, string name, string dataJson)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var r = R(caller, target);
            var s = r != null ? Which(r, caller, target, occ) : null;
            string stop = s?.occurrenceId ?? (string.IsNullOrEmpty(occ) ? null : occ);
            return PlaylistEvents.Emit(PlaylistEvent.Event, name, dataJson, stop) != null;
        }

        /// A CPU's id in its one stable form ("cpu:<id>", Docs/CPU_PLAYERS.md); players' ids are never touched.
        static string EntrantId(string id, string kind)
        {
            if (string.IsNullOrEmpty(id)) return id;
            if (kind == PlaylistScoreRow.KindCpu || CpuRoster.IsCpuId(id)) return CpuRoster.Normalize(id);
            return kind != PlaylistScoreRow.KindPlayer && CpuRoster.Get(id) != null ? CpuRoster.Normalize(id) : id;   // a declared CPU by its bare id
        }

        static bool Score(GameObject go, string player, double n, string name)
        {
            var r = PlaylistRunner.For(go);
            if (r == null) return false;
            r.Score(EntrantId(player, null), n, name);
            return true;
        }

        static bool Placing(GameObject go, string id, string name, string kind, string avatar, int place, double points, bool hasPoints, int medal)
        {
            var r = PlaylistRunner.For(go);
            if (r == null) return false;
            id = EntrantId(id, kind);
            if (CpuRoster.IsCpuId(id)) kind = PlaylistScoreRow.KindCpu;
            r.Placing(id, name, kind, avatar, place, hasPoints ? (double?)points : null, medal);
            return true;
        }

        // ── dp.cpu: the title's CPU cast (CpuRoster) ──

        static string CpuRegister(string id, string name, string avatar, string color, string difficulty, string title, string tag,
            bool champion, UnityEngine.Object portrait, double u0, double v0, double u1, double v1)
        {
            var e = new CpuEntrant
            {
                id = id, name = name, avatar = Blank(avatar), color = Blank(color), difficulty = Blank(difficulty), title = Blank(title),
                tag = Blank(tag), champion = champion, portrait = portrait,
                uv = u1 > u0 && v1 > v0 ? new Rect((float)u0, (float)v0, (float)(u1 - u0), (float)(v1 - v0)) : new Rect(0, 0, 1, 1),
            };
            return CpuRoster.Register(e);
        }

        static string Blank(string s) => string.IsNullOrEmpty(s) ? null : s;

        static LuaTable CpuRow(CpuEntrant e)
        {
            if (e == null) return null;
            var t = LuaBehaviour.GetLuaEnv().NewTable();
            t.Set("id", e.id ?? ""); t.Set("name", e.name ?? ""); t.Set("kind", PlaylistScoreRow.KindCpu);
            t.Set("tag", string.IsNullOrEmpty(e.tag) ? CpuRoster.DefaultTag : e.tag);
            if (e.avatar != null) t.Set("avatar", e.avatar);
            if (e.color != null) t.Set("color", e.color);
            if (e.difficulty != null) t.Set("difficulty", e.difficulty);
            if (e.title != null) t.Set("title", e.title);
            t.Set("champion", e.champion);
            return t;
        }

        static LuaTable CpuGet(string id) => CpuRow(CpuRoster.Get(id));

        static LuaTable CpuList()
        {
            var arr = LuaBehaviour.GetLuaEnv().NewTable();
            int n = 1;
            foreach (var e in CpuRoster.All) arr.Set(n++, CpuRow(e));
            return arr;
        }

        static bool Declare(GameObject go, string signal, string kind)
        {
            var r = PlaylistRunner.For(go);
            if (r == null) return false;
            r.Declare(signal, kind);
            return true;
        }

        // dp.playlist.set_flag(name, value): value true/false/number/string (nil = true). Lua passes its type + text.
        static bool SetFlag(GameObject go, string name, string type, string text)
        {
            var r = PlaylistRunner.For(go);
            if (r == null) return false;
            Defective.JSON.JSONObject v;
            switch (type)
            {
                case "boolean": v = new Defective.JSON.JSONObject(text == "true"); break;
                case "number":
                    if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) return false;
                    v = new Defective.JSON.JSONObject(d); break;
                case "string": v = Defective.JSON.JSONObject.CreateStringObject(text ?? ""); break;
                case "nil": v = null; break;
                default: return false;
            }
            return r.SetFlag(name, v);
        }

        static bool Begin(GameObject go) { var r = PlaylistRunner.For(go); r?.Begin(); return r != null; }
        static bool Skip(GameObject go) { var r = PlaylistRunner.For(go); r?.SkipLeg(); return r != null; }

        static LuaTable Challenges()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var arr = env.NewTable();
            int n = 1;
            foreach (var r in PlaylistRunner.All)
            {
                if (r == null) continue;
                foreach (var (root, json) in r.DrainChallenges())
                {
                    if (root == null) continue;
                    var e = env.NewTable();
                    e.Set("root", root);
                    var p = DreamParkLuaNet.JsonToLua(env, SharedState.ParseFragment(json)) as LuaTable;
                    if (p != null) { p.Set("root", root); e.Set("params", p); }
                    arr.Set(n++, e);
                }
            }
            return arr;
        }

        static bool Pending()
        {
            foreach (var r in PlaylistRunner.All) if (r != null && r.HasChallenges) return true;
            return false;
        }

        // ── § 3.2a challenge events (pl-sets): runtime → the attraction's own objects, on every device ──
        // challenge {occurrence, kind, options, epoch} · challenge_won {occurrence, by} · challenge_lost {occurrence, why} ·
        // challenge_retry {occurrence, epoch} (reset in place, then dp.event('reset_done', {occurrence, epoch})) · challenge_skip.
        // Each script under the attraction with onevent(name, e) gets it, and every dp.on(name, fn[, go]) handler whose go is
        // inside the attraction (or that gave none).
        internal static void DeliverChallengeEvent(GameObject root, string name, string json)
        {
            if (root == null || string.IsNullOrEmpty(name)) return;
            var env = LuaBehaviour.GetLuaEnv();
            if (env == null) return;
            var data = DreamParkLuaNet.JsonToLua(env, SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json)) as LuaTable ?? env.NewTable();
            data.Set("name", name);
            data.Set("root", root);
            foreach (var lb in root.GetComponentsInChildren<LuaBehaviour>(false))
            {
                LuaTable sc = null;
                try { sc = lb.ScriptScope; } catch (Exception) { }
                LuaFunction fn = null;
                try { fn = sc?.Get<LuaFunction>("onevent"); } catch (Exception) { }
                if (fn == null) continue;
                try { fn.Call(name, data); }
                catch (Exception e) { Debug.LogWarning($"[dp.playlist] {lb.name}.onevent('{name}') threw: {e.Message}"); }
            }
            try { env.Global.Get<LuaFunction>("__dp_on_dispatch")?.Call(root, name, data); }
            catch (Exception e) { Debug.LogWarning($"[dp.playlist] dp.on('{name}') threw: {e.Message}"); }
        }

        static bool Under(GameObject go, GameObject root) => go != null && root != null && (go == root || go.transform.IsChildOf(root.transform));

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_pl_active",     new Func<GameObject, bool>(Active));
            env.Global.Set("dp_pl_version",    new Func<GameObject, int>(Version));
            env.Global.Set("dp_pl_state",      new Func<GameObject, LuaTable>(State));
            env.Global.Set("dp_pl_stop",       new Func<GameObject, GameObject, string, LuaTable>(Stop));
            env.Global.Set("dp_pl_signal",     new Func<GameObject, GameObject, string, string, double, bool, bool>(Signal));
            env.Global.Set("dp_pl_result",     new Func<GameObject, GameObject, string, double, bool, double, bool>(Result));
            env.Global.Set("dp_pl_declare",    new Func<GameObject, string, string, bool>(Declare));
            env.Global.Set("dp_pl_progress",   new Func<GameObject, GameObject, string, double, double, string, bool>(Progress));
            env.Global.Set("dp_pl_time_limit", new Func<GameObject, GameObject, string, double, bool>(TimeLimit));
            env.Global.Set("dp_pl_score",      new Func<GameObject, string, double, string, bool>(Score));
            env.Global.Set("dp_pl_placing",    new Func<GameObject, string, string, string, string, int, double, bool, int, bool>(Placing));
            env.Global.Set("dp_cpu_register",  new Func<string, string, string, string, string, string, string, bool, UnityEngine.Object, double, double, double, double, string>(CpuRegister));
            env.Global.Set("dp_cpu_norm",      new Func<string, string>(CpuRoster.Normalize));
            env.Global.Set("dp_cpu_get",       new Func<string, LuaTable>(CpuGet));
            env.Global.Set("dp_cpu_list",      new Func<LuaTable>(CpuList));
            env.Global.Set("dp_pl_begin",      new Func<GameObject, bool>(Begin));
            env.Global.Set("dp_pl_set_flag",   new Func<GameObject, string, string, string, bool>(SetFlag));
            env.Global.Set("dp_pl_skip",       new Func<GameObject, bool>(Skip));
            env.Global.Set("dp_pl_pending",    new Func<bool>(Pending));
            env.Global.Set("dp_pl_challenges", new Func<LuaTable>(Challenges));
            env.Global.Set("dp_pl_event",      new Func<GameObject, GameObject, string, string, string, bool>(Event));
            env.Global.Set("dp_pl_under",      new Func<GameObject, GameObject, bool>(Under));

            // No double quotes inside this verbatim block.
            env.DoString(@"
dp = dp or {}
local P = {}
dp.playlist = P

-- stop = an occurrence id (string), a GameObject inside the stop, or nil (the caller's own stop).
local function __pl_split(stop)
    if type(stop) == 'string' then return nil, stop end
    return stop, ''
end

P.active = function(go) return dp_pl_active(go) end
P.state = function(go) return dp_pl_state(go) end
P.stop = function(stop, go)
    local t, occ = __pl_split(stop)
    return dp_pl_stop(go, t or go, occ)
end
P.signal = function(name, n, stop, go)
    if type(name) ~= 'string' then return false end
    local t, occ = __pl_split(stop)
    local has = type(n) == 'number'
    return dp_pl_signal(go, t, occ, name, has and n or 0, has)
end
P.stop_complete = function(stop, result, go)
    if type(stop) == 'table' and result == nil then result, stop = stop, nil end
    local n = nil
    if type(result) == 'table' then n = result.score or result.n end
    if type(result) == 'number' then n = result end
    return P.signal('complete', n, stop, go)
end
-- One race / match's standings for EVERY entrant, players and CPUs: { {racer, name, kind='player'|'cpu', place, points?,
-- medal?, dnf?, avatar?}, ... }. place 1 = first; dnf (or place 0) scores 0 and ranks last. The run's scoreboard sums them
-- (rules.scoring 'placings': points per place, ties by best finishes). medal 'gold'|'silver'|'bronze'|'none' (or 3/2/1/0):
-- a medal-scored title's result, worth 3/2/1/0 points unless points are given; with a medal (or points) and no place,
-- no finishing order is recorded. A CPU is kind 'cpu' or a 'cpu:' id; a declared one (dp.cpu.register) brings its face.
local __pl_medals = { gold = 3, silver = 2, bronze = 1, none = 0 }
local function __pl_placing(e, go)
    if type(e) ~= 'table' then return false end
    local id = e.racer or e.player or e.id
    local pts = tonumber(e.points)
    local medal = -1
    if e.medal ~= nil then
        medal = tonumber(e.medal) or __pl_medals[string.lower(tostring(e.medal))] or 0
        if medal < 0 or medal > 3 then medal = 0 end
    end
    local place = tonumber(e.place)
    if e.dnf == true then place = 0
    elseif place == nil then place = (medal >= 0 or pts ~= nil) and -1 or 0 end
    return dp_pl_placing(go, id and tostring(id) or '', e.name and tostring(e.name) or '', e.kind and tostring(e.kind) or 'player',
        e.avatar and tostring(e.avatar) or '', math.floor(place), pts or 0, pts ~= nil, math.floor(medal))
end
P.standings = function(list, go)
    if type(list) ~= 'table' then return false end
    local ok = true
    for i = 1, #list do ok = __pl_placing(list[i], go) and ok end
    return ok
end
P.report_result = function(r, stop, go)
    r = type(r) == 'table' and r or {}
    local t, occ = __pl_split(stop)
    if r.racer ~= nil then __pl_placing(r, go) end
    if r.player ~= nil and r.racer == nil then dp_pl_score(go, tostring(r.player), tonumber(r.score) or 0, r.name and tostring(r.name) or '') end
    return dp_pl_result(go, t, occ, tonumber(r.score) or 0, r.completed ~= false, tonumber(r.timeSec or r.time) or 0)
end
-- The title's own progress at a stop: n of a goal, in its own word ('coins'). Feeds the goal line + wrist chip.
P.progress = function(n, of, unit, stop, go)
    local t, occ = __pl_split(stop)
    return dp_pl_progress(go, t, occ, tonumber(n) or 0, tonumber(of) or 0, unit and tostring(unit) or '')
end
-- The title's own time limit for a stop (seconds); the runner times it out with it (nobody gets stuck).
P.time_limit = function(seconds, stop, go)
    local t, occ = __pl_split(stop)
    return dp_pl_time_limit(go, t, occ, tonumber(seconds) or 0)
end
-- The session scoreboard: a player's ABSOLUTE score in this leg (player nil = this device's), carried across the run.
P.score = function(player, n, name, go)
    return dp_pl_score(go, player and tostring(player) or '', tonumber(n) or 0, name and tostring(name) or '')
end
P.declare = function(map, go)
    if type(map) ~= 'table' then return false end
    local ok = true
    for k, v in pairs(map) do ok = dp_pl_declare(go, tostring(k), tostring(v)) and ok end
    return ok
end
-- dp.event(name, data[, stop]): a title EVENT (repair-oct20 CONTRACT § 1). stop = an occurrence id, a GameObject inside the
-- stop, or nil (the caller's own stop; data.stop also names one). data <= 1 KB. Goal blocks: stop.on('event', {name=...}, fn).
dp.event = function(name, data, stop, go)
    if type(name) ~= 'string' or name == '' then return false end
    local t, occ = __pl_split(stop)
    if t == nil and occ == '' and type(data) == 'table' and type(data.stop) == 'string' then occ = data.stop end
    local j = '{}'
    if type(data) == 'table' then
        if dp.__packv ~= nil then j = dp.__packv(data) elseif dp.dreamie ~= nil and dp.dreamie.__json ~= nil then j = dp.dreamie.__json(data) end
    end
    return dp_pl_event(go, t, occ, name, j)
end
-- dp.on(name, fn[, go]): the playlist runtime's challenge events at this attraction (§ 3.2a): challenge, challenge_won,
-- challenge_lost, challenge_retry, challenge_skip. fn(e): e.occurrence, e.kind, e.options, e.epoch, e.by, e.why, e.root.
-- go (any object of the attraction) narrows it to that attraction; nil = every attraction. Scripts may define
-- onevent(name, e) instead. After a retry reset in place, then dp.event('reset_done', {occurrence = e.occurrence, epoch = e.epoch}).
local __dp_on = {}
dp.on = function(name, fn, go)
    if type(name) ~= 'string' or type(fn) ~= 'function' then return nil end
    local h = { name = name, fn = fn, go = go }
    __dp_on[#__dp_on + 1] = h
    return h
end
dp.off = function(h)
    for i = #__dp_on, 1, -1 do if __dp_on[i] == h or __dp_on[i].fn == h then table.remove(__dp_on, i) end end
end
function __dp_on_dispatch(root, name, e)
    local snap = {}
    for i = 1, #__dp_on do snap[i] = __dp_on[i] end
    for i = 1, #snap do
        local h = snap[i]
        local gone = false
        if h.go ~= nil then pcall(function() gone = h.go:Equals(nil) end) end
        if gone then dp.off(h)
        elseif (h.name == name or h.name == '*') and (h.go == nil or dp_pl_under(h.go, root)) then
            local ok, err = pcall(h.fn, e)
            if not ok then print('[dp.on] ' .. name .. ' threw: ' .. tostring(err)) end
        end
    end
end
P.on = function(name, fn, go) return dp.on(name, fn, go) end
P.begin = function(go) return dp_pl_begin(go) end
P.skip_leg = function(go) return dp_pl_skip(go) end
-- A story flag decided at runtime (e.g. the chosen starter): recorded on every present player's progress with this
-- leg's complete. value: true / false / a number / a short string; nil = true.
P.set_flag = function(name, value, go)
    if type(name) ~= 'string' then return false end
    return dp_pl_set_flag(go, name, type(value), value == nil and '' or tostring(value))
end

-- ── dp.cpu: CPU players as a platform primitive (Docs/CPU_PLAYERS.md) ──
-- Declare the cast once; report a CPU anywhere a player is reported with its id ('cpu:pip'); every surface shows it with
-- its portrait and a small tag. Old apps: no dp.cpu (guard with `if dp.cpu then`).
local C = {}
dp.cpu = C
-- e = { id, name, avatar = 'https://...', portrait = Texture|Sprite|Material, uv = {u0, v0, u1, v1}, color = '#RRGGBB',
--       difficulty = 'hard', title = 'Tennis Champion', tag = 'DreamPal', champion = true } -> 'cpu:<id>' ('' = refused)
C.register = function(e)
    if type(e) ~= 'table' then return '' end
    local uv = type(e.uv) == 'table' and e.uv or {}
    local function s(v) return v ~= nil and tostring(v) or '' end
    return dp_cpu_register(s(e.id), s(e.name), s(e.avatar), s(e.color), s(e.difficulty), s(e.title), s(e.tag),
        e.champion == true, e.portrait, tonumber(uv[1]) or 0, tonumber(uv[2]) or 0, tonumber(uv[3]) or 0, tonumber(uv[4]) or 0)
end
-- The whole cast at once: { {id, name, ...}, ... } -> how many were registered.
C.roster = function(list)
    if type(list) ~= 'table' then return 0 end
    local n = 0
    for i = 1, #list do if C.register(list[i]) ~= '' then n = n + 1 end end
    return n
end
C.id = function(x) return dp_cpu_norm(x ~= nil and tostring(x) or '') end
C.is_cpu = function(id)
    if type(id) ~= 'string' or id == '' then return false end
    return string.lower(string.sub(id, 1, 4)) == 'cpu:' or dp_cpu_get(id) ~= nil
end
C.get = function(id) return dp_cpu_get(id ~= nil and tostring(id) or '') end
C.list = function() return dp_cpu_list() end

local __pl_change = {}
P.on_change = function(fn, owner, go)
    if type(fn) ~= 'function' then return nil end
    local h = { fn = fn, owner = owner, go = go, v = -2 }
    __pl_change[#__pl_change + 1] = h
    return h
end
P.off_change = function(h)
    for i = #__pl_change, 1, -1 do
        local x = __pl_change[i]
        if x == h or x.fn == h then table.remove(__pl_change, i) end
    end
end

-- A script's own facade: the go arguments are automatic (DreamParkLuaAPI.BindScope).
P.__bind = function(go, owner)
    return setmetatable({
        active = function() return P.active(go) end,
        state = function() return P.state(go) end,
        stop = function(stop) return P.stop(stop or go, go) end,
        signal = function(name, n, stop) return P.signal(name, n, stop, go) end,
        stop_complete = function(stop, result) return P.stop_complete(stop, result, go) end,
        report_result = function(r, stop) return P.report_result(r, stop, go) end,
        progress = function(n, of, unit, stop) return P.progress(n, of, unit, stop, go) end,
        time_limit = function(seconds, stop) return P.time_limit(seconds, stop, go) end,
        score = function(player, n, name) return P.score(player, n, name, go) end,
        standings = function(list) return P.standings(list, go) end,
        declare = function(map) return P.declare(map, go) end,
        begin = function() return P.begin(go) end,
        skip_leg = function() return P.skip_leg(go) end,
        set_flag = function(name, value) return P.set_flag(name, value, go) end,
        on_change = function(fn, o) return P.on_change(fn, o or owner, go) end,
        on = function(name, fn) return dp.on(name, fn, go) end,
        off_change = function(h) return P.off_change(h) end,
    }, { __index = P })
end

local function __pl_gone(owner)
    if owner == nil then return false end
    local ok, gone = pcall(function() return owner:Equals(nil) end)
    return ok and gone == true
end

function __dp_pump_playlist()
    if #__pl_change > 0 then
        local keep = {}
        for i = 1, #__pl_change do
            local h = __pl_change[i]
            if not __pl_gone(h.owner) then
                keep[#keep + 1] = h
                local v = dp_pl_version(h.go)
                if v ~= h.v then
                    h.v = v
                    if v >= 0 then
                        local ok, err = pcall(h.fn, dp_pl_state(h.go))
                        if not ok then print('[dp.playlist] on_change threw: ' .. tostring(err)) end
                    end
                end
            end
        end
        __pl_change = keep
    end
    if not dp_pl_pending() then return end
    local evs = dp_pl_challenges()
    for i = 1, #evs do
        local e = evs[i]
        -- The attraction's own start_challenge(params), else its title's Game Manager's.
        local fn, sc = nil, nil
        pcall(function() sc = dp_attraction_scope(e.root); fn = sc and sc.start_challenge end)
        if fn == nil then pcall(function() sc = dp_container(e.root); fn = sc and sc.start_challenge end) end
        if fn ~= nil then
            local ok, err = pcall(fn, e.params)
            if not ok then print('[dp.playlist] start_challenge threw: ' .. tostring(err)) end
        end
    end
end
", "dp.playlist.bootstrap");
        }
    }
}
