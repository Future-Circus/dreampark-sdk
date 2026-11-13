// ─────────────────────────────────────────────────────────────────────
//  RunnerLua.cs — the DIRECTOR's Lua (ways-to-play contract §4). Runs on the
//  session LEADER only (RunnerDirector decides that); every call goes through
//  the platform, which writes the board and every headset renders it.
//
//  Each director script compiles into its OWN environment table (no CS, no
//  load / require / io / os / debug, no _G): DreamPark.runner{}, dp.flow,
//  dp.edges, dp.session, dp.log, math / string / table and the basics.
//  dp.flow.state is the script's state that SURVIVES a leader hand-over (read
//  back as JSON after every call, synced, handed to onResume).
//
//  Globally (every title's gameplay Lua): dp.level.complete{result, scores, go}
//  — "this level is done" → the board (RunnerLua.LevelComplete).
//
//  Same bridge shape as DreamParkLuaNet: flat dp_rn_* delegates of strings /
//  LuaTable, nothing enters the generated XLua wrappers. Engine-free C# (no
//  UnityEngine calls except the GameObject handed to dp.level.complete), so
//  Tools/runner-offline runs it in a real XLua VM per fake headset.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using UnityEngine;
using XLua;

namespace DreamPark
{
    /// What a director script may ask of the platform (RunnerDirector implements it).
    public interface IRunnerDirectorHost
    {
        /// op: load | swap | transform | end | choose | edge_open | edge_lock | edge_link | edge_unlink | zone_open | zone_load |
        /// zone_close | zone_send | zone_lock | zone_door | zone_undoor | log. Returns a JSON / text answer ("" = none).
        string Op(string op, string a, string json);
        /// what: levels | board | time_left | starts_in | wait_total | zones | players | zone_of:&lt;player&gt; | zone_members:&lt;zone&gt;. JSON.
        string Get(string what);
    }

    public sealed class RunnerLua
    {
        public static Action<string> Log = m => Debug.Log(m);
        /// dp.level.complete from gameplay Lua on this device → the board (RunnerHost wires RunnerDirector.LevelCompleteFrom).
        public static Func<GameObject, string, bool> LevelComplete = (go, json) => false;
        /// The director this device renders (gameplay dp.zones.* reads it; RunnerHost wires it, the offline sim per env).
        public static Func<RunnerDirector> CurrentDirector = () => null;
        public Func<RunnerDirector> Director;   // per env (offline sim); null = CurrentDirector
        /// The load a gameplay object stands under (RunnerHost: its RunnerLevelRoot), -1 = none.
        public static Func<GameObject, int> LoadIdOf = go => -1;
        /// Board frame (metres) → world (RunnerHost: its board root). Default = the same numbers.
        public static Func<float, float, Vector3> BoardToWorld;
        /// "main" | "<id>" | "zone:<id>" | "" (this device's zone) → a zone id ("main" for the main board).
        static string ZoneArg(RunnerDirector d, string arg)
        {
            if (string.IsNullOrEmpty(arg)) return d.MyZone() ?? "main";
            return arg.StartsWith("zone:", StringComparison.Ordinal) ? arg.Substring(5) : arg;
        }
        RunnerDirector Dir() { try { return (Director ?? CurrentDirector)?.Invoke(); } catch (Exception) { return null; } }

        readonly LuaEnv _env;
        readonly Dictionary<string, IRunnerDirectorHost> _hosts = new Dictionary<string, IRunnerDirectorHost>(StringComparer.Ordinal);
        LuaFunction _new;
        static readonly Dictionary<LuaEnv, RunnerLua> s_byEnv = new Dictionary<LuaEnv, RunnerLua>();

        public static RunnerLua For(LuaEnv env)
        {
            if (env == null) return null;
            lock (s_byEnv)
            {
                if (s_byEnv.TryGetValue(env, out var r)) return r;
                r = new RunnerLua(env);
                s_byEnv[env] = r;
                return r;
            }
        }

        RunnerLua(LuaEnv env)
        {
            _env = env;
            Register(env);
            _new = env.Global.Get<LuaTable>("__dp_runner")?.Get<LuaFunction>("new");
        }

        public LuaEnv Env => _env;

        IRunnerDirectorHost H(string rid) => rid != null && _hosts.TryGetValue(rid, out var h) ? h : null;

        object Table(string json)
        {
            var j = RunnerBoardRules.Parse(json);
            return j == null ? _env.NewTable() : DreamParkLuaNet.JsonToLua(_env, j);
        }

        /// The global bridges + the dp.level.complete binding (idempotent per env). DreamParkLuaAPI.RegisterLua calls this
        /// for the shared env, so every title's gameplay Lua has dp.level.
        internal static void RegisterGlobal(LuaEnv env) { For(env); }

        void Register(LuaEnv env)
        {
            var g = env.Global;
            g.Set("dp_rn_op", new Func<string, string, string, string, string>((rid, op, a, j) =>
            {
                var h = H(rid);
                if (h == null) { Log?.Invoke($"[runner] {op}: no director for '{rid}' (it ended)"); return ""; }
                try { return h.Op(op, a ?? "", j ?? "") ?? ""; }
                catch (Exception e) { Log?.Invoke($"[runner] {op} threw: {e}"); return ""; }
            }));
            g.Set("dp_rn_get", new Func<string, string, LuaTable>((rid, what) =>
            {
                var h = H(rid);
                string j = h?.Get(what);
                return Table(string.IsNullOrEmpty(j) ? (what == "levels" ? "[]" : "{}") : j) as LuaTable;
            }));
            g.Set("dp_rn_str", new Func<string, string, string>((rid, what) => H(rid)?.Get(what) ?? ""));
            g.Set("dp_rn_num", new Func<string, string, double>((rid, what) =>
            {
                var h = H(rid);
                string j = h?.Get(what);
                return double.TryParse(j, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : -1;
            }));
            // gameplay dp.zones.* (every title's Lua, on any headset): read-only views of this device's board
            g.Set("dp_zn_q", new Func<string, string, string>((what, arg) =>
            {
                var d = Dir();
                if (d == null) return what == "mine" || what == "of" ? "main" : "";
                switch (what)
                {
                    case "mine": return d.MyZone() ?? "main";
                    case "of": return d.ZoneOf(arg);
                    case "leader": return d.ZoneLeader(ZoneArg(d, arg)) ?? "";
                    case "me": return d.DeviceId;
                }
                return "";
            }));
            g.Set("dp_zn_t", new Func<string, string, LuaTable>((what, arg) =>
            {
                var d = Dir();
                string j = "[]";
                if (d != null)
                {
                    if (what == "players") j = d.PlayersJson();
                    else if (what == "zones") j = d.ZonesJson();
                    else if (what == "members")
                    {
                        var arr = new JSONObject(JSONObject.Type.Array);
                        foreach (var m in d.ZoneMembers(ZoneArg(d, arg))) arr.Add(m);
                        j = arr.Print();
                    }
                }
                return Table(j) as LuaTable;
            }));
            g.Set("dp_zn_room", new Func<GameObject, string>(go =>
            {
                var d = Dir();
                if (d == null) return "main";
                int id = -1;
                try { id = go != null ? (LoadIdOf?.Invoke(go) ?? -1) : -1; } catch (Exception) { }
                string z = null;
                if (id >= 0) { var l = d.Loads.FirstOrDefault(x => x.id == id); z = l?.zone; }
                else z = d.MyZone();
                return string.IsNullOrEmpty(z) ? "main" : "zone:" + z;
            }));
            g.Set("dp_lv_runner", new Func<GameObject, LuaTable>(go =>
            {
                var d = Dir();
                if (d == null) return null;
                int id = -1;
                try { id = go != null ? (LoadIdOf?.Invoke(go) ?? -1) : -1; } catch (Exception) { }
                // an object under no runner level (the Game Manager, the Player rig): the board, and this headset's standing load
                string j = d.RunnerInfoJson(id);
                return j == null ? null : Table(j) as LuaTable;
            }));
            g.Set("dp_bd_vote", new Func<LuaTable>(() =>
            {
                string j = Dir()?.VoteJson();
                return j == null ? null : Table(j) as LuaTable;
            }));
            g.Set("dp_bd_pick", new Func<string, bool>(o => Dir()?.OverrideVote(o) ?? false));
            g.Set("dp_bd_board", new Func<LuaTable>(() => { var d = Dir(); return d == null ? null : Table(d.BoardJson(true)) as LuaTable; }));
            g.Set("dp_bd_request", new Func<string, string, bool>((k, d) => { var dir = Dir(); if (dir == null) return false; dir.Request(k, d); return true; }));
            g.Set("dp_bd_world", new Func<float, float, Vector3>((x, z) =>
            {
                try { return BoardToWorld?.Invoke(x, z) ?? new Vector3(x, 0, z); } catch (Exception) { return new Vector3(x, 0, z); }
            }));
            g.Set("dp_lv_complete", new Func<GameObject, string, bool>((go, j) =>
            {
                try { return LevelComplete?.Invoke(go, j) ?? false; }
                catch (Exception e) { Log?.Invoke("[runner] dp.level.complete threw: " + e.Message); return false; }
            }));

            // No double quotes inside this verbatim block.
            env.DoString(@"
dp = dp or {}
local RN = {}
__dp_runner = RN

local Q = string.char(34)
local function esc(s)
    s = string.gsub(s, '[%c\\' .. Q .. ']', function(c)
        if c == Q then return '\\' .. Q end
        if c == '\\' then return '\\\\' end
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
    return 'null'
end
RN.json = json

-- dp.level.complete{result = 'won'|'lost'|..., scores = {[player] = n}, go = self.gameObject} (gameplay Lua, any title).
dp.level = dp.level or {}
dp.level.complete = function(t, go)
    t = type(t) == 'table' and t or { result = t }
    go = go or t.go
    return dp_lv_complete(go, json({ result = t.result and tostring(t.result) or 'done', scores = type(t.scores) == 'table' and t.scores or nil,
        data = type(t.data) == 'table' and t.data or nil, level = t.level }))
end
-- dp.level.runner([go]) → nil (no way to play / go is not on the board) or {id, name, content, tiles, level, load, span,
-- zone, done, options, state}. dp.level.info is the same; dp.level.options(go) → that load's options or {}.
dp.level.runner = function(go) return dp_lv_runner(go) end
dp.level.info = dp.level.runner
dp.level.options = function(go) local r = dp_lv_runner(go) return r and r.options or {} end
-- dp.board (gameplay): the open vote (to draw its zones) and the board frame → world
dp.board = dp.board or {}
dp.board.vote = function() return dp_bd_vote() end
-- the board as the director sees it: {tiles, widthFt, depthFt, cols, rows, minutes, startsInSec, players, list, zones?,
-- state = dp.flow.state}; nil when no way to play runs here. dp.level.board is the same.
dp.board.get = function() return dp_bd_board() end
dp.level.board = dp.board.get
dp.board.toWorld = function(x, z) return dp_bd_world(tonumber(x) or 0, tonumber(z) or 0) end
-- pick an option of the open vote for everyone (the phone's pick overrides the zones); true when it applied
dp.board.pick = function(option) return dp_bd_pick(tostring(option or '')) end
-- ask the director something (it runs on another headset): dp.flow.onRequest(fn(player, kind, data)) receives it
dp.board.request = function(kind, data) return dp_bd_request(tostring(kind or ''), type(data) == 'table' and __dp_runner.json(data) or '') end

-- dp.zones (gameplay Lua, any title, any headset): where everyone is, for the map and for zone-scoped play.
dp.zones = dp.zones or {}
dp.zones.mine = function() return dp_zn_q('mine', '') end
dp.zones.of = function(player) return dp_zn_q('of', tostring(player or '')) end
dp.zones.players = function() return dp_zn_t('players', '') end
dp.zones.list = function() return dp_zn_t('zones', '') end
dp.zones.peers = function(zone) return dp_zn_t('members', type(zone) == 'userdata' and dp_zn_room(zone) or (zone and tostring(zone) or '')) end
-- 'main' or 'zone:<id>' for the level an object stands in: prefix your own shared-state names / trade channels with it
dp.zones.room = function(go) return dp_zn_room(go) end
-- the player who runs a zone's own things (its wild critters, its CPU trainer): the lowest live player id in it.
-- zone = 'main' | '<id>' | 'zone:<id>' | a GameObject (its level's zone) | nil (my zone)
local function zarg(z) if type(z) == 'userdata' then return dp_zn_room(z) end return z and tostring(z) or '' end
dp.zones.leader = function(zone) local l = dp_zn_q('leader', zarg(zone)) return l ~= '' and l or nil end
dp.zones.isLeader = function(zone) local l = dp_zn_q('leader', zarg(zone)) return l ~= '' and l == dp_zn_q('me', '') end

function RN.new(rid)
    local H = { complete = {}, empty = {}, enter = {}, choice = {}, ending = {}, votes = {}, clock = {}, tb = {},
                zenter = {}, zleave = {}, zrefused = {}, zempty = {}, join = {}, leave = {}, request = {} }
    local function op(name, a, t) return dp_rn_op(rid, name, a or '', t ~= nil and json(t) or '') end
    local E = {
        math = math, string = string, table = table, pairs = pairs, ipairs = ipairs, next = next, type = type,
        tostring = tostring, tonumber = tonumber, select = select, pcall = pcall, error = error, assert = assert,
        unpack = table.unpack, os = os and { time = os.time, clock = os.clock, date = os.date } or nil,
    }
    E.print = function(...)
        local p = {}
        for i = 1, select('#', ...) do p[i] = tostring((select(i, ...))) end
        op('log', table.concat(p, ' '))
    end
    E.DreamPark = { runner = function(t) return t end }
    local flow = { state = {} }
    flow.levels = function() return dp_rn_get(rid, 'levels') end
    flow.board = function() return dp_rn_get(rid, 'board') end
    local function nu(o)
        if type(o) ~= 'table' then return nil end
        if o.nextUp == true then return {} end   -- nextUp = true: 'Next up: <the level's name>'
        if type(o.nextUp) == 'table' then return { title = o.nextUp.title and tostring(o.nextUp.title) or nil, subtitle = o.nextUp.subtitle and tostring(o.nextUp.subtitle) or nil } end
        return nil
    end
    flow.load = function(level, o)
        o = type(o) == 'table' and o or {}
        return op('load', tostring(level or ''), { tile = tonumber(o.tile), span = o.span and tostring(o.span) or nil, nextUp = nu(o),
            options = type(o.options) == 'table' and o.options or nil }) == 'true'
    end
    flow.swapWhenClear = function(tile, level) return op('swap', tostring(level or ''), { tile = tonumber(tile) }) == 'true' end
    flow.transform = function(level, o) return op('transform', tostring(level or ''), { nextUp = nu(o) }) == 'true' end
    flow.endLevel = function(level, result)
        local a = type(level) == 'number' and ('#' .. math.floor(level)) or tostring(level or '')
        return op('end', a, { result = result and tostring(result) or 'done' }) == 'true'
    end
    flow.onLevelComplete = function(fn) if type(fn) == 'function' then H.complete[#H.complete + 1] = fn end end
    flow.onTileEmpty = function(fn) if type(fn) == 'function' then H.empty[#H.empty + 1] = fn end end
    flow.onPlayerEnterTile = function(fn) if type(fn) == 'function' then H.enter[#H.enter + 1] = fn end end
    flow.onChoice = function(fn) if type(fn) == 'function' then H.choice[#H.choice + 1] = fn end end
    -- close the open vote now: on `option`, or on the zones as they stand
    flow.closeVote = function(option) return op('vote_close', option and tostring(option) or '') == 'true' end
    flow.onPlayerJoin = function(fn) if type(fn) == 'function' then H.join[#H.join + 1] = fn end end
    flow.onPlayerLeave = function(fn) if type(fn) == 'function' then H.leave[#H.leave + 1] = fn end end
    -- gameplay's dp.board.request(kind, data) on any headset → fn(player, kind, data) here
    flow.onRequest = function(fn) if type(fn) == 'function' then H.request[#H.request + 1] = fn end end
    flow.choose = function(t, cb)
        t = type(t) == 'table' and t or {}
        cb = cb or t.callback or t.onResult
        -- an option is an id ('pitrun') or {id = 'pitrun', label = 'Pit Run', description = 'Jump the pits'};
        -- labels = {pitrun = 'Pit Run'} / descriptions = {pitrun = '…'} also work. The zones, panels and the phone show the
        -- label (else the id) and the description; onChoice / the callback always get the id.
        local opts, labels, descs = {}, {}, {}
        local lmap = type(t.labels) == 'table' and t.labels or {}
        local dmap = type(t.descriptions) == 'table' and t.descriptions or {}
        for i, o in ipairs(t.options or {}) do
            local lb, ds
            if type(o) == 'table' then opts[i] = tostring(o.id or o.label or i) lb = o.label or o.name ds = o.description or o.blurb else opts[i] = tostring(o) end
            lb = lb or lmap[opts[i]] or lmap[i]
            ds = ds or dmap[opts[i]] or dmap[i]
            labels[i] = lb ~= nil and tostring(lb) or ''
            descs[i] = ds ~= nil and tostring(ds) or ''
        end
        local zones = {}
        for i, z in ipairs(t.zones or {}) do
            if type(z) == 'number' then zones[i] = { tile = z } elseif type(z) == 'table' then zones[i] = { tile = tonumber(z.tile), x = tonumber(z.x), z = tonumber(z.z), r = tonumber(z.r), w = tonumber(z.w), d = tonumber(z.d) } else zones[i] = {} end
        end
        local def = t.default
        if type(def) == 'string' then
            local d = def
            for i, o in ipairs(opts) do if o == d then def = i break end end
            if def == d then for i, l in ipairs(labels) do if l == d then def = i break end end end
        end
        local tb = t.tiebreak
        local tbm = type(tb) == 'function' and 'fn' or (type(tb) == 'string' and tb or nil)
        local dw = t.dwell == true and 1 or tonumber(t.dwell)
        -- panel = false: no platform card and no floor outlines (the title draws its own vote from dp.board.vote());
        -- outline = false: the card stays, the floor outlines go (the title draws its own zones)
        local o = { options = opts, labels = labels, descriptions = descs, zones = zones, seconds = tonumber(t.seconds), default = tonumber(def), tag = t.tag and tostring(t.tag) or nil, tiebreak = tbm, dwell = dw }
        if t.prompt ~= nil then o.prompt = tostring(t.prompt) end   -- what the group is choosing ('Pick the next game')
        if t.panel == false then o.panel = false end
        if t.outline == false then o.outline = false end
        local id = op('choose', '', o)
        if id ~= '' and type(cb) == 'function' then H.votes[id] = cb end
        if id ~= '' and type(tb) == 'function' then H.tb[id] = tb end
        return id ~= '' and id or nil
    end
    local edges = {}
    edges.open = function(tile, side, look) return op('edge_open', '', { tile = tonumber(tile), side = tostring(side or ''), look = look and tostring(look) or 'open' }) == 'true' end
    edges.lock = function(tile, side, on) return op('edge_lock', '', { tile = tonumber(tile), side = tostring(side or ''), on = on ~= false }) == 'true' end
    -- dp.edges.link(tile, side, link) or dp.edges.link{a = levelA, b = levelB, link = 'Arch'}: an Edge Link prefab on the seam
    edges.link = function(tile, side, link)
        if type(tile) == 'table' then
            local t = tile
            return op('edge_link', '', { a = t.a and tostring(t.a) or nil, b = t.b and tostring(t.b) or nil, link = tostring(t.link or t.name or ''),
                tile = tonumber(t.tile), side = t.side and tostring(t.side) or nil }) == 'true'
        end
        return op('edge_link', '', { tile = tonumber(tile), side = tostring(side or ''), link = tostring(link or '') }) == 'true'
    end
    edges.unlink = function(tile, side) return op('edge_unlink', '', { tile = tonumber(tile), side = tostring(side or '') }) == 'true' end
    edges.close = function(tile, side) return edges.open(tile, side, 'closed') end
    local session = {}
    session.timeLeft = function() return dp_rn_num(rid, 'time_left') end
    session.onEnding = function(fn, seconds) if type(fn) == 'function' then H.ending[#H.ending + 1] = { fn = fn, sec = tonumber(seconds) or 60, fired = false } end end
    -- the shared clock starts once the 30 s pile-in ends (fires at once when there is no pile-in); never refires after a hand-over
    session.onClockStart = function(fn) if type(fn) == 'function' then H.clock[#H.clock + 1] = fn end end
    session.startsIn = function() return dp_rn_num(rid, 'starts_in') end
    -- dp.zones (director): players in different levels at once
    local zones = {}
    local function plist(p)
        local l = {}
        if type(p) == 'table' then for i, x in ipairs(p) do l[i] = tostring(x) end else l[1] = tostring(p or '') end
        return l
    end
    zones.open = function(id, o)
        o = type(o) == 'table' and o or {}
        return op('zone_open', tostring(id or ''), { level = o.level and tostring(o.level) or nil, tile = tonumber(o.tile), span = o.span and tostring(o.span) or nil,
            capacity = tonumber(o.capacity), name = o.name and tostring(o.name) or nil, locked = o.locked, alone = o.alone }) == 'true'
    end
    zones.load = function(id, level, o)
        o = type(o) == 'table' and o or {}
        return op('zone_load', tostring(id or ''), { level = tostring(level or ''), tile = tonumber(o.tile), span = o.span and tostring(o.span) or nil,
            options = type(o.options) == 'table' and o.options or nil }) == 'true'
    end
    zones.close = function(id) return op('zone_close', tostring(id or '')) == 'true' end
    zones.send = function(players, id) return tonumber(op('zone_send', tostring(id or 'main'), { players = plist(players) })) or 0 end
    zones.follow = function(player, friend) return zones.send(player, dp_rn_str(rid, 'zone_of:' .. tostring(friend or ''))) end
    zones.lock = function(id, on) return op('zone_lock', tostring(id or ''), { on = on ~= false }) == 'true' end
    zones.door = function(id, o)
        o = type(o) == 'table' and o or {}
        return op('zone_door', tostring(id or ''), { zone = o.zone and tostring(o.zone) or nil, from = o.from and tostring(o.from) or nil,
            tile = tonumber(o.tile), x = tonumber(o.x), z = tonumber(o.z), r = tonumber(o.r), dwell = tonumber(o.dwell) }) == 'true'
    end
    zones.removeDoor = function(id) return op('zone_undoor', tostring(id or '')) == 'true' end
    zones.of = function(player) return dp_rn_str(rid, 'zone_of:' .. tostring(player or '')) end
    zones.members = function(id) return dp_rn_get(rid, 'zone_members:' .. tostring(id or 'main')) end
    zones.list = function() return dp_rn_get(rid, 'zones') end
    zones.players = function() return dp_rn_get(rid, 'players') end
    zones.onEnter = function(fn) if type(fn) == 'function' then H.zenter[#H.zenter + 1] = fn end end
    zones.onLeave = function(fn) if type(fn) == 'function' then H.zleave[#H.zleave + 1] = fn end end
    zones.onRefused = function(fn) if type(fn) == 'function' then H.zrefused[#H.zrefused + 1] = fn end end
    zones.onEmpty = function(fn) if type(fn) == 'function' then H.zempty[#H.zempty + 1] = fn end end
    -- what this runtime supports (feature checks for directors that also run on older builds)
    flow.features = { lobby = true, dwell = true, tiebreak = true, options = true, nextUpTrue = true, zones = true, clock = true, addresses = true, override = true, closeVote = true, labels = true, panel = true, outline = true, prompt = true, descriptions = true, grid = true }
    -- dp.lobby: the Waiting for players wait (gameplay Lua has the same four, DreamParkLuaLobby.cs). onEnd = onClockStart's hook.
    local lobby = {}
    lobby.total = function() local v = dp_rn_num(rid, 'wait_total') return v > 0 and v or 0 end
    lobby.left = function() local v = dp_rn_num(rid, 'starts_in') return v > 0 and v or 0 end
    lobby.over = function() return lobby.left() <= 0 end
    lobby.onEnd = function(fn)
        if type(fn) ~= 'function' then return end
        if H.clockDone then
            local ok, err = pcall(fn)
            if not ok then op('log', 'ERROR dp.lobby.onEnd ' .. tostring(err)) end
            return
        end
        H.clock[#H.clock + 1] = fn
    end
    E.dp = { flow = flow, edges = edges, session = session, zones = zones, lobby = lobby, level = dp.level, log = E.print }

    local R = { env = E }
    local function try(f, ...)
        local ok, err = pcall(f, ...)
        if not ok then op('log', 'ERROR ' .. tostring(err)) end
        return ok
    end
    R.call = function(name, arg)
        local f = rawget(E, name)
        if type(f) ~= 'function' then return false end
        try(f, arg)
        return true
    end
    R.fire = function(kind, a, b, c)
        local list = H[kind]
        if list == nil then return 0 end
        local n = 0
        for _, fn in ipairs(list) do n = n + 1 try(fn, a, b, c) end
        return n
    end
    R.vote = function(id, winner, tag, info)
        local cb = H.votes[id]
        H.votes[id] = nil
        H.tb[id] = nil
        if cb then try(cb, winner, tag, info) end
        for _, fn in ipairs(H.choice) do try(fn, winner, tag, info) end
    end
    R.tiebreak = function(id, tied)
        local fn = H.tb[id]
        if type(fn) ~= 'function' then return nil end
        local ok, res = pcall(fn, tied)
        if not ok then op('log', 'ERROR tiebreak ' .. tostring(res)) return nil end
        return res ~= nil and tostring(res) or nil
    end
    -- A new leader of a board whose wait is over: onEnd runs at once from here on, the handlers it already holds never again.
    R.clockPast = function() H.clockDone = true end
    R.clock = function()
        H.clockDone = true
        for _, fn in ipairs(H.clock) do try(fn) end
    end
    R.ending = function(left)
        for _, h in ipairs(H.ending) do
            if not h.fired and left <= h.sec then h.fired = true try(h.fn, left) end
        end
    end
    R.prime = function(left)
        for _, h in ipairs(H.ending) do if left <= h.sec then h.fired = true end end
    end
    R.state = function() return json(E.dp.flow.state or {}) end
    R.setState = function(t) if type(t) == 'table' then E.dp.flow.state = t end end
    return R
end
", "dp.runner");
        }

        // ── One director's script ──────────────────────────────────────

        public sealed class Script
        {
            internal RunnerLua owner;
            internal string rid;
            internal LuaTable r, env;
            internal LuaFunction call, fire, vote, ending, prime, state, setState, tiebreak, clock, clockPast;
            public bool Loaded { get; internal set; }
            public string Error { get; internal set; }

            public bool Call(string name, LuaTable arg)
            {
                if (!Loaded) return false;
                try { var res = call.Call(name, arg); return res != null && res.Length > 0 && res[0] is bool b && b; }
                catch (Exception e) { Log?.Invoke($"[runner] {rid}: {name} threw: {e.Message}"); return false; }
            }

            public void Fire(string kind, object a, object b = null, object c = null)
            {
                if (!Loaded) return;
                try { fire.Call(kind, a, b, c); } catch (Exception e) { Log?.Invoke($"[runner] {rid}: {kind} threw: {e.Message}"); }
            }

            public void Vote(string id, string winner, string tag, string infoJson = null)
            {
                if (!Loaded) return;
                try { vote.Call(id, winner, tag, string.IsNullOrEmpty(infoJson) ? null : owner.Table(infoJson)); } catch (Exception e) { Log?.Invoke($"[runner] {rid}: vote threw: {e.Message}"); }
            }

            /// The game's tiebreak function for an open vote (null = none / lost): the tied labels → the winning label.
            public string TieBreak(string id, string tiedJson)
            {
                if (!Loaded || tiebreak == null) return null;
                try { var res = tiebreak.Call(id, owner.Table(tiedJson)); return res != null && res.Length > 0 ? res[0] as string : null; }
                catch (Exception e) { Log?.Invoke($"[runner] {rid}: tiebreak threw: {e.Message}"); return null; }
            }

            public void ClockPast()
            {
                if (!Loaded || clockPast == null) return;
                try { clockPast.Call(); } catch (Exception e) { Log?.Invoke($"[runner] {rid}: clockPast threw: {e.Message}"); }
            }

            public void ClockStart()
            {
                if (!Loaded || clock == null) return;
                try { clock.Call(); } catch (Exception e) { Log?.Invoke($"[runner] {rid}: onClockStart threw: {e.Message}"); }
            }

            public void Ending(double left)
            {
                if (!Loaded) return;
                try { ending.Call(left); } catch (Exception e) { Log?.Invoke($"[runner] {rid}: ending threw: {e.Message}"); }
            }

            /// A new leader: every ending hook whose threshold an earlier leader already crossed counts as fired.
            public void PrimeEnding(double left)
            {
                if (!Loaded) return;
                try { prime.Call(left); } catch (Exception) { }
            }

            public string StateJson()
            {
                if (!Loaded) return "{}";
                try { var res = state.Call(); return res != null && res.Length > 0 ? res[0] as string ?? "{}" : "{}"; }
                catch (Exception) { return "{}"; }
            }

            public void SetState(string json)
            {
                if (!Loaded || string.IsNullOrEmpty(json)) return;
                try { setState.Call(owner.Table(json)); } catch (Exception) { }
            }

            public LuaTable TableOf(string json) => owner.Table(json) as LuaTable;

            public void Dispose()
            {
                Loaded = false;
                owner?._hosts.Remove(rid ?? "");
                try { call?.Dispose(); fire?.Dispose(); vote?.Dispose(); ending?.Dispose(); prime?.Dispose(); state?.Dispose(); setState?.Dispose(); tiebreak?.Dispose(); clock?.Dispose(); env?.Dispose(); r?.Dispose(); } catch (Exception) { }
                call = fire = vote = ending = prime = state = setState = tiebreak = clock = clockPast = null; env = r = null;
            }
        }

        /// Compile + run a director script's chunk in its own environment (top level registers its handlers).
        public Script Load(string rid, string lua, IRunnerDirectorHost host)
        {
            var s = new Script { owner = this, rid = rid };
            _hosts[rid] = host;
            try
            {
                if (_new == null) _new = _env.Global.Get<LuaTable>("__dp_runner")?.Get<LuaFunction>("new");
                var res = _new.Call(rid);
                s.r = res != null && res.Length > 0 ? res[0] as LuaTable : null;
                if (s.r == null) { s.Error = "no runner table"; return s; }
                s.env = s.r.Get<LuaTable>("env");
                s.call = s.r.Get<LuaFunction>("call");
                s.fire = s.r.Get<LuaFunction>("fire");
                s.vote = s.r.Get<LuaFunction>("vote");
                s.ending = s.r.Get<LuaFunction>("ending");
                s.prime = s.r.Get<LuaFunction>("prime");
                s.state = s.r.Get<LuaFunction>("state");
                s.setState = s.r.Get<LuaFunction>("setState");
                s.tiebreak = s.r.Get<LuaFunction>("tiebreak");
                s.clock = s.r.Get<LuaFunction>("clock");
                s.clockPast = s.r.Get<LuaFunction>("clockPast");
                var chunk = _env.LoadString(lua ?? "", "runner:" + rid, s.env);
                chunk.Call();
                chunk.Dispose();
                s.Loaded = true;
            }
            catch (Exception e)
            {
                s.Error = e.Message;
                Log?.Invoke($"[runner] {rid}: the director script failed to load: {e.Message}");
            }
            return s;
        }
    }
}
