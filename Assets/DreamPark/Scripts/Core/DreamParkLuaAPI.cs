// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaAPI.cs — SDK-synced creator-facing Lua helpers
//
//  Everything here exists to delete code a creator should never have had to
//  write. The test for anything added to this file is:
//
//      does this let a developer remove code that isn't about their game?
//
//  Each helper below replaces a specific pattern found in shipped content.
//
//  Follows the established bridge shape exactly (GameStorageAPI.cs:843,
//  ProfileAPI.cs:1131): flat snake_case C# bindings on env.Global, then a Lua
//  bootstrap that wraps them into a camelCase `dp.*` table via the idempotent
//  `dp = dp or {}` idiom so module registration order never matters.
//
//  Nothing here returns a custom C# type across the boundary — only primitives,
//  GameObject/Transform (already generated), and LuaTable. That is deliberate:
//  a bespoke return type would force every consumer to add [LuaCallCSharp] and
//  re-run XLua codegen before every release.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;
using XLua;

namespace DreamPark
{
    [LuaCallCSharp]
    public static class DreamParkLuaAPI
    {
        // ── dp.is_player(collider) ───────────────────────────────────
        //
        // REPLACES, in shipped content:
        //     ok = (other.tag == "Player")
        //          or (other.gameObject.layer == UE.LayerMask.NameToLayer("Player"))
        //
        // Every creator writes some version of this and they disagree with each
        // other — tag-only, layer-only, both, or ancestry. Worse, the two-signal
        // version above is ALREADY WRONG against our own rig: the `Goo` collider
        // on BothHands ships Untagged on layer Default, so content using that test
        // rejects part of the player's own hand.
        //
        // Core owns the definition instead. Rig ancestry is checked FIRST because
        // it is the only signal that cannot be defeated by an inconsistently
        // authored collider; tag and layer stay as fallbacks so a collider parented
        // outside the rig (a thrown object still counted as "the player") keeps
        // working.
        public static bool IsPlayer(Collider other)
        {
            if (other == null) return false;
            return IsPlayerObject(other.gameObject);
        }

        public static bool IsPlayerObject(GameObject go)
        {
            if (go == null) return false;

            // Authoritative: anything under a PlayerRig is the player, whatever
            // its tag or layer happen to be.
            if (go.GetComponentInParent<PlayerRig>(true) != null) return true;

            if (go.CompareTag("Player")) return true;

            int playerLayer = LayerMask.NameToLayer("Player");
            return playerLayer >= 0 && go.layer == playerLayer;
        }

        // ── dp.player() / dp.head() ──────────────────────────────────
        //
        // REPLACES: FindObjectsOfType scans, GameObject.Find("Player"), and
        // caching Camera.main by hand in every script that needs the head pose.
        public static GameObject Player()
        {
            var rig = PlayerRig.Instance;
            if (rig != null) return rig.gameObject;

            if (PlayerRig.instances != null)
                foreach (var kv in PlayerRig.instances)
                    if (kv.Value != null) return kv.Value.gameObject;

            return null;
        }

        public static Transform Head()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        // ── dp.attraction(gameObject) ────────────────────────────────
        //
        // REPLACES, in shipped content:
        //     local p = tf.parent
        //     while p ~= nil and found == nil do
        //         local lbs = p:GetComponents(typeof(CS.LuaBehaviour))
        //         for i = 0, lbs.Length - 1 do
        //             if lbs[i].ScriptScope.is_zombiez_level then found = ... end
        //
        // …and, elsewhere, a full-scene FindObjectsOfType(typeof(LuaBehaviour))
        // scan per script just to reach a manager. A creator hand-rolling service
        // discovery, with reflection, to find the attraction they are standing in.
        //
        // Returns the containing attraction's Lua ScriptScope, or nil. The walk
        // mirrors GameStorageAPI.ResolveScopeTable — GameArea first (attractions
        // always carry one), then PropTemplate, then LevelTemplate.
        //
        // ScriptScope is already a LuaTable and its XLua wrapper is generated, so
        // this crosses the boundary with no codegen.
        public static LuaTable AttractionScope(GameObject go)
        {
            var root = AttractionRoot(go);
            if (root == null) return null;

            var lb = root.GetComponent<LuaBehaviour>();
            if (lb == null) lb = root.GetComponentInChildren<LuaBehaviour>(true);
            // Reading ScriptScope boots the script if it hasn't run, so a
            // cross-script reference does not depend on Awake ordering.
            return lb != null ? lb.ScriptScope : null;
        }

        /// <summary>The attraction/prop root GameObject this object belongs to.</summary>
        public static GameObject AttractionRoot(GameObject go)
        {
            if (go == null) return null;

            var area = go.GetComponentInParent<GameArea>(true);
            if (area != null) return area.gameObject;

            var prop = go.GetComponentInParent<PropTemplate>(true);
            if (prop != null) return prop.gameObject;

            var level = go.GetComponentInParent<LevelTemplate>(true);
            return level != null ? level.gameObject : null;
        }

        /// <summary>gameId of the containing content, or null.</summary>
        public static string GameId(GameObject go)
        {
            if (go == null) return null;
            var area = go.GetComponentInParent<GameArea>(true);
            if (area != null && !string.IsNullOrEmpty(area.gameId)) return area.gameId;
            var prop = go.GetComponentInParent<PropTemplate>(true);
            if (prop != null && !string.IsNullOrEmpty(prop.gameId)) return prop.gameId;
            var level = go.GetComponentInParent<LevelTemplate>(true);
            return level != null ? level.gameId : null;
        }

        // ── dp.scope(gameObject) ─────────────────────────────────────
        //
        // REPLACES, in shipped content (three separate copies in Zombiez alone):
        //     local function scope_of(go)
        //         local sc = nil
        //         pcall(function()
        //             local lb = go:GetComponent(typeof(CS.LuaBehaviour))
        //             if lb ~= nil then sc = lb.ScriptScope end
        //         end)
        //         return sc
        //     end
        //
        // Reaching another script from Lua should not require knowing the C#
        // component type, the typeof() idiom, or that the call can throw.
        //
        // Also fixes a bug every hand-rolled copy has: GetComponent returns only
        // the FIRST LuaBehaviour, so an object carrying two scripts is
        // half-invisible to its neighbours. This returns the first scope that
        // actually exists, across all of them.
        //
        // Reading ScriptScope boots the script if it has not run yet, so a
        // cross-script reference never depends on Awake ordering.
        public static LuaTable Scope(GameObject go)
        {
            if (go == null) return null;

            var behaviours = go.GetComponents<LuaBehaviour>();
            if (behaviours == null) return null;

            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null) continue;
                LuaTable scope = null;
                try { scope = behaviours[i].ScriptScope; }
                catch (Exception e)
                {
                    Debug.LogWarning("[dp.scope] " + go.name + " script threw while booting: " + e.Message);
                }
                if (scope != null) return scope;
            }
            return null;
        }

        // ── dp.on_global(name, fn) ───────────────────────────────────
        //
        // REPLACES: `if manager then ... end` on every single call, plus
        // per-frame `if not registered then try_register() end` retry loops.
        //
        // An attraction script's manager typically lives on Player.prefab — a
        // DIFFERENT addressable — so "my dependency does not exist yet" is a
        // normal state here, not an error. Fires immediately if the global is
        // already bound, otherwise as soon as it appears. Late is normal; missing
        // it must be impossible.
        //
        // Driven by a pump rather than a C#→Lua delegate per waiter: pure Lua
        // storage, IL2CPP-safe, and one Update for all waiters in the park.
        internal static void EnsurePump()
        {
            if (_pump != null) return;
            var go = new GameObject("~DreamParkLuaPump") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _pump = go.AddComponent<DreamParkLuaPump>();
        }
        private static DreamParkLuaPump _pump;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RegisterLua()
        {
            try
            {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return;

                env.Global.Set("dp_is_player",        new Func<Collider, bool>(IsPlayer));
                env.Global.Set("dp_is_player_go",     new Func<GameObject, bool>(IsPlayerObject));
                env.Global.Set("dp_player",           new Func<GameObject>(Player));
                env.Global.Set("dp_head",             new Func<Transform>(Head));
                env.Global.Set("dp_scope",            new Func<GameObject, LuaTable>(Scope));
                env.Global.Set("dp_attraction_scope", new Func<GameObject, LuaTable>(AttractionScope));
                env.Global.Set("dp_attraction_root",  new Func<GameObject, GameObject>(AttractionRoot));
                env.Global.Set("dp_game_id",          new Func<GameObject, string>(GameId));
                env.Global.Set("dp_ensure_pump",      new Action(EnsurePump));

                // No double quotes inside this verbatim block.
                env.DoString(@"
dp = dp or {}

dp.is_player   = function(x)  if x == nil then return false end
                              local ok, r = pcall(function() return dp_is_player(x) end)
                              if ok then return r end
                              local ok2, r2 = pcall(function() return dp_is_player_go(x) end)
                              return ok2 and r2 or false end
dp.player      = function()   return dp_player() end
dp.head        = function()   return dp_head() end
dp.scope       = function(go) return dp_scope(go) end
dp.attraction  = function(go) return dp_attraction_scope(go) end
dp.attraction_root = function(go) return dp_attraction_root(go) end
dp.game_id     = function(go) return dp_game_id(go) end

-- Sticky global waiter. Fires now if the global exists, else when it appears.
__dp_global_waiters = __dp_global_waiters or {}

dp.on_global = function(name, fn)
    if name == nil or fn == nil then return end
    local existing = rawget(_G, name)
    if existing ~= nil then fn(existing) return end
    __dp_global_waiters[#__dp_global_waiters + 1] = { name = name, fn = fn }
    dp_ensure_pump()
end

function __dp_pump_globals()
    if #__dp_global_waiters == 0 then return end
    local still = {}
    for i = 1, #__dp_global_waiters do
        local w = __dp_global_waiters[i]
        local v = rawget(_G, w.name)
        if v ~= nil then
            local ok, err = pcall(function() w.fn(v) end)
            if not ok then print('[dp.on_global] handler for ' .. tostring(w.name) .. ' threw: ' .. tostring(err)) end
        else
            still[#still + 1] = w
        end
    end
    __dp_global_waiters = still
end
", "dp.api.bootstrap");
            }
            catch (Exception e)
            {
                Debug.LogError("[DreamParkLuaAPI] Failed to register Lua bridge: " + e);
            }
        }
    }

    /// <summary>
    /// One Update for every pending dp.on_global waiter in the park. Self-creates
    /// on first use and idles at a single Lua call once the list drains.
    /// </summary>
    internal class DreamParkLuaPump : MonoBehaviour
    {
        private LuaFunction _pump;

        void Update()
        {
            try
            {
                if (_pump == null)
                {
                    var env = LuaBehaviour.GetLuaEnv();
                    if (env == null) return;
                    _pump = env.Global.Get<LuaFunction>("__dp_pump_globals");
                    if (_pump == null) return;
                }
                _pump.Call();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DreamParkLuaPump] " + e.Message);
                enabled = false;   // never spam every frame
            }
        }
    }
}
