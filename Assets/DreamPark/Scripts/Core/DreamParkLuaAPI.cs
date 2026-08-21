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


        // ── dp.park() / dp.to_park() / dp.from_park() / dp.head_park() ─
        //
        // REPLACES: InverseTransformPoint against a hand-walked Player.parent
        // in every networked script. Each headset has its own XR origin;
        // parks are QR-anchored in a shared park-local space. dp.head() and
        // transform.position are WORLD. Streaming those numbers is the
        // "room lines up, the other player is two metres off" bug.
        //
        // Runtime tree (types live in dreampark-core, not this assembly):
        //   ParkAnchor          <- park-document origin (shared across portals)
        //     PortalAnchor      <- QR frame
        //       SubLevelRoot    <- identity
        //         LevelAnchor   <- LevelAnchor.LoadLevel parents Player here
        //           Player
        //
        // Player.parent is the LevelAnchor, NOT the park. Two headsets that
        // claimed different levels (or portals) disagree in LevelAnchor space.
        // Walk by type NAME so the SDK does not take a compile dep on core.
        // Prefer ParkAnchor, then PortalAnchor, then LevelAnchor, then parent.
        // Nil in Editor Play with no anchors — to_park / from_park are identity.
        // Do not call from awake(): the rig is not parented yet.
        static Transform PlayerRoot(GameObject go)
        {
            if (go == null) return null;
            var t = go.transform;
            var cursor = t;
            for (int hops = 0; hops < 8 && cursor != null; hops++)
            {
                if (cursor.name == "Player") return cursor;
                cursor = cursor.parent;
            }
            return t;
        }

        static bool HasTypeNamed(Transform t, string typeName)
        {
            if (t == null) return false;
            var comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] != null && comps[i].GetType().Name == typeName)
                    return true;
            }
            return false;
        }

        static Transform ParkFrame(Transform start)
        {
            if (start == null) return null;
            Transform park = null, portal = null, level = null;
            var t = start;
            for (int hops = 0; hops < 16 && t != null; hops++)
            {
                if (park == null && HasTypeNamed(t, "ParkAnchor")) park = t;
                if (portal == null && HasTypeNamed(t, "PortalAnchor")) portal = t;
                if (level == null && HasTypeNamed(t, "LevelAnchor")) level = t;
                t = t.parent;
            }
            if (park != null) return park;
            if (portal != null) return portal;
            if (level != null) return level;
            return start.parent;
        }

        public static Transform Park()
        {
            var frame = ParkFrame(PlayerRoot(Player()));
            if (frame != null) return frame;
            if (PlayerRig.instances == null) return null;
            foreach (var kv in PlayerRig.instances)
            {
                if (kv.Value == null) continue;
                frame = ParkFrame(PlayerRoot(kv.Value.gameObject));
                if (frame != null) return frame;
            }
            return null;
        }

        public static Vector3 ToPark(Vector3 world)
        {
            var park = Park();
            return park != null ? park.InverseTransformPoint(world) : world;
        }

        public static Vector3 FromPark(Vector3 local)
        {
            var park = Park();
            return park != null ? park.TransformPoint(local) : local;
        }

        public static Vector3 ToParkDir(Vector3 worldDir)
        {
            var park = Park();
            if (park == null) return worldDir;
            var d = park.InverseTransformDirection(worldDir);
            return d.sqrMagnitude > 0.0001f ? d.normalized : d;
        }

        public static Vector3 FromParkDir(Vector3 localDir)
        {
            var park = Park();
            if (park == null) return localDir;
            var d = park.TransformDirection(localDir);
            return d.sqrMagnitude > 0.0001f ? d.normalized : d;
        }

        // Park-local head pose as primitives (no custom type, no codegen).
        // Lua wrapper returns position, forward. Nil if the head is not up.
        public static LuaTable HeadPark()
        {
            var head = Head();
            if (head == null) return null;
            var env = LuaBehaviour.GetLuaEnv();
            if (env == null) return null;
            var t = env.NewTable();
            var pos = ToPark(head.position);
            var fwd = ToParkDir(head.forward);
            // Primitives only — Vector3 on LuaTable.Set is not a pattern
            // this file uses (see Relay/Session). Lua builds the Vector3s.
            t.Set("x", pos.x);
            t.Set("y", pos.y);
            t.Set("z", pos.z);
            t.Set("fx", fwd.x);
            t.Set("fy", fwd.y);
            t.Set("fz", fwd.z);
            return t;
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

        // ── dp.hand(side) / dp.hands() ───────────────────────────────
        //
        // REPLACES, in shipped content:
        //     GameObject.Find("RightHandAnchor")   -- every frame, in Lua
        //     ...or giving up and using the collider that happened to touch
        //     the trigger, which is whichever child collider Unity reported.
        //
        // Content that spawns FROM the hand (a bolt, a spell, a thrown item)
        // has had no way to ask where the hand is. The three sanctioned
        // interaction patterns are all collider-driven, so a script only ever
        // learned about a hand at the moment one hit something — which is too
        // late to aim with, and gives a different transform depending on which
        // sub-collider of the rig fired.
        //
        // Resolved through the ACTIVE RIG's HandTracker, not a global
        // GameObject.Find. HandTracker is the SDK component that owns hand
        // state, it sits on every Player.prefab, and going through it means
        // dp.hand() and dp.player() are always talking about the same rig —
        // PlayerRig.instances is a dictionary, so more than one rig can exist
        // and a naked Find would return whichever the scene graph offered.
        //
        // GameObject.Find on the Meta anchor names stays as a FALLBACK because
        // that is what HandTracker itself falls back to (HandTracker.cs), and
        // what Simulator creates in the Editor when there is no headset. It is
        // the second choice, not the convention.
        static Transform _handL, _handR;

        static HandTracker RigHandTracker()
        {
            var rig = Player();
            return rig != null ? rig.GetComponentInChildren<HandTracker>(true) : null;
        }

        // The anchor is the OVRHand's parent (same relationship Simulator
        // builds); fall back to the hand transform itself if it is unparented.
        static Transform AnchorOf(OVRHand hand)
        {
            if (hand == null) return null;
            var t = hand.transform;
            return t.parent != null ? t.parent : t;
        }

        public static Transform Hand(string side)
        {
            bool wantLeft  = side == "left"  || side == "L" || side == "l";
            bool wantRight = side == "right" || side == "R" || side == "r";

            var tracker = RigHandTracker();
            if (tracker != null)
            {
                if (!wantLeft && !wantRight)
                {
                    var active = AnchorOf(tracker.ActiveHand);
                    if (active != null) return active;
                }
                else
                {
                    var picked = AnchorOf(wantLeft ? tracker.leftHand : tracker.rightHand);
                    if (picked != null) return picked;
                }
            }

            // Fallback path.
            if (!wantLeft && !wantRight)
            {
                var r = Hand("right");
                return r != null ? r : Hand("left");
            }

            // Explicit branches rather than a ref local: Unity's == overload
            // means a DESTROYED Transform is "fake null", which ?? does not see.
            if (wantLeft)
            {
                if (_handL == null)
                {
                    var gl = GameObject.Find("LeftHandAnchor");
                    _handL = gl != null ? gl.transform : null;
                }
                return _handL;
            }

            if (_handR == null)
            {
                var gr = GameObject.Find("RightHandAnchor");
                _handR = gr != null ? gr.transform : null;
            }
            return _handR;
        }

        // ── dp.relay() / dp.session() ────────────────────────────────
        //
        // REPLACES, in shipped content:
        //     pcall(function() local c = CS.DreamBoxClient.Instance
        //                      if c ~= nil then ping = c.Ping end end)
        //
        // Every multiplayer game wants to show connection state, and every one
        // of them reaches through CS.* to a singleton that may not exist, in a
        // pcall, because a nil there is a hard error mid-frame. Worse, the two
        // things a creator actually needs to reason about — am I over the send
        // budget, and did the host just change — were not reachable at all.
        //
        // Returns a plain LuaTable of primitives (no bespoke C# type crossing
        // the boundary, so no [LuaCallCSharp] and no XLua codegen churn).
        public static LuaTable Relay()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var t = env.NewTable();
            var c = DreamBoxClient.Instance;
            t.Set("present",   c != null);
            t.Set("state",     c != null ? c.ConnectionState.ToString() : "none");
            t.Set("connected", c != null && c.ConnectionState == DreamBoxClient.State.Connected);
            t.Set("ping",      c != null ? c.Ping : -1);
            t.Set("received",  c != null ? c.MessageCount : 0);
            t.Set("sent",      c != null ? c.SentCount : 0);
            t.Set("send_rate", c != null ? c.SendRate : 0);
            // TWO numbers, deliberately.
            //   cap    — what THIS host enforces right now. 0 = it advertises
            //            none (every kiosk today). Diagnostic; it moves.
            //   budget — what to DESIGN against. Always the floor, whoever is
            //            hosting, because a kiosk session can lose the kiosk
            //            mid-play and a headset takes over. Budgeting to kiosk
            //            headroom breaks at exactly that moment.
            // If you are putting one number in your head, it is budget.
            t.Set("cap",       c != null ? c.EffectiveSendCap : 0);
            t.Set("budget",    DreamBoxClient.DesignBudget);
            // Outbox depth. Lets content report "queued" as a fact rather than
            // inferring it from present && !connected.
            t.Set("queued",    c != null ? c.QueuedCount : 0);
            return t;
        }

        public static LuaTable Session()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var t = env.NewTable();
            var a = NetSessionArbiter.Instance;
            t.Set("present", a != null);
            t.Set("state",   a != null ? a.State.ToString() : "none");
            t.Set("host",    a != null ? (a.CurrentHostId ?? "") : "");
            t.Set("is_host", a != null && a.IsHost);
            t.Set("peers",   a != null ? a.HostedPeerCount : 0);
            t.Set("park",    a != null ? (a.parkId ?? "") : "");
            return t;
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
                env.Global.Set("dp_park",             new Func<Transform>(Park));
                env.Global.Set("dp_to_park",          new Func<Vector3, Vector3>(ToPark));
                env.Global.Set("dp_from_park",        new Func<Vector3, Vector3>(FromPark));
                env.Global.Set("dp_to_park_dir",      new Func<Vector3, Vector3>(ToParkDir));
                env.Global.Set("dp_from_park_dir",    new Func<Vector3, Vector3>(FromParkDir));
                env.Global.Set("dp_head_park",        new Func<LuaTable>(HeadPark));
                env.Global.Set("dp_scope",            new Func<GameObject, LuaTable>(Scope));
                env.Global.Set("dp_attraction_scope", new Func<GameObject, LuaTable>(AttractionScope));
                env.Global.Set("dp_attraction_root",  new Func<GameObject, GameObject>(AttractionRoot));
                env.Global.Set("dp_game_id",          new Func<GameObject, string>(GameId));
                env.Global.Set("dp_ensure_pump",      new Action(EnsurePump));
                env.Global.Set("dp_hand",             new Func<string, Transform>(Hand));
                // Cheap enough to poll every frame — dp.relay() allocates a table,
                // this does not.
                env.Global.Set("dp_connected",        new Func<bool>(() => {
                    var c = DreamBoxClient.Instance;
                    return c != null && c.ConnectionState == DreamBoxClient.State.Connected;
                }));
                env.Global.Set("dp_relay",            new Func<LuaTable>(Relay));
                env.Global.Set("dp_session",          new Func<LuaTable>(Session));

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

-- Park-local space. Each headset has its own XR origin; the park is the
-- shared frame. dp.head() / transform.position are WORLD. Use these on
-- the wire. Identity (world) when there is no park parent (Editor Play).
-- after onready(), not awake() — the rig is not parented yet in awake().
dp.park        = function()   return dp_park() end
dp.to_park     = function(p)  if p == nil then return nil end return dp_to_park(p) end
dp.from_park   = function(p)  if p == nil then return nil end return dp_from_park(p) end
dp.to_park_dir = function(d)  if d == nil then return nil end return dp_to_park_dir(d) end
dp.from_park_dir = function(d) if d == nil then return nil end return dp_from_park_dir(d) end
-- returns position, forward (Vector3, park-local), or nil
dp.head_park   = function()
    local t = dp_head_park()
    if t == nil then return nil end
    local V = CS.UnityEngine.Vector3
    return V(t.x, t.y, t.z), V(t.fx, t.fy, t.fz)
end
dp.scope       = function(go) return dp_scope(go) end
dp.attraction  = function(go) return dp_attraction_scope(go) end
dp.attraction_root = function(go) return dp_attraction_root(go) end
dp.game_id     = function(go) return dp_game_id(go) end

-- Where the player's hand is, right now. side is 'left' / 'right', or
-- omitted for the rig's own hand anchor. May be nil before the rig exists.
dp.hand        = function(side) return dp_hand(side or '') end
dp.hands       = function()   return dp_hand('left'), dp_hand('right') end

-- Live networking state. Never nil, never throws: fields report an absent
-- client rather than making every caller wrap the lookup in a pcall.
--   dp.relay()   -> { present, state, connected, ping, received, sent,
--                     send_rate, cap }
--   dp.session() -> { present, state, host, is_host, peers, park }
dp.relay       = function()   return dp_relay() end
dp.session     = function()   return dp_session() end

-- One-shot: run fn as soon as the relay link is up (immediately if it already
-- is). This is the hook for anything whose TIMING matters, not just its
-- delivery — a join handshake has to open its listen window when the link
-- comes up, and no amount of queueing on the send side fixes a window that
-- opened and closed while the client was still connecting.
__dp_connect_waiters = __dp_connect_waiters or {}

dp.on_connected = function(fn)
    if fn == nil then return end
    if dp_connected() then fn() return end
    __dp_connect_waiters[#__dp_connect_waiters + 1] = fn
    dp_ensure_pump()
end

function __dp_pump_connected()
    if #__dp_connect_waiters == 0 then return end
    if not dp_connected() then return end
    local list = __dp_connect_waiters
    __dp_connect_waiters = {}
    for i = 1, #list do
        local ok, err = pcall(list[i])
        if not ok then print('[dp.on_connected] handler threw: ' .. tostring(err)) end
    end
end

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
    __dp_pump_connected()
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
