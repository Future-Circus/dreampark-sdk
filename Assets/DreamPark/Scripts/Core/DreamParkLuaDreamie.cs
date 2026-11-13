// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaDreamie.cs — dp.dreamie, the Lua face of the DreamieMoments
//  seam (repair-oct20 CONTRACT § 4):
//
//    dp.dreamie.moment(name, data, {guide=, priority='low'|'normal'|'high', point=go|pos, once=, cooldown=})
//    dp.dreamie.say(text, {emotion=})          -- exact words, voiced live
//    dp.dreamie.busy() -> bool
//    dp.dreamie.title_speaking(bool)           -- a title's own narrator is talking: Dreamie yields
//
//  Same bridge shape as DreamParkLuaPlaylist: flat internal dp_dm_* delegates of
//  primitives + GameObject, so nothing enters the generated XLua wrappers.
//  Goal blocks get the same table through PlaylistLuaRuntime's sandbox.
//  Older apps have no dp.dreamie: content guards with `if dp.dreamie then`.
// ─────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;
using XLua;

namespace DreamPark
{
    internal static class DreamParkLuaDreamie
    {
        // The caller's stop (its occurrence id) when it runs inside a playlist stop.
        static string StopOf(GameObject go)
        {
            if (go == null) return null;
            try { return PlaylistRunner.For(go)?.StopOf(go)?.occurrenceId; } catch (Exception) { return null; }
        }

        static bool Moment(GameObject caller, string name, string dataJson, string guide, string priority,
            GameObject point, bool hasPos, double x, double y, double z, bool once, double cooldown, string stop)
        {
            return DreamieMoments.Moment(name, dataJson, guide, priority, point,
                hasPos ? new Vector3((float)x, (float)y, (float)z) : (Vector3?)null, once, cooldown,
                string.IsNullOrEmpty(stop) ? StopOf(caller) : stop);
        }

        static bool Say(GameObject caller, string text, string emotion) => DreamieMoments.Say(text, string.IsNullOrEmpty(emotion) ? null : emotion, StopOf(caller));
        static bool Busy() => DreamieMoments.Busy;
        static void TitleSpeaking(bool on) => DreamieMoments.SetTitleSpeaking(on);

        internal static void Register(LuaEnv env)
        {
            RegisterBridges(env);
            env.DoString(Bootstrap, "dp.dreamie.bootstrap");
        }

        /// The flat bridges only (PlaylistLuaRuntime's offline sim registers them on its own VM).
        internal static void RegisterBridges(LuaEnv env)
        {
            env.Global.Set("dp_dm_moment", new Func<GameObject, string, string, string, string, GameObject, bool, double, double, double, bool, double, string, bool>(Moment));
            env.Global.Set("dp_dm_say", new Func<GameObject, string, string, bool>(Say));
            env.Global.Set("dp_dm_busy", new Func<bool>(Busy));
            env.Global.Set("dp_dm_title", new Action<bool>(TitleSpeaking));
        }

        // No double quotes inside this verbatim block. Pure Lua 5.3 (no CS.*): it also runs in the goal-block sandbox.
        internal const string Bootstrap = @"
dp = dp or {}
local D = {}
dp.dreamie = D

local function __dm_esc(s)
    s = string.gsub(s, '[%c\\' .. string.char(34) .. ']', function(c)
        if c == '\\' then return '\\\\' end
        if c == string.char(34) then return '\\' .. string.char(34) end
        if c == '\n' then return '\\n' end
        if c == '\r' then return '\\r' end
        if c == '\t' then return '\\t' end
        return string.format('\\u%04x', string.byte(c))
    end)
    return string.char(34) .. s .. string.char(34)
end

local function __dm_json(v, depth)
    depth = depth or 0
    local tv = type(v)
    if depth > 6 then return 'null' end
    if tv == 'nil' then return 'null' end
    if tv == 'boolean' then return v and 'true' or 'false' end
    if tv == 'number' then
        if v ~= v or v == math.huge or v == -math.huge then return '0' end
        if math.type and math.type(v) == 'integer' then return tostring(v) end
        if v == math.floor(v) and math.abs(v) < 1e15 then return string.format('%d', v) end
        return string.format('%.6g', v)
    end
    if tv == 'string' then return __dm_esc(v) end
    if tv == 'table' then
        local n = #v
        if n > 0 then
            local parts = {}
            for i = 1, n do parts[i] = __dm_json(v[i], depth + 1) end
            return '[' .. table.concat(parts, ',') .. ']'
        end
        local parts = {}
        for k, val in pairs(v) do
            local tk = type(k)
            if (tk == 'string' or tk == 'number') and type(val) ~= 'function' and type(val) ~= 'userdata' then
                parts[#parts + 1] = __dm_esc(tostring(k)) .. ':' .. __dm_json(val, depth + 1)
            end
        end
        return '{' .. table.concat(parts, ',') .. '}'
    end
    return 'null'
end
D.__json = __dm_json

-- point = a GameObject (its position, this device) or a Vector3 / {x,y,z} / {1,2,3}.
local function __dm_point(p)
    if p == nil then return nil, false, 0, 0, 0 end
    if type(p) == 'table' then
        local x = p.x or p[1]; local y = p.y or p[2]; local z = p.z or p[3]
        if type(x) == 'number' and type(y) == 'number' and type(z) == 'number' then return nil, true, x, y, z end
        return nil, false, 0, 0, 0
    end
    if type(p) == 'userdata' then
        local okT, t = pcall(function() return p.transform end)
        if okT and t ~= nil then
            local okG, g = pcall(function() return p.gameObject end)
            if okG and g ~= nil then return g, false, 0, 0, 0 end
        end
        local okV, x, y, z = pcall(function() return p.x, p.y, p.z end)
        if okV and type(x) == 'number' then return nil, true, x, y, z end
    end
    return nil, false, 0, 0, 0
end

D.moment = function(name, data, opts, go)
    if type(name) ~= 'string' then return false end
    opts = type(opts) == 'table' and opts or {}
    local pgo, has, x, y, z = __dm_point(opts.point)
    local stop = opts.stop
    if type(stop) ~= 'string' then stop = '' end
    return dp_dm_moment(go, name, type(data) == 'table' and __dm_json(data) or '{}',
        opts.guide ~= nil and tostring(opts.guide) or '', opts.priority ~= nil and tostring(opts.priority) or '',
        pgo, has, x, y, z, opts.once == true, tonumber(opts.cooldown) or 0, stop)
end
D.say = function(text, opts, go)
    if text == nil then return false end
    opts = type(opts) == 'table' and opts or {}
    return dp_dm_say(go, tostring(text), opts.emotion ~= nil and tostring(opts.emotion) or '')
end
D.busy = function() return dp_dm_busy() end
D.title_speaking = function(on) dp_dm_title(on ~= false) end

-- A script's own facade (DreamParkLuaAPI.BindScope): the caller's object is automatic.
D.__bind = function(go)
    return setmetatable({
        moment = function(name, data, opts) return D.moment(name, data, opts, go) end,
        say = function(text, opts) return D.say(text, opts, go) end,
        busy = D.busy,
        title_speaking = D.title_speaking,
    }, { __index = D })
end
";
    }
}
