-- dp gestures + device harness: the Lua half of DreamParkGestures /
-- DreamParkDevice against a fake C# side. Checks dp.device / is_phone /
-- is_headset / device_of passthrough, the on_tap/on_touch/on_drag/on_pinch
-- pump (routing by kind, handles, off_* by handle and by fn, owner pruning,
-- throwing handlers contained, events drained even with no listener).
--   lua gestures_harness.lua dp_bootstrap.lua

__BOOTSTRAP = (arg and arg[1]) or 'dp_bootstrap.lua'

local env = setmetatable({}, { __index = _G })
env.CS = { UnityEngine = {
    Vector3 = setmetatable({}, { __call = function(_, x, y, z) return { x = x, y = y, z = z } end }),
    Quaternion = setmetatable({ Inverse = function(q) return q end }, { __call = function(_, x, y, z, w) return { x = x, y = y, z = z, w = w } end }),
    Color = setmetatable({}, { __call = function(_, r, g, b, a) return { r = r, g = g, b = b, a = a } end }),
} }
env.dp_park = function() return nil end
env.print = function() end

-- ── Fake C# side ──────────────────────────────────────────────────────
local fake = { device = 'phone', peers = { aaaa0001 = 'headset' }, queue = {}, pumps = 0 }
env.dp_device = function() return fake.device end
env.dp_is_phone = function() return fake.device == 'phone' end
env.dp_is_headset = function() return fake.device ~= 'phone' end
env.dp_device_of = function(id) if id == 'me000001' then return fake.device end return fake.peers[id] end
env.dp_gesture_event_count = function() return #fake.queue end
env.dp_gesture_events = function() local q = fake.queue; fake.queue = {}; return q end
env.dp_ensure_pump = function() fake.pumps = fake.pumps + 1 end
-- presence stubs the pump touches
env.dp_peer_event_count = function() return 0 end
env.dp_connected = function() return false end

local function submit(kind, phase, extra)
    local t = { kind = kind, phase = phase, x = 0.5, y = 0.5, dx = 0, dy = 0, scale = 1, fingerId = 0 }
    for k, v in pairs(extra or {}) do t[k] = v end
    fake.queue[#fake.queue + 1] = t
end

assert(loadfile(__BOOTSTRAP, 't', env))()
local dp = env.dp
local function pump() env.__dp_pump_globals() end

local fails, checks = 0, 0
local function check(label, cond, extra)
    checks = checks + 1
    if cond then print('  PASS  ' .. label) else fails = fails + 1; print('  FAIL  ' .. label .. '   ' .. tostring(extra or '')) end
end

print('=== device ===')
check('dp.device() passes through', dp.device() == 'phone')
check('dp.is_phone()', dp.is_phone() == true)
check('dp.is_headset() false on a phone', dp.is_headset() == false)
check('dp.device_of(peer)', dp.device_of('aaaa0001') == 'headset')
check('dp.device_of(unknown) is nil', dp.device_of('nobody') == nil)
check('dp.device_of(nil) is nil', dp.device_of(nil) == nil)
check('dp.device_of(me) is dp.device()', dp.device_of('me000001') == 'phone')

print('\n=== routing by kind ===')
local taps, touches, drags, pinches = {}, {}, {}, {}
local h_tap = dp.on_tap(function(t) taps[#taps + 1] = t end)
dp.on_touch(function(t) touches[#touches + 1] = t.phase end)
local function ondrag(t) drags[#drags + 1] = t.phase end
dp.on_drag(ondrag)
dp.on_pinch(function(t) pinches[#pinches + 1] = t.scale end)
check('on_tap returns a handle with off()', type(h_tap) == 'table' and type(h_tap.off) == 'function')
check('registering starts the pump', fake.pumps > 0)

submit('touch', 'down'); submit('drag', 'begin'); submit('drag', 'move'); submit('drag', 'end')
submit('touch', 'up'); submit('tap', 'tap', { object = 'Cube', position = { x = 1, y = 2, z = 3 } })
submit('pinch', 'change', { scale = 1.2 })
pump()
check('tap delivered once', #taps == 1)
check('tap carries hit object/position', taps[1] and taps[1].object == 'Cube' and taps[1].position.z == 3)
check('touch phases in order', table.concat(touches, ',') == 'down,up', table.concat(touches, ','))
check('drag phases in order', table.concat(drags, ',') == 'begin,move,end', table.concat(drags, ','))
check('pinch scale', pinches[1] == 1.2)
check('queue drained', #fake.queue == 0)

print('\n=== off_ by handle / by fn / h.off() ===')
h_tap.off()
submit('tap', 'tap'); pump()
check('h.off() removed the tap handler', #taps == 1)
dp.off_drag(ondrag)
submit('drag', 'begin'); pump()
check('off_drag(fn) removed the drag handler', #drags == 3)
local n = 0
local h2 = dp.on_tap(function() n = n + 1 end)
dp.off_tap(h2)
submit('tap', 'tap'); pump()
check('off_tap(handle) removed the handler', n == 0)

print('\n=== owner pruning, throwing handlers, no listener ===')
local owner = { dead = false, Equals = function(self, other) return other == nil and self.dead end }
local owned = 0
dp.on_tap(function() owned = owned + 1 end, owner)
dp.on_tap(function() error('boom') end)
local after = 0
dp.on_tap(function() after = after + 1 end)
submit('tap', 'tap'); pump()
check('owned handler fires while owner lives', owned == 1)
check('a throwing handler does not stop the others', after == 1)
owner.dead = true
submit('tap', 'tap'); pump()
check('handler dropped once its owner is destroyed', owned == 1)
submit('touch', 'down'); submit('touch', 'up')
dp.off_touch(nil)
pump()
check('events with no listener are still drained', #fake.queue == 0)

print(string.format('\n%d/%d checks passed', checks - fails, checks))
os.exit(fails == 0 and 0 or 1)
