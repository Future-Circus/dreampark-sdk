
dp = dp or {}
dp.time = dp.time or {}
local T = dp.time

-- ── Device time ──────────────────────────────────────────
--   dp.time.now()  -> { year, month, day, hour, min, sec, wday (1 = Sunday),
--                       yday, isdst, ms, utc_offset (minutes), unix (float) }
--                     LOCAL time, same field names as os.date('*t')
--   dp.time.utc()  -> the same table in UTC (utc_offset 0, isdst false)
--   dp.time.unix() -> seconds since 1970-01-01 UTC, float, ms precision
--   dp.time.hour() -> local hour as a float: 19.5 = 7:30 pm (day/night)
--   dp.time.utc_offset() -> local offset from UTC in whole minutes
--   dp.time.format(fmt[, utc]) -> .NET pattern ('HH:mm', 'ddd d MMM',
--                     'yyyy-MM-dd'), InvariantCulture; nil for a bad pattern
--   dp.time.simulated() -> the Editor's simulated hour, nil = real clock
--   dp.time.debug_set(hour | nil) -> Editor only: make the clock read
--                     `hour` now (it keeps running); nil = real clock.
--                     Returns false on a device.
--   This is the DEVICE's own clock and time zone: NOT synced between
--   headsets. Use it for cosmetics (day/night, seasons, daily resets);
--   anything every headset must agree on uses dp.clock().
T.now        = function() return dp_time_now(false) end
T.utc        = function() return dp_time_now(true) end
T.unix       = function() return dp_time_unix() end
T.hour       = function() return dp_time_hour() end
T.utc_offset = function() return dp_time_offset() end
T.format     = function(fmt, utc)
    if fmt ~= nil and type(fmt) ~= 'string' then return nil end
    return dp_time_format(fmt or '', utc == true)
end
T.simulated  = function()
    local h = dp_time_simulated()
    if h == nil or h < 0 then return nil end
    return h
end
T.debug_set  = function(hour)
    if hour == nil then return dp_time_debug_set(-1) end
    local h = tonumber(hour)
    if h == nil then return false end
    return dp_time_debug_set(h)
end
