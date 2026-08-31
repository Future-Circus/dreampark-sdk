
dp = dp or {}

dp.is_player   = function(x)  if x == nil then return false end
                              local ok, r = pcall(function() return dp_is_player(x) end)
                              if ok then return r end
                              local ok2, r2 = pcall(function() return dp_is_player_go(x) end)
                              return ok2 and r2 or false end
dp.player      = function()   return dp_player() end
dp.head        = function()   return dp_head() end
dp.scope       = function(go) return dp_scope(go) end
dp.attraction  = function(go) return dp_attraction_scope(go) end
dp.attraction_root = function(go) return dp_attraction_root(go) end
dp.game_id     = function(go) return dp_game_id(go) end

-- Where the player's hand is, right now. side is 'left' / 'right', or
-- omitted for the rig's own hand anchor. May be nil before the rig exists.
dp.hand        = function(side) return dp_hand(side or '') end
dp.hands       = function()   return dp_hand('left'), dp_hand('right') end

-- The shared park frame. Unity world space is PER HEADSET on Quest, so
-- every position or direction that crosses the relay goes out as park-local
-- (dp.to_park) and comes back into this headset's world (dp.from_park).
-- Typed net_send / onmessage and dp.peers() do this for you; these are for
-- content that still formats its own JSON. All identity when there is no park.
dp.park        = function()   return dp_park() end

dp.to_park = function(v)
    if v == nil then return nil end
    local park = dp_park()
    if park == nil then return v end
    return park:InverseTransformPoint(v)
end
dp.from_park = function(v)
    if v == nil then return nil end
    local park = dp_park()
    if park == nil then return v end
    return park:TransformPoint(v)
end
dp.to_park_dir = function(d)
    if d == nil then return nil end
    local park = dp_park()
    if park == nil then return d end
    return park:InverseTransformDirection(d)
end
dp.from_park_dir = function(d)
    if d == nil then return nil end
    local park = dp_park()
    if park == nil then return d end
    return park:TransformDirection(d)
end
dp.to_park_rot = function(q)
    if q == nil then return nil end
    local park = dp_park()
    if park == nil then return q end
    return CS.UnityEngine.Quaternion.Inverse(park.rotation) * q
end
dp.from_park_rot = function(q)
    if q == nil then return nil end
    local park = dp_park()
    if park == nil then return q end
    return park.rotation * q
end
-- Yaw in degrees, Unity convention (0 = +Z, 90 = +X). Through a direction
-- rather than subtracting euler Y, so a park synced to a wall QR code (full
-- rotation, not yaw-only) still round-trips.
local function __dp_yaw_to_dir(deg)
    local r = math.rad(deg or 0)
    return CS.UnityEngine.Vector3(math.sin(r), 0, math.cos(r))
end
local function __dp_dir_to_yaw(d, fallback)
    if d == nil then return fallback end
    if d.x * d.x + d.z * d.z < 1e-6 then return fallback end
    return math.deg(math.atan(d.x, d.z))
end
dp.to_park_yaw = function(deg)
    if dp_park() == nil then return deg end
    return __dp_dir_to_yaw(dp.to_park_dir(__dp_yaw_to_dir(deg)), deg)
end
dp.from_park_yaw = function(deg)
    if dp_park() == nil then return deg end
    return __dp_dir_to_yaw(dp.from_park_dir(__dp_yaw_to_dir(deg)), deg)
end

-- ── Typed wire format ────────────────────────────────────
-- net_send(kind, table) and onmessage(kind, payload) carry Unity values
-- across the relay without the creator formatting JSON or thinking about
-- coordinate frames. On the way OUT every Vector3 is a POINT and goes as
-- park-local; wrap one in dp.dir(v) to send it as a DIRECTION; Quaternions
-- and Transforms (position + rotation) convert too; Colors pass as-is. On
-- the way IN they come back as Unity values in THIS headset's world. Plain
-- numbers, strings, booleans and nested tables pass through unchanged.
-- Wire shape, for anyone reading a packet: {'$p':[x,y,z]} point,
-- {'$d':[x,y,z]} direction, {'$q':[x,y,z,w]} rotation,
-- {'$t':[px,py,pz,qx,qy,qz,qw]} pose, {'$c':[r,g,b,a]} colour.
local __dpq = string.char(34)   -- the double-quote character

local function __dp_esc(s)
    s = string.gsub(s, '\\', '\\\\')
    s = string.gsub(s, __dpq, '\\' .. __dpq)
    s = string.gsub(s, '\n', '\\n')
    s = string.gsub(s, '\r', '\\r')
    s = string.gsub(s, '\t', '\\t')
    s = string.gsub(s, '%c', function(c) return string.format('\\u%04x', string.byte(c)) end)
    return __dpq .. s .. __dpq
end

local function __dp_num(n)
    if n ~= n or n == math.huge or n == -math.huge then return '0' end
    if math.type(n) == 'integer' or (n == math.floor(n) and math.abs(n) < 1e15) then
        return string.format('%d', n)
    end
    return string.format('%.10g', n)
end

local function __dp_has(v, k)
    local ok, r = pcall(function() return v[k] ~= nil end)
    return ok and r == true
end

-- What a Unity value is, by structure. XLua userdata has no portable type
-- query from Lua, and a missing member may raise or return nil depending on
-- the wrapper, so both are treated as absent.
local function __dp_kind(v)
    if __dp_has(v, 'position') and __dp_has(v, 'rotation') then return 'transform' end
    if __dp_has(v, 'x') and __dp_has(v, 'z') then
        if __dp_has(v, 'w') then return 'quat' end
        return 'vec3'
    end
    if __dp_has(v, 'r') and __dp_has(v, 'a') then return 'color' end
    if __dp_has(v, 'transform') then return 'gameobject' end
    return nil
end

local function __dp_v3(v, fmt)
    return '[' .. string.format(fmt, v.x) .. ',' .. string.format(fmt, v.y) .. ',' .. string.format(fmt, v.z) .. ']'
end

local function __dp_tag(tag, body)
    return '{' .. __dpq .. tag .. __dpq .. ':' .. body .. '}'
end

local function __dp_is_array(t)
    local n = #t
    if n == 0 then return false end
    for k in pairs(t) do
        if math.type(k) ~= 'integer' or k < 1 or k > n then return false end
    end
    return true
end

local __dp_pack_value
local function __dp_pack_unity(v, kind, depth)
    if kind == 'vec3' then
        return __dp_tag('$p', __dp_v3(dp.to_park(v), '%.3f'))
    elseif kind == 'quat' then
        local q = dp.to_park_rot(v)
        return __dp_tag('$q', string.format('[%.4f,%.4f,%.4f,%.4f]', q.x, q.y, q.z, q.w))
    elseif kind == 'transform' then
        local p = dp.to_park(v.position)
        local q = dp.to_park_rot(v.rotation)
        return __dp_tag('$t', string.format('[%.3f,%.3f,%.3f,%.4f,%.4f,%.4f,%.4f]',
            p.x, p.y, p.z, q.x, q.y, q.z, q.w))
    elseif kind == 'gameobject' then
        return __dp_pack_unity(v.transform, 'transform', depth)
    elseif kind == 'color' then
        return __dp_tag('$c', string.format('[%.3f,%.3f,%.3f,%.3f]', v.r, v.g, v.b, v.a))
    end
    return nil
end

__dp_pack_value = function(v, depth)
    local tv = type(v)
    if tv == 'nil' then return 'null' end
    if tv == 'boolean' then return v and 'true' or 'false' end
    if tv == 'number' then return __dp_num(v) end
    if tv == 'string' then return __dp_esc(v) end
    if depth > 8 then return 'null' end
    if tv == 'userdata' or (tv == 'table' and getmetatable(v) ~= nil) then
        local kind = __dp_kind(v)
        if kind ~= nil then return __dp_pack_unity(v, kind, depth) end
        if tv == 'userdata' then
            print('[dp.pack] cannot serialize a ' .. tostring(v) .. '; sent as null')
            return 'null'
        end
    end
    if tv == 'table' then
        local d = rawget(v, '$d')
        if d ~= nil then
            return __dp_tag('$d', __dp_v3(dp.to_park_dir(d), '%.4f'))
        end
        if __dp_is_array(v) then
            local parts = {}
            for i = 1, #v do parts[i] = __dp_pack_value(v[i], depth + 1) end
            return '[' .. table.concat(parts, ',') .. ']'
        end
        local parts = {}
        for k, val in pairs(v) do
            parts[#parts + 1] = __dp_esc(tostring(k)) .. ':' .. __dp_pack_value(val, depth + 1)
        end
        return '{' .. table.concat(parts, ',') .. '}'
    end
    return 'null'
end

-- A Vector3 that means a direction, not a point (no translation on the wire).
dp.dir = function(v) return { ['$d'] = v } end

-- Lua table -> wire JSON, park-local. net_send(kind, table) calls this for you.
dp.pack = function(t)
    if type(t) ~= 'table' then return '{}' end
    return __dp_pack_value(t, 0)
end

local __dp_unpack_value
__dp_unpack_value = function(v, depth)
    if type(v) ~= 'table' or depth > 8 then return v end
    local p = rawget(v, '$p')
    if p ~= nil then return dp.from_park(CS.UnityEngine.Vector3(p[1] or 0, p[2] or 0, p[3] or 0)) end
    local d = rawget(v, '$d')
    if d ~= nil then return dp.from_park_dir(CS.UnityEngine.Vector3(d[1] or 0, d[2] or 0, d[3] or 0)) end
    local q = rawget(v, '$q')
    if q ~= nil then return dp.from_park_rot(CS.UnityEngine.Quaternion(q[1] or 0, q[2] or 0, q[3] or 0, q[4] or 1)) end
    local tr = rawget(v, '$t')
    if tr ~= nil then
        local pos = dp.from_park(CS.UnityEngine.Vector3(tr[1] or 0, tr[2] or 0, tr[3] or 0))
        local rot = dp.from_park_rot(CS.UnityEngine.Quaternion(tr[4] or 0, tr[5] or 0, tr[6] or 0, tr[7] or 1))
        return { position = pos, rotation = rot, forward = rot * CS.UnityEngine.Vector3.forward }
    end
    local c = rawget(v, '$c')
    if c ~= nil then return CS.UnityEngine.Color(c[1] or 0, c[2] or 0, c[3] or 0, c[4] or 1) end
    for k, val in pairs(v) do
        if type(val) == 'table' then v[k] = __dp_unpack_value(val, depth + 1) end
    end
    return v
end

-- Parsed wire table -> Unity values in this headset's world. onmessage gets
-- this for free; call it yourself on json_parse(raw).payload inside onnet.
dp.unpack = function(t) return __dp_unpack_value(t, 0) end

-- ── Players in the park ──────────────────────────────────
-- The SDK streams every headset's head and hands (PlayerPresence.cs),
-- park-local, on a fixed budget. No game sends player positions, ever.
-- What other players SEE of you is the RemoteRig subtree of your
-- Player.prefab, cloned for each peer (RemoteRig.cs); scripts inside it
-- boot on the clones with peer_id = that player's id.
--   dp.me()          -> my id (stable per headset)
--   dp.peers()       -> array (join order) of { id, game, head, left, right,
--                        hand, active_hand, left_tracked, right_tracked,
--                        seen, age, state, rig, name }
--                        head/left/right/hand: Transforms in THIS headset's
--                        world; state: the table they dp.set_state()d, Unity
--                        values restored; rig: their RemoteRig clone, or
--                        nil; name: their display name (the SDK floats it
--                        over their head by default — RemoteNameTag.cs)
--   dp.peer(id)      -> one of the above, or nil (you are not your own peer)
--   dp.set_state(t)  -> publish a small table to everyone (colour, team,
--                        score). Resent to late joiners; keep it small.
--   dp.on_peer_join(fn(id, peer), owner)  fires for everyone already here,
--                        then for each arrival. owner (self, a GameObject) is
--                        optional: the handler is dropped when it is destroyed.
--   dp.on_peer_leave(fn(id), owner)
--   dp.off_peer_join(fn) / dp.off_peer_leave(fn)
local function __dp_peer_view(p)
    if p ~= nil and p.state ~= nil and dp.unpack ~= nil then p.state = dp.unpack(p.state) end
    return p
end
dp.me        = function()   return dp_me() end
dp.peers     = function()
    local list = dp_peers()
    for i = 1, #list do __dp_peer_view(list[i]) end
    return list
end
dp.peer      = function(id) if id == nil then return nil end return __dp_peer_view(dp_peer(id)) end
dp.set_state = function(t)  dp_set_state(dp.pack(t or {})) end

__dp_peer_join_fns  = __dp_peer_join_fns  or {}
__dp_peer_leave_fns = __dp_peer_leave_fns or {}

local function __dp_owner_gone(owner)
    if owner == nil then return false end
    local ok, gone = pcall(function() return owner:Equals(nil) end)
    return ok and gone == true
end

local function __dp_remove_fn(list, fn)
    for i = #list, 1, -1 do
        if list[i].fn == fn then table.remove(list, i) end
    end
end

dp.on_peer_join = function(fn, owner)
    if fn == nil then return end
    local h = { fn = fn, owner = owner, seen = {} }
    __dp_peer_join_fns[#__dp_peer_join_fns + 1] = h
    dp_ensure_pump()
    local list = dp.peers()
    for i = 1, #list do
        local p = list[i]
        h.seen[p.id] = true
        local ok, err = pcall(fn, p.id, p)
        if not ok then print('[dp.on_peer_join] handler threw: ' .. tostring(err)) end
    end
end
dp.on_peer_leave = function(fn, owner)
    if fn == nil then return end
    __dp_peer_leave_fns[#__dp_peer_leave_fns + 1] = { fn = fn, owner = owner }
    dp_ensure_pump()
end
dp.off_peer_join  = function(fn) __dp_remove_fn(__dp_peer_join_fns, fn) end
dp.off_peer_leave = function(fn) __dp_remove_fn(__dp_peer_leave_fns, fn) end

local function __dp_prune_fns(list)
    for i = #list, 1, -1 do
        if __dp_owner_gone(list[i].owner) then table.remove(list, i) end
    end
end

function __dp_pump_peers()
    if dp_peer_event_count() == 0 then return end
    __dp_prune_fns(__dp_peer_join_fns)
    __dp_prune_fns(__dp_peer_leave_fns)
    local evs = dp_peer_events()
    for i = 1, #evs do
        local e = evs[i]
        if e.kind == 'join' then
            local p = __dp_peer_view(e.peer)
            for j = 1, #__dp_peer_join_fns do
                local h = __dp_peer_join_fns[j]
                if not h.seen[e.id] then
                    h.seen[e.id] = true
                    local ok, err = pcall(h.fn, e.id, p)
                    if not ok then print('[dp.on_peer_join] handler threw: ' .. tostring(err)) end
                end
            end
        else
            for j = 1, #__dp_peer_join_fns do __dp_peer_join_fns[j].seen[e.id] = nil end
            for j = 1, #__dp_peer_leave_fns do
                local ok, err = pcall(__dp_peer_leave_fns[j].fn, e.id)
                if not ok then print('[dp.on_peer_leave] handler threw: ' .. tostring(err)) end
            end
        end
    end
end

-- Live networking state. Never nil, never throws: fields report an absent
-- client rather than making every caller wrap the lookup in a pcall.
--   dp.relay()   -> { present, state, connected, ping, received, sent,
--                     send_rate, cap }
--   dp.session() -> { present, state, host, is_host, peers, park }
dp.relay       = function()   return dp_relay() end
dp.session     = function()   return dp_session() end

-- One-shot: run fn as soon as the relay link is up (immediately if it already
-- is). This is the hook for anything whose TIMING matters, not just its
-- delivery — a join handshake has to open its listen window when the link
-- comes up, and no amount of queueing on the send side fixes a window that
-- opened and closed while the client was still connecting.
__dp_connect_waiters = __dp_connect_waiters or {}

dp.on_connected = function(fn)
    if fn == nil then return end
    if dp_connected() then fn() return end
    __dp_connect_waiters[#__dp_connect_waiters + 1] = fn
    dp_ensure_pump()
end

function __dp_pump_connected()
    if #__dp_connect_waiters == 0 then return end
    if not dp_connected() then return end
    local list = __dp_connect_waiters
    __dp_connect_waiters = {}
    for i = 1, #list do
        local ok, err = pcall(list[i])
        if not ok then print('[dp.on_connected] handler threw: ' .. tostring(err)) end
    end
end

-- Sticky global waiter. Fires now if the global exists, else when it appears.
__dp_global_waiters = __dp_global_waiters or {}

dp.on_global = function(name, fn)
    if name == nil or fn == nil then return end
    local existing = rawget(_G, name)
    if existing ~= nil then fn(existing) return end
    __dp_global_waiters[#__dp_global_waiters + 1] = { name = name, fn = fn }
    dp_ensure_pump()
end

function __dp_pump_globals()
    __dp_pump_connected()
    __dp_pump_peers()
    if #__dp_global_waiters == 0 then return end
    local still = {}
    for i = 1, #__dp_global_waiters do
        local w = __dp_global_waiters[i]
        local v = rawget(_G, w.name)
        if v ~= nil then
            local ok, err = pcall(function() w.fn(v) end)
            if not ok then print('[dp.on_global] handler for ' .. tostring(w.name) .. ' threw: ' .. tostring(err)) end
        else
            still[#still + 1] = w
        end
    end
    __dp_global_waiters = still
end
