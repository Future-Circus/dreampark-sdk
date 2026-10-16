// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaLight.cs — dp.light: the ROOM's real light for Lua
//  (Oct 2026).
//
//      dp.light.ambient()    -> 0..1 how bright the real room is (0 = pitch
//                               dark, 1 = a normal bright room or brighter)
//      dp.light.direction()  -> Vector3, unit, world: points TOWARD where the
//                               light comes from (a shadow falls along -dir)
//      dp.light.confidence() -> 0..1 how sure the direction is
//      dp.light.live()       -> true while a real estimate is flowing; false =
//                               fallback values (ambient 1, direction = the
//                               scene sun's reverse forward, confidence 0)
//      dp.light.simulated()  -> Editor: is DreamPark → Simulated Room Light on?
//      dp.light.debug_set(ambient[, dir]) -> Editor only (false on a device)
//
//  The values come from the headset's / phone's camera (core's REAL LIGHT
//  estimator) through DreamParkRoomLight.Provider. The SDK alone has no
//  camera: the Editor menu supplies values, otherwise the fallback.
//
//  Measure on demand: every read stamps DreamParkRoomLight.LastAskedAt;
//  core keeps the camera estimate running while Lua asked in the last
//  RequestHoldSeconds, even when the REAL LIGHT scene lighting is off.
//
//  NOT synced: each device reads its OWN room. A multiplayer puzzle decides
//  on one device (the leader) and sends the result.
//
//  Same bridge shape as DreamParkLuaTime: flat internal dp_light_* delegates
//  taking/returning primitives, so nothing enters the generated XLua
//  wrappers. Registered from DreamParkLuaAPI.RegisterLua.
// ─────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;
using XLua;

namespace DreamPark
{
    /// <summary>One reading of the room's light (see <see cref="DreamParkRoomLight"/>).</summary>
    public struct DreamParkRoomLightReading
    {
        /// 0 = pitch dark, 1 = a normal bright room or brighter (realistic, before any artistic grade).
        public float ambient;
        /// Unit vector, world space, pointing TOWARD the light.
        public Vector3 direction;
        /// 0..1 how sure <see cref="direction"/> is.
        public float confidence;
    }

    /// <summary>
    /// The room's real light as content sees it. Core fills <see cref="Provider"/> from its camera
    /// estimate; the Editor can simulate a room (DreamPark → Simulated Room Light); otherwise the
    /// fallback (ambient 1, the scene sun's direction, confidence 0, not live).
    /// </summary>
    public static class DreamParkRoomLight
    {
        /// <summary>Fills <paramref name="reading"/> and returns true while a real estimate is flowing.</summary>
        public delegate bool ReadFn(out DreamParkRoomLightReading reading);

        /// <summary>Set by core (RealLightLuaBridge). Null in the SDK: no camera estimate.</summary>
        public static ReadFn Provider;

        /// <summary>A read keeps the measurement wanted for this long.</summary>
        public const double RequestHoldSeconds = 10.0;

        /// <summary>Time.realtimeSinceStartupAsDouble of the last read from Lua; -∞ = never.</summary>
        public static double LastAskedAt { get; private set; } = double.NegativeInfinity;

        /// <summary>Did content read the light in the last <see cref="RequestHoldSeconds"/>?</summary>
        public static bool Requested => Time.realtimeSinceStartupAsDouble - LastAskedAt < RequestHoldSeconds;

        /// <summary>Raised on the first read after an idle stretch (core starts measuring).</summary>
        public static event Action RequestStarted;

        /// <summary>Stamp a read (main thread).</summary>
        public static void NoteAsked()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            bool was = now - LastAskedAt < RequestHoldSeconds;
            LastAskedAt = now;
            if (was) return;
            try { RequestStarted?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>Forget the last read (tests).</summary>
        public static void ClearRequest() => LastAskedAt = double.NegativeInfinity;

        // ── Editor simulation ────────────────────────────────────────
        static float _simAmbient = -1f;
        static Vector3 _simDirection = Vector3.up;

        public static bool Simulating => _simAmbient >= 0f;
        /// <summary>The simulated ambient 0..1, or -1 = not simulating.</summary>
        public static float SimulatedAmbient => _simAmbient;
        public static Vector3 SimulatedDirection => _simDirection;

        /// <summary>
        /// Editor only: dp.light reads <paramref name="ambient"/> (clamped 0..1) and
        /// <paramref name="direction"/> (toward the light; zero = keep the current one), live, confidence 1.
        /// ambient &lt; 0 (or NaN) = back to the real estimate. Returns false and changes nothing in a player.
        /// </summary>
        public static bool SetSimulated(float ambient, Vector3 direction)
        {
#if UNITY_EDITOR
            if (float.IsNaN(ambient) || ambient < 0f) { _simAmbient = -1f; return true; }
            _simAmbient = Mathf.Clamp01(ambient);
            if (IsFinite(direction) && direction.sqrMagnitude > 1e-8f) _simDirection = direction.normalized;
            return true;
#else
            return false;
#endif
        }

        // ── Reads ────────────────────────────────────────────────────
        /// <summary>
        /// The current reading. Returns true when it is live (a real estimate or the Editor simulation);
        /// false = the fallback (ambient 1, the scene sun's reverse forward, confidence 0).
        /// </summary>
        public static bool Read(out DreamParkRoomLightReading r)
        {
            if (Simulating)
            {
                r.ambient = _simAmbient;
                r.direction = _simDirection;
                r.confidence = 1f;
                return true;
            }
            var p = Provider;
            if (p != null)
            {
                bool live = false;
                try { live = p(out r); }
                catch (Exception e) { r = default; LogProviderError(e); }
                if (live && Sane(ref r)) return true;
            }
            r.ambient = 1f;
            r.direction = SceneSunFrom();
            r.confidence = 0f;
            return false;
        }

        static bool Sane(ref DreamParkRoomLightReading r)
        {
            if (float.IsNaN(r.ambient) || !IsFinite(r.direction) || r.direction.sqrMagnitude < 1e-8f) return false;
            r.ambient = Mathf.Clamp01(r.ambient);
            r.direction = r.direction.normalized;
            r.confidence = float.IsNaN(r.confidence) ? 0f : Mathf.Clamp01(r.confidence);
            return true;
        }

        static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        static double _lastErrorLog = double.NegativeInfinity;
        static void LogProviderError(Exception e)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastErrorLog < 10.0) return;
            _lastErrorLog = now;
            Debug.LogWarning("[dp.light] provider failed: " + e.Message);
        }

        // ── Fallback: the scene sun ──────────────────────────────────
        static Light _sun;
        static float _nextSunScan;

        /// <summary>-forward of the scene's directional light (RenderSettings.sun first); straight up if none.</summary>
        public static Vector3 SceneSunFrom()
        {
            Light s = RenderSettings.sun;
            if (!IsSun(s))
            {
                s = _sun;
                if (!IsSun(s) && Time.unscaledTime >= _nextSunScan)
                {
                    _nextSunScan = Time.unscaledTime + 2f;
                    s = null;
                    foreach (Light l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                        if (IsSun(l) && (s == null || l.intensity > s.intensity)) s = l;
                    _sun = s;
                }
            }
            if (!IsSun(s)) return Vector3.up;
            return -(s.transform.rotation * Vector3.forward);
        }

        static bool IsSun(Light l) => l != null && l.type == LightType.Directional && l.isActiveAndEnabled;
    }

    internal static class DreamParkLuaLight
    {
        // dp_light_get(what): one primitive per call, never a wrapper type.
        const int Ambient = 0, DirX = 1, DirY = 2, DirZ = 3, Confidence = 4, Live = 5, Simulated = 6;

        static double Get(int what)
        {
            if (what == Simulated) return DreamParkRoomLight.Simulating ? 1.0 : 0.0;   // not a read: asks for nothing
            DreamParkRoomLight.NoteAsked();
            bool live = DreamParkRoomLight.Read(out var r);
            switch (what)
            {
                case Ambient: return r.ambient;
                case DirX: return r.direction.x;
                case DirY: return r.direction.y;
                case DirZ: return r.direction.z;
                case Confidence: return r.confidence;
                case Live: return live ? 1.0 : 0.0;
                default: return 0.0;
            }
        }

        static bool DebugSet(double ambient, double x, double y, double z) =>
            DreamParkRoomLight.SetSimulated((float)ambient, new Vector3((float)x, (float)y, (float)z));

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_light_get",       new Func<int, double>(Get));
            env.Global.Set("dp_light_debug_set", new Func<double, double, double, double, bool>(DebugSet));

            // No double quotes inside this verbatim block.
            env.DoString(@"
dp = dp or {}
dp.light = dp.light or {}
local L = dp.light

-- ── The room's real light ────────────────────────────────
--   dp.light.ambient()    -> 0..1: 0 = pitch dark, 1 = a normal bright
--                            room or brighter (the camera's estimate)
--   dp.light.direction()  -> Vector3, unit, world space, pointing TOWARD
--                            where the light comes from (a shadow falls
--                            along -direction)
--   dp.light.confidence() -> 0..1 for the direction (0 = no clear light)
--   dp.light.live()       -> true while a real estimate is flowing; false =
--                            fallback (ambient 1, the scene sun, confidence 0)
--   dp.light.simulated()  -> Editor: DreamPark > Simulated Room Light is on
--   dp.light.debug_set(ambient[, dir]) -> Editor only: read ambient (0..1)
--                            and dir (Vector3 or {x, y, z}, toward the
--                            light; omitted = keep); nil = the real estimate.
--                            Returns false on a device.
--   Each device reads its OWN room: NOT synced. A multiplayer puzzle
--   decides on one device (the leader) and sends the result.
--   Reading starts the camera estimate on this device; it stops ~10 s
--   after the last read.
L.ambient    = function() return dp_light_get(0) end
L.direction  = function()
    return CS.UnityEngine.Vector3(dp_light_get(1), dp_light_get(2), dp_light_get(3))
end
L.confidence = function() return dp_light_get(4) end
L.live       = function() return dp_light_get(5) > 0.5 end
L.simulated  = function() return dp_light_get(6) > 0.5 end
L.debug_set  = function(ambient, dir)
    if ambient == nil then return dp_light_debug_set(-1, 0, 0, 0) end
    local a = tonumber(ambient)
    if a == nil or a ~= a then return false end
    if a < 0 then a = 0 end
    local x, y, z = 0, 0, 0
    if dir ~= nil then
        local ok, dx, dy, dz = pcall(function()
            local px, py, pz = dir.x, dir.y, dir.z
            if px == nil and type(dir) == 'table' then px, py, pz = dir[1], dir[2], dir[3] end
            return px, py, pz
        end)
        dx, dy, dz = ok and tonumber(dx), ok and tonumber(dy), ok and tonumber(dz)
        if not (dx and dy and dz) then return false end
        x, y, z = dx, dy, dz
    end
    return dp_light_debug_set(a, x, y, z)
end
", "dp.light.bootstrap");
        }
    }
}
