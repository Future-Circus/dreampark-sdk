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

        // ── dp.park() / dp.to_park() / dp.from_park() ────────────────
        //
        // REPLACES, in shipped content:
        //     local player = self.transform
        //     while player.parent ~= nil and player.name ~= "Player" do
        //         player = player.parent            -- never matched on device:
        //     end                                   -- core renames the rig root
        //     park = player.parent                  -- ...so this was nil, and
        //     p = park:InverseTransformPoint(p)     -- every pose went out in
        //                                           -- WORLD space, per headset.
        //
        // Unity world space is per headset on Quest (see ParkRoot.cs). A
        // creator should never have to know that, let alone find the frame by
        // walking the hierarchy. The frame is the ONE thing every headset in a
        // park agrees on, so it is exposed once, here, and everything the SDK
        // puts on the wire converts through it. The maths lives in the Lua
        // bootstrap on top of generated Transform methods, so no struct-typed
        // delegate ever crosses the boundary (IL2CPP-safe, no codegen).
        //
        // Returns null when there is no park (SDK test scene): every helper is
        // then the identity, and the same numbers still work at a desk.
        public static Transform Park()
        {
            return ParkRoot.Resolve();
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

        // Package context is resolved from the CALLING LuaBehaviour's hierarchy.
        // It is intentionally not a global current sequence: parks can contain
        // several packages (or players) at once.
        public static DreamParkPackageHost Package(GameObject go)
        {
            if (go == null) return null;
            // Sequence spatial roots have the host as an ancestor. Adventure
            // and Arena occurrences may live in separate calibrated anchors,
            // each linked explicitly to the non-spatial owner.
            var direct = go.GetComponentInParent<DreamParkPackageHost>(true);
            if (direct != null) return direct;
            return go.GetComponentInParent<DreamParkPackageMembership>(true)?.host;
        }

        public static GameObject PackageObject(GameObject go) => Package(go)?.gameObject;

        public static GameObject ContainerObject(GameObject go) => Package(go)?.container;

        public static LuaTable ContainerScope(GameObject go) => Scope(ContainerObject(go));

        /// <summary>Press a default Sequence control, including its feedback and cooldown.</summary>
        public static bool PressButton(GameObject go)
        {
            if (go == null) return false;
            DreamSequenceButtonFeedback feedback = go.GetComponent<DreamSequenceButtonFeedback>();
            return feedback == null || feedback.TryPress();
        }

        public static bool IsSequence(GameObject go)
        {
            var package = Package(go);
            return package != null && package.kind == DreamParkPackageKind.Sequence;
        }

        public static bool IsAdventure(GameObject go)
        {
            var package = Package(go);
            return package != null && package.kind == DreamParkPackageKind.Adventure;
        }

        public static bool IsArena(GameObject go)
        {
            var package = Package(go);
            return package != null && package.kind == DreamParkPackageKind.Arena;
        }

        public static int CurrentLevelIndex(GameObject go) => Package(go)?.CurrentLevelIndex ?? 0;

        public static int LevelCount(GameObject go, string groupId = null)
        {
            DreamParkPackageHost package = Package(go);
            return package == null ? 0 : string.IsNullOrEmpty(groupId)
                ? package.LevelCount : package.GroupLevelCount(groupId);
        }

        public static int LevelSlot(GameObject go, int index, string groupId)
            => Package(go)?.ResolveGroupSlot(index, groupId) ?? 0;

        /// <summary>Placed Groups in package order, with IDs accepted by grouped level APIs.</summary>
        public static LuaTable Groups(GameObject go)
        {
            DreamParkPackageHost package = Package(go);
            if (package == null) return null;
            LuaTable result = LuaBehaviour.GetLuaEnv().NewTable();
            var levels = package.GetComponent<DreamSequenceTemplate>()?.levels;
            if (package.kind != DreamParkPackageKind.Sequence || levels == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int position = 1;
            foreach (DreamSequenceLevel level in levels)
            {
                if (level == null || string.IsNullOrEmpty(level.groupOccurrenceId)
                    || !seen.Add(level.groupOccurrenceId)) continue;
                LuaTable group = LuaBehaviour.GetLuaEnv().NewTable();
                group.Set("id", level.groupOccurrenceId);
                group.Set("name", level.groupName ?? string.Empty);
                group.Set("sourceId", level.sourceGroupId ?? string.Empty);
                group.Set("count", package.GroupLevelCount(level.groupOccurrenceId));
                result.Set(position++, group);
            }
            return result;
        }

        /// <summary>1-based display names in package order; no Addressable IDs.</summary>
        public static LuaTable Levels(GameObject go, string groupId = null)
        {
            DreamParkPackageHost package = Package(go);
            if (package == null) return null;
            LuaTable result = LuaBehaviour.GetLuaEnv().NewTable();
            if (package.kind == DreamParkPackageKind.Sequence)
            {
                if (!string.IsNullOrEmpty(groupId))
                {
                    var grouped = package.GetComponent<DreamSequenceTemplate>()?.levels;
                    string resolvedGroupId = package.ResolveGroupId(groupId);
                    int position = 1;
                    if (grouped != null)
                        foreach (DreamSequenceLevel level in grouped)
                            if (level != null && string.Equals(level.groupOccurrenceId,
                                resolvedGroupId, StringComparison.Ordinal)
                                && resolvedGroupId != null)
                                result.Set(position++, level.displayName ?? "Level");
                    return result;
                }
                result.Set(1, package.startLevel != null ? package.startLevel.name : "Start Level");
                var sequence = package.GetComponent<DreamSequenceTemplate>();
                if (sequence != null && sequence.levels != null)
                    for (int i = 0; i < sequence.levels.Count; i++)
                        result.Set(i + 2, sequence.levels[i]?.displayName ?? "Level");
                result.Set(package.LevelCount, package.gameOverLevel != null
                    ? package.gameOverLevel.name : "Game Over Level");
            }
            else if ((package.kind == DreamParkPackageKind.Adventure
                || package.kind == DreamParkPackageKind.Arena)
                && package.adventureLevelNames != null)
                for (int i = 0; i < package.adventureLevelNames.Count; i++)
                    result.Set(i + 1, package.adventureLevelNames[i]);
            else if (package.levelParent != null)
                for (int i = 0; i < package.levelParent.childCount; i++)
                {
                    Transform stop = package.levelParent.GetChild(i);
                    LevelTemplate attraction = stop.GetComponentInChildren<LevelTemplate>(true);
                    PropTemplate prop = attraction == null ? stop.GetComponentInChildren<PropTemplate>(true) : null;
                    result.Set(i + 1, attraction != null ? attraction.name
                        : prop != null ? prop.name : stop.name);
                }
            return result;
        }

        public static bool LoadLevel(GameObject go, int slot, string groupId = null)
        {
            var package = Package(go);
            if (package == null) return false;
            int resolved = string.IsNullOrEmpty(groupId) ? slot
                : package.ResolveGroupSlot(slot, groupId);
            return resolved > 0 && package.LoadLevel(resolved);
        }

        public static bool PreloadLevel(GameObject go, int slot, string groupId = null)
        {
            var package = Package(go);
            if (package == null) return false;
            int resolved = string.IsNullOrEmpty(groupId) ? slot
                : package.ResolveGroupSlot(slot, groupId);
            return resolved > 0 && package.PreloadLevel(resolved);
        }

        public static bool LevelReady(GameObject go, int slot, string groupId = null)
        {
            var package = Package(go);
            if (package == null) return false;
            int resolved = string.IsNullOrEmpty(groupId) ? slot
                : package.ResolveGroupSlot(slot, groupId);
            return resolved > 0 && package.IsLevelReady(resolved);
        }

        public static bool LevelLoading(GameObject go) => Package(go)?.IsChangingLevel ?? false;
        public static string LevelError(GameObject go) => Package(go)?.LastLevelError;

        public static bool OnLevelLoaded(GameObject go, Action<int> callback)
        {
            var package = Package(go);
            if (package == null || callback == null) return false;
            package.LevelActivated += callback;
            return true;
        }

        public static bool OffLevelLoaded(GameObject go, Action<int> callback)
        {
            var package = Package(go);
            if (package == null || callback == null) return false;
            package.LevelActivated -= callback;
            return true;
        }

        public static bool OnLevelFailed(GameObject go, Action<int> callback)
        {
            var package = Package(go);
            if (package == null || callback == null) return false;
            package.LevelChangeFailed += callback;
            return true;
        }

        public static bool OffLevelFailed(GameObject go, Action<int> callback)
        {
            var package = Package(go);
            if (package == null || callback == null) return false;
            package.LevelChangeFailed -= callback;
            return true;
        }

        public static void ShowOverlay(GameObject go, bool visible)
            => Package(go)?.ShowOverlay(visible);

        public static void SetOverlayAutoVisibility(GameObject go, bool enabled)
            => Package(go)?.SetOverlayAutoVisibility(enabled);

        /// <summary>
        /// Install a scope-local dp facade after self is injected but before the
        /// creator script executes. Lua functions can then omit game IDs and caller
        /// arguments, while the global dp table remains useful from console code.
        /// </summary>
        public static void BindScope(LuaTable scriptScope)
        {
            if (scriptScope == null) return;
            LuaBehaviour.GetLuaEnv().DoString(@"
local base = dp or {}
dp = setmetatable({
    package = function() return dp_package(self.gameObject) end,
    container = function() return dp_container(self.gameObject) end,
    container_object = function() return dp_container_object(self.gameObject) end,
    game_manager = function() return dp_container(self.gameObject) end,
    game_manager_object = function() return dp_container_object(self.gameObject) end,
    button_press = function() return dp_button_press(self.gameObject) end,
    is_sequence = function() return dp_is_sequence(self.gameObject) end,
    is_adventure = function() return dp_is_adventure(self.gameObject) end,
    is_arena = function() return dp_is_arena(self.gameObject) end,
    current_level_index = function() return dp_current_level_index(self.gameObject) end,
    level_count = function(groupId) return dp_level_count(self.gameObject, groupId or '') end,
    groups = function() return dp_groups(self.gameObject) end,
    levels = function(groupId) return dp_levels(self.gameObject, groupId or '') end,
    level_slot = function(index, groupId) return dp_level_slot(self.gameObject, index, groupId or '') end,
    load_level = function(slot, groupId) return dp_load_level(self.gameObject, slot, groupId or '') end,
    preload_level = function(slot, groupId) return dp_preload_level(self.gameObject, slot, groupId or '') end,
    level_ready = function(slot, groupId) return dp_level_ready(self.gameObject, slot, groupId or '') end,
    level_loading = function() return dp_level_loading(self.gameObject) end,
    level_error = function() return dp_level_error(self.gameObject) end,
    on_level_loaded = function(fn) return dp_on_level_loaded(self.gameObject, fn) end,
    off_level_loaded = function(fn) return dp_off_level_loaded(self.gameObject, fn) end,
    on_level_failed = function(fn) return dp_on_level_failed(self.gameObject, fn) end,
    off_level_failed = function(fn) return dp_off_level_failed(self.gameObject, fn) end,
    show_overlay = function(visible) dp_show_overlay(self.gameObject, visible) end,
    overlay_auto_visibility = function(enabled) dp_overlay_auto_visibility(self.gameObject, enabled) end,
    next_level = function()
        local c = dp_container(self.gameObject)
        if c == nil or c.next_level == nil then return false end
        return c.next_level()
    end,
    previous_level = function()
        local c = dp_container(self.gameObject)
        if c == nil or c.previous_level == nil then return false end
        return c.previous_level()
    end
}, {__index = base})
", "dreampark.package_scope", scriptScope);
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

        // ── dp.me() / dp.peers() / dp.peer() / dp.set_state() ────────
        //
        // REPLACES, in shipped content: every game's own pose stream —
        // Player.prefab scripts sending head/hand positions at some rate, in
        // some frame, and a mirror script on every other headset dragging a
        // marker around after them. That was the whole class of bug behind
        // LaserTag's inverted players (ParkRoot.cs has the story), and a tax
        // on every multiplayer game before it. The SDK streams every headset's
        // head and hands ONCE, park-local (PlayerPresence.cs); a game reads
        // them here; what other players SEE is the RemoteRig subtree of a
        // game's Player.prefab, cloned once per peer (RemoteRig.cs).
        //
        // Plain LuaTables of primitives, Transforms and GameObjects — one
        // table per peer, kept by its RemotePlayer and refreshed in place, so
        // polling every frame allocates nothing but the array.
        public static string Me() => PlayerPresence.MyId;

        public static LuaTable Peers()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var arr = env.NewTable();
            var pp = PlayerPresence.Instance;
            if (pp == null) return arr;
            _peerSort.Clear();
            foreach (var kv in pp.Peers) if (kv.Value != null) _peerSort.Add(kv.Value);
            _peerSort.Sort(ByJoin);
            for (int i = 0; i < _peerSort.Count; i++) arr.Set(i + 1, _peerSort[i].LuaView(env));
            _peerSort.Clear();
            return arr;
        }
        static readonly List<RemotePlayer> _peerSort = new List<RemotePlayer>();
        static readonly Comparison<RemotePlayer> ByJoin = (a, b) =>
        {
            int c = a.joinedAt.CompareTo(b.joinedAt);
            return c != 0 ? c : string.CompareOrdinal(a.id, b.id);
        };

        public static LuaTable Peer(string id)
        {
            var pp = PlayerPresence.Instance;
            var p = pp != null ? pp.GetPeer(id) : null;
            return p != null ? p.LuaView(LuaBehaviour.GetLuaEnv()) : null;
        }

        public static void SetState(string json)
        {
            var pp = PlayerPresence.Instance;
            if (pp != null) pp.SetStateJson(json);
        }

        public static int PeerEventCount()
        {
            var pp = PlayerPresence.Instance;
            return pp != null ? pp.PendingEventCount : 0;
        }

        public static LuaTable PeerEvents()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var arr = env.NewTable();
            var pp = PlayerPresence.Instance;
            if (pp == null) return arr;
            var evs = pp.DrainEvents();
            for (int i = 0; i < evs.Count; i++)
            {
                var e = env.NewTable();
                e.Set("kind", evs[i].kind);
                e.Set("id",   evs[i].id);
                if (evs[i].peer != null) e.Set("peer", evs[i].peer.LuaView(env));
                arr.Set(i + 1, e);
            }
            return arr;
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
                env.Global.Set("dp_package",          new Func<GameObject, GameObject>(PackageObject));
                env.Global.Set("dp_container",        new Func<GameObject, LuaTable>(ContainerScope));
                env.Global.Set("dp_container_object", new Func<GameObject, GameObject>(ContainerObject));
                env.Global.Set("dp_button_press",     new Func<GameObject, bool>(PressButton));
                env.Global.Set("dp_is_sequence",      new Func<GameObject, bool>(IsSequence));
                env.Global.Set("dp_is_adventure",     new Func<GameObject, bool>(IsAdventure));
                env.Global.Set("dp_is_arena",         new Func<GameObject, bool>(IsArena));
                env.Global.Set("dp_current_level_index", new Func<GameObject, int>(CurrentLevelIndex));
                env.Global.Set("dp_level_count",      new Func<GameObject, string, int>(LevelCount));
                env.Global.Set("dp_groups",           new Func<GameObject, LuaTable>(Groups));
                env.Global.Set("dp_levels",           new Func<GameObject, string, LuaTable>(Levels));
                env.Global.Set("dp_level_slot",       new Func<GameObject, int, string, int>(LevelSlot));
                env.Global.Set("dp_load_level",       new Func<GameObject, int, string, bool>(LoadLevel));
                env.Global.Set("dp_preload_level",    new Func<GameObject, int, string, bool>(PreloadLevel));
                env.Global.Set("dp_level_ready",      new Func<GameObject, int, string, bool>(LevelReady));
                env.Global.Set("dp_level_loading",    new Func<GameObject, bool>(LevelLoading));
                env.Global.Set("dp_level_error",      new Func<GameObject, string>(LevelError));
                env.Global.Set("dp_on_level_loaded",  new Func<GameObject, Action<int>, bool>(OnLevelLoaded));
                env.Global.Set("dp_off_level_loaded", new Func<GameObject, Action<int>, bool>(OffLevelLoaded));
                env.Global.Set("dp_on_level_failed",  new Func<GameObject, Action<int>, bool>(OnLevelFailed));
                env.Global.Set("dp_off_level_failed", new Func<GameObject, Action<int>, bool>(OffLevelFailed));
                env.Global.Set("dp_show_overlay",     new Action<GameObject, bool>(ShowOverlay));
                env.Global.Set("dp_overlay_auto_visibility", new Action<GameObject, bool>(SetOverlayAutoVisibility));
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
                env.Global.Set("dp_park",             new Func<Transform>(Park));
                env.Global.Set("dp_me",               new Func<string>(Me));
                env.Global.Set("dp_peers",            new Func<LuaTable>(Peers));
                env.Global.Set("dp_peer",             new Func<string, LuaTable>(Peer));
                env.Global.Set("dp_set_state",        new Action<string>(SetState));
                env.Global.Set("dp_peer_event_count", new Func<int>(PeerEventCount));
                env.Global.Set("dp_peer_events",      new Func<LuaTable>(PeerEvents));

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
-- Console/non-LuaBehaviour callers pass a GameObject explicitly. Each
-- LuaBehaviour gets a bound facade where these arguments are automatic.
dp.package     = function(go) return dp_package(go) end
dp.container   = function(go) return dp_container(go) end
dp.container_object = function(go) return dp_container_object(go) end
dp.game_manager = function(go) return dp_container(go) end
dp.game_manager_object = function(go) return dp_container_object(go) end
dp.button_press = function(go) return dp_button_press(go) end
dp.is_sequence = function(go) return dp_is_sequence(go) end
dp.is_adventure = function(go) return dp_is_adventure(go) end
dp.is_arena = function(go) return dp_is_arena(go) end
dp.current_level_index = function(go) return dp_current_level_index(go) end
dp.level_count = function(go, groupId) return dp_level_count(go, groupId or '') end
dp.groups = function(go) return dp_groups(go) end
dp.levels = function(go, groupId) return dp_levels(go, groupId or '') end
dp.level_slot = function(go, index, groupId) return dp_level_slot(go, index, groupId or '') end
dp.load_level  = function(go, slot, groupId) return dp_load_level(go, slot, groupId or '') end
dp.preload_level = function(go, slot, groupId) return dp_preload_level(go, slot, groupId or '') end
dp.level_ready = function(go, slot, groupId) return dp_level_ready(go, slot, groupId or '') end
dp.level_loading = function(go) return dp_level_loading(go) end
dp.level_error = function(go) return dp_level_error(go) end
dp.on_level_loaded = function(go, fn) return dp_on_level_loaded(go, fn) end
dp.off_level_loaded = function(go, fn) return dp_off_level_loaded(go, fn) end
dp.on_level_failed = function(go, fn) return dp_on_level_failed(go, fn) end
dp.off_level_failed = function(go, fn) return dp_off_level_failed(go, fn) end
dp.show_overlay = function(go, visible) dp_show_overlay(go, visible) end
dp.overlay_auto_visibility = function(go, enabled) dp_overlay_auto_visibility(go, enabled) end

-- Where the player's hand is, right now. side is 'left' / 'right', or
-- omitted for the rig's own hand anchor. May be nil before the rig exists.
dp.hand        = function(side) return dp_hand(side or '') end
dp.hands       = function()   return dp_hand('left'), dp_hand('right') end

-- The shared park frame. Unity world space is PER HEADSET on Quest, so
-- every position or direction that crosses the relay goes out as park-local
-- (dp.to_park) and comes back into this headset's world (dp.from_park).
-- Typed net_send / onmessage and dp.peers() do this for you; these are for
-- content that still formats its own JSON. All identity when there is no park.
dp.park        = function()   return dp_park() end

dp.to_park = function(v)
    if v == nil then return nil end
    local park = dp_park()
    if park == nil then return v end
    return park:InverseTransformPoint(v)
end
dp.from_park = function(v)
    if v == nil then return nil end
    local park = dp_park()
    if park == nil then return v end
    return park:TransformPoint(v)
end
dp.to_park_dir = function(d)
    if d == nil then return nil end
    local park = dp_park()
    if park == nil then return d end
    return park:InverseTransformDirection(d)
end
dp.from_park_dir = function(d)
    if d == nil then return nil end
    local park = dp_park()
    if park == nil then return d end
    return park:TransformDirection(d)
end
dp.to_park_rot = function(q)
    if q == nil then return nil end
    local park = dp_park()
    if park == nil then return q end
    return CS.UnityEngine.Quaternion.Inverse(park.rotation) * q
end
dp.from_park_rot = function(q)
    if q == nil then return nil end
    local park = dp_park()
    if park == nil then return q end
    return park.rotation * q
end
-- Yaw in degrees, Unity convention (0 = +Z, 90 = +X). Through a direction
-- rather than subtracting euler Y, so a park synced to a wall QR code (full
-- rotation, not yaw-only) still round-trips.
local function __dp_yaw_to_dir(deg)
    local r = math.rad(deg or 0)
    return CS.UnityEngine.Vector3(math.sin(r), 0, math.cos(r))
end
local function __dp_dir_to_yaw(d, fallback)
    if d == nil then return fallback end
    if d.x * d.x + d.z * d.z < 1e-6 then return fallback end
    return math.deg(math.atan(d.x, d.z))
end
dp.to_park_yaw = function(deg)
    if dp_park() == nil then return deg end
    return __dp_dir_to_yaw(dp.to_park_dir(__dp_yaw_to_dir(deg)), deg)
end
dp.from_park_yaw = function(deg)
    if dp_park() == nil then return deg end
    return __dp_dir_to_yaw(dp.from_park_dir(__dp_yaw_to_dir(deg)), deg)
end

-- ── Typed wire format ────────────────────────────────────
-- net_send(kind, table) and onmessage(kind, payload) carry Unity values
-- across the relay without the creator formatting JSON or thinking about
-- coordinate frames. On the way OUT every Vector3 is a POINT and goes as
-- park-local; wrap one in dp.dir(v) to send it as a DIRECTION; Quaternions
-- and Transforms (position + rotation) convert too; Colors pass as-is. On
-- the way IN they come back as Unity values in THIS headset's world. Plain
-- numbers, strings, booleans and nested tables pass through unchanged.
-- Wire shape, for anyone reading a packet: {'$p':[x,y,z]} point,
-- {'$d':[x,y,z]} direction, {'$q':[x,y,z,w]} rotation,
-- {'$t':[px,py,pz,qx,qy,qz,qw]} pose, {'$c':[r,g,b,a]} colour.
local __dpq = string.char(34)   -- the double-quote character

local function __dp_esc(s)
    s = string.gsub(s, '\\', '\\\\')
    s = string.gsub(s, __dpq, '\\' .. __dpq)
    s = string.gsub(s, '\n', '\\n')
    s = string.gsub(s, '\r', '\\r')
    s = string.gsub(s, '\t', '\\t')
    s = string.gsub(s, '%c', function(c) return string.format('\\u%04x', string.byte(c)) end)
    return __dpq .. s .. __dpq
end

local function __dp_num(n)
    if n ~= n or n == math.huge or n == -math.huge then return '0' end
    if math.type(n) == 'integer' or (n == math.floor(n) and math.abs(n) < 1e15) then
        return string.format('%d', n)
    end
    return string.format('%.10g', n)
end

local function __dp_has(v, k)
    local ok, r = pcall(function() return v[k] ~= nil end)
    return ok and r == true
end

-- What a Unity value is, by structure. XLua userdata has no portable type
-- query from Lua, and a missing member may raise or return nil depending on
-- the wrapper, so both are treated as absent.
local function __dp_kind(v)
    if __dp_has(v, 'position') and __dp_has(v, 'rotation') then return 'transform' end
    if __dp_has(v, 'x') and __dp_has(v, 'z') then
        if __dp_has(v, 'w') then return 'quat' end
        return 'vec3'
    end
    if __dp_has(v, 'r') and __dp_has(v, 'a') then return 'color' end
    if __dp_has(v, 'transform') then return 'gameobject' end
    return nil
end

local function __dp_v3(v, fmt)
    return '[' .. string.format(fmt, v.x) .. ',' .. string.format(fmt, v.y) .. ',' .. string.format(fmt, v.z) .. ']'
end

local function __dp_tag(tag, body)
    return '{' .. __dpq .. tag .. __dpq .. ':' .. body .. '}'
end

local function __dp_is_array(t)
    local n = #t
    if n == 0 then return false end
    for k in pairs(t) do
        if math.type(k) ~= 'integer' or k < 1 or k > n then return false end
    end
    return true
end

local __dp_pack_value
local function __dp_pack_unity(v, kind, depth)
    if kind == 'vec3' then
        return __dp_tag('$p', __dp_v3(dp.to_park(v), '%.3f'))
    elseif kind == 'quat' then
        local q = dp.to_park_rot(v)
        return __dp_tag('$q', string.format('[%.4f,%.4f,%.4f,%.4f]', q.x, q.y, q.z, q.w))
    elseif kind == 'transform' then
        local p = dp.to_park(v.position)
        local q = dp.to_park_rot(v.rotation)
        return __dp_tag('$t', string.format('[%.3f,%.3f,%.3f,%.4f,%.4f,%.4f,%.4f]',
            p.x, p.y, p.z, q.x, q.y, q.z, q.w))
    elseif kind == 'gameobject' then
        return __dp_pack_unity(v.transform, 'transform', depth)
    elseif kind == 'color' then
        return __dp_tag('$c', string.format('[%.3f,%.3f,%.3f,%.3f]', v.r, v.g, v.b, v.a))
    end
    return nil
end

__dp_pack_value = function(v, depth)
    local tv = type(v)
    if tv == 'nil' then return 'null' end
    if tv == 'boolean' then return v and 'true' or 'false' end
    if tv == 'number' then return __dp_num(v) end
    if tv == 'string' then return __dp_esc(v) end
    if depth > 8 then return 'null' end
    if tv == 'userdata' or (tv == 'table' and getmetatable(v) ~= nil) then
        local kind = __dp_kind(v)
        if kind ~= nil then return __dp_pack_unity(v, kind, depth) end
        if tv == 'userdata' then
            print('[dp.pack] cannot serialize a ' .. tostring(v) .. '; sent as null')
            return 'null'
        end
    end
    if tv == 'table' then
        local d = rawget(v, '$d')
        if d ~= nil then
            return __dp_tag('$d', __dp_v3(dp.to_park_dir(d), '%.4f'))
        end
        if __dp_is_array(v) then
            local parts = {}
            for i = 1, #v do parts[i] = __dp_pack_value(v[i], depth + 1) end
            return '[' .. table.concat(parts, ',') .. ']'
        end
        local parts = {}
        for k, val in pairs(v) do
            parts[#parts + 1] = __dp_esc(tostring(k)) .. ':' .. __dp_pack_value(val, depth + 1)
        end
        return '{' .. table.concat(parts, ',') .. '}'
    end
    return 'null'
end

-- A Vector3 that means a direction, not a point (no translation on the wire).
dp.dir = function(v) return { ['$d'] = v } end

-- Lua table -> wire JSON, park-local. net_send(kind, table) calls this for you.
dp.pack = function(t)
    if type(t) ~= 'table' then return '{}' end
    return __dp_pack_value(t, 0)
end

local __dp_unpack_value
__dp_unpack_value = function(v, depth)
    if type(v) ~= 'table' or depth > 8 then return v end
    local p = rawget(v, '$p')
    if p ~= nil then return dp.from_park(CS.UnityEngine.Vector3(p[1] or 0, p[2] or 0, p[3] or 0)) end
    local d = rawget(v, '$d')
    if d ~= nil then return dp.from_park_dir(CS.UnityEngine.Vector3(d[1] or 0, d[2] or 0, d[3] or 0)) end
    local q = rawget(v, '$q')
    if q ~= nil then return dp.from_park_rot(CS.UnityEngine.Quaternion(q[1] or 0, q[2] or 0, q[3] or 0, q[4] or 1)) end
    local tr = rawget(v, '$t')
    if tr ~= nil then
        local pos = dp.from_park(CS.UnityEngine.Vector3(tr[1] or 0, tr[2] or 0, tr[3] or 0))
        local rot = dp.from_park_rot(CS.UnityEngine.Quaternion(tr[4] or 0, tr[5] or 0, tr[6] or 0, tr[7] or 1))
        return { position = pos, rotation = rot, forward = rot * CS.UnityEngine.Vector3.forward }
    end
    local c = rawget(v, '$c')
    if c ~= nil then return CS.UnityEngine.Color(c[1] or 0, c[2] or 0, c[3] or 0, c[4] or 1) end
    for k, val in pairs(v) do
        if type(val) == 'table' then v[k] = __dp_unpack_value(val, depth + 1) end
    end
    return v
end

-- Parsed wire table -> Unity values in this headset's world. onmessage gets
-- this for free; call it yourself on json_parse(raw).payload inside onnet.
dp.unpack = function(t) return __dp_unpack_value(t, 0) end

-- ── Players in the park ──────────────────────────────────
-- The SDK streams every headset's head and hands (PlayerPresence.cs),
-- park-local, on a fixed budget. No game sends player positions, ever.
-- What other players SEE of you is the RemoteRig subtree of your
-- Player.prefab, cloned for each peer (RemoteRig.cs); scripts inside it
-- boot on the clones with peer_id = that player's id.
--   dp.me()          -> my id (stable per headset)
--   dp.peers()       -> array (join order) of { id, game, head, left, right,
--                        hand, active_hand, left_tracked, right_tracked,
--                        seen, age, state, rig, name }
--                        head/left/right/hand: Transforms in THIS headset's
--                        world; state: the table they dp.set_state()d, Unity
--                        values restored; rig: their RemoteRig clone, or
--                        nil; name: their display name (the SDK floats it
--                        over their head by default — RemoteNameTag.cs)
--   dp.peer(id)      -> one of the above, or nil (you are not your own peer)
--   dp.set_state(t)  -> publish a small table to everyone (colour, team,
--                        score). Resent to late joiners; keep it small.
--   dp.on_peer_join(fn(id, peer), owner)  fires for everyone already here,
--                        then for each arrival. owner (self, a GameObject) is
--                        optional: the handler is dropped when it is destroyed.
--   dp.on_peer_leave(fn(id), owner)
--   dp.off_peer_join(fn) / dp.off_peer_leave(fn)
local function __dp_peer_view(p)
    if p ~= nil and p.state ~= nil and dp.unpack ~= nil then p.state = dp.unpack(p.state) end
    return p
end
dp.me        = function()   return dp_me() end
dp.peers     = function()
    local list = dp_peers()
    for i = 1, #list do __dp_peer_view(list[i]) end
    return list
end
dp.peer      = function(id) if id == nil then return nil end return __dp_peer_view(dp_peer(id)) end
dp.set_state = function(t)  dp_set_state(dp.pack(t or {})) end

__dp_peer_join_fns  = __dp_peer_join_fns  or {}
__dp_peer_leave_fns = __dp_peer_leave_fns or {}

local function __dp_owner_gone(owner)
    if owner == nil then return false end
    local ok, gone = pcall(function() return owner:Equals(nil) end)
    return ok and gone == true
end

local function __dp_remove_fn(list, fn)
    for i = #list, 1, -1 do
        if list[i].fn == fn then table.remove(list, i) end
    end
end

dp.on_peer_join = function(fn, owner)
    if fn == nil then return end
    local h = { fn = fn, owner = owner, seen = {} }
    __dp_peer_join_fns[#__dp_peer_join_fns + 1] = h
    dp_ensure_pump()
    local list = dp.peers()
    for i = 1, #list do
        local p = list[i]
        h.seen[p.id] = true
        local ok, err = pcall(fn, p.id, p)
        if not ok then print('[dp.on_peer_join] handler threw: ' .. tostring(err)) end
    end
end
dp.on_peer_leave = function(fn, owner)
    if fn == nil then return end
    __dp_peer_leave_fns[#__dp_peer_leave_fns + 1] = { fn = fn, owner = owner }
    dp_ensure_pump()
end
dp.off_peer_join  = function(fn) __dp_remove_fn(__dp_peer_join_fns, fn) end
dp.off_peer_leave = function(fn) __dp_remove_fn(__dp_peer_leave_fns, fn) end

local function __dp_prune_fns(list)
    for i = #list, 1, -1 do
        if __dp_owner_gone(list[i].owner) then table.remove(list, i) end
    end
end

function __dp_pump_peers()
    if dp_peer_event_count() == 0 then return end
    __dp_prune_fns(__dp_peer_join_fns)
    __dp_prune_fns(__dp_peer_leave_fns)
    local evs = dp_peer_events()
    for i = 1, #evs do
        local e = evs[i]
        if e.kind == 'join' then
            local p = __dp_peer_view(e.peer)
            for j = 1, #__dp_peer_join_fns do
                local h = __dp_peer_join_fns[j]
                if not h.seen[e.id] then
                    h.seen[e.id] = true
                    local ok, err = pcall(h.fn, e.id, p)
                    if not ok then print('[dp.on_peer_join] handler threw: ' .. tostring(err)) end
                end
            end
        else
            for j = 1, #__dp_peer_join_fns do __dp_peer_join_fns[j].seen[e.id] = nil end
            for j = 1, #__dp_peer_leave_fns do
                local ok, err = pcall(__dp_peer_leave_fns[j].fn, e.id)
                if not ok then print('[dp.on_peer_leave] handler threw: ' .. tostring(err)) end
            end
        end
    end
end

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
    __dp_pump_peers()
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
