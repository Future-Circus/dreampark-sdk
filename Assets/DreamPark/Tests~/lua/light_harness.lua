-- dp.light harness: the Lua half of DreamParkLuaLight.cs ("dp.light.bootstrap")
-- against a fake C# side that mirrors DreamParkRoomLight (provider, fallback,
-- Editor simulation, request stamp). Checks ambient / direction / confidence /
-- live / simulated, debug_set's argument handling (Vector3, {x, y, z}, nil,
-- refused values, false on a device), that every read except simulated() stamps
-- the request, that the bootstrap merges into an existing dp, and runs the two
-- documented examples (hidden object with hysteresis, sundial) against it.
--   lua light_harness.lua light_bootstrap.lua

__BOOTSTRAP = (arg and arg[1]) or 'light_bootstrap.lua'

local env = setmetatable({}, { __index = _G })
env.print = function() end
env.dp = { clock = function() return 42 end }          -- an earlier bootstrap's dp

-- ── Fake Unity: just enough Vector3 ──────────────────────────────────
local V3 = {}
V3.__index = V3
local function vec(x, y, z) return setmetatable({ x = x or 0, y = y or 0, z = z or 0 }, V3) end
function V3.__unm(a) return vec(-a.x, -a.y, -a.z) end
function V3.__mul(a, s) return vec(a.x * s, a.y * s, a.z * s) end
function V3.__add(a, b) return vec(a.x + b.x, a.y + b.y, a.z + b.z) end
function V3.__sub(a, b) return vec(a.x - b.x, a.y - b.y, a.z - b.z) end
function V3:mag() return math.sqrt(self.x * self.x + self.y * self.y + self.z * self.z) end
env.CS = { UnityEngine = { Vector3 = setmetatable({}, { __call = function(_, x, y, z) return vec(x, y, z) end }) } }

-- ── Fake C# side (what DreamParkLuaLight.cs binds) ───────────────────
local function norm(x, y, z)
    local m = math.sqrt(x * x + y * y + z * z)
    if m < 1e-4 then return nil end
    return x / m, y / m, z / m
end
local fake = {
    provider = nil,            -- nil | { live, ambient, x, y, z, confidence }
    sun = { 0.5, 0.70710678, 0.5 },
    sim = -1, sdir = { 0, 1, 0 },
    device = false,
    asks = 0, starts = 0, last_ask = -1e9, now = 0,
}
local function read()
    if fake.sim >= 0 then return true, fake.sim, fake.sdir[1], fake.sdir[2], fake.sdir[3], 1 end
    local p = fake.provider
    if p and p.live then
        local x, y, z = norm(p.x, p.y, p.z)
        if x then
            local a = math.max(0, math.min(1, p.ambient))
            return true, a, x, y, z, math.max(0, math.min(1, p.confidence))
        end
    end
    return false, 1, fake.sun[1], fake.sun[2], fake.sun[3], 0
end
env.dp_light_get = function(what)
    if what == 6 then return fake.sim >= 0 and 1.0 or 0.0 end
    if fake.now - fake.last_ask >= 10 then fake.starts = fake.starts + 1 end
    fake.last_ask = fake.now
    fake.asks = fake.asks + 1
    local live, a, x, y, z, c = read()
    if what == 0 then return a end
    if what == 1 then return x end
    if what == 2 then return y end
    if what == 3 then return z end
    if what == 4 then return c end
    if what == 5 then return live and 1.0 or 0.0 end
    return 0.0
end
env.dp_light_debug_set = function(a, x, y, z)
    assert(math.type(a) ~= nil and math.type(x) ~= nil, 'debug_set always passes numbers to C#')
    if fake.device then return false end
    if a ~= a or a < 0 then fake.sim = -1 return true end
    fake.sim = math.min(1, a)
    local nx, ny, nz = norm(x, y, z)
    if nx then fake.sdir = { nx, ny, nz } end
    return true
end

assert(loadfile(__BOOTSTRAP, 't', env))()
local dp = env.dp

local checks, fails = 0, 0
local function check(label, cond, extra)
    checks = checks + 1
    if cond then print('  PASS  ' .. label) else fails = fails + 1; print('  FAIL  ' .. label .. '   ' .. tostring(extra or '')) end
end
local function near(a, b, e) return math.abs(a - b) <= (e or 1e-6) end

print('=== bootstrap ===')
check('dp.light exists', type(dp.light) == 'table')
check('an existing dp table is kept (dp = dp or {})', dp.clock ~= nil and dp.clock() == 42)
for _, k in ipairs({ 'ambient', 'direction', 'confidence', 'live', 'simulated', 'debug_set' }) do
    check('dp.light.' .. k .. ' is a function', type(dp.light[k]) == 'function')
end

print('\n=== fallback (no estimate) ===')
check('live() is false', dp.light.live() == false)
check('ambient() is 1', dp.light.ambient() == 1)
local d = dp.light.direction()
check('direction() is a Vector3 = the scene sun\'s reverse forward',
      getmetatable(d) == V3 and near(d.x, 0.5) and near(d.y, 0.70710678) and near(d.z, 0.5))
check('confidence() is 0', dp.light.confidence() == 0)
check('simulated() is false', dp.light.simulated() == false)

print('\n=== live estimate ===')
fake.provider = { live = true, ambient = 0.12, x = 0, y = 3, z = 4, confidence = 0.7 }
check('live() is true', dp.light.live() == true)
check('ambient() passes through', near(dp.light.ambient(), 0.12))
d = dp.light.direction()
check('direction() is unit, toward the light', near(d:mag(), 1) and near(d.y, 0.6) and near(d.z, 0.8))
check('confidence() passes through', near(dp.light.confidence(), 0.7))
check('a shadow falls along -direction', near((-d).z, -0.8))
fake.provider = { live = true, ambient = 0.2, x = 0, y = 0, z = 0, confidence = 1 }
check('a zero direction is not a reading: fallback', dp.light.live() == false and dp.light.ambient() == 1)
fake.provider = { live = false, ambient = 0.2, x = 0, y = 1, z = 0, confidence = 1 }
check('provider not live: fallback', dp.light.live() == false and dp.light.confidence() == 0)

print('\n=== request stamp (measure on demand) ===')
fake.provider = nil
fake.asks, fake.starts, fake.last_ask, fake.now = 0, 0, -1e9, 100
dp.light.simulated()
check('simulated() asks for no measurement', fake.asks == 0 and fake.starts == 0)
dp.light.ambient()
check('the first read starts the measurement', fake.asks == 1 and fake.starts == 1)
fake.now = 105; dp.light.live(); dp.light.direction(); dp.light.confidence()
check('reads within 10 s keep it (no new start)', fake.starts == 1 and fake.asks == 6, fake.asks)
fake.now = 116; dp.light.ambient()
check('a read after a 10 s gap starts it again', fake.starts == 2)

print('\n=== debug_set ===')
check('debug_set(0.05, Vector3) works in the Editor', dp.light.debug_set(0.05, env.CS.UnityEngine.Vector3(1, 1, 0)) == true)
check('simulated() reports it', dp.light.simulated() == true)
check('live() is true while simulated', dp.light.live() == true)
check('ambient() follows it', near(dp.light.ambient(), 0.05))
d = dp.light.direction()
check('direction() follows it (normalised)', near(d.x, 0.70710678, 1e-6) and near(d.y, 0.70710678, 1e-6) and near(d.z, 0))
check('confidence() is 1 while simulated', dp.light.confidence() == 1)
check('debug_set(a) keeps the simulated direction', dp.light.debug_set(0.6) == true and near(dp.light.direction().x, 0.70710678))
check('debug_set(a, {x, y, z}) takes a table', dp.light.debug_set(0.6, { 0, 0, -1 }) == true and near(dp.light.direction().z, -1))
check('debug_set(a, {x=, y=, z=}) takes a keyed table', dp.light.debug_set(0.6, { x = 1, y = 0, z = 0 }) == true and near(dp.light.direction().x, 1))
check('debug_set(\'0.3\') coerces a numeric string', dp.light.debug_set('0.3') == true and near(dp.light.ambient(), 0.3))
check('debug_set(-2) clamps to 0', dp.light.debug_set(-2) == true and dp.light.ambient() == 0)
check('debug_set(\'x\') is refused', dp.light.debug_set('x') == false and dp.light.ambient() == 0)
check('debug_set(0/0) (NaN) is refused', dp.light.debug_set(0 / 0) == false)
check('debug_set(a, \'up\') is refused', dp.light.debug_set(0.5, 'up') == false and dp.light.ambient() == 0)
check('debug_set(a, {1, 2}) is refused', dp.light.debug_set(0.5, { 1, 2 }) == false)
check('debug_set(nil) = back to the real estimate', dp.light.debug_set(nil) == true and dp.light.simulated() == false
      and dp.light.live() == false and dp.light.ambient() == 1)
fake.device = true
check('debug_set is false on a device', dp.light.debug_set(0.1) == false and dp.light.simulated() == false)
fake.device = false

print('\n=== docs example: hidden object (hysteresis 0.15 on / 0.25 off) ===')
do
    local shown = false
    local function tick()
        if not dp.light.live() then return shown end
        local a = dp.light.ambient()
        if not shown and a < 0.15 then shown = true
        elseif shown and a > 0.25 then shown = false end
        return shown
    end
    fake.provider = { live = true, ambient = 0.8, x = 0, y = 1, z = 0, confidence = 0.5 }
    check('bright room: hidden', tick() == false)
    fake.provider.ambient = 0.18
    check('0.18 (between thresholds) from bright: still hidden', tick() == false)
    fake.provider.ambient = 0.05
    check('lights off: appears', tick() == true)
    fake.provider.ambient = 0.2
    check('0.2 (between thresholds) from dark: stays (no flicker)', tick() == true)
    fake.provider.ambient = 0.6
    check('lights on: hides again', tick() == false)
    fake.provider.live = false
    check('no estimate: keeps its state', tick() == false)
end

print('\n=== docs example: sundial (gnomon shadow) ===')
do
    -- A vertical gnomon of height h casts its tip shadow at -dir * (h / dir.y), flattened onto the dial.
    local function shadow_tip(h)
        if dp.light.confidence() < 0.3 then return nil end
        local l = dp.light.direction()
        if l.y < 0.05 then return nil end
        local s = l * (-h / l.y)
        return env.CS.UnityEngine.Vector3(s.x, 0, s.z)
    end
    fake.provider = { live = true, ambient = 0.7, x = 1, y = 1, z = 0, confidence = 0.9 }
    local tip = shadow_tip(0.2)
    check('light from +X at 45 deg: shadow 0.2 m toward -X', tip and near(tip.x, -0.2, 1e-6) and near(tip.z, 0, 1e-6))
    fake.provider.confidence = 0.1
    check('low confidence: no shadow', shadow_tip(0.2) == nil)
end

print(string.format('\n%d/%d checks passed', checks - fails, checks))
os.exit(fails == 0 and 0 or 1)
