// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaTime.cs — dp.time: the DEVICE's own wall clock for Lua
//  (Oct 2026).
//
//      dp.time.now()      -> local time table (os.date('*t') field names,
//                            plus ms, utc_offset, unix)
//      dp.time.utc()      -> the same table in UTC
//      dp.time.unix()     -> seconds since 1970-01-01 UTC (float, ms precision)
//      dp.time.hour()     -> local hour as a float (19.5 = 7:30 pm)
//      dp.time.utc_offset() -> local offset from UTC, whole minutes
//      dp.time.format(fmt[, utc]) -> .NET custom pattern, InvariantCulture
//      dp.time.simulated() -> the Editor's simulated hour, or nil
//      dp.time.debug_set(hour | nil) -> Editor only (false on a device)
//
//  REPLACES, in shipped content: pcall(os.date, '*t') and pcall(os.time)
//  guesses. `os` IS reachable (core's LuaSecuritySandbox keeps os.time /
//  os.date / os.clock / os.difftime and drops the rest), but it has no
//  Editor override, so night content can only be checked at night. Every
//  value here goes through DreamParkTime, which the Editor can shift
//  (DreamPark → Simulated Local Time).
//
//  NOT synced between headsets: each device reads its own clock and time
//  zone. Fine for cosmetics (day/night, seasons, daily resets); anything
//  every headset must agree on uses dp.clock().
//
//  Same bridge shape as DreamParkLuaNet: flat internal dp_time_* delegates
//  returning primitives / LuaTable, so nothing enters the generated XLua
//  wrappers. Registered from DreamParkLuaAPI.RegisterLua.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Globalization;
using UnityEngine;
using XLua;

namespace DreamPark
{
    /// <summary>
    /// The device's wall clock as content sees it. In the Editor (Park Simulator
    /// included) it can be shifted to a simulated local hour; a player build
    /// always reads the real clock.
    /// </summary>
    public static class DreamParkTime
    {
        internal static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ── Time zone (cached; IL2CPP/Android/iOS safe) ───────────────
        // TimeZoneInfo.Local is resolved once. Any failure falls back to the
        // offset DateTimeOffset reports, then to UTC (offset 0), and never throws.
        static TimeZoneInfo _tz;
        static bool _tzTried;
        static TimeSpan? _fixedOffset;

        static TimeZoneInfo Zone()
        {
            if (_tzTried) return _tz;
            _tzTried = true;
            try { _tz = TimeZoneInfo.Local; }
            catch (Exception e)
            {
                _tz = null;
                Debug.LogWarning("[dp.time] TimeZoneInfo.Local failed (" + e.Message + "); using a fixed offset.");
            }
            return _tz;
        }

        static TimeSpan FixedOffset()
        {
            if (_fixedOffset.HasValue) return _fixedOffset.Value;
            TimeSpan o = TimeSpan.Zero;
            try { o = DateTimeOffset.Now.Offset; } catch { o = TimeSpan.Zero; }
            _fixedOffset = o;
            return o;
        }

        /// <summary>Local offset from UTC at <paramref name="utc"/>, whole minutes.</summary>
        public static TimeSpan OffsetAt(DateTime utc)
        {
            TimeZoneInfo tz = Zone();
            TimeSpan o;
            if (tz != null)
            {
                try { o = tz.GetUtcOffset(utc); }
                catch { _tz = null; o = FixedOffset(); }
            }
            else o = FixedOffset();
            return TimeSpan.FromMinutes(Math.Round(o.TotalMinutes));
        }

        /// <summary>Is <paramref name="utc"/> in daylight-saving time locally? False when unknown.</summary>
        public static bool IsDstAt(DateTime utc)
        {
            TimeZoneInfo tz = Zone();
            if (tz == null) return false;
            try { return tz.IsDaylightSavingTime(utc); }
            catch { return false; }
        }

        // ── Editor simulation ────────────────────────────────────────
        static double _simHour = -1;
        static TimeSpan _shift = TimeSpan.Zero;

        /// <summary>The simulated local hour [0, 24), or -1 for the real clock.</summary>
        public static double SimulatedHour => _simHour;
        public static bool Simulating => _simHour >= 0;

        /// <summary>
        /// Editor only: make the local clock read <paramref name="hour"/> now (it keeps
        /// running from there). hour &lt; 0 (or NaN) = back to the real clock. Returns
        /// false and changes nothing in a player build.
        /// </summary>
        public static bool SetSimulatedHour(double hour)
        {
#if UNITY_EDITOR
            if (double.IsNaN(hour) || double.IsInfinity(hour) || hour < 0)
            {
                _simHour = -1;
                _shift = TimeSpan.Zero;
                return true;
            }
            _simHour = hour % 24.0;
            Reanchor();
            return true;
#else
            return false;
#endif
        }

        /// <summary>Editor: restart the simulated clock at <see cref="SimulatedHour"/> (on Play).</summary>
        public static void Reanchor()
        {
            if (_simHour < 0) { _shift = TimeSpan.Zero; return; }
            DateTime realUtc = DateTime.UtcNow;
            DateTime realLocal = realUtc + OffsetAt(realUtc);
            _shift = realLocal.Date.AddHours(_simHour) - realLocal;
        }

        // ── Reads (every one goes through the shift) ─────────────────
        public static DateTime UtcNow => _shift == TimeSpan.Zero ? DateTime.UtcNow : DateTime.UtcNow + _shift;

        /// <summary>Local wall time with its offset.</summary>
        public static DateTimeOffset LocalNow
        {
            get
            {
                DateTime utc = UtcNow;
                TimeSpan off = OffsetAt(utc);
                return new DateTimeOffset(DateTime.SpecifyKind(utc + off, DateTimeKind.Unspecified), off);
            }
        }

        public static double Unix => Math.Round((UtcNow - Epoch).TotalSeconds * 1000.0) / 1000.0;

        public static double LocalHour
        {
            get
            {
                DateTime t = LocalNow.DateTime;
                return t.Hour + t.Minute / 60.0 + (t.Second + t.Millisecond / 1000.0) / 3600.0;
            }
        }
    }

    internal static class DreamParkLuaTime
    {
        const int MaxFormat = 64;

        static LuaTable Now(bool utc)
        {
            var env = LuaBehaviour.GetLuaEnv();
            var t = env.NewTable();
            DateTime u = DreamParkTime.UtcNow;
            TimeSpan off = utc ? TimeSpan.Zero : DreamParkTime.OffsetAt(u);
            DateTime d = u + off;
            t.Set("year", d.Year);
            t.Set("month", d.Month);
            t.Set("day", d.Day);
            t.Set("hour", d.Hour);
            t.Set("min", d.Minute);
            t.Set("sec", d.Second);
            t.Set("ms", d.Millisecond);
            t.Set("wday", (int)d.DayOfWeek + 1);           // 1 = Sunday, as os.date
            t.Set("yday", d.DayOfYear);
            t.Set("isdst", !utc && DreamParkTime.IsDstAt(u));
            t.Set("utc_offset", (int)Math.Round(off.TotalMinutes));
            t.Set("unix", Math.Round((u - DreamParkTime.Epoch).TotalSeconds * 1000.0) / 1000.0);
            return t;
        }

        static double Unix() => DreamParkTime.Unix;
        static double Hour() => DreamParkTime.LocalHour;
        static int Offset() => (int)Math.Round(DreamParkTime.OffsetAt(DreamParkTime.UtcNow).TotalMinutes);
        static double Simulated() => DreamParkTime.SimulatedHour;
        static bool DebugSet(double hour) => DreamParkTime.SetSimulatedHour(hour);

        /// <summary>
        /// .NET custom date pattern, InvariantCulture (English names, no locale
        /// surprises). Allowed: the specifier letters y M d H h m s f F t z K g, a
        /// literal T, the separators space : - / . , _ ( ) and 'quoted text'.
        /// Anything else, or a pattern over 64 chars, returns nil.
        /// </summary>
        static string Format(string fmt, bool utc)
        {
            if (string.IsNullOrEmpty(fmt)) fmt = "yyyy-MM-dd HH:mm:ss";
            if (fmt.Length > MaxFormat || !Allowed(fmt)) return null;
            try
            {
                DateTimeOffset t = utc ? new DateTimeOffset(DreamParkTime.UtcNow, TimeSpan.Zero) : DreamParkTime.LocalNow;
                return t.ToString(fmt, CultureInfo.InvariantCulture);
            }
            catch (FormatException) { return null; }
        }

        static bool Allowed(string fmt)
        {
            bool quoted = false;
            foreach (char c in fmt)
            {
                if (c == '\'') { quoted = !quoted; continue; }
                if (c == '\\' || c == '"' || c == '%' || c < ' ') return false;
                if (quoted) continue;
                if ("yMdHhmsfFtzKgT :-/.,_()".IndexOf(c) < 0) return false;
            }
            return !quoted;
        }

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_time_now",       new Func<bool, LuaTable>(Now));
            env.Global.Set("dp_time_unix",      new Func<double>(Unix));
            env.Global.Set("dp_time_hour",      new Func<double>(Hour));
            env.Global.Set("dp_time_offset",    new Func<int>(Offset));
            env.Global.Set("dp_time_format",    new Func<string, bool, string>(Format));
            env.Global.Set("dp_time_simulated", new Func<double>(Simulated));
            env.Global.Set("dp_time_debug_set", new Func<double, bool>(DebugSet));

            // No double quotes inside this verbatim block.
            env.DoString(@"
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
", "dp.time.bootstrap");
        }
    }
}
