// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaMap.cs — dp.map for game Lua (pl-announce, Oct 22 2026; PlaylistMap.cs has the model):
//
//    dp.map.mark(target, { done = true|false })   -- add / update a mark on the map (every headset in the run)
//    dp.map.unmark(target)                        -- remove it
//    dp.map.clear()                               -- remove this attraction's marks
//    dp.map.show{ seconds = 3, mark = { done = true[, target = …] } }   -- pop the map over AR (and mark, together)
//    dp.map.hide()
//
//  target: nil = this script's attraction · 'A_CoinGarden' / a pool id / a stop id · an object (GameObject, component,
//  script self) · a position {x, y, z} (world). The map is the game's and the playlist's to show: the platform never pops it.
//  Outside a playlist run the calls do nothing (and return false). Older apps have no dp.map: guard with `if dp.map then`.
//  Same bridge shape as DreamParkLuaAnnounce: ONE flat dp_map_op delegate of primitives + GameObjects.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Globalization;
using UnityEngine;
using XLua;

namespace DreamPark
{
    internal static class DreamParkLuaMap
    {
        static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        /// spec = "kind|ref|x|y|z|done|seconds|withMark" (kind: self | ref | object | world).
        static bool Op(GameObject caller, GameObject obj, string op, string spec)
        {
            var p = (spec ?? "").Split('|');
            string At(int i) => i < p.Length ? p[i] : "";
            try
            {
                return PlaylistMap.GameOp(caller, op, At(0), At(1), obj, new Vector3(F(At(2)), F(At(3)), F(At(4))), At(5) == "1", F(At(6)), At(7) == "1");
            }
            catch (Exception e) { Debug.LogWarning($"[pl-map] dp.map.{op} threw: {e.Message}"); return false; }
        }

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_map_op", new Func<GameObject, GameObject, string, string, bool>(Op));
            env.DoString(Bootstrap, "dp.map.bootstrap");
        }

        // No double quotes inside this verbatim block. Pure Lua 5.3.
        internal const string Bootstrap = @"
dp = dp or {}

local function __map_go(x)
    if x == nil then return nil end
    if type(x) == 'table' then x = rawget(x, 'gameObject') or x.gameObject end
    if type(x) ~= 'userdata' then return nil end
    local ok, g = pcall(function() return x.gameObject end)
    if ok and g ~= nil then return g end
    return nil
end

-- target → kind, ref, object, x, y, z
local function __map_target(t)
    if t == nil then return 'self', '', nil, 0, 0, 0 end
    if type(t) == 'string' then return 'ref', t, nil, 0, 0, 0 end
    local ok, x, y, z = pcall(function() return t.x, t.y, t.z end)
    if ok and type(x) == 'number' and type(y) == 'number' and type(z) == 'number' then return 'world', '', nil, x, y, z end
    local g = __map_go(t)
    if g ~= nil then return 'object', '', g, 0, 0, 0 end
    return nil
end

local function __map_call(go, op, target, done, seconds, with_mark)
    local k, r, g, x, y, z = __map_target(target)
    if k == nil then print('[pl-map] dp.map.' .. op .. ': unknown target ' .. tostring(target)) return false end
    local spec = table.concat({ k, r, tostring(x), tostring(y), tostring(z), done and '1' or '0', tostring(seconds or 0), with_mark and '1' or '0' }, '|')
    return dp_map_op(go, g, op, spec)
end

dp.map = {
    mark = function(target, o, go) return __map_call(go, 'mark', target, type(o) == 'table' and o.done == true) end,
    unmark = function(target, go) return __map_call(go, 'unmark', target, false) end,
    clear = function(go) return __map_call(go, 'clear', nil, false) end,
    show = function(o, go)
        o = type(o) == 'table' and o or { seconds = tonumber(o) }
        local m = type(o.mark) == 'table' and o.mark or nil
        return __map_call(go, 'show', m and m.target or nil, m ~= nil and m.done == true, tonumber(o.seconds) or 4, m ~= nil)
    end,
    hide = function(go) return __map_call(go, 'hide', nil, false) end,
}

-- a script's own dp (DreamParkLuaAPI.BindScope): its object is the caller (its attraction is the default target)
dp.map.__bind = function(go)
    return {
        mark = function(t, o) return dp.map.mark(t, o, go) end,
        unmark = function(t) return dp.map.unmark(t, go) end,
        clear = function() return dp.map.clear(go) end,
        show = function(o) return dp.map.show(o, go) end,
        hide = function() return dp.map.hide(go) end,
    }
end
";
    }
}
