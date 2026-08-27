-- dp.peers harness: the Lua half of PlayerPresence (dp_peers.lua) against a
-- fake C# side. Checks the view/unpack path, the join/leave pump, the
-- fire-for-existing-then-arrivals contract (never twice), owner pruning,
-- and off_ removal.
--   lua5.4 peers_harness.lua

-- ── Fake Unity values (no park: every conversion is identity) ─────────
__BOOTSTRAP = (arg and arg[1]) or 'dp_bootstrap.lua'   -- extracted from DreamParkLuaAPI.cs (see run_tests.sh)
local V3mt = {}
V3mt.__index = function() return nil end
local function V3(x, y, z) return setmetatable({ x = x or 0, y = y or 0, z = z or 0 }, V3mt) end

local env = setmetatable({}, { __index = _G })
env.CS = { UnityEngine = {
    Vector3 = setmetatable({}, { __call = function(_, x, y, z) return V3(x, y, z) end }),
    Quaternion = setmetatable({ Inverse = function(q) return q end }, { __call = function(_, x, y, z, w) return { x = x, y = y, z = z, w = w } end }),
    Color = setmetatable({}, { __call = function(_, r, g, b, a) return { r = r, g = g, b = b, a = a } end }),
} }
env.dp_park = function() return nil end
env.print = function() end

-- ── Fake PlayerPresence (what DreamParkLuaAPI's dp_* bindings would see) ──
local presence = { peers = {}, order = {}, events = {}, pumps = 0, my = 'me000001', sent_state = nil }
local function view(p)
    -- RemotePlayer.LuaView: one table per peer, refreshed in place
    p.view = p.view or { id = p.id, head = p.head, left = p.left, right = p.right }
    local v = p.view
    v.game = p.game; v.hand = p.right; v.active_hand = 'r'
    v.left_tracked = true; v.right_tracked = true; v.seen = 0; v.age = 0
    if v.state == nil or p.state_json ~= p.view_state_json then
        v.state = p.state_table            -- JsonParseToLuaTable(stateJson): WIRE form
        p.view_state_json = p.state_json
    end
    v.rig = p.rig
    return v
end
function presence.join(id, game)
    local p = { id = id, game = game or 'LaserTag', head = { name = 'Head' }, left = {}, right = {}, state_json = '{}', state_table = {} }
    presence.peers[id] = p
    presence.order[#presence.order + 1] = id
    presence.events[#presence.events + 1] = { kind = 'join', id = id, peer = p }
    return p
end
function presence.leave(id)
    presence.peers[id] = nil
    for i = #presence.order, 1, -1 do if presence.order[i] == id then table.remove(presence.order, i) end end
    presence.events[#presence.events + 1] = { kind = 'leave', id = id }
end
function presence.set_state(id, state_json, state_table)
    local p = presence.peers[id]; p.state_json = state_json; p.state_table = state_table
end

env.dp_me = function() return presence.my end
env.dp_peers = function()
    local arr = {}
    for _, id in ipairs(presence.order) do arr[#arr + 1] = view(presence.peers[id]) end
    return arr
end
env.dp_peer = function(id) local p = presence.peers[id]; return p and view(p) or nil end
env.dp_set_state = function(json) presence.sent_state = json end
env.dp_peer_event_count = function() return #presence.events end
env.dp_peer_events = function()
    local evs = presence.events; presence.events = {}
    for _, e in ipairs(evs) do if e.peer then e.peer = view(e.peer) end end
    return evs
end
env.dp_ensure_pump = function() presence.pumps = presence.pumps + 1 end

for _, f in ipairs { __BOOTSTRAP } do
    local chunk = assert(loadfile(f, 't', env))
    chunk()
end
local dp = env.dp
local function pump() env.__dp_pump_peers() end

local fails, checks = 0, 0
local function check(label, cond, extra)
    checks = checks + 1
    if cond then print('  PASS  ' .. label) else fails = fails + 1; print('  FAIL  ' .. label .. '   ' .. tostring(extra or '')) end
end

print('=== identity / empty park ===')
check('dp.me() is the presence id', dp.me() == 'me000001')
check('dp.peers() empty', #dp.peers() == 0)
check('dp.peer(nil) is nil', dp.peer(nil) == nil)
check('dp.peer(unknown) is nil', dp.peer('nobody') == nil)

print('\n=== set_state packs through dp.pack ===')
dp.set_state({ team = 'red', score = 3, tint = V3(1, 0, 0) })
check('state sent as JSON', type(presence.sent_state) == 'string' and presence.sent_state:find('"team":"red"', 1, true) ~= nil, presence.sent_state)
check('Vector3 in state is a park-local point on the wire', presence.sent_state:find('"$p":[1.000,0.000,0.000]', 1, true) ~= nil, presence.sent_state)
dp.set_state(nil)
check('set_state(nil) sends {}', presence.sent_state == '{}', presence.sent_state)

print('\n=== peers view + state unpack ===')
presence.join('aaaa0001')
presence.set_state('aaaa0001', '{"team":"red","tint":{"$p":[1,0,0]}}', { team = 'red', tint = { ['$p'] = { 1, 0, 0 } } })
local list = dp.peers()
check('one peer', #list == 1 and list[1].id == 'aaaa0001')
check('state.team passes through', list[1].state.team == 'red')
check('state Vector3 restored to a Unity value', getmetatable(list[1].state.tint) == V3mt and list[1].state.tint.x == 1, tostring(list[1].state.tint))
check('same view table on the next call (no per-frame rebuild)', dp.peers()[1] == list[1])
check('unpack is idempotent on the cached state', dp.peers()[1].state.tint == list[1].state.tint)
check('dp.peer(id) is the same view', dp.peer('aaaa0001') == list[1])
check('head transform exposed', list[1].head.name == 'Head')
presence.set_state('aaaa0001', '{"team":"blue"}', { team = 'blue' })
check('state change is picked up', dp.peer('aaaa0001').state.team == 'blue')

print('\n=== on_peer_join: existing first, then arrivals, never twice ===')
local joins, leaves = {}, {}
local function onjoin(id, p) joins[#joins + 1] = id; assert(p.id == id, 'peer view passed') end
local function onleave(id) leaves[#leaves + 1] = id end
dp.on_peer_join(onjoin)
dp.on_peer_leave(onleave)
check('pump requested', presence.pumps >= 1)
check('fired immediately for the peer already here', #joins == 1 and joins[1] == 'aaaa0001', table.concat(joins, ','))
-- The join event for aaaa0001 is still queued (nobody pumped yet): must NOT fire again.
pump()
check('queued join for a peer already delivered is suppressed', #joins == 1, table.concat(joins, ','))
presence.join('bbbb0002', 'MultiplayerLand')
pump()
check('arrival fires once', #joins == 2 and joins[2] == 'bbbb0002', table.concat(joins, ','))
pump()
check('idle pump fires nothing', #joins == 2)
presence.leave('bbbb0002')
pump()
check('leave fires', #leaves == 1 and leaves[1] == 'bbbb0002')
presence.join('bbbb0002')
pump()
check('a peer that left and came back fires join again', #joins == 3 and joins[3] == 'bbbb0002', table.concat(joins, ','))

print('\n=== handler errors are contained ===')
dp.on_peer_join(function() error('boom') end)
presence.join('cccc0003')
pump()
check('a throwing handler does not stop the others', joins[#joins] == 'cccc0003')

print('\n=== owner pruning + off_ ===')
local owner = { dead = false, Equals = function(self, other) return other == nil and self.dead end }
local owned = 0
local function onjoin_owned() owned = owned + 1 end
dp.on_peer_join(onjoin_owned, owner)
check('owned handler fired for existing peers', owned == 3, owned)
owner.dead = true
presence.join('dddd0004')
pump()
check('handler whose owner was destroyed is dropped before dispatch', owned == 3, owned)
check('unowned handler still fires', joins[#joins] == 'dddd0004')
dp.off_peer_join(onjoin)
dp.off_peer_leave(onleave)
presence.join('eeee0005'); presence.leave('eeee0005')
pump()
check('off_peer_join removed the handler', joins[#joins] == 'dddd0004')
check('off_peer_leave removed the handler', #leaves == 1)

print(string.format('\n%d/%d checks passed', checks - fails, checks))
os.exit(fails == 0 and 0 or 1)
