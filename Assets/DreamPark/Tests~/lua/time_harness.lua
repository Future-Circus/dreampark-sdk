-- dp.time harness: the Lua half of DreamParkLuaTime.cs ("dp.time.bootstrap")
-- against a fake C# side built on this machine's os.date. Checks the field
-- names (os.date('*t') + ms / utc_offset / unix), utc(), hour() as a float,
-- format's argument handling, simulated() / debug_set(nil) mapping, and that
-- the bootstrap merges into an existing dp table without clobbering it.
--   lua time_harness.lua dp_time_bootstrap.lua

__BOOTSTRAP = (arg and arg[1]) or 'dp_time_bootstrap.lua'

local env = setmetatable({}, { __index = _G })
env.print = function() end
env.dp = { clock = function() return 42 end }          -- an earlier bootstrap's dp

-- ── Fake C# side (what DreamParkLuaTime.cs binds) ────────────────────
local fake = { shift = 0, sim = -1, device = false, last_fmt = nil }
local function now_s() return os.time() + fake.shift end
local function offset_min()
    local t = now_s()
    local l, u = os.date('*t', t), os.date('!*t', t)
    u.isdst = l.isdst
    return math.floor(os.difftime(os.time(l), os.time(u)) / 60 + 0.5)
end
env.dp_time_now = function(utc)
    local t = now_s()
    local d = os.date(utc and '!*t' or '*t', t)
    return { year = d.year, month = d.month, day = d.day, hour = d.hour, min = d.min, sec = d.sec,
             wday = d.wday, yday = d.yday, isdst = (not utc) and d.isdst or false, ms = 0,
             utc_offset = utc and 0 or offset_min(), unix = t + 0.0 }
end
env.dp_time_unix = function() return now_s() + 0.0 end
env.dp_time_hour = function() local d = os.date('*t', now_s()) return d.hour + d.min / 60 + d.sec / 3600 end
env.dp_time_offset = function() return offset_min() end
env.dp_time_format = function(fmt, utc) fake.last_fmt = { fmt, utc } if fmt == 'bad{' then return nil end return 'F:' .. fmt end
env.dp_time_simulated = function() return fake.sim end
env.dp_time_debug_set = function(h)
    if fake.device then return false end
    if h < 0 then fake.sim, fake.shift = -1, 0 return true end
    local d = os.date('*t')
    fake.sim = h % 24
    fake.shift = math.floor((fake.sim - (d.hour + d.min / 60 + d.sec / 3600)) * 3600)
    return true
end

assert(loadfile(__BOOTSTRAP, 't', env))()
local dp = env.dp

local checks, fails = 0, 0
local function check(label, cond, extra)
    checks = checks + 1
    if cond then print('  PASS  ' .. label) else fails = fails + 1; print('  FAIL  ' .. label .. '   ' .. tostring(extra or '')) end
end

print('=== bootstrap ===')
check('dp.time exists', type(dp.time) == 'table')
check('an existing dp table is kept (dp = dp or {})', dp.clock ~= nil and dp.clock() == 42)

print('\n=== now / utc ===')
local t, ref = dp.time.now(), os.date('*t')
for _, k in ipairs({ 'year', 'month', 'day', 'hour', 'min', 'sec', 'wday', 'yday', 'isdst', 'ms', 'utc_offset', 'unix' }) do
    check('now().' .. k .. ' present', t[k] ~= nil)
end
check('now() matches os.date(*t) hour/day', t.hour == ref.hour and t.day == ref.day, t.hour .. ' vs ' .. ref.hour)
local u = dp.time.utc()
check('utc() has offset 0 and no DST', u.utc_offset == 0 and u.isdst == false)
check('utc() hour matches os.date(!*t)', u.hour == os.date('!*t').hour)
check('unix() ~= os.time()', math.abs(dp.time.unix() - os.time()) <= 1)
check('utc_offset() == now().utc_offset', dp.time.utc_offset() == t.utc_offset)
local h = dp.time.hour()
check('hour() is a float in [0, 24)', math.type(h) == 'float' and h >= 0 and h < 24)
check('floor(hour()) == now().hour', math.floor(h) == dp.time.now().hour)

print('\n=== format ===')
check('format passes the pattern', dp.time.format('HH:mm') == 'F:HH:mm' and fake.last_fmt[2] == false)
check('format(fmt, true) asks for UTC', dp.time.format('HH', true) == 'F:HH' and fake.last_fmt[2] == true)
check('format() = default pattern (empty string to C#)', dp.time.format() == 'F:' and fake.last_fmt[1] == '')
check('a rejected pattern is nil', dp.time.format('bad{') == nil)
fake.last_fmt = nil
check('a non-string pattern is nil and never reaches C#', dp.time.format(42) == nil and fake.last_fmt == nil)

print('\n=== simulated / debug_set ===')
check('simulated() is nil on the real clock', dp.time.simulated() == nil)
check('debug_set(22.5) works in the Editor', dp.time.debug_set(22.5) == true)
check('simulated() reports it', dp.time.simulated() == 22.5)
check('hour() follows it', math.abs(dp.time.hour() - 22.5) < 0.02, dp.time.hour())
check('now().hour follows it', dp.time.now().hour == 22)
check('debug_set(\'x\') is refused', dp.time.debug_set('x') == false and dp.time.simulated() == 22.5)
check('debug_set(nil) = back to the device clock', dp.time.debug_set(nil) == true and dp.time.simulated() == nil)
check('hour() is real again', math.floor(dp.time.hour()) == os.date('*t').hour)
fake.device = true
check('debug_set is false on a device', dp.time.debug_set(3) == false and dp.time.simulated() == nil)

print(string.format('\n%d/%d checks passed', checks - fails, checks))
os.exit(fails == 0 and 0 or 1)
