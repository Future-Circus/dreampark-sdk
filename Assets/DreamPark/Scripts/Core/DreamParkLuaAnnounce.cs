// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaAnnounce.cs — dp.announce / dp.announce_done (PLAYLIST-PLAN § 4.2; pl-announce, Oct 21 2026):
//
//    dp.announce(kind, { item = 'coin', holds = { collect = 1 }, from = <source> })
//        kind: collect | defeat | break | target | reach | find | protect | catch | waypoint (+ label = 'Gate')
//        from: the source's GameObject (or a component / script self on it) — the count moves from it to this object
//    dp.announce_done({ by = player })      -- this object is collected / defeated / broken (by = a player id)
//    dp.hud(go[, on])                       -- go is the title's HUD / UI: drawn over the objective markers, which fade there
//
//  Same bridge shape as DreamParkLuaDreamie: flat internal dp_an_* delegates of primitives + GameObject, so nothing
//  enters the generated XLua wrappers. A script's own dp facade (DreamParkLuaAPI.BindScope) passes its object.
//  Older apps have no dp.announce: content guards with `if dp.announce then`.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;
using XLua;

namespace DreamPark
{
    internal static class DreamParkLuaAnnounce
    {
        static bool Announce(GameObject caller, string kind, string item, string holdsJson, GameObject from)
        {
            if (caller == null) { Debug.LogWarning("[pl-announce] dp.announce needs a script's own dp (no object)"); return false; }
            Dictionary<string, int> holds = null;
            if (!string.IsNullOrEmpty(holdsJson))
            {
                var j = SharedState.ParseFragment(holdsJson);
                if (j != null && j.keys != null)
                    for (int i = 0; i < j.keys.Count; i++)
                    {
                        var v = j.list[i];
                        string k = AnnounceKinds.Normalize(j.keys[i]);
                        if (k == null || v == null || v.type != Defective.JSON.JSONObject.Type.Number) continue;
                        int n = (int)Math.Round(v.doubleValue);
                        if (n > 0) (holds ??= new Dictionary<string, int>())[k] = n;
                    }
            }
            return PlaylistAnnounce.Announce(caller, kind, string.IsNullOrEmpty(item) ? null : item, holds, from) != null;
        }

        static bool Done(GameObject caller, string by) => PlaylistAnnounce.Done(caller, by);

        /// dp.hud(go[, on]): this root is the title's HUD / UI — it draws over the objective markers, which fade where it is.
        static bool Hud(GameObject go, bool on) { if (go == null) return false; PlaylistGoalSight.Hud(go.transform, on); return true; }

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_an_announce", new Func<GameObject, string, string, string, GameObject, bool>(Announce));
            env.Global.Set("dp_an_done", new Func<GameObject, string, bool>(Done));
            env.Global.Set("dp_an_hud", new Func<GameObject, bool, bool>(Hud));
            env.DoString(Bootstrap, "dp.announce.bootstrap");
        }

        // No double quotes inside this verbatim block. Pure Lua 5.3.
        internal const string Bootstrap = @"
dp = dp or {}

-- A GameObject from a GameObject, a component on it, or a script's self (self.gameObject).
local function __an_go(x)
    if x == nil then return nil end
    if type(x) == 'table' then x = rawget(x, 'gameObject') or x.gameObject end
    if type(x) ~= 'userdata' then return nil end
    local ok, g = pcall(function() return x.gameObject end)
    if ok and g ~= nil then return g end
    return nil
end

local function __an_player(p)
    if p == nil then return '' end
    if type(p) == 'table' then p = p.id or p.player or p.playerId end
    if p == nil then return '' end
    return tostring(p)
end

-- dp.announce(kind, {item, holds, from}): this object is a collect / defeat / break / target / reach / find / protect /
-- catch thing. holds = {collect = 1, ...}: what it will release. from = the source it came out of.
dp.announce = function(kind, opts, go)
    if type(kind) ~= 'string' then return false end
    opts = type(opts) == 'table' and opts or {}
    local holds = ''
    if type(opts.holds) == 'table' then
        local parts = {}
        for k, v in pairs(opts.holds) do
            local n = tonumber(v)
            if type(k) == 'string' and n ~= nil and n > 0 then parts[#parts + 1] = string.format('%q:%d', k, math.floor(n)) end
        end
        if #parts > 0 then holds = '{' .. table.concat(parts, ',') .. '}' end
    end
    local item = opts.item ~= nil and opts.item or opts.label   -- a waypoint's label rides as its item
    return dp_an_announce(go, kind, item ~= nil and tostring(item) or '', holds, __an_go(opts.from))
end

-- dp.hud(go[, on]): go is the title's HUD / UI (a strip, a card, a text blast): it draws over the objective markers, and a
-- marker under it fades. on = false unmarks it.
dp.hud = function(go, on) return dp_an_hud(__an_go(go), on ~= false) end

-- dp.announce_done({by = player}): this object was collected / defeated / broken.
dp.announce_done = function(opts, go)
    opts = type(opts) == 'table' and opts or {}
    return dp_an_done(go, __an_player(opts.by))
end
";
    }
}
