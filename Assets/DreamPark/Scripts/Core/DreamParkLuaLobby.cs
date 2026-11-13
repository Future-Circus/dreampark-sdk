// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaLobby.cs — dp.lobby (start-wait round, Oct 31 2026): the Location's "Waiting for players" wait, for gameplay Lua.
//
//    dp.lobby.total()         -- seconds of wait set for this session (0 = none: Off, or no group session)
//    dp.lobby.left()          -- seconds left, on the server clock (headsets agree within a few seconds); 0 when over
//    dp.lobby.over()          -- true once the wait has ended
//    dp.lobby.onEnd(fn)       -- fn runs once when it ends, or at once when it is already over; dropped with its script
//
//  Nov 9 2026 (onoff round): with an ONBOARDING (the Location's onboarding time > 0) total() = that time + the 3-2-1, left() =
//  the cap minus the counter (+ the 3-2-1) until the server decides, then the seconds to GO; onEnd fires at GO, when the game
//  comes in (it is held through the onboarding). With none (wait 0, offline, an older server) the old meaning stands.
//  A script that wants a lobby waits on this itself. Directors get the same four calls from RunnerLua (their onEnd is
//  dp.session.onClockStart's hook: never again after a leader hand-over).
//  Same bridge shape as DreamParkLuaAnnounce: flat internal dp_lb_* delegates, so nothing enters the generated XLua
//  wrappers. The app sets the two providers (core: RunnerCoreBridge); unset, there is no wait. Older apps have no
//  dp.lobby: content guards with `if dp.lobby then`.
// ─────────────────────────────────────────────────────────────────────

using System;
using XLua;

namespace DreamPark
{
    public static class DreamParkLobby
    {
        /// The wait set for this session, in seconds (0 = none).
        public static Func<double> TotalSeconds = () => 0;
        /// Seconds of it left (0 = over).
        public static Func<double> LeftSeconds = () => 0;

        static double Read(Func<double> f)
        {
            try { double v = f?.Invoke() ?? 0; return double.IsNaN(v) ? 0 : Math.Max(0, v); }
            catch (Exception) { return 0; }
        }

        public static void Register(LuaEnv env)
        {
            env.Global.Set("dp_lb_total", new Func<double>(() => Read(TotalSeconds)));
            env.Global.Set("dp_lb_left", new Func<double>(() => Read(LeftSeconds)));
            env.DoString(Bootstrap, "dp.lobby.bootstrap");
        }

        // No double quotes inside this verbatim block. Pure Lua 5.3.
        internal const string Bootstrap = @"
dp = dp or {}
dp.lobby = dp.lobby or {}
__dp_lobby_waiters = __dp_lobby_waiters or {}

dp.lobby.total = function() return dp_lb_total() end
dp.lobby.left = function() return dp_lb_left() end
dp.lobby.over = function() return dp_lb_left() <= 0 end

local function __lb_run(fn)
    local ok, err = pcall(fn)
    if not ok then print('[dp.lobby.onEnd] handler threw: ' .. tostring(err)) end
end

dp.lobby.onEnd = function(fn, owner)
    if type(fn) ~= 'function' then return end
    if dp_lb_left() <= 0 then __lb_run(fn) return end
    __dp_lobby_waiters[#__dp_lobby_waiters + 1] = { fn = fn, owner = owner }
    dp_ensure_pump()
end

local function __lb_gone(owner)
    if owner == nil then return false end
    local ok, gone = pcall(function() return owner:Equals(nil) end)
    return ok and gone == true
end

-- Runs on the park's one pump (DreamParkLuaPump → __dp_pump_globals).
function __dp_pump_lobby()
    if #__dp_lobby_waiters == 0 or dp_lb_left() > 0 then return end
    local list = __dp_lobby_waiters
    __dp_lobby_waiters = {}
    for i = 1, #list do
        if not __lb_gone(list[i].owner) then __lb_run(list[i].fn) end
    end
end

local __lb_next = __dp_pump_globals
__dp_pump_globals = function()
    if __lb_next then __lb_next() end
    __dp_pump_lobby()
end
";
    }
}
