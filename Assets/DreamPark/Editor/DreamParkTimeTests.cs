#if UNITY_EDITOR
using System;
using System.Reflection;
using DreamPark;
using NUnit.Framework;
using XLua;

/// <summary>dp.time (DreamParkLuaTime.cs) against the system clock, plus the Editor override.</summary>
public sealed class DreamParkTimeTests
{
    static LuaEnv Env()
    {
        var env = LuaBehaviour.GetLuaEnv();
        typeof(DreamParkLuaAPI).GetMethod("RegisterLua", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        return env;
    }

    double _saved;

    [SetUp] public void Save() => _saved = DreamParkTime.SimulatedHour;
    [TearDown] public void Restore() => DreamParkTime.SetSimulatedHour(_saved);

    [Test]
    public void NowMatchesTheSystemClock()
    {
        var env = Env();
        DreamParkTime.SetSimulatedHour(-1);
        DateTime before = DateTime.Now;
        object[] r = env.DoString(@"
local t = dp.time.now()
local u = dp.time.utc()
return t.year, t.month, t.day, t.hour, t.min, t.wday, t.yday, t.utc_offset, t.unix,
       dp.time.unix(), dp.time.hour(), u.hour, u.utc_offset, math.type(t.year), dp.time.utc_offset(),
       os.date('*t').hour
", "time.test");
        DateTime after = DateTime.Now;
        // Skip the one-in-many-hours case where the call straddled a minute boundary.
        if (before.Minute != after.Minute) Assert.Inconclusive("straddled a minute");

        Assert.That(Convert.ToInt32(r[0]), Is.EqualTo(before.Year));
        Assert.That(Convert.ToInt32(r[1]), Is.EqualTo(before.Month));
        Assert.That(Convert.ToInt32(r[2]), Is.EqualTo(before.Day));
        Assert.That(Convert.ToInt32(r[3]), Is.EqualTo(before.Hour), "local hour");
        Assert.That(Convert.ToInt32(r[4]), Is.EqualTo(before.Minute));
        Assert.That(Convert.ToInt32(r[5]), Is.EqualTo((int)before.DayOfWeek + 1), "wday: 1 = Sunday");
        Assert.That(Convert.ToInt32(r[6]), Is.EqualTo(before.DayOfYear));
        Assert.That(Convert.ToInt32(r[7]), Is.EqualTo((int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes)));
        double unixNow = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        Assert.That(Convert.ToDouble(r[8]), Is.EqualTo(unixNow).Within(2.0));
        Assert.That(Convert.ToDouble(r[9]), Is.EqualTo(unixNow).Within(2.0));
        double h = Convert.ToDouble(r[10]);
        Assert.That(Math.Floor(h), Is.EqualTo(before.Hour), "dp.time.hour() is the local hour as a float");
        Assert.That(Convert.ToInt32(r[11]), Is.EqualTo(DateTime.UtcNow.Hour), "utc()");
        Assert.That(Convert.ToInt32(r[12]), Is.EqualTo(0));
        Assert.That((string)r[13], Is.EqualTo("integer"), "calendar fields are Lua integers");
        Assert.That(Convert.ToInt32(r[14]), Is.EqualTo(Convert.ToInt32(r[7])));
        Assert.That(Convert.ToInt32(r[15]), Is.EqualTo(before.Hour), "agrees with os.date('*t')");
    }

    [Test]
    public void SimulatedHourShiftsEveryRead()
    {
        var env = Env();
        object[] r = env.DoString(@"
local ok = dp.time.debug_set(22.5)
return ok, dp.time.hour(), dp.time.now().hour, dp.time.now().min, dp.time.simulated(), dp.time.format('HH:mm')
", "time.sim");
        Assert.That((bool)r[0], Is.True, "debug_set works in the Editor");
        Assert.That(Convert.ToDouble(r[1]), Is.EqualTo(22.5).Within(0.01));
        Assert.That(Convert.ToInt32(r[2]), Is.EqualTo(22));
        Assert.That(Convert.ToInt32(r[3]), Is.EqualTo(30));
        Assert.That(Convert.ToDouble(r[4]), Is.EqualTo(22.5).Within(1e-6));
        Assert.That((string)r[5], Is.EqualTo("22:30"));

        r = env.DoString("dp.time.debug_set(nil) return dp.time.simulated(), dp.time.now().hour", "time.sim.off");
        Assert.That(r[0], Is.Null, "nil = back to the device clock");
        Assert.That(Convert.ToInt32(r[1]), Is.EqualTo(DateTime.Now.Hour).Or.EqualTo(DateTime.Now.AddMinutes(-1).Hour));
    }

    [Test]
    public void FormatIsInvariantAndWhitelisted()
    {
        var env = Env();
        DreamParkTime.SetSimulatedHour(9.25);
        object[] r = env.DoString(@"
return dp.time.format('HH:mm'), dp.time.format('ddd'), dp.time.format('yyyy-MM-dd'),
       dp.time.format('HH:mm:ss') ~= nil, dp.time.format('{0}'), dp.time.format(string.rep('y', 65)),
       dp.time.format('h:mm tt'), dp.time.format(), dp.time.format(42), dp.time.format([['Day' d]])
", "time.fmt");
        DateTimeOffset now = DreamParkTime.LocalNow;
        Assert.That((string)r[0], Is.EqualTo("09:15"));
        Assert.That((string)r[1], Is.EqualTo(now.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)), "English day name");
        Assert.That((string)r[2], Is.EqualTo(now.ToString("yyyy-MM-dd")));
        Assert.That((bool)r[3], Is.True);
        Assert.That(r[4], Is.Null, "braces are not in the whitelist");
        Assert.That(r[5], Is.Null, "over 64 chars");
        Assert.That((string)r[6], Is.EqualTo("9:15 AM"));
        StringAssert.StartsWith(now.ToString("yyyy-MM-dd") + " 09:15", (string)r[7], "default pattern");
        Assert.That(r[8], Is.Null, "non-string pattern");
        Assert.That((string)r[9], Is.EqualTo("Day " + now.Day), "quoted literal text");
    }
}
#endif
