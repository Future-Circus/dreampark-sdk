// ─────────────────────────────────────────────────────────────────────
//  PortalLua.cs — dp.portal_cross (Nov 2026, sdk-crossers): a play-space object passing through ONE portal opening.
//
//    dp.portal_cross(go, opening[, on[, scene]]) -> bool
//        go:      the object passing through (a GameObject, Transform or component; all renderers under it)
//        opening: the PortalSurface's object it passes through (or an object with one under it), or any object on the
//                 opening's plane whose +Z (blue) points into the room (then pass `scene`, its PortalScene's object)
//        on:      true (default) = split it at that opening, per pixel: the room draws the part on the room side, the
//                 portal the part beyond (mapped through an offset scene); calling it again with another opening moves it.
//                 false = it leaves every portal opening and is drawn by the room as authored.
//        Returns whether it is now (on) / was (off) an opening crosser.
//
//  Same bridge shape as DreamParkLuaAnnounce: one flat dp_portal_cross delegate of GameObjects + a bool, so nothing new
//  enters the generated XLua wrappers. Registered from DreamParkLuaAPI.RegisterLua. Older apps have no dp.portal_cross:
//  content guards with `if dp.portal_cross then`.
// ─────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;
using XLua;

namespace DreamPark
{
    internal static class DreamParkLuaPortals
    {
        static bool Cross(GameObject go, GameObject opening, bool on, GameObject scene)
        {
            try { return PortalScene.Cross(go, opening, on, scene != null ? scene.GetComponent<PortalScene>() : null); }
            catch (Exception e) { Debug.LogWarning("[dp.portal_cross] " + e.Message); return false; }
        }

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_portal_cross", new Func<GameObject, GameObject, bool, GameObject, bool>(Cross));

            // No double quotes inside this verbatim block.
            env.DoString(@"
dp = dp or {}
-- dp.portal_cross(go, opening[, on[, scene]]) -> bool   (PortalLua.cs; PORTALS.md § Crossers)
local function portal_go(x)
    if x == nil then return nil end
    local ok, g = pcall(function() return x.gameObject end)
    if ok and g ~= nil then return g end
    return nil
end
dp.portal_cross = function(go, opening, on, scene)
    return dp_portal_cross(portal_go(go), portal_go(opening), on ~= false, portal_go(scene))
end
", "dp.portal.bootstrap");
        }
    }
}
