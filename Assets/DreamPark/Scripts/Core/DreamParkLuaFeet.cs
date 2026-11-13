// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaFeet.cs — dp.feet: the player's estimated kicking foot
//  for Lua (Oct 27 2026). Contract: Scripts/Features/Player/FEET.md.
//
//      dp.feet.state()                 -> nil | { x,y,z, vx,vy,vz, speed, dx,dz,
//                                         floor_y, floor, source, tracked, striking, t }
//      dp.feet.on_kick(fn [, owner_go]) -> fn(k) per kick contact
//      dp.feet.target(key, x,y,z, r) / dp.feet.untarget(key)  virtual kickables
//      dp.feet.strike(x,y,z [, confidence]) -> bool
//      dp.feet.last_kick()             -> the newest kick table or nil
//
//  Same bridge shape as DreamParkLuaTime: flat internal dp_feet_*
//  delegates (only signatures already used by the other bridges), so
//  nothing new enters the generated XLua wrappers. Kick events reach Lua
//  through the global __dp_feet_dispatch. Registered from
//  DreamParkLuaAPI.RegisterLua. New headset app only: content must guard
//  `if dp.feet then ... end`.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Globalization;
using UnityEngine;
using XLua;

namespace DreamPark
{
    internal static class DreamParkLuaFeet
    {
        static LuaEnv s_env;
        static bool s_subscribed;

        static LuaTable State()
        {
            var f = FeetTracker.Local;
            if (f == null || !f.isActiveAndEnabled) return null;
            var env = LuaBehaviour.GetLuaEnv();
            var s = f.State;
            var t = env.NewTable();
            t.Set("x", (double)s.position.x); t.Set("y", (double)s.position.y); t.Set("z", (double)s.position.z);
            t.Set("vx", (double)s.velocity.x); t.Set("vy", (double)s.velocity.y); t.Set("vz", (double)s.velocity.z);
            t.Set("speed", (double)new Vector2(s.velocity.x, s.velocity.z).magnitude);
            t.Set("dx", (double)s.direction.x); t.Set("dz", (double)s.direction.z);
            t.Set("floor_y", (double)s.floorY);
            t.Set("floor", s.floorMeasured);
            var src = f.Source;
            t.Set("source", src == FeetTracker.FootSource.Detected ? "detected" : src == FeetTracker.FootSource.Predicted ? "predicted" : "estimated");
            t.Set("tracked", src == FeetTracker.FootSource.Detected);
            t.Set("striking", s.striking);
            t.Set("t", (double)Time.time);
            return t;
        }

        static LuaTable KickTable(FeetTracker.KickEvent e)
        {
            var env = LuaBehaviour.GetLuaEnv();
            var t = env.NewTable();
            t.Set("x", (double)e.point.x); t.Set("y", (double)e.point.y); t.Set("z", (double)e.point.z);
            t.Set("vx", (double)e.velocity.x); t.Set("vy", (double)e.velocity.y); t.Set("vz", (double)e.velocity.z);
            t.Set("speed", (double)e.speed);
            t.Set("dx", (double)e.direction.x); t.Set("dy", (double)e.direction.y); t.Set("dz", (double)e.direction.z);
            t.Set("confidence", (double)e.confidence);
            t.Set("source", e.source ?? "contact");
            if (e.target != null) t.Set("target", e.target);
            if (!string.IsNullOrEmpty(e.targetKey)) t.Set("target_key", e.targetKey);
            t.Set("t", e.time);
            t.Set("seq", e.seq);
            return t;
        }

        static LuaTable Last()
        {
            var k = FeetTracker.LastKick;
            return k.HasValue ? KickTable(k.Value) : null;
        }

        static double[] Nums(string packed, int skip, int count)
        {
            var parts = packed.Split('|');
            if (parts.Length < skip + count) return null;
            var r = new double[count];
            for (int i = 0; i < count; i++)
                if (!double.TryParse(parts[skip + i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]) || double.IsNaN(r[i]) || double.IsInfinity(r[i])) return null;
            return r;
        }

        // "key|x|y|z|r"
        static bool Target(string packed)
        {
            var f = FeetTracker.Local;
            if (f == null || string.IsNullOrEmpty(packed)) return false;
            int bar = packed.IndexOf('|');
            if (bar <= 0) return false;
            var n = Nums(packed, 1, 4);
            if (n == null) return false;
            f.SetTarget(packed.Substring(0, bar), new Vector3((float)n[0], (float)n[1], (float)n[2]), (float)n[3]);
            return true;
        }

        static bool Untarget(string key)
        {
            var f = FeetTracker.Local;
            if (f == null || string.IsNullOrEmpty(key)) return false;
            f.ClearTarget(key);
            return true;
        }

        // "x|y|z|confidence"
        static bool Strike(string packed)
        {
            var f = FeetTracker.Local;
            if (f == null || string.IsNullOrEmpty(packed)) return false;
            var n = Nums(packed, 0, 4);
            if (n == null) return false;
            return f.RequestStrike(new Vector3((float)n[0], (float)n[1], (float)n[2]), Mathf.Clamp01((float)n[3]), "requested");
        }

        static void OnKicked(FeetTracker.KickEvent e)
        {
            var env = s_env;
            if (env == null) return;
            try { env.Global.Get<LuaFunction>("__dp_feet_dispatch")?.Call(KickTable(e)); }
            catch (Exception ex) { Debug.LogWarning("[dp.feet] dispatch: " + ex.Message); }
        }

        internal static void Register(LuaEnv env)
        {
            s_env = env;
            if (!s_subscribed) { FeetTracker.Kicked += OnKicked; s_subscribed = true; }

            env.Global.Set("dp_feet_state",    new Func<LuaTable>(State));
            env.Global.Set("dp_feet_last",     new Func<LuaTable>(Last));
            env.Global.Set("dp_feet_target",   new Func<string, bool>(Target));
            env.Global.Set("dp_feet_untarget", new Func<string, bool>(Untarget));
            env.Global.Set("dp_feet_strike",   new Func<string, bool>(Strike));

            // No double quotes inside this verbatim block.
            env.DoString(@"
dp = dp or {}
dp.feet = dp.feet or {}
local F = dp.feet
local listeners = {}

local function alive(go)
    if go == nil then return true end
    local ok, n = pcall(function() return go.name end)
    return ok and n ~= nil
end

local function num(v) return string.format('%.5f', tonumber(v) or 0) end

-- ── The kicking foot (FEET.md) ───────────────────────────
--   dp.feet.state() -> nil | { x,y,z (y = floor), vx,vy,vz, speed, dx,dz, floor_y,
--                     floor, source ('estimated'|'predicted'|'detected'), tracked,
--                     striking, t }
--   dp.feet.on_kick(fn [, owner_go]) -> fn(k): k = { x,y,z, vx,vy,vz, speed, dx,dy,dz,
--                     confidence, source ('contact'|'predicted'|'depth'|...),
--                     target (GameObject|nil), target_key, t, seq }
--   dp.feet.target(key, x,y,z, radius) / dp.feet.untarget(key)
--   dp.feet.strike(x,y,z [, confidence]) -> bool
--   dp.feet.last_kick() -> the newest kick table or nil
F.state = function() return dp_feet_state() end
F.last_kick = function() return dp_feet_last() end
F.on_kick = function(fn, owner)
    if type(fn) ~= 'function' then return false end
    listeners[#listeners + 1] = { fn = fn, owner = owner }
    return true
end
F.target = function(key, x, y, z, r)
    if key == nil then return false end
    return dp_feet_target(tostring(key) .. '|' .. num(x) .. '|' .. num(y) .. '|' .. num(z) .. '|' .. num(r or 0.11))
end
F.untarget = function(key)
    if key == nil then return false end
    return dp_feet_untarget(tostring(key))
end
F.strike = function(x, y, z, c)
    return dp_feet_strike(num(x) .. '|' .. num(y) .. '|' .. num(z) .. '|' .. num(c or 0.7))
end

__dp_feet_dispatch = function(k)
    local i = 1
    while i <= #listeners do
        local l = listeners[i]
        if not alive(l.owner) then
            table.remove(listeners, i)
        else
            local ok, err = pcall(l.fn, k)
            if not ok then print('[dp.feet] on_kick: ' .. tostring(err)) end
            i = i + 1
        end
    end
end
", "dp.feet.bootstrap");
        }
    }
}
