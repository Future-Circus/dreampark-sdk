-- Typed wire harness: dp.pack on headset A -> JSON -> json_parse -> dp.unpack
-- on headset B, where the two headsets place the park at different world
-- poses. Every Unity value must land at the physically identical spot.
--   lua5.4 typed_wire_harness.lua

-- ── Fake Unity: Vector3, Quaternion (real maths), Color, Transform ─────
__BOOTSTRAP = (arg and arg[1]) or 'dp_bootstrap.lua'   -- extracted from DreamParkLuaAPI.cs (see run_tests.sh)
local V3mt, Qmt, Cmt, Tmt = {}, {}, {}, {}
local function V3(x, y, z) return setmetatable({ x = x or 0, y = y or 0, z = z or 0 }, V3mt) end
V3mt.__add = function(a, b) return V3(a.x + b.x, a.y + b.y, a.z + b.z) end
V3mt.__sub = function(a, b) return V3(a.x - b.x, a.y - b.y, a.z - b.z) end
V3mt.__mul = function(a, b) if type(a) == 'number' then return V3(a * b.x, a * b.y, a * b.z) end return V3(a.x * b, a.y * b, a.z * b) end
V3mt.__index = function(t, k)
    if k == 'magnitude' then return math.sqrt(t.x * t.x + t.y * t.y + t.z * t.z) end
    if k == 'normalized' then local m = math.sqrt(t.x * t.x + t.y * t.y + t.z * t.z); return m > 0 and V3(t.x / m, t.y / m, t.z / m) or V3() end
    return nil
end
V3mt.__tostring = function(t) return string.format('(%.3f, %.3f, %.3f)', t.x, t.y, t.z) end

local function Q(x, y, z, w) return setmetatable({ x = x, y = y, z = z, w = w }, Qmt) end
local function qmul(a, b)
    return Q(a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
             a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
             a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
             a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z)
end
local function qrot(q, v)  -- rotate vector by unit quaternion
    local p = Q(v.x, v.y, v.z, 0)
    local r = qmul(qmul(q, p), Q(-q.x, -q.y, -q.z, q.w))
    return V3(r.x, r.y, r.z)
end
Qmt.__mul = function(a, b)
    if getmetatable(b) == V3mt then return qrot(a, b) end
    return qmul(a, b)
end
Qmt.__index = function(t, k)
    if k == 'eulerAngles' then
        local y = math.deg(math.atan(2 * (t.w * t.y + t.x * t.z), 1 - 2 * (t.y * t.y + t.z * t.z)))
        return { x = 0, y = y % 360, z = 0 }
    end
    return nil
end
local function Qyaw(deg) local r = math.rad(deg) / 2; return Q(0, math.sin(r), 0, math.cos(r)) end
local Quaternion = setmetatable({
    identity = Q(0, 0, 0, 1),
    Inverse = function(q) return Q(-q.x, -q.y, -q.z, q.w) end,
    Euler = function(x, y, z) return Qyaw(y) end,
    Angle = function(a, b) local d = math.abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w); return math.deg(2 * math.acos(math.min(1, d))) end,
}, { __call = function(_, x, y, z, w) return Q(x, y, z, w) end })

local function Color(r, g, b, a) return setmetatable({ r = r, g = g, b = b, a = a or 1 }, Cmt) end
Cmt.__index = function() return nil end

-- Transform with a world pose (position + yaw); no hierarchy needed here.
local function T(pos, yaw)
    local t = setmetatable({ _p = pos, _yaw = yaw }, Tmt)
    return t
end
Tmt.__index = function(t, k)
    if k == 'position' then return t._p end
    if k == 'rotation' then return Qyaw(t._yaw) end
    if k == 'forward' then return qrot(Qyaw(t._yaw), V3(0, 0, 1)) end
    if k == 'TransformPoint' then return function(self, p) return self._p + qrot(Qyaw(self._yaw), p) end end
    if k == 'InverseTransformPoint' then return function(self, w) return qrot(Quaternion.Inverse(Qyaw(self._yaw)), w - self._p) end end
    if k == 'TransformDirection' then return function(self, d) return qrot(Qyaw(self._yaw), d) end end
    if k == 'InverseTransformDirection' then return function(self, d) return qrot(Quaternion.Inverse(Qyaw(self._yaw)), d) end end
    return nil
end
local Vector3 = setmetatable({ forward = V3(0, 0, 1), up = V3(0, 1, 0), zero = V3() },
    { __call = function(_, x, y, z) return V3(x, y, z) end })

-- ── Minimal JSON parser (same as the two-headset harness) ─────────────
local function json_parse(s)
    local pos = 1
    local function ws() while true do local c = s:sub(pos, pos); if c == ' ' or c == '\n' or c == '\t' or c == '\r' then pos = pos + 1 else break end end end
    local parse_value
    local function parse_string()
        assert(s:sub(pos, pos) == '"', 'string expected at ' .. pos); pos = pos + 1
        local out = {}
        while true do
            local c = s:sub(pos, pos)
            if c == '"' then pos = pos + 1; break end
            if c == '\\' then
                pos = pos + 1; c = s:sub(pos, pos)
                if c == 'n' then c = '\n' elseif c == 't' then c = '\t' elseif c == 'r' then c = '\r'
                elseif c == 'u' then c = string.char(tonumber(s:sub(pos + 1, pos + 4), 16)); pos = pos + 4 end
            end
            out[#out + 1] = c; pos = pos + 1
        end
        return table.concat(out)
    end
    function parse_value()
        ws()
        local c = s:sub(pos, pos)
        if c == '{' then
            pos = pos + 1; local t = {}
            ws(); if s:sub(pos, pos) == '}' then pos = pos + 1; return t end
            while true do
                ws(); local k = parse_string(); ws(); assert(s:sub(pos, pos) == ':'); pos = pos + 1
                t[k] = parse_value(); ws()
                local d = s:sub(pos, pos); pos = pos + 1
                if d == '}' then return t end
                assert(d == ',', 'bad object at ' .. pos)
            end
        elseif c == '[' then
            pos = pos + 1; local t = {}
            ws(); if s:sub(pos, pos) == ']' then pos = pos + 1; return t end
            while true do
                t[#t + 1] = parse_value(); ws()
                local d = s:sub(pos, pos); pos = pos + 1
                if d == ']' then return t end
                assert(d == ',', 'bad array at ' .. pos)
            end
        elseif c == '"' then return parse_string()
        else
            local num = s:match('^-?%d+%.?%d*[eE]?[-+]?%d*', pos)
            if num and #num > 0 then pos = pos + #num; return tonumber(num) end
            if s:sub(pos, pos + 3) == 'true' then pos = pos + 4; return true end
            if s:sub(pos, pos + 4) == 'false' then pos = pos + 5; return false end
            if s:sub(pos, pos + 3) == 'null' then pos = pos + 4; return nil end
            error('json: unexpected at ' .. pos .. ': ' .. s:sub(pos, pos + 10))
        end
    end
    return parse_value()
end

-- ── Boot the SDK Lua for two headsets ─────────────────────────────────
local function boot(park)
    local env = setmetatable({}, { __index = _G })
    env.CS = { UnityEngine = { Vector3 = Vector3, Quaternion = Quaternion, Color = setmetatable({}, { __call = function(_, r, g, b, a) return Color(r, g, b, a) end }) } }
    env.dp_park = function() return park end
    env.print = function() end
    for _, f in ipairs { __BOOTSTRAP } do
        local chunk = assert(loadfile(f, 't', env))
        chunk()
    end
    return env.dp
end

local parkA = T(V3(0, 0, 0), 0)
local parkB = T(V3(3.2, 0, -4.1), 180)
local dpA, dpB = boot(parkA), boot(parkB)
local dpNone = boot(nil)

local fails, checks = 0, 0
local function check(label, cond, extra)
    checks = checks + 1
    if cond then print('  PASS  ' .. label) else fails = fails + 1; print('  FAIL  ' .. label .. '   ' .. tostring(extra or '')) end
end
local function close(a, b, eps) eps = eps or 0.005; return math.abs(a.x - b.x) < eps and math.abs(a.y - b.y) < eps and math.abs(a.z - b.z) < eps end

-- A physical point/direction/rotation expressed in A's world and in B's world.
local physLocal = V3(1.0, 1.6, 2.0)                     -- park-local
local pA = parkA:TransformPoint(physLocal)
local pB = parkB:TransformPoint(physLocal)
local dirLocal = V3(1, 0, 0)
local dA, dB = parkA:TransformDirection(dirLocal), parkB:TransformDirection(dirLocal)
local rotLocal = Qyaw(90)
local rA, rB = parkA.rotation * rotLocal, parkB.rotation * rotLocal
local poseA = T(pA, 90)                                  -- a transform in A's world facing park +X

print('=== pack on A ===')
local msg = {
    u = 'abc-123', n = 1.5, i = 3, big = 123456789, neg = -2.25, b = true, f = false,
    s = 'he"llo\\ wor\nld\t' .. string.char(7),
    pos = pA, dir = dpA.dir(dA), rot = rA, pose = poseA, col = Color(1, 0.5, 0.25, 1),
    list = { 1, 2, 3 }, nested = { p = pA, deeper = { name = 'x', arr = { pA, dpA.dir(dA) } } },
    holes = { [1] = 'a', [3] = 'c' },
}
local json = dpA.pack(msg)
check('pack produced JSON', type(json) == 'string' and #json > 20, json)
check('point went out park-local', json:find('"$p":[1.000,1.600,2.000]', 1, true) ~= nil, json)
check('direction went out park-local', json:find('"$d":[1.0000,0.0000,0.0000]', 1, true) ~= nil, json)
check('integers stay integers', json:find('"i":3', 1, true) ~= nil and json:find('"big":123456789', 1, true) ~= nil, json)
check('float kept', json:find('"n":1.5', 1, true) ~= nil and json:find('"neg":-2.25', 1, true) ~= nil, json)
check('strings escaped', json:find('"s":"he\\"llo\\\\ wor\\nld\\t\\u0007"', 1, true) ~= nil, json)
check('booleans', json:find('"b":true', 1, true) ~= nil and json:find('"f":false', 1, true) ~= nil, json)
check('array', json:find('"list":[1,2,3]', 1, true) ~= nil, json)
check('sparse table is an object, not an array', json:find('"holes":{', 1, true) ~= nil, json)

print('\n=== unpack on B ===')
local ok, parsed = pcall(json_parse, json)
check('JSON parses', ok and parsed ~= nil, tostring(parsed))
local got = dpB.unpack(parsed)
check('point lands at the physical spot in B', close(got.pos, pB), tostring(got.pos) .. ' vs ' .. tostring(pB))
check('direction lands correctly in B', close(got.dir, dB), tostring(got.dir))
check('rotation lands correctly in B', close(got.rot * V3(0, 0, 1), rB * V3(0, 0, 1), 0.001) and close(got.rot * V3(1, 0, 0), rB * V3(1, 0, 0), 0.001), tostring(got.rot * V3(0, 0, 1)))
check('pose position', close(got.pose.position, pB))
check('pose forward = physical +X in B', close(got.pose.forward, dB, 0.01), tostring(got.pose.forward))
check('colour survives', got.col.r == 1 and math.abs(got.col.g - 0.5) < 1e-3 and math.abs(got.col.b - 0.25) < 1e-3)
check('nested point converted', close(got.nested.p, pB))
check('array of unity values converted', close(got.nested.deeper.arr[1], pB) and close(got.nested.deeper.arr[2], dB))
check('plain fields untouched', got.u == 'abc-123' and got.n == 1.5 and got.i == 3 and got.b == true and got.f == false)
check('string round-trips', got.s == msg.s, got.s)
check('list round-trips', got.list[1] == 1 and got.list[3] == 3)

print('\n=== identity without a park (Editor) ===')
local j2 = dpNone.pack({ p = pA, d = dpNone.dir(dA) })
local g2 = dpNone.unpack(json_parse(j2))
check('no park: point unchanged', close(g2.p, pA))
check('no park: direction unchanged', close(g2.d, dA))

print('\n=== A->A is the identity too ===')
local g3 = dpA.unpack(json_parse(json))
check('A unpacks its own packet to the same world point', close(g3.pos, pA))
check('A unpacks its own rotation', close(g3.rot * V3(0, 0, 1), rA * V3(0, 0, 1), 0.001))

print(string.format('\n%d/%d checks passed', checks - fails, checks))
os.exit(fails == 0 and 0 or 1)
