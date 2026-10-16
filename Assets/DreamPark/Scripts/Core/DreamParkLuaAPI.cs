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
            var over = PlayerHead.Override;   // see PlayerHead.cs
            if (over != null) return over;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        // Oct 11 2026 (core-playergate): what LUA sees as the head / Camera.main. While the
        // host says the local player is not in the park (PlayerHead.LocalPlayerAbsent) it is
        // PlayerHead's stand-in (limbo, heals at the landing); otherwise Head() / Camera.main.
        // PlayerPresence and GameArea keep reading Head() / PlayerHead.Override.
        public static Transform ContentHead()
        {
            if (!PlayerHead.LocalPlayerAbsent) return Head();
            var cam = PlayerHead.ContentCamera();
            return cam != null ? cam.transform : null;
        }

        public static Camera ContentCamera() => PlayerHead.ContentCamera();
        public static bool LocalPlayerPresent() => PlayerHead.LocalPlayerPresent;

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

        public static bool OnLevelPresented(GameObject go, Action<int> callback)
        {
            var package = Package(go);
            if (package == null || callback == null) return false;
            package.LevelPresented += callback;
            return true;
        }

        public static bool OffLevelPresented(GameObject go, Action<int> callback)
        {
            var package = Package(go);
            if (package == null || callback == null) return false;
            package.LevelPresented -= callback;
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
        /// Oct 11 2026 (occurrence teardown): hand one occurrence removal to the
        /// Lua handlers registered with dp.on_occurrence_removed (pure Lua
        /// storage, called synchronously — no C#→Lua delegate type, IL2CPP-safe).
        /// Called by <see cref="DreamParkPackageHost.RaiseOccurrenceRemoved"/>.
        /// </summary>
        public static void DispatchOccurrenceRemoved(GameObject host, string occurrenceId,
            string resource, GameObject anchor)
        {
            LuaFunction fn = null;
            try
            {
                LuaEnv env = LuaBehaviour.GetLuaEnv();
                fn = env?.Global.Get<LuaFunction>("__dp_occurrence_removed");
                fn?.Call(host, occurrenceId ?? "", resource ?? "", anchor);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DreamParkLuaAPI] dp.on_occurrence_removed dispatch failed: " + e.Message);
            }
            finally { fn?.Dispose(); }
        }

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
    on_level_presented = function(fn) return dp_on_level_presented(self.gameObject, fn) end,
    off_level_presented = function(fn) return dp_off_level_presented(self.gameObject, fn) end,
    on_level_failed = function(fn) return dp_on_level_failed(self.gameObject, fn) end,
    off_level_failed = function(fn) return dp_off_level_failed(self.gameObject, fn) end,
    -- Oct 11 2026: this package's occurrences only; drops with this script.
    on_occurrence_removed = function(fn, owner) return base.on_occurrence_removed and base.on_occurrence_removed(fn, owner or self, self.gameObject) end,
    -- Gesture handlers registered from a script drop with that script.
    on_tap = function(fn, owner) return base.on_tap and base.on_tap(fn, owner or self) end,
    on_touch = function(fn, owner) return base.on_touch and base.on_touch(fn, owner or self) end,
    on_drag = function(fn, owner) return base.on_drag and base.on_drag(fn, owner or self) end,
    on_pinch = function(fn, owner) return base.on_pinch and base.on_pinch(fn, owner or self) end,
    -- Leader / rank are scoped to this script's title.
    leader = function() return base.leader and base.leader(self.gameObject) end,
    is_leader = function() return base.is_leader and base.is_leader(self.gameObject) end,
    rank = function(id) return base.rank and base.rank(id, self.gameObject) end,
    on_leader_changed = function(fn, owner) return base.on_leader_changed and base.on_leader_changed(fn, owner or self, self.gameObject) end,
    to_attraction = function(v, go) return base.to_attraction and base.to_attraction(v, go or self.gameObject) end,
    from_attraction = function(v, go) return base.from_attraction and base.from_attraction(v, go or self.gameObject) end,
    to_attraction_dir = function(v, go) return base.to_attraction_dir and base.to_attraction_dir(v, go or self.gameObject) end,
    from_attraction_dir = function(v, go) return base.from_attraction_dir and base.from_attraction_dir(v, go or self.gameObject) end,
    -- Bus defaults to this script's attraction; handlers drop with this script.
    shared_state = function(name, go)
        local s = base.shared_state and base.shared_state(name, go, self.gameObject)
        if s == nil then return nil end
        return setmetatable({
            on_change = function(fn, owner) return s.on_change(fn, owner or self) end,
            on_request = function(fn, owner) return s.on_request(fn, owner or self) end,
        }, {__index = s})
    end,
    show_overlay = function(visible) dp_show_overlay(self.gameObject, visible) end,
    -- dp.optimizer_ignore() / dp.optimizer_ignore(false): this script's object.
    -- An explicit nil target stays a no-op (-1), never 'self'.
    optimizer_ignore = function(...)
        local go, on = ...
        if select('#', ...) == 0 then go = self.gameObject
        elseif type(go) == 'boolean' then on = go; go = self.gameObject end
        return base.optimizer_ignore and base.optimizer_ignore(go, on)
    end,
    rect_openings = function() return dp_rect_openings(self.gameObject) end,
    rect_is_wall = function(side, t) return dp_rect_is_wall(self.gameObject, side, t or 0) end,
    rect_is_open = function(side, t) return dp_rect_is_open(self.gameObject, side, t or 0) end,
    rect_is_opening = function(side, t) return dp_rect_is_opening(self.gameObject, side, t or 0) end,
    rect_is_unknown = function(side, t) return dp_rect_is_unknown(self.gameObject, side, t or 0) end,
    rect_is_open_area = function()
        local o = dp_rect_openings(self.gameObject)
        return o == nil or o.open_area ~= false
    end,
    rect_spans = function(side)
        local o = dp_rect_openings(self.gameObject)
        local s = o and side and o[side]
        return s and s.spans or nil
    end,
    rect_span_at = function(side, t) return base.rect_span_at and base.rect_span_at(self.gameObject, side, t) end,
    rect_point = function(side, t) return dp_rect_point(self.gameObject, side, t or 0) end,
    -- Routes (Docs/ROUTES.md): this script's attraction by default.
    ports = function(go) return base.ports and base.ports(go or self.gameObject) end,
    neighbors = function(go) return base.neighbors and base.neighbors(go or self.gameObject) end,
    rect = function(go) return base.rect and base.rect(go or self.gameObject) end,
    route_plan = function(name, go) return base.route_plan and base.route_plan(name, go or self.gameObject, self.gameObject) end,
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

#if !UNITY_IOS || UNITY_EDITOR
        // The anchor is the OVRHand's parent (same relationship Simulator
        // builds); fall back to the hand transform itself if it is unparented.
        static Transform AnchorOf(OVRHand hand)
        {
            if (hand == null) return null;
            var t = hand.transform;
            return t.parent != null ? t.parent : t;
        }
#endif

        public static Transform Hand(string side)
        {
            bool wantLeft  = side == "left"  || side == "L" || side == "l";
            bool wantRight = side == "right" || side == "R" || side == "r";

#if !UNITY_IOS || UNITY_EDITOR
            // OVRHand refs exist only off iOS (the iOS player has no Meta assemblies,
            // Assets/Editor/IosQuestAssemblyFilter.cs). The fallback below finds the
            // same Left/RightHandAnchor the OVRHand's parent was.
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
#endif

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

        // ── dp.device() / dp.is_phone() / dp.is_headset() / dp.device_of() ──
        //
        // REPLACES: `CS.UnityEngine.Application.platform == ...` checks, and
        // guessing "phone" from a missing hand tracker. The platform is the
        // wrong question — the Editor is neither, and Core decides what a
        // device is (DreamParkDevice.Set at boot). Strings only across the
        // boundary, no codegen.
        internal static string Device() => DreamParkDevice.Wire(DreamParkDevice.Current);
        internal static bool IsPhoneDevice() => DreamParkDevice.IsPhone;
        internal static bool IsHeadsetDevice() => DreamParkDevice.IsHeadset;

        /// <summary>
        /// "phone" / "headset" for another player (carried on their presence
        /// stream), dp.device() for yourself, nil when unknown — a peer on an
        /// older client, or one not heard from yet.
        /// </summary>
        internal static string DeviceOf(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;
            if (playerId == PlayerPresence.MyId) return Device();
            var pp = PlayerPresence.Instance;
            var p = pp != null ? pp.GetPeer(playerId) : null;
            return p != null && !string.IsNullOrEmpty(p.device) ? p.device : null;
        }

        // ── dp.on_tap / on_touch / on_drag / on_pinch ────────────────
        //
        // Gestures are routed by DreamParkGestures (raycast + ontap/ontouch
        // object hooks) and queued for these global handlers. Same pump shape
        // as dp.on_peer_join: C# queue, Lua handler lists, drained once a frame.
        //
        // These bindings (and the device ones above) are internal on purpose:
        // Lua reaches them only through the dp_* delegates registered below,
        // so they never enter the generated XLua wrapper for this class and
        // adding them causes no Gen/ drift.
        internal static int GestureEventCount() => DreamParkGestures.PendingCount;

        internal static LuaTable GestureEvents()
        {
            var env = LuaBehaviour.GetLuaEnv();
            var arr = env.NewTable();
            var evs = DreamParkGestures.DrainPending();
            for (int i = 0; i < evs.Count; i++)
                arr.Set(i + 1, DreamParkGestures.ToLuaTable(env, evs[i]));
            return arr;
        }

        // ── dp.rect_openings() / rect_is_wall / rect_is_open / rect_point ──
        //
        // RECT_EDGES_SPEC.md (v3 spans: wall / opening / unknown). The rect the
        // calling script's attraction is seated on, in the attraction's own
        // frame — so a game (CrashCourse) can line a track up with a door
        // instead of guessing. A plain LuaTable of numbers, strings and
        // Vector3s; internal like the bindings below, so nothing new enters
        // the generated XLua wrapper. No AttractionTemplate above the script →
        // nil. No park data → every side one unknown span.
        internal static AttractionTemplate RectAttraction(GameObject go) =>
            go != null ? go.GetComponentInParent<AttractionTemplate>(true) : null;

        internal static LuaTable RectOpenings(GameObject go)
        {
            var attraction = RectAttraction(go);
            return attraction != null ? RectOpeningsToLua(LuaBehaviour.GetLuaEnv(), attraction) : null;
        }

        /// <summary>
        /// dp.rect_openings() for a live attraction: the data plus what only the
        /// template knows (wave 2, LayoutLab finding 4): `source`
        /// ('park'|'preview'|'default'), `entry_side` (core's Arena entry, else
        /// the attraction's front = its first Entry portal) and per side
        /// `walled` (a wall layer would apply there).
        /// </summary>
        // public (not internal) so SDK editor tests can call it: core grants internals to the
        // editor assembly (SpaceMapPacker.cs InternalsVisibleTo), the SDK does not. BlackList keeps
        // it off the XLua wrapper (Lua reaches it as dp.rect_openings()).
        [BlackList]
        public static LuaTable RectOpeningsToLua(LuaEnv env, AttractionTemplate attraction)
        {
            if (env == null || attraction == null) return null;
            AttractionRectOpenings data = attraction.RectOpenings;
            LuaTable t = RectOpeningsToLua(env, data);
            if (t == null) return null;
            t.Set("source", attraction.RectOpeningsSource);
            if (string.IsNullOrEmpty(data.entrySide))
            {
                AttractionWallSide front = AttractionWallSide.Back;
                IReadOnlyList<AttractionPortal> portals = attraction.CurrentPortals;
                for (int i = 0; i < portals.Count; i++)
                    if (portals[i] != null && portals[i].IsEntry) { front = DreamParkLuaRoutes.SideOfPortal(portals[i].position); break; }
                t.Set("entry_side", AttractionWallSides.Name(front));
                t.Set("entry_reason", "attraction front (first Entry portal)");
            }
            for (int s = 0; s < AttractionRectOpenings.AllSides.Length; s++)
            {
                AttractionWallSide side = AttractionRectOpenings.AllSides[s];
                var st = t.Get<LuaTable>(AttractionWallSides.Name(side));
                st?.Set("walled", attraction.HasWall(side));
            }
            return t;
        }

        // public (not internal) so SDK editor tests can call it: core grants internals to the
        // editor assembly (SpaceMapPacker.cs InternalsVisibleTo), the SDK does not. BlackList keeps
        // it off the XLua wrapper (Lua reaches it as dp.rect_openings()).
        [BlackList]
        public static LuaTable RectOpeningsToLua(LuaEnv env, AttractionRectOpenings data)
        {
            if (env == null || data == null) return null;
            var t = env.NewTable();
            t.Set("rect_id", data.rectId ?? "");
            t.Set("has_data", data.hasData);
            t.Set("width", data.rectSize.x);
            t.Set("depth", data.rectSize.y);
            t.Set("center", new Vector3(data.rectCenter.x, 0f, data.rectCenter.y));
            t.Set("source", data.hasData ? "park" : "default");
            if (!string.IsNullOrEmpty(data.entrySide))
            {
                t.Set("entry_side", data.entrySide);
                t.Set("entry_reason", data.entryReason ?? "");
            }
            // v3 (RECT_EDGES_SPEC.md): `version` 3 = spans; `open_area` = every side all unknown.
            t.Set("version", AttractionRectOpenings.Version);
            t.Set("open_area", data.IsOpenArea);
            var names = env.NewTable();
            for (int s = 0; s < AttractionRectOpenings.AllSides.Length; s++)
            {
                AttractionWallSide side = AttractionRectOpenings.AllSides[s];
                string name = AttractionWallSides.Name(side);
                names.Set(s + 1, name);
                var st = env.NewTable();
                st.Set("side", name);
                st.Set("length", data.Length(side));
                st.Set("kind", data.Kind(side));
                st.Set("outward", AttractionRectOpenings.Outward(side));
                st.Set("wall_height", data.WallHeight(side));
                st.Set("wall_offset", data.WallOffset(side));
                st.Set("walled", false);
                st.Set("full_wall", data.IsFullWall(side));
                st.Set("all_unknown", data.IsAllUnknown(side));
                // spans: the raw v3 list, tiling the side.
                var spans = env.NewTable();
                IReadOnlyList<AttractionRectSpan> sl = data.Spans(side);
                for (int i = 0; i < sl.Count; i++)
                {
                    AttractionRectSpan sp = sl[i];
                    var row = RectRow(env, data, side, sp.a, sp.b);
                    row.Set("kind", sp.kind);
                    if (!string.IsNullOrEmpty(sp.type)) row.Set("type", sp.type);
                    if (sp.height > 0f) row.Set("height", sp.height);
                    if (sp.IsWall) row.Set("offset", sp.offset);
                    if (sp.window) row.Set("window", true);
                    if (sp.Touches) row.Set("touches", sp.touches);
                    spans.Set(i + 1, row);
                }
                st.Set("spans", spans);
                st.Set("openings", RectRows(env, data, side, data.Openings(side), "opening"));
                st.Set("passages", RectRows(env, data, side, data.Passages(side), null));
                st.Set("links", RectRows(env, data, side, data.Links(side), "link"));
                t.Set(name, st);
            }
            t.Set("sides", names);
            return t;
        }

        /// One row of a rect-side list: a, b, width, center (metres along the side) and
        /// p0 / p1 / mid (attraction-local Vector3s).
        static LuaTable RectRow(LuaEnv env, AttractionRectOpenings data, AttractionWallSide side, float a, float b)
        {
            var row = env.NewTable();
            row.Set("a", a);
            row.Set("b", b);
            row.Set("width", Mathf.Max(0f, b - a));
            row.Set("center", (a + b) * 0.5f);
            row.Set("p0", data.LocalPoint(side, a));
            row.Set("p1", data.LocalPoint(side, b));
            row.Set("mid", data.LocalPoint(side, (a + b) * 0.5f));
            return row;
        }

        /// openings ("opening": type door|opening, kind 'opening'), passages (null: type
        /// door|opening|unknown, kind) or links ("link": kind = the span kind under it), each with `to`.
        static LuaTable RectRows(LuaEnv env, AttractionRectOpenings data, AttractionWallSide side,
            IReadOnlyList<AttractionRectOpening> list, string what)
        {
            var arr = env.NewTable();
            int n = 1;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o == null) continue;
                var row = RectRow(env, data, side, o.a, o.b);
                if (what == "link") row.Set("kind", o.type);
                else
                {
                    row.Set("type", o.type ?? AttractionRectOpening.Unknown);
                    row.Set("kind", o.type == AttractionRectOpening.Unknown ? AttractionRectSpan.Unknown : AttractionRectSpan.Opening);
                }
                if (o.LeadsToRect) row.Set("to", o.to);
                arr.Set(n++, row);
            }
            return arr;
        }

        internal static bool RectIsOpening(GameObject go, string side, float t)
        {
            var attraction = RectAttraction(go);
            if (attraction == null || !AttractionWallSides.TryParse(side, out AttractionWallSide parsed)) return false;
            return attraction.RectOpenings.IsOpening(parsed, t);
        }

        internal static bool RectIsUnknown(GameObject go, string side, float t)
        {
            var attraction = RectAttraction(go);
            if (attraction == null || !AttractionWallSides.TryParse(side, out AttractionWallSide parsed)) return true;
            return attraction.RectOpenings.IsUnknown(parsed, t);
        }

        internal static bool RectIsWall(GameObject go, string side, float t)
        {
            var attraction = RectAttraction(go);
            if (attraction == null || !AttractionWallSides.TryParse(side, out AttractionWallSide parsed)) return false;
            return attraction.RectOpenings.IsWall(parsed, t);
        }

        internal static bool RectIsOpen(GameObject go, string side, float t)
        {
            var attraction = RectAttraction(go);
            if (attraction == null || !AttractionWallSides.TryParse(side, out AttractionWallSide parsed)) return true;
            return attraction.RectOpenings.IsOpen(parsed, t);
        }

        internal static Vector3 RectPoint(GameObject go, string side, float t)
        {
            var attraction = RectAttraction(go);
            if (attraction == null || !AttractionWallSides.TryParse(side, out AttractionWallSide parsed)) return Vector3.zero;
            return attraction.RectOpenings.LocalPoint(parsed, t);
        }

        // ── dp.enabled(c) / dp.set_enabled(c, on) ────────────────────
        //
        // REPLACES, in shipped content: reading `renderer.enabled` (or any
        // `.enabled`) in start() to learn how the attraction was authored. The
        // optimizer parks a live-spawned object's components during the load and
        // whenever it is far away, so that read can return the PARK's false —
        // BirthdayBash's Cake Boss recorded its whole rig as "should be off" and
        // never rendered again. These return / set the INTENDED state: the value
        // the unpark will restore while the object is parked (or frozen by the
        // game pause), otherwise the live flag. Writes while parked land at the
        // unpark (LevelObjectManager.TryGetIntendedEnabled has the rules).
        //
        // `object`, not Component: anything a script passes (nil, a GameObject,
        // a table) answers "no enabled flag" instead of throwing in the
        // marshaller. Internal + delegates like the bindings below: nothing new
        // enters the generated XLua wrapper, so no Gen/ drift.
        /// <summary>1 / 0 = intended enabled / disabled; -1 = not a live component with an enabled flag.</summary>
        internal static int IntendedEnabled(object component)
        {
            var c = component as Component;
            if (c == null) return -1;
            return global::DreamPark.ParkBuilder.LevelObjectManager.TryGetIntendedEnabled(c, out bool on) ? (on ? 1 : 0) : -1;
        }

        /// <summary>False when the argument is not a live component with an enabled flag.</summary>
        internal static bool SetIntendedEnabled(object component, bool on)
        {
            var c = component as Component;
            return c != null && global::DreamPark.ParkBuilder.LevelObjectManager.SetIntendedEnabled(c, on);
        }

        // ── dp.optimizer_ignore(go, on) ──────────────────────────────
        //
        // The runtime half of `-- @optimizer-ignore` (LevelObjectManager has the
        // rules): exempt a manager / always-on system from distance culling, or
        // hand it back. A Component is accepted for its GameObject; anything else
        // (nil included) changes nothing. Internal + delegate: no Gen/ drift.
        /// <summary>Registered LevelObjects changed (0 = none yet: remembered for registration), -1 = not an object.</summary>
        internal static int OptimizerIgnore(object target, bool on)
        {
            var go = target as GameObject;
            if (go == null && target is Component c && c != null) go = c.gameObject;
            if (go == null) return -1;
            return global::DreamPark.ParkBuilder.LevelObjectManager.SetOptimizerIgnore(go, on);
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
            // Play mode only: DontDestroyOnLoad throws in edit mode (EditMode tests, Editor
            // tooling calling dp.* handlers), and nothing pumps there anyway — an edit-time
            // pump would survive into the next Play session as a second pump. Edit-mode
            // callers pump by hand (__dp_pump_net / __dp_pump_globals).
            if (_pump != null || !Application.isPlaying) return;
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
                env.Global.Set("dp_head",             new Func<Transform>(ContentHead));
                env.Global.Set("dp_content_camera",   new Func<Camera>(ContentCamera));
                env.Global.Set("dp_local_player_present", new Func<bool>(LocalPlayerPresent));
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
                env.Global.Set("dp_on_level_presented",  new Func<GameObject, Action<int>, bool>(OnLevelPresented));
                env.Global.Set("dp_off_level_presented", new Func<GameObject, Action<int>, bool>(OffLevelPresented));
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
                env.Global.Set("dp_device",           new Func<string>(Device));
                env.Global.Set("dp_is_phone",         new Func<bool>(IsPhoneDevice));
                env.Global.Set("dp_is_headset",       new Func<bool>(IsHeadsetDevice));
                env.Global.Set("dp_device_of",        new Func<string, string>(DeviceOf));
                env.Global.Set("dp_gesture_event_count", new Func<int>(GestureEventCount));
                env.Global.Set("dp_gesture_events",   new Func<LuaTable>(GestureEvents));
                env.Global.Set("dp_rect_openings",    new Func<GameObject, LuaTable>(RectOpenings));
                env.Global.Set("dp_rect_is_wall",     new Func<GameObject, string, float, bool>(RectIsWall));
                env.Global.Set("dp_rect_is_open",     new Func<GameObject, string, float, bool>(RectIsOpen));
                env.Global.Set("dp_rect_is_opening",  new Func<GameObject, string, float, bool>(RectIsOpening));
                env.Global.Set("dp_rect_is_unknown",  new Func<GameObject, string, float, bool>(RectIsUnknown));
                env.Global.Set("dp_rect_point",       new Func<GameObject, string, float, Vector3>(RectPoint));
                env.Global.Set("dp_enabled",          new Func<object, int>(IntendedEnabled));
                env.Global.Set("dp_set_enabled",      new Func<object, bool, bool>(SetIntendedEnabled));
                env.Global.Set("dp_optimizer_ignore", new Func<object, bool, int>(OptimizerIgnore));

                // No double quotes inside this verbatim block.
                env.DoString(@"
dp = dp or {}

dp.is_player   = function(x)  if x == nil then return false end
                              local ok, r = pcall(function() return dp_is_player(x) end)
                              if ok then return r end
                              local ok2, r2 = pcall(function() return dp_is_player_go(x) end)
                              return ok2 and r2 or false end
-- dp.player() is nil whenever there is no rig: before it loads, and for the
-- whole session on a phone whose title has no Phone Player (phones watch by
-- default). Nil-check it; put title-wide systems on the Game Manager.
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
-- Same frame the new level becomes visible (dp.on_level_loaded is one drawn
-- frame later in a Sequence, after the new level's floor is built).
dp.on_level_presented = function(go, fn) return dp_on_level_presented(go, fn) end
dp.off_level_presented = function(go, fn) return dp_off_level_presented(go, fn) end
dp.on_level_failed = function(go, fn) return dp_on_level_failed(go, fn) end
dp.off_level_failed = function(go, fn) return dp_off_level_failed(go, fn) end
dp.show_overlay = function(go, visible) dp_show_overlay(go, visible) end
dp.overlay_auto_visibility = function(go, enabled) dp_overlay_auto_visibility(go, enabled) end

-- Rect edges (RECT_EDGES_SPEC.md, v3): the rect this attraction is seated on,
-- per side ('front'/'back'/'right'/'left'), in the attraction's own frame, as
-- spans of 'wall' | 'opening' | 'unknown'. Inside a script the go argument is
-- automatic (dp.rect_openings()). No park data = every side all unknown.
-- rect_is_open = not a wall (opening or unknown: passable).
dp.rect_openings = function(go) return dp_rect_openings(go) end
dp.rect_is_wall = function(go, side, t) return dp_rect_is_wall(go, side, t or 0) end
dp.rect_is_open = function(go, side, t) return dp_rect_is_open(go, side, t or 0) end
dp.rect_is_opening = function(go, side, t) return dp_rect_is_opening(go, side, t or 0) end
dp.rect_is_unknown = function(go, side, t) return dp_rect_is_unknown(go, side, t or 0) end
dp.rect_is_open_area = function(go)
    local o = dp_rect_openings(go)
    return o == nil or o.open_area ~= false
end
dp.rect_spans = function(go, side)
    local o = dp_rect_openings(go)
    local s = o and side and o[side]
    return s and s.spans or nil
end
-- The span at metre t of a side ({a, b, kind, type, touches, ...}); a boundary reads the higher
-- precedence (RoomPlan opening > wall > adjacent opening > unknown). type tells door / opening / adjacent.
dp.rect_span_at = function(go, side, t)
    local spans = dp.rect_spans(go, side)
    if spans == nil then return nil end
    t = t or 0
    local function rank(s)
        if s.kind == 'opening' then return (s.type == 'adjacent') and 1 or 3 end
        return (s.kind == 'wall') and 2 or 0
    end
    local best = nil
    for i = 1, #spans do
        local s = spans[i]
        if t >= s.a - 1e-4 and t <= s.b + 1e-4 and (best == nil or rank(s) > rank(best)) then best = s end
    end
    return best
end
dp.rect_point = function(go, side, t) return dp_rect_point(go, side, t or 0) end

-- The enabled state the GAME intends for a Renderer / Collider / Behaviour
-- (Light, AudioSource, LuaBehaviour, Animator ...). The optimizer parks
-- far-away and still-loading content by switching it off, so reading
-- .enabled in start() can return the park's false; dp.enabled returns the
-- value the unpark will restore, dp.set_enabled writes it (a write while
-- parked lands at the unpark). Live objects: exactly .enabled. nil / false
-- for anything without an enabled flag. Older apps lack these: feature-test
-- with `if dp.enabled then ... end`.
dp.enabled = function(c)
    local r = dp_enabled(c)
    if r < 0 then return nil end
    return r == 1
end
dp.set_enabled = function(c, on) return dp_set_enabled(c, on and true or false) end

-- Keep a manager / always-on system out of DISTANCE culling (like an
-- OptimizedAFIgnore component): on ~= false exempts go and its subtree,
-- restoring anything parked; false hands it back. The load, build mode and
-- the operator pause still park it. Prefer the static `-- @optimizer-ignore`
-- comment in the script, which applies before the first park. Returns the
-- number of registered objects changed (0 = remembered until registration),
-- -1 = go is not an object. Not for props: every exempt object costs frame
-- time all session.
dp.optimizer_ignore = function(go, on) return dp_optimizer_ignore(go, on ~= false) end

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
    -- Never exponent notation (%g gave 1e-05): JSONObject cannot parse it.
    local s = string.format('%.10f', n)
    s = string.gsub(s, '0+$', '')
    s = string.gsub(s, '%.$', '')
    if s == '-0' or s == '' then s = '0' end
    return s
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

-- Park-local AND attraction-relative in one value. Receivers inside an
-- attraction read the $a* form (robust even if two headsets' park frames
-- disagree slightly); older apps and receivers outside an attraction read
-- the park-local one. That pair is the backwards compatibility.
local function __dp_tag2(tag, body, atag, abody)
    if abody == nil then return __dp_tag(tag, body) end
    return '{' .. __dpq .. tag .. __dpq .. ':' .. body .. ',' .. __dpq .. atag .. __dpq .. ':' .. abody .. '}'
end

local function __dp_q4(q)
    return string.format('[%.4f,%.4f,%.4f,%.4f]', q.x, q.y, q.z, q.w)
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
local function __dp_pose7(p, q)
    return string.format('[%.3f,%.3f,%.3f,%.4f,%.4f,%.4f,%.4f]', p.x, p.y, p.z, q.x, q.y, q.z, q.w)
end
local function __dp_pack_unity(v, kind, depth, frame)
    if kind == 'vec3' then
        return __dp_tag2('$p', __dp_v3(dp.to_park(v), '%.3f'),
            '$a', frame ~= nil and __dp_v3(frame:InverseTransformPoint(v), '%.3f') or nil)
    elseif kind == 'quat' then
        return __dp_tag2('$q', __dp_q4(dp.to_park_rot(v)),
            '$aq', frame ~= nil and __dp_q4(CS.UnityEngine.Quaternion.Inverse(frame.rotation) * v) or nil)
    elseif kind == 'transform' then
        local abody = nil
        if frame ~= nil then
            abody = __dp_pose7(frame:InverseTransformPoint(v.position),
                CS.UnityEngine.Quaternion.Inverse(frame.rotation) * v.rotation)
        end
        return __dp_tag2('$t', __dp_pose7(dp.to_park(v.position), dp.to_park_rot(v.rotation)), '$at', abody)
    elseif kind == 'gameobject' then
        return __dp_pack_unity(v.transform, 'transform', depth, frame)
    elseif kind == 'color' then
        return __dp_tag('$c', string.format('[%.3f,%.3f,%.3f,%.3f]', v.r, v.g, v.b, v.a))
    end
    return nil
end

__dp_pack_value = function(v, depth, frame)
    local tv = type(v)
    if tv == 'nil' then return 'null' end
    if tv == 'boolean' then return v and 'true' or 'false' end
    if tv == 'number' then return __dp_num(v) end
    if tv == 'string' then return __dp_esc(v) end
    if depth > 8 then return 'null' end
    if tv == 'userdata' or (tv == 'table' and getmetatable(v) ~= nil) then
        local kind = __dp_kind(v)
        if kind ~= nil then return __dp_pack_unity(v, kind, depth, frame) end
        if tv == 'userdata' then
            print('[dp.pack] cannot serialize a ' .. tostring(v) .. '; sent as null')
            return 'null'
        end
    end
    if tv == 'table' then
        local d = rawget(v, '$d')
        if d ~= nil then
            return __dp_tag2('$d', __dp_v3(dp.to_park_dir(d), '%.4f'),
                '$ad', frame ~= nil and __dp_v3(frame:InverseTransformDirection(d), '%.4f') or nil)
        end
        if __dp_is_array(v) then
            local parts = {}
            for i = 1, #v do parts[i] = __dp_pack_value(v[i], depth + 1, frame) end
            return '[' .. table.concat(parts, ',') .. ']'
        end
        local parts = {}
        for k, val in pairs(v) do
            parts[#parts + 1] = __dp_esc(tostring(k)) .. ':' .. __dp_pack_value(val, depth + 1, frame)
        end
        return '{' .. table.concat(parts, ',') .. '}'
    end
    return 'null'
end

-- A Vector3 that means a direction, not a point (no translation on the wire).
dp.dir = function(v) return { ['$d'] = v } end

-- Lua table -> wire JSON, park-local. net_send(kind, table) calls this for you,
-- with frame = the sending script's attraction (dp_net_frame), so points
-- ALSO travel attraction-relative ($a / $ad / $aq / $at beside $p / $d / $q / $t).
dp.pack = function(t, frame)
    if type(t) ~= 'table' then return '{}' end
    return __dp_pack_value(t, 0, frame)
end
-- Any single value (number, string, Vector3, table...) -> JSON fragment. SDK use.
dp.__packv = function(v, frame) return __dp_pack_value(v, 0, frame) end

local function __dp_vec(a) return CS.UnityEngine.Vector3(a[1] or 0, a[2] or 0, a[3] or 0) end
local function __dp_quat(a, o) o = o or 0 return CS.UnityEngine.Quaternion(a[o + 1] or 0, a[o + 2] or 0, a[o + 3] or 0, a[o + 4] or 1) end

local __dp_unpack_value
__dp_unpack_value = function(v, depth, frame)
    if type(v) ~= 'table' or depth > 8 then return v end
    if frame ~= nil then
        local a = rawget(v, '$a')
        if a ~= nil then return frame:TransformPoint(__dp_vec(a)) end
        local ad = rawget(v, '$ad')
        if ad ~= nil then return frame:TransformDirection(__dp_vec(ad)) end
        local aq = rawget(v, '$aq')
        if aq ~= nil then return frame.rotation * __dp_quat(aq) end
        local at = rawget(v, '$at')
        if at ~= nil then
            local pos = frame:TransformPoint(CS.UnityEngine.Vector3(at[1] or 0, at[2] or 0, at[3] or 0))
            local rot = frame.rotation * __dp_quat(at, 3)
            return { position = pos, rotation = rot, forward = rot * CS.UnityEngine.Vector3.forward }
        end
    end
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
        if type(val) == 'table' then v[k] = __dp_unpack_value(val, depth + 1, frame) end
    end
    return v
end

-- Parsed wire table -> Unity values in this headset's world. onmessage gets
-- this for free (frame = its attraction); call it yourself on
-- json_parse(raw).payload inside onnet: dp.unpack(t, dp_net_frame(self.gameObject)).
dp.unpack = function(t, frame) return __dp_unpack_value(t, 0, frame) end
dp.__unpackv = function(v, frame) return __dp_unpack_value(v, 0, frame) end

-- ── Players in the park ──────────────────────────────────
-- The SDK streams every headset's head and hands (PlayerPresence.cs),
-- park-local, on a fixed budget. No game sends player positions, ever.
-- What other players SEE of you is the RemoteRig subtree of your
-- Player.prefab, cloned for each peer (RemoteRig.cs); scripts inside it
-- boot on the clones with peer_id = that player's id.
--   dp.me()          -> my id (stable per headset)
--   dp.peers()       -> array (join order) of { id, game, head, left, right,
--                        hand, active_hand, left_tracked, right_tracked,
--                        seen, age, state, rig, name, synced, frame }
--                        head/left/right/hand: Transforms in THIS headset's
--                        world; state: the table they dp.set_state()d, Unity
--                        values restored; rig: their RemoteRig clone, or
--                        nil; name: their display name (the SDK floats it
--                        over their head by default — RemoteNameTag.cs);
--                        synced: false while their pose is not a place here
--                        (they or you have not localized, or you are in
--                        different locations) — rig and name hidden,
--                        head/hands parked 10 km below the park, so skip
--                        them for aiming, hits and proximity
--   dp.peer(id)      -> one of the above, or nil (you are not your own peer)
--   dp.set_state(t)  -> publish a small table to everyone (colour, team,
--                        score). Resent to late joiners; keep it small.
--   dp.on_peer_join(fn(id, peer), owner)  fires for everyone already here,
--                        then for each arrival. owner (self, a GameObject) is
--                        optional: the handler is dropped when it is destroyed.
--   dp.on_peer_leave(fn(id), owner)
--   dp.off_peer_join(fn) / dp.off_peer_leave(fn)
-- One shared pump runs every title's peer / gesture / net handlers, so one
-- title's error must never stop the rest (a dead pump = dead phone taps for
-- every title). Each sub-pump runs in pcall; failures print at most once per
-- 10 s per sub-pump.
__dp_pump_warn_at = __dp_pump_warn_at or {}
function __dp_pump_warn(name, err)
    local now = (os ~= nil and os.time ~= nil) and os.time() or 0
    local last = __dp_pump_warn_at[name]
    if last ~= nil and now - last < 10 then return end
    __dp_pump_warn_at[name] = now
    print('[DreamParkLuaPump] ' .. tostring(name) .. ' threw (pump keeps running): ' .. tostring(err))
end
local function __dp_peer_view(p)
    if p ~= nil and p.state ~= nil and dp.unpack ~= nil then
        local ok, v = pcall(dp.unpack, p.state)
        if ok then p.state = v else __dp_pump_warn('peer_view', v) end
    end
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

-- ── Device & touch (phones) ──────────────────────────────
--   dp.device()          -> 'phone' | 'headset' | 'editor' (this device)
--   dp.is_phone()        -> true on a phone (or the Editor simulating one)
--   dp.is_headset()      -> true on a headset, and in the Editor
--   dp.device_of(id)     -> 'phone' | 'headset' for another player, nil when
--                           unknown (older client); dp.device() for dp.me()
--   dp.on_tap(fn(t), owner)    tap anywhere on the phone screen
--   dp.on_touch(fn(t), owner)  finger down / up   (t.phase 'down' | 'up')
--   dp.on_drag(fn(t), owner)   t.phase 'begin' | 'move' | 'end'
--   dp.on_pinch(fn(t), owner)  t.scale: multiplicative since the last pinch
--     each returns a handle: h.off() or dp.off_tap(h) (off_* also accept fn).
--     owner (self / a GameObject) is optional — inside a LuaBehaviour it
--     defaults to the script, and the handler drops when it is destroyed.
--   t = { kind, phase, x, y, dx, dy, scale, fingerId,
--         position, normal, object, distance }   x/y: 0..1 from bottom-left;
--         position/normal/object/distance are nil when the tap hit nothing.
--   Object hooks: ontap(t) / ontouch(t) on a LuaBehaviour fire when the ray
--   hits it or a child; return true to stop the walk to its parents.
dp.device     = function()   return dp_device() end
dp.is_phone   = function()   return dp_is_phone() end
dp.is_headset = function()   return dp_is_headset() end
dp.device_of  = function(id) if id == nil then return nil end return dp_device_of(id) end

__dp_gesture_fns = __dp_gesture_fns or { tap = {}, touch = {}, drag = {}, pinch = {} }

local function __dp_gesture_on(kind)
    return function(fn, owner)
        if fn == nil then return nil end
        local list = __dp_gesture_fns[kind]
        local h = { fn = fn, owner = owner }
        h.off = function()
            for i = #list, 1, -1 do if list[i] == h then table.remove(list, i) end end
        end
        list[#list + 1] = h
        dp_ensure_pump()
        return h
    end
end
local function __dp_gesture_off(kind)
    return function(x)
        if x == nil then return end
        local list = __dp_gesture_fns[kind]
        for i = #list, 1, -1 do
            if list[i] == x or list[i].fn == x then table.remove(list, i) end
        end
    end
end
dp.on_tap    = __dp_gesture_on('tap')
dp.on_touch  = __dp_gesture_on('touch')
dp.on_drag   = __dp_gesture_on('drag')
dp.on_pinch  = __dp_gesture_on('pinch')
dp.off_tap   = __dp_gesture_off('tap')
dp.off_touch = __dp_gesture_off('touch')
dp.off_drag  = __dp_gesture_off('drag')
dp.off_pinch = __dp_gesture_off('pinch')

function __dp_pump_gestures()
    if dp_gesture_event_count() == 0 then return end
    local evs = dp_gesture_events()
    for _, list in pairs(__dp_gesture_fns) do __dp_prune_fns(list) end
    for i = 1, #evs do
        local e = evs[i]
        local list = __dp_gesture_fns[e.kind]
        if list ~= nil and #list > 0 then
            -- Snapshot: a handler may off() itself or register another.
            local snap = {}
            for j = 1, #list do snap[j] = list[j] end
            for j = 1, #snap do
                local ok, err = pcall(snap[j].fn, e)
                if not ok then print('[dp.on_' .. tostring(e.kind) .. '] handler threw: ' .. tostring(err)) end
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
    local ok, err = pcall(__dp_pump_connected)
    if not ok then __dp_pump_warn('pump_connected', err) end
    ok, err = pcall(__dp_pump_peers)
    if not ok then __dp_pump_warn('pump_peers', err) end
    ok, err = pcall(__dp_pump_gestures)
    if not ok then __dp_pump_warn('pump_gestures', err) end
    if __dp_pump_net ~= nil then
        ok, err = pcall(__dp_pump_net)
        if not ok then __dp_pump_warn('pump_net', err) end
    end
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

                // Oct 11 2026 — occurrence teardown. No double quotes inside.
                env.DoString(@"
-- dp.on_occurrence_removed(fn, owner)  fn(t) when ONE attraction (occurrence)
--   of this script's package is removed while the title stays loaded: an Arena
--   rect set to 'No Attraction', a swap to another attraction, a repack that
--   retires a rect. t = { occurrence_id = string, resource = string,
--   anchor = GameObject }. Fired BEFORE the anchor is destroyed (it may already
--   be on its way out: read what you need, stop sounds/bots, drop held props).
--   Inside a script only this package's occurrences are reported and the
--   handler drops with the script (owner defaults to self). Returns a handle:
--   h.off() or dp.off_occurrence_removed(h or fn).
--   Still clean up in ondisable/ondestroy, and call it guarded so older
--   platforms keep working:
--     if dp.on_occurrence_removed then dp.on_occurrence_removed(fn) end
__dp_occ_removed_fns = __dp_occ_removed_fns or {}

local function __dp_occ_owner_gone(owner)
    if owner == nil then return false end
    local ok, gone = pcall(function() return owner:Equals(nil) end)
    return ok and gone == true
end

dp.on_occurrence_removed = function(fn, owner, go)
    if fn == nil then return nil end
    if go == nil and owner ~= nil then
        pcall(function() go = owner.gameObject end)
    end
    local list = __dp_occ_removed_fns
    local h = { fn = fn, owner = owner, go = go }
    h.off = function()
        for i = #list, 1, -1 do if list[i] == h then table.remove(list, i) end end
    end
    list[#list + 1] = h
    return h
end
dp.off_occurrence_removed = function(x)
    if x == nil then return end
    local list = __dp_occ_removed_fns
    for i = #list, 1, -1 do
        if list[i] == x or list[i].fn == x then table.remove(list, i) end
    end
end

-- Called from C# (DreamParkLuaAPI.DispatchOccurrenceRemoved).
function __dp_occurrence_removed(host, id, resource, anchor)
    local list = __dp_occ_removed_fns
    for i = #list, 1, -1 do
        if __dp_occ_owner_gone(list[i].owner) then table.remove(list, i) end
    end
    local snap = {}
    for j = 1, #list do snap[j] = list[j] end
    for j = 1, #snap do
        local h = snap[j]
        local mine = true
        if h.go ~= nil and host ~= nil then
            local ok, same = pcall(function()
                local p = dp_package(h.go)
                return p ~= nil and p:Equals(host)
            end)
            mine = ok and same == true
        end
        if mine then
            local ok, err = pcall(h.fn, { occurrence_id = id, resource = resource, anchor = anchor })
            if not ok then print('[dp.on_occurrence_removed] handler threw: ' .. tostring(err)) end
        end
    end
end
", "dp.api.occurrence");

                // Oct 11 2026 — the local player is not always in the park (core-playergate).
                // No double quotes inside.
                env.DoString(@"
-- dp.local_player_present() -> bool. False while this device's player is NOT in the
--   park: the park is still a diorama / relocalizing (not landed), or the phone shows
--   the miniature. The park keeps playing meanwhile; enemies must not target, chase
--   or damage the player. dp.head() and Camera.main then return a stand-in parked far
--   away (y = -10000) that rides the real head again once present.
dp.local_player_present = function() return dp_local_player_present() end

-- Camera.main in Lua = the content camera (PlayerHead.ContentCamera): the real camera
-- while the player is present, the far-away stand-in while not. C# Camera.main (and so
-- rendering, XR, AR and the diorama) is unchanged.
do
    local ok, err = pcall(function()
        local cls = CS.UnityEngine.Camera
        local mt = getmetatable(cls)
        if mt == nil or rawget(mt, '__dp_main_shim') then return end
        local base = rawget(mt, '__index')
        if base == nil then return end
        rawset(mt, '__dp_main_shim', true)
        rawset(mt, '__index', function(t, k)
            if k == 'main' then return dp_content_camera() end
            if type(base) == 'function' then return base(t, k) end
            return base[k]
        end)
    end)
    if not ok then print('[dp] Camera.main content shim not installed: ' .. tostring(err)) end
end
", "dp.api.presence");

                // Multiplayer services (leader, clock, random, shared_state) —
                // after the bootstrap above, whose dp.__packv it builds on.
                DreamParkLuaNet.Register(env);
                // Routes (dp.ports / neighbors / rect / route / tour / route_plan) —
                // after LuaNet, whose dp.shared_state route_plan builds on.
                DreamParkLuaRoutes.Register(env);
                // Device wall clock (dp.time.now / utc / unix / hour / format) —
                // standalone, DreamParkLuaTime.cs.
                DreamParkLuaTime.Register(env);
                // The room's real light (dp.light.ambient / direction / confidence / live) —
                // standalone, DreamParkLuaLight.cs.
                DreamParkLuaLight.Register(env);
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
                // Never disable: this one pump delivers every title's taps,
                // peer and shared_state events, so stopping it kills phone
                // input park-wide (BirthdayBash keyboard, Oct 11). Warn at most
                // once per 10 s instead, and re-fetch the function in case the
                // Lua env was replaced.
                _failures++;
                float now = Time.realtimeSinceStartup;
                if (now - _lastWarnAt >= 10f || _lastWarnAt < 0f)
                {
                    _lastWarnAt = now;
                    Debug.LogWarning("[DreamParkLuaPump] pump threw (" + _failures + " since last warning, pump keeps running): " + e.Message);
                    _failures = 0;
                    try { _pump?.Dispose(); } catch (Exception) { }
                    _pump = null;
                }
            }
        }

        private float _lastWarnAt = -1f;
        private int _failures;
    }
}
