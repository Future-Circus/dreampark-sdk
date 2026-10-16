// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaNet.cs — the Lua face of the multiplayer services
//  (DreamParkSession, SharedState): dp.leader / is_leader /
//  on_leader_changed / clock / clock_synced / random, dp.shared_state.
//
//  Same bridge shape as DreamParkLuaAPI: flat dp_* delegates on env.Global
//  plus a Lua bootstrap that builds the dp.* functions. Only primitives,
//  GameObject/Transform and LuaTable cross the boundary, and every C#
//  method here is internal — nothing enters the generated XLua wrappers,
//  so adding them causes no Gen/ drift and needs no codegen.
//
//  Registered from DreamParkLuaAPI.RegisterLua (after the dp.pack bootstrap,
//  which this builds on) — never on its own, so the order is fixed.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;
using XLua;
using Defective.JSON;

namespace DreamPark
{
    internal static class DreamParkLuaNet
    {
        // ── Session ──────────────────────────────────────────────────
        // go = the calling script's object: leader and rank are scoped to its
        // title (only devices running that title count). nil = whole session.
        static string Title(GameObject go) => go != null ? SharedState.TitleOf(go) : null;
        static string TitleOfGo(GameObject go) => Title(go) ?? "";
        static string LeaderFor(string title) => DreamParkSession.LeaderFor(string.IsNullOrEmpty(title) ? null : title);
        static int Rank(string id, string title) => DreamParkSession.Rank(id, string.IsNullOrEmpty(title) ? null : title);
        static double Clock() => DreamParkSession.Now;
        static bool ClockSynced() => DreamParkSession.ClockSynced;
        static double Random(string key, int salt) => DreamParkSession.Random01(key ?? "", salt);

        /// <summary>The attraction frame typed net_send / onmessage encode against (null = park-local).</summary>
        static Transform NetFrame(GameObject go) => DreamParkSession.FrameOf(go);

        // ── SharedState ──────────────────────────────────────────────
        static int SsOpen(string name, GameObject explicitObject, GameObject caller)
        {
            var s = SharedState.Open(name, explicitObject, caller);
            return s != null ? s.Handle : 0;
        }

        static object SsGet(int h, string key)
        {
            var s = SharedState.ByHandle(h);
            string json = s?.GetJson(key);
            if (json == null) return null;
            return JsonToLua(LuaBehaviour.GetLuaEnv(), Parse(json));
        }

        static int SsVersion(int h) => SharedState.ByHandle(h)?.Version ?? -1;
        static bool SsSet(int h, string key, string json) => SharedState.ByHandle(h)?.Set(key, json) ?? false;
        static void SsRequest(int h, string kind, string json) => SharedState.ByHandle(h)?.Request(kind, json);
        static bool SsSynced(int h) => SharedState.ByHandle(h)?.Synced ?? false;
        static int SsRev(int h) => SharedState.ByHandle(h)?.Rev ?? 0;
        static string SsLeader(int h) => SharedState.ByHandle(h)?.LeaderId ?? "";
        static bool SsIsLeader(int h) => SharedState.ByHandle(h)?.IsLeader ?? false;
        static Transform SsFrame(int h) => SharedState.ByHandle(h)?.Frame;

        static LuaTable SsKeys(int h)
        {
            var env = LuaBehaviour.GetLuaEnv();
            var t = env.NewTable();
            var s = SharedState.ByHandle(h);
            if (s == null) return t;
            int i = 1;
            foreach (var k in s.Keys) t.Set(i++, k);
            return t;
        }

        // Merge cells (SharedState: counter / union / owner / max).
        static double SsAdd(int h, string key, double n) => SharedState.ByHandle(h)?.Add(key, n) ?? 0;
        static double SsTotal(int h, string key) => SharedState.ByHandle(h)?.Total(key) ?? 0;
        static bool SsUnion(int h, string key, string itemsJson)
        {
            var s = SharedState.ByHandle(h);
            var j = Parse(itemsJson);
            if (s == null || j == null) return false;
            var items = new List<string>();
            if (j.type == JSONObject.Type.Array && j.list != null)
            {
                foreach (var x in j.list) if (x != null) items.Add(x.type == JSONObject.Type.String ? x.stringValue : x.Print());
            }
            else items.Add(j.type == JSONObject.Type.String ? j.stringValue : j.Print());
            return s.Union(key, items);
        }
        static bool SsHas(int h, string key, string item) => SharedState.ByHandle(h)?.Contains(key, item) ?? false;
        static bool SsMine(int h, string key, string json) => SharedState.ByHandle(h)?.SetMine(key, json) ?? false;
        static object SsOf(int h, string key, string id)
        {
            string json = SharedState.ByHandle(h)?.MineOf(key, id);
            return json == null ? null : JsonToLua(LuaBehaviour.GetLuaEnv(), Parse(json));
        }
        static double SsMax(int h, string key, double v) => SharedState.ByHandle(h)?.Max(key, v) ?? v;
        static bool SsClear(int h, string key) => SharedState.ByHandle(h)?.Clear(key) ?? false;

        static void SsListen(int h)
        {
            var s = SharedState.ByHandle(h);
            if (s == null || s.LuaListening) return;
            s.LuaListening = true;
            int handle = h;
            s.Changed += (k, json) => Queue(new Ev { t = "ch", h = handle, a = k, json = json });
            s.Requested += (kind, json, from) => Queue(new Ev { t = "rq", h = handle, a = kind, json = json, c = from });
        }

        // ── Physics + hand-held (PHYS engine services, Lua face only) ─
        static bool NbAttach(GameObject go) => NetBodies.Attach(go) != null;
        static bool NbAttachOpts(GameObject go, float maxSpeed, float maxSpin, float maxPush, float rate, bool home,
            float homeFall, float homeRadius)
            => NetBodies.Attach(go, new NetBodyOptions
            {
                maxSpeed = maxSpeed, maxSpin = maxSpin, maxPush = maxPush, rate = rate, home = home,
                homeFall = homeFall, homeRadius = homeRadius,
            }) != null;
        static string NbOwner(GameObject go) => NetBodies.Owner(go) ?? "";
        static bool NbIsMine(GameObject go) => NetBodies.IsMine(go);
        static bool NbClaim(GameObject go) => NetBodies.Claim(go);
        static void NbRelease(GameObject go) => NetBodies.Release(go);
        static bool NbResetHome(GameObject go) => NetBodies.ResetHome(go);
        static void NbSetHome(GameObject go, Vector3 world) => NetBodies.SetHome(go, world);
        static bool Hold(GameObject prop, string side, Transform handNode) => Handhelds.Hold(prop, string.IsNullOrEmpty(side) ? "active" : side, handNode);
        static bool ReleaseHold(GameObject prop) => Handhelds.Release(prop);
        static bool HoldTwoHanded(GameObject prop, string side, Transform offHand, Transform handNode)
            => Handhelds.HoldTwoHanded(prop, string.IsNullOrEmpty(side) ? "active" : side, offHand, handNode);
        static string EditorHandFrame() => HandheldMath.EditorHandFrame;
        static Vector3 MuzzlePos(GameObject prop) => Handhelds.MuzzlePosition(prop);
        static Vector3 MuzzleFwd(GameObject prop) => Handhelds.MuzzleForward(prop);

        // ── dp.budget() ───────────────────────────────────────────────
        static LuaTable Budget()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var t = env.NewTable();
            var c = DreamBoxClient.Instance;
            int floor = DreamBoxClient.DesignBudget;
            int now = c != null ? c.SentThisSecond : 0;
            var b = DreamParkSession.Budget;
            t.Set("floor", floor);
            t.Set("presence", NetSendBudget.PresenceShare);
            t.Set("sdk", NetSendBudget.SdkShare);
            t.Set("game", NetSendBudget.GameShare);
            t.Set("this_second", now);
            t.Set("last_second", c != null ? c.SendRate : 0);
            t.Set("remaining", Math.Max(0, floor - now));
            t.Set("sdk_available", Math.Max(0f, b.Available));
            t.Set("sdk_sent", b.Sent);
            t.Set("sdk_denied", b.Denied);
            return t;
        }

        // ── Event queue, drained by the dp pump (__dp_pump_net) ──────
        struct Ev { public string t; public int h; public string a, json, c; }
        const int MaxEvents = 512;
        static readonly List<Ev> _events = new List<Ev>();

        static void Queue(Ev e)
        {
            if (_events.Count >= MaxEvents)
            {
                _events.RemoveAt(0);
                if (!_warnedOverflow)
                {
                    _warnedOverflow = true;
                    Debug.LogWarning("[dp.shared_state] event queue overflow — is the dp pump running?");
                }
            }
            _events.Add(e);
        }
        static bool _warnedOverflow;

        static int EventCount() => _events.Count;

        static LuaTable Events()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var arr = env.NewTable();
            int n = 1;
            if (_events.Count > 0)
            {
                var list = new List<Ev>(_events);
                _events.Clear();
                foreach (var ev in list)
                {
                    var e = env.NewTable();
                    e.Set("t", ev.t);
                    e.Set("h", ev.h);
                    e.Set("a", ev.a ?? "");
                    if (ev.c != null) e.Set("c", ev.c);
                    if (ev.json != null)
                    {
                        object v = JsonToLua(env, Parse(ev.json));
                        if (v != null) e.Set("v", v);
                    }
                    arr.Set(n++, e);
                }
            }
            return arr;
        }

        // ── JSON → Lua with doubles (json_parse rounds every number to float32) ──
        static JSONObject Parse(string json) => SharedState.ParseFragment(json);

        internal static object JsonToLua(LuaEnv env, JSONObject j)
        {
            if (j == null) return null;
            switch (j.type)
            {
                case JSONObject.Type.String: return j.stringValue;
                case JSONObject.Type.Number: return j.isInteger ? (object)j.longValue : j.doubleValue;
                case JSONObject.Type.Bool: return j.boolValue;
                case JSONObject.Type.Object:
                {
                    var t = env.NewTable();
                    if (j.keys != null)
                        for (int i = 0; i < j.keys.Count; i++)
                        {
                            object v = JsonToLua(env, j.list[i]);
                            if (v != null) t.Set(j.keys[i], v);
                        }
                    return t;
                }
                case JSONObject.Type.Array:
                {
                    var t = env.NewTable();
                    if (j.list != null)
                        for (int i = 0; i < j.list.Count; i++)
                        {
                            object v = JsonToLua(env, j.list[i]);
                            if (v != null) t.Set(i + 1, v);
                        }
                    return t;
                }
            }
            return null;
        }

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_title_of",        new Func<GameObject, string>(TitleOfGo));
            env.Global.Set("dp_leader_for",      new Func<string, string>(LeaderFor));
            env.Global.Set("dp_rank",            new Func<string, string, int>(Rank));
            env.Global.Set("dp_clock",           new Func<double>(Clock));
            env.Global.Set("dp_clock_synced",    new Func<bool>(ClockSynced));
            env.Global.Set("dp_random",          new Func<string, int, double>(Random));
            env.Global.Set("dp_net_frame",       new Func<GameObject, Transform>(NetFrame));
            env.Global.Set("dp_ss_open",         new Func<string, GameObject, GameObject, int>(SsOpen));
            env.Global.Set("dp_ss_get",          new Func<int, string, object>(SsGet));
            env.Global.Set("dp_ss_version",      new Func<int, int>(SsVersion));
            env.Global.Set("dp_ss_set",          new Func<int, string, string, bool>(SsSet));
            env.Global.Set("dp_ss_request",      new Action<int, string, string>(SsRequest));
            env.Global.Set("dp_ss_synced",       new Func<int, bool>(SsSynced));
            env.Global.Set("dp_ss_rev",          new Func<int, int>(SsRev));
            env.Global.Set("dp_ss_leader",       new Func<int, string>(SsLeader));
            env.Global.Set("dp_ss_is_leader",    new Func<int, bool>(SsIsLeader));
            env.Global.Set("dp_ss_frame",        new Func<int, Transform>(SsFrame));
            env.Global.Set("dp_ss_keys",         new Func<int, LuaTable>(SsKeys));
            env.Global.Set("dp_ss_listen",       new Action<int>(SsListen));
            env.Global.Set("dp_ss_add",          new Func<int, string, double, double>(SsAdd));
            env.Global.Set("dp_ss_total",        new Func<int, string, double>(SsTotal));
            env.Global.Set("dp_ss_union",        new Func<int, string, string, bool>(SsUnion));
            env.Global.Set("dp_ss_has",          new Func<int, string, string, bool>(SsHas));
            env.Global.Set("dp_ss_mine",         new Func<int, string, string, bool>(SsMine));
            env.Global.Set("dp_ss_of",           new Func<int, string, string, object>(SsOf));
            env.Global.Set("dp_ss_max",          new Func<int, string, double, double>(SsMax));
            env.Global.Set("dp_ss_clear",        new Func<int, string, bool>(SsClear));
            env.Global.Set("dp_nb_attach",       new Func<GameObject, bool>(NbAttach));
            env.Global.Set("dp_nb_attach_opts",  new Func<GameObject, float, float, float, float, bool, float, float, bool>(NbAttachOpts));
            env.Global.Set("dp_nb_owner",        new Func<GameObject, string>(NbOwner));
            env.Global.Set("dp_nb_is_mine",      new Func<GameObject, bool>(NbIsMine));
            env.Global.Set("dp_nb_claim",        new Func<GameObject, bool>(NbClaim));
            env.Global.Set("dp_nb_release",      new Action<GameObject>(NbRelease));
            env.Global.Set("dp_nb_reset_home",   new Func<GameObject, bool>(NbResetHome));
            env.Global.Set("dp_nb_set_home",     new Action<GameObject, Vector3>(NbSetHome));
            env.Global.Set("dp_hold",            new Func<GameObject, string, Transform, bool>(Hold));
            env.Global.Set("dp_release_hold",    new Func<GameObject, bool>(ReleaseHold));
            env.Global.Set("dp_hold_two_handed", new Func<GameObject, string, Transform, Transform, bool>(HoldTwoHanded));
            env.Global.Set("dp_editor_hand_frame", new Func<string>(EditorHandFrame));
            env.Global.Set("dp_muzzle_pos",      new Func<GameObject, Vector3>(MuzzlePos));
            env.Global.Set("dp_muzzle_fwd",      new Func<GameObject, Vector3>(MuzzleFwd));
            env.Global.Set("dp_budget",          new Func<LuaTable>(Budget));
            env.Global.Set("dp_net_event_count", new Func<int>(EventCount));
            env.Global.Set("dp_net_events",      new Func<LuaTable>(Events));

            // No double quotes inside this verbatim block.
            env.DoString(@"
dp = dp or {}

local function __dpn_owner_gone(owner)
    if owner == nil then return false end
    local ok, gone = pcall(function() return owner:Equals(nil) end)
    return ok and gone == true
end

local function __dpn_add(list, fn, owner)
    local h = { fn = fn, owner = owner }
    h.off = function()
        for i = #list, 1, -1 do if list[i] == h then table.remove(list, i) end end
    end
    list[#list + 1] = h
    return h
end

local function __dpn_off(list, x)
    if list == nil or x == nil then return end
    for i = #list, 1, -1 do
        if list[i] == x or list[i].fn == x then table.remove(list, i) end
    end
end

local function __dpn_fire(list, tag, ...)
    if list == nil or #list == 0 then return end
    for i = #list, 1, -1 do
        if __dpn_owner_gone(list[i].owner) then table.remove(list, i) end
    end
    local snap = {}
    for i = 1, #list do snap[i] = list[i] end
    for i = 1, #snap do
        local ok, err = pcall(snap[i].fn, ...)
        if not ok then print('[' .. tag .. '] handler threw: ' .. tostring(err)) end
    end
end

-- ── Session: leader, shared clock, shared random ─────────
--   dp.leader()        -> id of the device that leads THIS title (inside a
--                         script; from the console: the whole session). The
--                         most senior live, synced headset: the incumbent keeps
--                         the lead until it leaves, so a newcomer never takes
--                         over mid-round. Phones lead only with no headset here.
--   dp.is_leader()     -> dp.leader() == dp.me()
--   dp.on_leader_changed(fn(old, new)[, owner]) -> handle (h.off())
--                         fires once now with (nil, current), then on change
--   dp.rank([id])      -> 1-based join order among this title's players
--                         (colours, turn queues); nil if not in the room
--   dp.clock()         -> shared session clock, seconds (same on every device,
--                         never runs backwards once synced)
--   dp.clock_synced()  -> false until this device joined the room's timeline
--   dp.random(key)     -> [0,1), identical on every device for the same key
--                         once dp.clock_synced() (the seed rides the leader's beat)
--   dp.random(key, m)  -> integer 1..m      dp.random(key, a, b) -> a..b
--                         (integers when a and b are integers, else a float)
--   Gameplay-visible randomness goes through dp.random (or the leader);
--   never math.random — that is a different number on every headset.
--   dp.to_attraction(pos[, go]) / dp.from_attraction(pos[, go]) (+ _dir):
--                         attraction-relative coordinates for hand-packed
--                         numbers (park-local outside an attraction).
-- ── Send budget ──────────────────────────────────────────
--   dp.budget() -> { floor = 60, presence = 12, sdk = 24, game = 24,
--                    this_second, last_second, remaining, sdk_available,
--                    sdk_sent, sdk_denied }
--   The relay drops message 61 of any second, silently, for the whole
--   headset. It is split once: player presence (≤12/s), SDK services
--   (shared state, dp.net_body, the session; ≤24/s) and YOUR net_send,
--   which is guaranteed `game` (24/s) whatever the SDK is doing. Design
--   every title's own traffic to fit inside `game`; `remaining` is what is
--   left of the floor in the current second, all senders included.
dp.budget = function() return dp_budget() end

local function __dpn_title(go) if go == nil then return '' end return dp_title_of(go) end
dp.leader       = function(go) return dp_leader_for(__dpn_title(go)) end
dp.is_leader    = function(go) return dp_leader_for(__dpn_title(go)) == dp_me() end
dp.rank         = function(id, go)
    local r = dp_rank(id or dp_me(), __dpn_title(go))
    if r == nil or r == 0 then return nil end
    return r
end
dp.clock        = function() return dp_clock() end
dp.clock_synced = function() return dp_clock_synced() end
dp.random = function(key, a, b)
    local r = dp_random(tostring(key), 0)
    if a == nil then return r end
    if b == nil then a, b = 1, a end
    if math.type(a) == 'integer' and math.type(b) == 'integer' then
        if b < a then a, b = b, a end
        local n = a + math.floor(r * (b - a + 1))
        if n > b then n = b end
        return n
    end
    return a + (b - a) * r
end
dp.to_attraction = function(v, go)
    if v == nil then return nil end
    local f = go ~= nil and dp_net_frame(go) or nil
    if f == nil then return dp.to_park(v) end
    return f:InverseTransformPoint(v)
end
dp.from_attraction = function(v, go)
    if v == nil then return nil end
    local f = go ~= nil and dp_net_frame(go) or nil
    if f == nil then return dp.from_park(v) end
    return f:TransformPoint(v)
end
dp.to_attraction_dir = function(v, go)
    if v == nil then return nil end
    local f = go ~= nil and dp_net_frame(go) or nil
    if f == nil then return dp.to_park_dir(v) end
    return f:InverseTransformDirection(v)
end
dp.from_attraction_dir = function(v, go)
    if v == nil then return nil end
    local f = go ~= nil and dp_net_frame(go) or nil
    if f == nil then return dp.from_park_dir(v) end
    return f:TransformDirection(v)
end

-- Polled by the pump (one cheap call per handler per frame): each handler is
-- scoped to the title of the object that registered it.
__dp_leader_fns = __dp_leader_fns or {}
dp.on_leader_changed = function(fn, owner, go)
    if fn == nil then return nil end
    local h = __dpn_add(__dp_leader_fns, fn, owner)
    h.title = __dpn_title(go)
    h.last = dp_leader_for(h.title)
    dp_ensure_pump()
    if h.last ~= nil and h.last ~= '' then
        local ok, err = pcall(fn, nil, h.last)
        if not ok then print('[dp.on_leader_changed] handler threw: ' .. tostring(err)) end
    end
    return h
end
dp.off_leader_changed = function(x) __dpn_off(__dp_leader_fns, x) end

local function __dpn_pump_leaders()
    local list = __dp_leader_fns
    if #list == 0 then return end
    for i = #list, 1, -1 do
        if __dpn_owner_gone(list[i].owner) then table.remove(list, i) end
    end
    local snap = {}
    for i = 1, #list do snap[i] = list[i] end
    for i = 1, #snap do
        local h = snap[i]
        local cur = dp_leader_for(h.title)
        if cur ~= h.last then
            local old = h.last
            h.last = cur
            if old == '' then old = nil end
            local ok, err = pcall(h.fn, old, cur)
            if not ok then print('[dp.on_leader_changed] handler threw: ' .. tostring(err)) end
        end
    end
end

-- ── Shared state ─────────────────────────────────────────
--   local s = dp.shared_state(name[, gameObject])
--     s.get(k[, default])  s.set(k, v)  (v: number/string/bool/Vector3/
--       rotation/small table; nil deletes. The leader applies; anyone else's
--       set is a request the leader applies, so starts are idempotent.)
--     s.request(kind, payload)  +  s.on_request(fn(kind, payload, from))
--       (handlers run on the leader only; the leader decides)
--     s.on_change(fn(key, value))  fires for every key already present,
--       then on each change (value nil = deleted)
--     s.synced()  false until the room's snapshot arrived, or the room is
--       known to be empty: every live player of the title announced itself
--       and none holds it, or 10 s with no holder (no relay client: 0.25 s;
--       a client that never connects: 6 s; a state synced alone adopts the
--       room's snapshot once it connects). Writes made before that are held:
--       applied if the room was empty, dropped if it already had state.
--     s.rev()  s.leader()  s.is_leader()  s.keys()
--   Bus: the calling script's attraction (or gameObject's); outside an
--   attraction, the title. Vector3s are attraction-relative on the wire.
__dp_ss_facades     = __dp_ss_facades     or {}
__dp_ss_change_fns  = __dp_ss_change_fns  or {}
__dp_ss_request_fns = __dp_ss_request_fns or {}

local function __dp_ss_make(h, name)
    local s = { name = name }
    local cache, cver = {}, nil
    local function frame() return dp_ss_frame(h) end
    s.get = function(k, default)
        if k == nil then return default end
        k = tostring(k)
        local ver = dp_ss_version(h)
        if ver ~= cver then cache = {} cver = ver end
        local c = cache[k]
        if c ~= nil then return c end
        local v = dp_ss_get(h, k)
        if v == nil then return default end
        -- Tables may hold attraction-relative points: decode every read
        -- (the attraction may have moved); scalars are cached per version.
        if type(v) == 'table' then return dp.__unpackv(v, frame()) end
        cache[k] = v
        return v
    end
    s.set = function(k, v)
        if k == nil then return false end
        return dp_ss_set(h, tostring(k), dp.__packv(v, frame()))
    end
    s.request = function(kind, payload)
        if kind == nil then return end
        dp_ss_request(h, tostring(kind), dp.__packv(payload or {}, frame()))
    end
    s.on_change = function(fn, owner)
        if fn == nil then return nil end
        local list = __dp_ss_change_fns[h]
        if list == nil then list = {} __dp_ss_change_fns[h] = list end
        local hd = __dpn_add(list, fn, owner)
        dp_ss_listen(h)
        dp_ensure_pump()
        local keys = dp_ss_keys(h)
        for i = 1, #keys do
            local ok, err = pcall(fn, keys[i], s.get(keys[i]))
            if not ok then print('[shared_state.on_change] handler threw: ' .. tostring(err)) end
        end
        return hd
    end
    s.off_change = function(x) __dpn_off(__dp_ss_change_fns[h], x) end
    s.on_request = function(fn, owner)
        if fn == nil then return nil end
        local list = __dp_ss_request_fns[h]
        if list == nil then list = {} __dp_ss_request_fns[h] = list end
        local hd = __dpn_add(list, fn, owner)
        dp_ss_listen(h)
        dp_ensure_pump()
        return hd
    end
    s.off_request = function(x) __dpn_off(__dp_ss_request_fns[h], x) end
    -- Merge cells: every device writes its own part directly (no leader
    -- round trip); everyone merges. get(key) on a cell returns its aggregate.
    s.add   = function(k, n) return dp_ss_add(h, tostring(k), n or 1) end      -- grow my part; returns it
    s.total = function(k) return dp_ss_total(h, tostring(k)) end               -- sum of every part
    s.union = function(k, items)                                               -- grow-only set
        if type(items) ~= 'table' then items = { items } end
        return dp_ss_union(h, tostring(k), dp.__packv(items))
    end
    s.has   = function(k, item) return dp_ss_has(h, tostring(k), tostring(item)) end
    s.items = function(k) return s.get(k, {}) end
    s.mine  = function(k, v) return dp_ss_mine(h, tostring(k), dp.__packv(v, frame())) end   -- my own value
    s.of    = function(k, id)
        local v = dp_ss_of(h, tostring(k), id or dp_me())
        if type(v) == 'table' then return dp.__unpackv(v, frame()) end
        return v
    end
    s.all   = function(k) return s.get(k, {}) end                              -- { id = value }
    s.max   = function(k, n) return dp_ss_max(h, tostring(k), n) end           -- merged maximum
    s.clear = function(k) return dp_ss_clear(h, tostring(k)) end               -- leader only: new epoch
    s.synced    = function() return dp_ss_synced(h) end
    s.rev       = function() return dp_ss_rev(h) end
    s.leader    = function() return dp_ss_leader(h) end
    s.is_leader = function() return dp_ss_is_leader(h) end
    s.keys      = function() return dp_ss_keys(h) end
    return s
end

-- caller: the script's GameObject (bound automatically inside a LuaBehaviour).
dp.shared_state = function(name, go, caller)
    if name == nil then return nil end
    local h = dp_ss_open(tostring(name), go, caller)
    if h == nil or h == 0 then return nil end
    local s = __dp_ss_facades[h]
    if s == nil then
        s = __dp_ss_make(h, tostring(name))
        __dp_ss_facades[h] = s
    end
    return s
end

-- ── Synced physics + hand-held props (no components to add) ─
--   local body = dp.net_body(go_or_rigidbody[, opts])   idempotent per object
--     opts = { max_speed = 2.6, max_spin = 9, max_push = 1.2, rate = 10, home = true,
--              home_fall = 1.5, home_radius = 6 }
--     body.owner()  body.is_mine()  body.claim()  body.release()
--     body.home()          put it back where it rested (claims it)
--     body.home(pos)       set home to a world position
--     body.on_owner_changed(fn(old, new)[, owner]) -> handle (h.off())
--   dp.hold(prop, 'left'|'right'|'active'[, hand_node])  mount a Grip prop in the hand
--   dp.release_hold(prop)        stop managing it (it stays where it is)
--   dp.muzzle(prop) -> pos, fwd  its Muzzle (the root when it has none), world
--   dp.hold_two_handed(prop, side, off_hand[, hand_node])  aim Grip -> Grip2 at the off hand
--   dp.editor_hand_frame() -> 'device' (the Editor hand frame now matches a headset;
--                         content that compensated for the old Editor frame checks this)
local function __dpn_go(x)
    if x == nil then return nil end
    local ok, g = pcall(function() return x.gameObject end)
    if ok and g ~= nil then return g end
    return x
end
__dp_nb_handles = __dp_nb_handles or {}
__dp_nb_watch = __dp_nb_watch or {}
dp.net_body = function(x, opts)
    local go = __dpn_go(x)
    if go == nil then return nil end
    local ok
    if type(opts) == 'table' then
        ok = dp_nb_attach_opts(go, opts.max_speed or 2.6, opts.max_spin or 9, opts.max_push or 1.2,
            opts.rate or 10, opts.home ~= false, opts.home_fall or 1.5, opts.home_radius or 6)
    else
        ok = dp_nb_attach(go)
    end
    if not ok then return nil end
    local key = go:GetInstanceID()
    local b = __dp_nb_handles[key]
    if b ~= nil and not __dpn_owner_gone(b.gameObject) then return b end
    b = { gameObject = go }
    b.owner   = function() local o = dp_nb_owner(go) if o == '' then return nil end return o end
    b.is_mine = function() return dp_nb_is_mine(go) end
    b.claim   = function() return dp_nb_claim(go) end
    b.release = function() dp_nb_release(go) end
    b.home    = function(pos)
        if pos == nil then return dp_nb_reset_home(go) end
        dp_nb_set_home(go, pos)
        return true
    end
    b.on_owner_changed = function(fn, owner)
        if fn == nil then return nil end
        local h = __dpn_add(__dp_nb_watch, fn, owner or go)
        h.go = go
        h.last = dp_nb_owner(go)
        dp_ensure_pump()
        return h
    end
    __dp_nb_handles[key] = b
    return b
end
dp.hold = function(prop, side, hand)
    local go = __dpn_go(prop)
    if go == nil then return false end
    local node = nil
    if hand ~= nil then node = hand.transform end
    return dp_hold(go, side or 'active', node)
end
dp.hold_two_handed = function(prop, side, off, hand)
    local go = __dpn_go(prop)
    if go == nil then return false end
    return dp_hold_two_handed(go, side or 'active', off ~= nil and off.transform or nil, hand ~= nil and hand.transform or nil)
end
dp.editor_hand_frame = function() return dp_editor_hand_frame() end
dp.release_hold = function(prop)
    local go = __dpn_go(prop)
    if go == nil then return false end
    return dp_release_hold(go)
end
dp.muzzle = function(prop)
    local go = __dpn_go(prop)
    if go == nil then return nil end
    return dp_muzzle_pos(go), dp_muzzle_fwd(go)
end

local function __dpn_pump_bodies()
    local list = __dp_nb_watch
    if #list == 0 then return end
    for i = #list, 1, -1 do
        if __dpn_owner_gone(list[i].owner) or __dpn_owner_gone(list[i].go) then table.remove(list, i) end
    end
    for i = 1, #list do
        local h = list[i]
        if h ~= nil then
            local cur = dp_nb_owner(h.go)
            if cur ~= h.last then
                local old = h.last
                h.last = cur
                if old == '' then old = nil end
                if cur == '' then cur = nil end
                local ok, err = pcall(h.fn, old, cur)
                if not ok then print('[body.on_owner_changed] handler threw: ' .. tostring(err)) end
            end
        end
    end
end

-- A value whose frame was destroyed (attraction swap) must not throw out of
-- the pump and drop the rest of the frame's events: deliver it unpacked.
local function __dpn_unpack(v, h)
    local ok, r = pcall(function() return dp.__unpackv(v, dp_ss_frame(h)) end)
    if ok then return r end
    if __dp_pump_warn ~= nil then __dp_pump_warn('shared_state unpack', r) end
    return v
end

function __dp_pump_net()
    local ok, err = pcall(__dpn_pump_leaders)
    if not ok and __dp_pump_warn ~= nil then __dp_pump_warn('pump_leaders', err) end
    ok, err = pcall(__dpn_pump_bodies)
    if not ok and __dp_pump_warn ~= nil then __dp_pump_warn('pump_bodies', err) end
    if dp_net_event_count() == 0 then return end
    local evs = dp_net_events()
    for i = 1, #evs do
        local e = evs[i]
        if e.t == 'ch' then
            local list = __dp_ss_change_fns[e.h]
            if list ~= nil and #list > 0 then
                local v = e.v
                if type(v) == 'table' then v = __dpn_unpack(v, e.h) end
                __dpn_fire(list, 'shared_state.on_change', e.a, v)
            end
        elseif e.t == 'rq' then
            local list = __dp_ss_request_fns[e.h]
            if list ~= nil and #list > 0 then
                local v = e.v
                if type(v) == 'table' then v = __dpn_unpack(v, e.h) end
                __dpn_fire(list, 'shared_state.on_request', e.a, v, e.c)
            end
        end
    end
end
", "dp.net.bootstrap");
        }
    }
}
