
dp = dp or {}

dp.is_player   = function(x)  if x == nil then return false end
                              local ok, r = pcall(function() return dp_is_player(x) end)
                              if ok then return r end
                              local ok2, r2 = pcall(function() return dp_is_player_go(x) end)
                              return ok2 and r2 or false end
-- dp.player() is nil whenever there is no rig: before it loads, and for the
-- whole session on a phone whose title has no Phone Player (phones watch by
-- default). Nil-check it; put title-wide systems on the Game Manager.
dp.player      = function()   return dp_player() end
dp.head        = function()   return dp_head() end
dp.scope       = function(go) return dp_scope(go) end
dp.attraction  = function(go) return dp_attraction_scope(go) end
dp.attraction_root = function(go) return dp_attraction_root(go) end
dp.game_id     = function(go) return dp_game_id(go) end
-- Console/non-LuaBehaviour callers pass a GameObject explicitly. Each
-- LuaBehaviour gets a bound facade where these arguments are automatic.
dp.package     = function(go) return dp_package(go) end
dp.container   = function(go) return dp_container(go) end
dp.container_object = function(go) return dp_container_object(go) end
dp.game_manager = function(go) return dp_container(go) end
dp.game_manager_object = function(go) return dp_container_object(go) end
dp.button_press = function(go) return dp_button_press(go) end
dp.is_sequence = function(go) return dp_is_sequence(go) end
dp.is_adventure = function(go) return dp_is_adventure(go) end
dp.is_arena = function(go) return dp_is_arena(go) end
dp.current_level_index = function(go) return dp_current_level_index(go) end
dp.level_count = function(go, groupId) return dp_level_count(go, groupId or '') end
dp.groups = function(go) return dp_groups(go) end
dp.levels = function(go, groupId) return dp_levels(go, groupId or '') end
dp.level_slot = function(go, index, groupId) return dp_level_slot(go, index, groupId or '') end
dp.load_level  = function(go, slot, groupId) return dp_load_level(go, slot, groupId or '') end
dp.preload_level = function(go, slot, groupId) return dp_preload_level(go, slot, groupId or '') end
dp.level_ready = function(go, slot, groupId) return dp_level_ready(go, slot, groupId or '') end
dp.level_loading = function(go) return dp_level_loading(go) end
dp.level_error = function(go) return dp_level_error(go) end
dp.on_level_loaded = function(go, fn) return dp_on_level_loaded(go, fn) end
dp.off_level_loaded = function(go, fn) return dp_off_level_loaded(go, fn) end
-- Same frame the new level becomes visible (dp.on_level_loaded is one drawn
-- frame later in a Sequence, after the new level's floor is built).
dp.on_level_presented = function(go, fn) return dp_on_level_presented(go, fn) end
dp.off_level_presented = function(go, fn) return dp_off_level_presented(go, fn) end
dp.on_level_failed = function(go, fn) return dp_on_level_failed(go, fn) end
dp.off_level_failed = function(go, fn) return dp_off_level_failed(go, fn) end
dp.show_overlay = function(go, visible) dp_show_overlay(go, visible) end
dp.overlay_auto_visibility = function(go, enabled) dp_overlay_auto_visibility(go, enabled) end

-- Rect openings (RECT_EDGES_SPEC.md): where guests can walk into the rect
-- this attraction is seated on, per side ('front'/'back'/'right'/'left'),
-- in the attraction's own frame. Inside a script the go argument is
-- automatic (dp.rect_openings()). No park data = every side fully open.
dp.rect_openings = function(go) return dp_rect_openings(go) end
dp.rect_is_wall = function(go, side, t) return dp_rect_is_wall(go, side, t or 0) end
dp.rect_is_open = function(go, side, t) return dp_rect_is_open(go, side, t or 0) end
dp.rect_point = function(go, side, t) return dp_rect_point(go, side, t or 0) end

-- The enabled state the GAME intends for a Renderer / Collider / Behaviour
-- (Light, AudioSource, LuaBehaviour, Animator ...). The optimizer parks
-- far-away and still-loading content by switching it off, so reading
-- .enabled in start() can return the park's false; dp.enabled returns the
-- value the unpark will restore, dp.set_enabled writes it (a write while
-- parked lands at the unpark). Live objects: exactly .enabled. nil / false
-- for anything without an enabled flag. Older apps lack these: feature-test
-- with `if dp.enabled then ... end`.
dp.enabled = function(c)
    local r = dp_enabled(c)
    if r < 0 then return nil end
    return r == 1
end
dp.set_enabled = function(c, on) return dp_set_enabled(c, on and true or false) end

-- Keep a manager / always-on system out of DISTANCE culling (like an
-- OptimizedAFIgnore component): on ~= false exempts go and its subtree,
-- restoring anything parked; false hands it back. The load, build mode and
-- the operator pause still park it. Prefer the static `-- @optimizer-ignore`
-- comment in the script, which applies before the first park. Returns the
-- number of registered objects changed (0 = remembered until registration),
-- -1 = go is not an object. Not for props: every exempt object costs frame
-- time all session.
dp.optimizer_ignore = function(go, on) return dp_optimizer_ignore(go, on ~= false) end

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
    -- Never exponent notation (%g gave 1e-05): JSONObject cannot parse it.
    local s = string.format('%.10f', n)
    s = string.gsub(s, '0+$', '')
    s = string.gsub(s, '%.$', '')
    if s == '-0' or s == '' then s = '0' end
    return s
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

-- Park-local AND attraction-relative in one value. Receivers inside an
-- attraction read the $a* form (robust even if two headsets' park frames
-- disagree slightly); older apps and receivers outside an attraction read
-- the park-local one. That pair is the backwards compatibility.
local function __dp_tag2(tag, body, atag, abody)
    if abody == nil then return __dp_tag(tag, body) end
    return '{' .. __dpq .. tag .. __dpq .. ':' .. body .. ',' .. __dpq .. atag .. __dpq .. ':' .. abody .. '}'
end

local function __dp_q4(q)
    return string.format('[%.4f,%.4f,%.4f,%.4f]', q.x, q.y, q.z, q.w)
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
local function __dp_pose7(p, q)
    return string.format('[%.3f,%.3f,%.3f,%.4f,%.4f,%.4f,%.4f]', p.x, p.y, p.z, q.x, q.y, q.z, q.w)
end
local function __dp_pack_unity(v, kind, depth, frame)
    if kind == 'vec3' then
        return __dp_tag2('$p', __dp_v3(dp.to_park(v), '%.3f'),
            '$a', frame ~= nil and __dp_v3(frame:InverseTransformPoint(v), '%.3f') or nil)
    elseif kind == 'quat' then
        return __dp_tag2('$q', __dp_q4(dp.to_park_rot(v)),
            '$aq', frame ~= nil and __dp_q4(CS.UnityEngine.Quaternion.Inverse(frame.rotation) * v) or nil)
    elseif kind == 'transform' then
        local abody = nil
        if frame ~= nil then
            abody = __dp_pose7(frame:InverseTransformPoint(v.position),
                CS.UnityEngine.Quaternion.Inverse(frame.rotation) * v.rotation)
        end
        return __dp_tag2('$t', __dp_pose7(dp.to_park(v.position), dp.to_park_rot(v.rotation)), '$at', abody)
    elseif kind == 'gameobject' then
        return __dp_pack_unity(v.transform, 'transform', depth, frame)
    elseif kind == 'color' then
        return __dp_tag('$c', string.format('[%.3f,%.3f,%.3f,%.3f]', v.r, v.g, v.b, v.a))
    end
    return nil
end

__dp_pack_value = function(v, depth, frame)
    local tv = type(v)
    if tv == 'nil' then return 'null' end
    if tv == 'boolean' then return v and 'true' or 'false' end
    if tv == 'number' then return __dp_num(v) end
    if tv == 'string' then return __dp_esc(v) end
    if depth > 8 then return 'null' end
    if tv == 'userdata' or (tv == 'table' and getmetatable(v) ~= nil) then
        local kind = __dp_kind(v)
        if kind ~= nil then return __dp_pack_unity(v, kind, depth, frame) end
        if tv == 'userdata' then
            print('[dp.pack] cannot serialize a ' .. tostring(v) .. '; sent as null')
            return 'null'
        end
    end
    if tv == 'table' then
        local d = rawget(v, '$d')
        if d ~= nil then
            return __dp_tag2('$d', __dp_v3(dp.to_park_dir(d), '%.4f'),
                '$ad', frame ~= nil and __dp_v3(frame:InverseTransformDirection(d), '%.4f') or nil)
        end
        if __dp_is_array(v) then
            local parts = {}
            for i = 1, #v do parts[i] = __dp_pack_value(v[i], depth + 1, frame) end
            return '[' .. table.concat(parts, ',') .. ']'
        end
        local parts = {}
        for k, val in pairs(v) do
            parts[#parts + 1] = __dp_esc(tostring(k)) .. ':' .. __dp_pack_value(val, depth + 1, frame)
        end
        return '{' .. table.concat(parts, ',') .. '}'
    end
    return 'null'
end

-- A Vector3 that means a direction, not a point (no translation on the wire).
dp.dir = function(v) return { ['$d'] = v } end

-- Lua table -> wire JSON, park-local. net_send(kind, table) calls this for you,
-- with frame = the sending script's attraction (dp_net_frame), so points
-- ALSO travel attraction-relative ($a / $ad / $aq / $at beside $p / $d / $q / $t).
dp.pack = function(t, frame)
    if type(t) ~= 'table' then return '{}' end
    return __dp_pack_value(t, 0, frame)
end
-- Any single value (number, string, Vector3, table...) -> JSON fragment. SDK use.
dp.__packv = function(v, frame) return __dp_pack_value(v, 0, frame) end

local function __dp_vec(a) return CS.UnityEngine.Vector3(a[1] or 0, a[2] or 0, a[3] or 0) end
local function __dp_quat(a, o) o = o or 0 return CS.UnityEngine.Quaternion(a[o + 1] or 0, a[o + 2] or 0, a[o + 3] or 0, a[o + 4] or 1) end

local __dp_unpack_value
__dp_unpack_value = function(v, depth, frame)
    if type(v) ~= 'table' or depth > 8 then return v end
    if frame ~= nil then
        local a = rawget(v, '$a')
        if a ~= nil then return frame:TransformPoint(__dp_vec(a)) end
        local ad = rawget(v, '$ad')
        if ad ~= nil then return frame:TransformDirection(__dp_vec(ad)) end
        local aq = rawget(v, '$aq')
        if aq ~= nil then return frame.rotation * __dp_quat(aq) end
        local at = rawget(v, '$at')
        if at ~= nil then
            local pos = frame:TransformPoint(CS.UnityEngine.Vector3(at[1] or 0, at[2] or 0, at[3] or 0))
            local rot = frame.rotation * __dp_quat(at, 3)
            return { position = pos, rotation = rot, forward = rot * CS.UnityEngine.Vector3.forward }
        end
    end
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
        if type(val) == 'table' then v[k] = __dp_unpack_value(val, depth + 1, frame) end
    end
    return v
end

-- Parsed wire table -> Unity values in this headset's world. onmessage gets
-- this for free (frame = its attraction); call it yourself on
-- json_parse(raw).payload inside onnet: dp.unpack(t, dp_net_frame(self.gameObject)).
dp.unpack = function(t, frame) return __dp_unpack_value(t, 0, frame) end
dp.__unpackv = function(v, frame) return __dp_unpack_value(v, 0, frame) end

-- ── Players in the park ──────────────────────────────────
-- The SDK streams every headset's head and hands (PlayerPresence.cs),
-- park-local, on a fixed budget. No game sends player positions, ever.
-- What other players SEE of you is the RemoteRig subtree of your
-- Player.prefab, cloned for each peer (RemoteRig.cs); scripts inside it
-- boot on the clones with peer_id = that player's id.
--   dp.me()          -> my id (stable per headset)
--   dp.peers()       -> array (join order) of { id, game, head, left, right,
--                        hand, active_hand, left_tracked, right_tracked,
--                        seen, age, state, rig, name, synced, frame }
--                        head/left/right/hand: Transforms in THIS headset's
--                        world; state: the table they dp.set_state()d, Unity
--                        values restored; rig: their RemoteRig clone, or
--                        nil; name: their display name (the SDK floats it
--                        over their head by default — RemoteNameTag.cs);
--                        synced: false while their pose is not a place here
--                        (they or you have not localized, or you are in
--                        different locations) — rig and name hidden,
--                        head/hands parked 10 km below the park, so skip
--                        them for aiming, hits and proximity
--   dp.peer(id)      -> one of the above, or nil (you are not your own peer)
--   dp.set_state(t)  -> publish a small table to everyone (colour, team,
--                        score). Resent to late joiners; keep it small.
--   dp.on_peer_join(fn(id, peer), owner)  fires for everyone already here,
--                        then for each arrival. owner (self, a GameObject) is
--                        optional: the handler is dropped when it is destroyed.
--   dp.on_peer_leave(fn(id), owner)
--   dp.off_peer_join(fn) / dp.off_peer_leave(fn)
-- One shared pump runs every title's peer / gesture / net handlers, so one
-- title's error must never stop the rest (a dead pump = dead phone taps for
-- every title). Each sub-pump runs in pcall; failures print at most once per
-- 10 s per sub-pump.
__dp_pump_warn_at = __dp_pump_warn_at or {}
function __dp_pump_warn(name, err)
    local now = (os ~= nil and os.time ~= nil) and os.time() or 0
    local last = __dp_pump_warn_at[name]
    if last ~= nil and now - last < 10 then return end
    __dp_pump_warn_at[name] = now
    print('[DreamParkLuaPump] ' .. tostring(name) .. ' threw (pump keeps running): ' .. tostring(err))
end
local function __dp_peer_view(p)
    if p ~= nil and p.state ~= nil and dp.unpack ~= nil then
        local ok, v = pcall(dp.unpack, p.state)
        if ok then p.state = v else __dp_pump_warn('peer_view', v) end
    end
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

-- ── Device & touch (phones) ──────────────────────────────
--   dp.device()          -> 'phone' | 'headset' | 'editor' (this device)
--   dp.is_phone()        -> true on a phone (or the Editor simulating one)
--   dp.is_headset()      -> true on a headset, and in the Editor
--   dp.device_of(id)     -> 'phone' | 'headset' for another player, nil when
--                           unknown (older client); dp.device() for dp.me()
--   dp.on_tap(fn(t), owner)    tap anywhere on the phone screen
--   dp.on_touch(fn(t), owner)  finger down / up   (t.phase 'down' | 'up')
--   dp.on_drag(fn(t), owner)   t.phase 'begin' | 'move' | 'end'
--   dp.on_pinch(fn(t), owner)  t.scale: multiplicative since the last pinch
--     each returns a handle: h.off() or dp.off_tap(h) (off_* also accept fn).
--     owner (self / a GameObject) is optional — inside a LuaBehaviour it
--     defaults to the script, and the handler drops when it is destroyed.
--   t = { kind, phase, x, y, dx, dy, scale, fingerId,
--         position, normal, object, distance }   x/y: 0..1 from bottom-left;
--         position/normal/object/distance are nil when the tap hit nothing.
--   Object hooks: ontap(t) / ontouch(t) on a LuaBehaviour fire when the ray
--   hits it or a child; return true to stop the walk to its parents.
dp.device     = function()   return dp_device() end
dp.is_phone   = function()   return dp_is_phone() end
dp.is_headset = function()   return dp_is_headset() end
dp.device_of  = function(id) if id == nil then return nil end return dp_device_of(id) end

__dp_gesture_fns = __dp_gesture_fns or { tap = {}, touch = {}, drag = {}, pinch = {} }

local function __dp_gesture_on(kind)
    return function(fn, owner)
        if fn == nil then return nil end
        local list = __dp_gesture_fns[kind]
        local h = { fn = fn, owner = owner }
        h.off = function()
            for i = #list, 1, -1 do if list[i] == h then table.remove(list, i) end end
        end
        list[#list + 1] = h
        dp_ensure_pump()
        return h
    end
end
local function __dp_gesture_off(kind)
    return function(x)
        if x == nil then return end
        local list = __dp_gesture_fns[kind]
        for i = #list, 1, -1 do
            if list[i] == x or list[i].fn == x then table.remove(list, i) end
        end
    end
end
dp.on_tap    = __dp_gesture_on('tap')
dp.on_touch  = __dp_gesture_on('touch')
dp.on_drag   = __dp_gesture_on('drag')
dp.on_pinch  = __dp_gesture_on('pinch')
dp.off_tap   = __dp_gesture_off('tap')
dp.off_touch = __dp_gesture_off('touch')
dp.off_drag  = __dp_gesture_off('drag')
dp.off_pinch = __dp_gesture_off('pinch')

function __dp_pump_gestures()
    if dp_gesture_event_count() == 0 then return end
    local evs = dp_gesture_events()
    for _, list in pairs(__dp_gesture_fns) do __dp_prune_fns(list) end
    for i = 1, #evs do
        local e = evs[i]
        local list = __dp_gesture_fns[e.kind]
        if list ~= nil and #list > 0 then
            -- Snapshot: a handler may off() itself or register another.
            local snap = {}
            for j = 1, #list do snap[j] = list[j] end
            for j = 1, #snap do
                local ok, err = pcall(snap[j].fn, e)
                if not ok then print('[dp.on_' .. tostring(e.kind) .. '] handler threw: ' .. tostring(err)) end
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
    local ok, err = pcall(__dp_pump_connected)
    if not ok then __dp_pump_warn('pump_connected', err) end
    ok, err = pcall(__dp_pump_peers)
    if not ok then __dp_pump_warn('pump_peers', err) end
    ok, err = pcall(__dp_pump_gestures)
    if not ok then __dp_pump_warn('pump_gestures', err) end
    if __dp_pump_net ~= nil then
        ok, err = pcall(__dp_pump_net)
        if not ok then __dp_pump_warn('pump_net', err) end
    end
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
