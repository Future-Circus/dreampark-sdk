#if UNITY_EDITOR
using System;
using System.Reflection;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using XLua;

/// <summary>dp.light (DreamParkLuaLight.cs): provider pass-through, fallback, Editor simulation, request stamp.</summary>
public sealed class DreamParkLightTests
{
    static LuaEnv Env()
    {
        var env = LuaBehaviour.GetLuaEnv();
        typeof(DreamParkLuaAPI).GetMethod("RegisterLua", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        return env;
    }

    DreamParkRoomLight.ReadFn _savedProvider;
    float _savedAmbient;
    Vector3 _savedDir;

    [SetUp]
    public void Save()
    {
        _savedProvider = DreamParkRoomLight.Provider;
        _savedAmbient = DreamParkRoomLight.SimulatedAmbient;
        _savedDir = DreamParkRoomLight.SimulatedDirection;
        DreamParkRoomLight.SetSimulated(-1f, Vector3.zero);
        DreamParkRoomLight.ClearRequest();
    }

    [TearDown]
    public void Restore()
    {
        DreamParkRoomLight.Provider = _savedProvider;
        DreamParkRoomLight.SetSimulated(1f, _savedDir);              // the direction first,
        DreamParkRoomLight.SetSimulated(_savedAmbient, Vector3.zero); // then the saved on/off + ambient
    }

    static object[] ReadAll(LuaEnv env) => env.DoString(@"
local d = dp.light.direction()
return dp.light.ambient(), d.x, d.y, d.z, dp.light.confidence(), dp.light.live(), dp.light.simulated()
", "light.read");

    [Test]
    public void NoProviderIsTheFallback()
    {
        var env = Env();
        DreamParkRoomLight.Provider = null;
        object[] r = ReadAll(env);
        Vector3 sun = DreamParkRoomLight.SceneSunFrom();
        Assert.That(Convert.ToDouble(r[0]), Is.EqualTo(1.0), "ambient 1");
        Assert.That(new Vector3(Convert.ToSingle(r[1]), Convert.ToSingle(r[2]), Convert.ToSingle(r[3])),
            Is.EqualTo(sun).Using(Vec(1e-4f)), "the scene sun's reverse forward");
        Assert.That(Convert.ToDouble(r[4]), Is.EqualTo(0.0), "confidence 0");
        Assert.That((bool)r[5], Is.False, "not live");
        Assert.That((bool)r[6], Is.False, "not simulated");
    }

    [Test]
    public void ProviderValuesPassThroughClamped()
    {
        var env = Env();
        DreamParkRoomLight.Provider = (out DreamParkRoomLightReading x) =>
        {
            x = new DreamParkRoomLightReading { ambient = 0.3f, direction = new Vector3(0f, 2f, 2f), confidence = 0.8f };
            return true;
        };
        object[] r = ReadAll(env);
        Assert.That(Convert.ToDouble(r[0]), Is.EqualTo(0.3).Within(1e-6));
        Assert.That(Convert.ToDouble(r[1]), Is.EqualTo(0.0).Within(1e-6));
        Assert.That(Convert.ToDouble(r[2]), Is.EqualTo(Math.Sqrt(0.5)).Within(1e-5), "direction is normalised");
        Assert.That(Convert.ToDouble(r[3]), Is.EqualTo(Math.Sqrt(0.5)).Within(1e-5));
        Assert.That(Convert.ToDouble(r[4]), Is.EqualTo(0.8).Within(1e-6));
        Assert.That((bool)r[5], Is.True, "live");

        DreamParkRoomLight.Provider = (out DreamParkRoomLightReading x) =>
        {
            x = new DreamParkRoomLightReading { ambient = 3f, direction = Vector3.up, confidence = -1f };
            return true;
        };
        r = ReadAll(env);
        Assert.That(Convert.ToDouble(r[0]), Is.EqualTo(1.0), "ambient clamps to 1");
        Assert.That(Convert.ToDouble(r[4]), Is.EqualTo(0.0), "confidence clamps to 0");

        // A provider with no estimate, a broken reading, or one that throws = the fallback.
        DreamParkRoomLight.Provider = (out DreamParkRoomLightReading x) => { x = default; x.ambient = 0.1f; return false; };
        r = ReadAll(env);
        Assert.That((bool)r[5], Is.False); Assert.That(Convert.ToDouble(r[0]), Is.EqualTo(1.0));
        DreamParkRoomLight.Provider = (out DreamParkRoomLightReading x) => { x = default; x.ambient = 0.1f; return true; };
        r = ReadAll(env);
        Assert.That((bool)r[5], Is.False, "zero direction is not a reading");
        DreamParkRoomLight.Provider = (out DreamParkRoomLightReading x) => throw new InvalidOperationException("boom");
        r = ReadAll(env);
        Assert.That((bool)r[5], Is.False, "a throwing provider never reaches Lua");
    }

    [Test]
    public void SimulationOverridesTheProvider()
    {
        var env = Env();
        DreamParkRoomLight.Provider = (out DreamParkRoomLightReading x) =>
        {
            x = new DreamParkRoomLightReading { ambient = 0.9f, direction = Vector3.up, confidence = 0.2f };
            return true;
        };
        object[] s = env.DoString("return dp.light.debug_set(0.05, CS.UnityEngine.Vector3(1, 1, 0))", "light.sim");
        Assert.That((bool)s[0], Is.True, "debug_set works in the Editor");
        object[] r = ReadAll(env);
        Assert.That(Convert.ToDouble(r[0]), Is.EqualTo(0.05).Within(1e-6));
        Assert.That(Convert.ToDouble(r[1]), Is.EqualTo(Math.Sqrt(0.5)).Within(1e-5));
        Assert.That(Convert.ToDouble(r[2]), Is.EqualTo(Math.Sqrt(0.5)).Within(1e-5));
        Assert.That(Convert.ToDouble(r[4]), Is.EqualTo(1.0), "simulated confidence 1");
        Assert.That((bool)r[5], Is.True, "simulated counts as live");
        Assert.That((bool)r[6], Is.True);

        s = env.DoString("return dp.light.debug_set(0.6), dp.light.ambient(), dp.light.direction().x, dp.light.debug_set({0, 0, -1}), dp.light.direction().z, dp.light.debug_set('x')", "light.sim2");
        Assert.That((bool)s[0], Is.True);
        Assert.That(Convert.ToDouble(s[1]), Is.EqualTo(0.6).Within(1e-6));
        Assert.That(Convert.ToDouble(s[2]), Is.EqualTo(Math.Sqrt(0.5)).Within(1e-5), "no dir = keep the simulated one");
        Assert.That((bool)s[3], Is.False, "a table dir needs an ambient");
        Assert.That((bool)s[5], Is.False, "a non-number ambient is refused");

        s = env.DoString("return dp.light.debug_set(0.6, {0, 0, -1}), dp.light.direction().z", "light.sim3");
        Assert.That((bool)s[0], Is.True);
        Assert.That(Convert.ToDouble(s[1]), Is.EqualTo(-1.0).Within(1e-5), "{x, y, z} table dir");

        s = env.DoString("return dp.light.debug_set(nil), dp.light.simulated(), dp.light.ambient()", "light.sim.off");
        Assert.That((bool)s[0], Is.True);
        Assert.That((bool)s[1], Is.False, "nil = back to the real estimate");
        Assert.That(Convert.ToDouble(s[2]), Is.EqualTo(0.9).Within(1e-6), "the provider again");
    }

    [Test]
    public void ReadsStampTheRequestSimulatedDoesNot()
    {
        var env = Env();
        int started = 0;
        Action onStart = () => started++;
        DreamParkRoomLight.RequestStarted += onStart;
        try
        {
            env.DoString("return dp.light.simulated()", "light.req0");
            Assert.That(DreamParkRoomLight.Requested, Is.False, "simulated() asks for no measurement");
            Assert.That(started, Is.EqualTo(0));
            env.DoString("return dp.light.ambient(), dp.light.live(), dp.light.direction()", "light.req1");
            Assert.That(DreamParkRoomLight.Requested, Is.True, "a read wants the measurement");
            Assert.That(started, Is.EqualTo(1), "RequestStarted once per idle → asked edge");
            DreamParkRoomLight.ClearRequest();
            env.DoString("return dp.light.confidence()", "light.req2");
            Assert.That(started, Is.EqualTo(2));
        }
        finally { DreamParkRoomLight.RequestStarted -= onStart; }
    }

    static System.Collections.Generic.IEqualityComparer<Vector3> Vec(float eps) => new VecComparer(eps);

    sealed class VecComparer : System.Collections.Generic.IEqualityComparer<Vector3>
    {
        readonly float _eps;
        public VecComparer(float eps) { _eps = eps; }
        public bool Equals(Vector3 a, Vector3 b) => (a - b).sqrMagnitude <= _eps * _eps;
        public int GetHashCode(Vector3 v) => 0;
    }
}
#endif
