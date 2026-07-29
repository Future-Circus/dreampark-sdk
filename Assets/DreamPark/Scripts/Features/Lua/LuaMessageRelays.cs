using System;
using System.Collections.Generic;
using UnityEngine;
using XLua;

/// <summary>
/// Opt-in MonoBehaviour message relays for Lua scripts.
///
/// Unity only invokes a magic method (FixedUpdate, OnTriggerExit, …) on a
/// component that actually declares it. If LuaBehaviour declared every message
/// itself, every LuaBehaviour in the scene would pay the per-object engine cost
/// for FixedUpdate/OnTriggerStay/etc. even when its script never uses them —
/// which adds up fast on Quest 3S (XR2 Gen 2).
///
/// Instead, each optional message lives on a tiny dedicated relay component that
/// is AddComponent'd at runtime ONLY when the Lua script defines the matching
/// function. A script with no physics callbacks registers zero physics methods.
///
/// The .lua.txt function names are the lower-cased Unity message names:
///   fixedupdate()                  → FixedUpdate
///   lateupdate()                   → LateUpdate
///   ontriggerenter(other)          → OnTriggerEnter(Collider)
///   ontriggerexit(other)           → OnTriggerExit(Collider)
///   ontriggerstay(other)           → OnTriggerStay(Collider)
///   oncollisionenter(collision)    → OnCollisionEnter(Collision)
///   oncollisionexit(collision)     → OnCollisionExit(Collision)
///   oncollisionstay(collision)     → OnCollisionStay(Collision)
///   onapplicationpause(paused)     → OnApplicationPause(bool)
///   onapplicationfocus(focused)    → OnApplicationFocus(bool)
///
/// Two hooks are NOT Unity messages — they are DreamPark zone events:
///   onzoneenter()                  → the player entered this script's attraction
///   onzoneexit()                   → …and left it
/// See LuaZoneRelay for why these exist and why polling isPlaying by hand is a
/// trap.
/// </summary>
public static class LuaMessageRelays {

    /// <summary>
    /// Inspect <paramref name="scope"/> for any optional lifecycle functions and,
    /// for each one present, add the matching relay to <paramref name="go"/> wired
    /// to that Lua function. Added relays are appended to <paramref name="outRelays"/>
    /// so the host can enable/disable them in step with its own OnEnable/OnDisable.
    /// </summary>
    public static void Bind(GameObject go, LuaTable scope, List<Behaviour> outRelays) {
        if (go == null || scope == null) return;

        WireVoid<LuaFixedUpdateRelay>(go, scope, "fixedupdate", outRelays);
        WireVoid<LuaLateUpdateRelay>(go, scope, "lateupdate", outRelays);

        WireCollider<LuaTriggerEnterRelay>(go, scope, "ontriggerenter", outRelays);
        WireCollider<LuaTriggerExitRelay>(go, scope, "ontriggerexit", outRelays);
        WireCollider<LuaTriggerStayRelay>(go, scope, "ontriggerstay", outRelays);

        WireCollision<LuaCollisionEnterRelay>(go, scope, "oncollisionenter", outRelays);
        WireCollision<LuaCollisionExitRelay>(go, scope, "oncollisionexit", outRelays);
        WireCollision<LuaCollisionStayRelay>(go, scope, "oncollisionstay", outRelays);

        WireBool<LuaAppPauseRelay>(go, scope, "onapplicationpause", outRelays);
        WireBool<LuaAppFocusRelay>(go, scope, "onapplicationfocus", outRelays);

        WireVoid<LuaZoneEnterRelay>(go, scope, "onzoneenter", outRelays);
        WireVoid<LuaZoneExitRelay>(go, scope, "onzoneexit", outRelays);
    }

    /// <summary>Enable/disable all relays in the list (null-safe).</summary>
    public static void SetEnabled(List<Behaviour> relays, bool enabled) {
        if (relays == null) return;
        for (int i = 0; i < relays.Count; i++)
            if (relays[i] != null)
                relays[i].enabled = enabled;
    }

    static void WireVoid<T>(GameObject go, LuaTable scope, string fn, List<Behaviour> outRelays)
        where T : LuaVoidRelay {
        var cb = scope.Get<Action>(fn);
        if (cb == null) return;
        var r = go.AddComponent<T>();
        r.cb = cb;
        outRelays.Add(r);
    }

    static void WireCollider<T>(GameObject go, LuaTable scope, string fn, List<Behaviour> outRelays)
        where T : LuaColliderRelay {
        scope.Get(fn, out Action<Collider> cb);
        if (cb == null) return;
        var r = go.AddComponent<T>();
        r.cb = cb;
        outRelays.Add(r);
    }

    static void WireCollision<T>(GameObject go, LuaTable scope, string fn, List<Behaviour> outRelays)
        where T : LuaCollisionRelay {
        scope.Get(fn, out Action<Collision> cb);
        if (cb == null) return;
        var r = go.AddComponent<T>();
        r.cb = cb;
        outRelays.Add(r);
    }

    static void WireBool<T>(GameObject go, LuaTable scope, string fn, List<Behaviour> outRelays)
        where T : LuaBoolRelay {
        scope.Get(fn, out Action<bool> cb);
        if (cb == null) return;
        var r = go.AddComponent<T>();
        r.cb = cb;
        outRelays.Add(r);
    }
}

// ── Relay base classes (one delegate signature each) ───────────────────────
// cb is [NonSerialized]: relays are added at runtime, never authored/serialized.

public abstract class LuaVoidRelay : MonoBehaviour { [NonSerialized] public Action cb; }
public abstract class LuaColliderRelay : MonoBehaviour { [NonSerialized] public Action<Collider> cb; }
public abstract class LuaCollisionRelay : MonoBehaviour { [NonSerialized] public Action<Collision> cb; }
public abstract class LuaBoolRelay : MonoBehaviour { [NonSerialized] public Action<bool> cb; }

// ── Concrete relays (each declares exactly one Unity message) ──────────────

public sealed class LuaFixedUpdateRelay : LuaVoidRelay { void FixedUpdate() => cb?.Invoke(); }
public sealed class LuaLateUpdateRelay  : LuaVoidRelay { void LateUpdate()  => cb?.Invoke(); }

/// <summary>
/// OnTriggerEnter, plus a ONE-TIME sweep for overlaps that were already in
/// progress when this relay first came up.
///
/// WHY: park objects spawn with their colliders DISABLED (LevelObject.Disable)
/// and are re-enabled together at the end of the load. Unity does not reliably
/// raise OnTriggerEnter for a collider that is ALREADY overlapping at the moment
/// it becomes enabled — least of all against a stationary kinematic rigidbody,
/// which is exactly what a tracked hand is when the player is standing still.
///
/// So a supply the player happens to spawn on top of would never fire, ever.
/// Same failure shape as the zone bug: an edge that occurred inside a window
/// where nobody could observe it. The sweep restores it.
///
/// DELIBERATELY ONCE, not on every enable. OptimizedAF toggles components
/// constantly during culling; re-sweeping each time would fire spurious enters
/// for anything the player merely happens to be standing in. Once, on the first
/// frame this relay genuinely runs, targets the load window and nothing else.
/// </summary>
public sealed class LuaTriggerEnterRelay : LuaColliderRelay {
    // Colliders already reported, so Unity's own event and the sweep can never
    // both deliver the same one. OnTriggerExit clears, so a real re-entry later
    // still fires normally.
    private readonly HashSet<Collider> _reported = new HashSet<Collider>();
    private bool _sweptOnce;

    void OnTriggerEnter(Collider c) {
        if (c == null || !_reported.Add(c)) return;
        cb?.Invoke(c);
    }

    // Declared purely for bookkeeping — without it a collider that left would
    // never be eligible to fire again.
    void OnTriggerExit(Collider c) {
        if (c != null) _reported.Remove(c);
    }

    void FixedUpdate() {
        if (_sweptOnce) return;      // early-out: costs a call, nothing more
        _sweptOnce = true;
        SweepExistingOverlaps();
    }

    private void SweepExistingOverlaps() {
        var mine = GetComponents<Collider>();
        if (mine == null || mine.Length == 0) return;

        for (int i = 0; i < mine.Length; i++) {
            var self = mine[i];
            if (self == null || !self.enabled) continue;

            // Bounds-based probe rather than an exact shape cast: this only has to
            // answer "was something already inside me", and an AABB is both cheap
            // and shape-agnostic. QueryTriggerInteraction.Collide so trigger-vs-
            // trigger pairs are visible too.
            var hits = Physics.OverlapBox(
                self.bounds.center, self.bounds.extents, Quaternion.identity,
                ~0, QueryTriggerInteraction.Collide);
            if (hits == null) continue;

            for (int h = 0; h < hits.Length; h++) {
                var other = hits[h];
                if (other == null || other == self) continue;
                if (other.transform.IsChildOf(transform)) continue;       // our own hierarchy
                if (transform.IsChildOf(other.transform)) continue;
                // Respect the collision matrix — OverlapBox ignores it.
                if (Physics.GetIgnoreLayerCollision(gameObject.layer, other.gameObject.layer)) continue;
                // A trigger event needs at least one trigger in the pair.
                if (!self.isTrigger && !other.isTrigger) continue;

                if (!_reported.Add(other)) continue;
                try { cb?.Invoke(other); }
                catch (Exception e) { Debug.LogError($"[LuaTriggerEnterRelay] {name}.ontriggerenter threw on initial sweep: {e}"); }
            }
        }
    }
}

public sealed class LuaTriggerExitRelay  : LuaColliderRelay { void OnTriggerExit(Collider c)  => cb?.Invoke(c); }
public sealed class LuaTriggerStayRelay  : LuaColliderRelay { void OnTriggerStay(Collider c)  => cb?.Invoke(c); }

public sealed class LuaCollisionEnterRelay : LuaCollisionRelay { void OnCollisionEnter(Collision c) => cb?.Invoke(c); }
public sealed class LuaCollisionExitRelay  : LuaCollisionRelay { void OnCollisionExit(Collision c)  => cb?.Invoke(c); }
public sealed class LuaCollisionStayRelay  : LuaCollisionRelay { void OnCollisionStay(Collision c)  => cb?.Invoke(c); }

public sealed class LuaAppPauseRelay : LuaBoolRelay { void OnApplicationPause(bool paused)  => cb?.Invoke(paused); }
public sealed class LuaAppFocusRelay : LuaBoolRelay { void OnApplicationFocus(bool focused) => cb?.Invoke(focused); }

// ── Zone relays (DreamPark events, not Unity messages) ─────────────────────
//
//  WHY THESE EXIST
//  ---------------
//  GameArea.isPlaying is a LEVEL — a polled boolean. What content wants is an
//  EDGE: "the player just walked in". Every creator who needed that used to
//  hand-roll the edge detection:
//
//      local now_inside = inside
//      if area ~= nil then now_inside = area.isPlaying end
//      if now_inside ~= inside then
//          inside = now_inside          -- latch
//          if manager then dispatch() end   -- receiver checked AFTER
//      end
//
//  That puts the edge state in CONTENT, and content can get the order wrong.
//  The line above latches before confirming anyone can receive, so if the
//  manager has not loaded yet the edge is consumed and NEVER RE-FIRES —
//  isPlaying stays true forever after, so the comparison never trips again.
//  This is not hypothetical: it is exactly how Zombiez's round failed to start
//  (July 2026). The park loader brings an attraction up as its own addressable,
//  and the manager it talks to lives on Player.prefab — a DIFFERENT addressable
//  — so "my receiver does not exist yet" is a NORMAL state here, unlike a
//  hand-authored scene where everything is alive by the first Update.
//
//  Moving edge detection into core makes the mistake unexpressible: content
//  never owns the flag, so content cannot corrupt it.
//
//  STICKY BY DESIGN
//  ----------------
//  OnEnable delivers the CURRENT state, not just future changes. Arriving after
//  the player already walked in is normal — the park spawner parents content
//  after Awake, and OptimizedAF disables/re-enables LuaBehaviours during load,
//  so a script routinely misses the frame the edge happened on. A late
//  subscriber still gets its onzoneenter. Worst case is LATE; never NEVER.
//
//  Re-entrancy is handled per relay: each tracks whether it has already
//  reported "inside", so a re-enable during load cannot double-fire enter.
// Derives from LuaVoidRelay so WireVoid<T>'s constraint is satisfied and `cb`
// (Action) is inherited rather than redeclared — these are wired exactly like
// fixedupdate/lateupdate, they just fire from a DreamPark event instead of a
// Unity message.
public abstract class LuaZoneRelay : LuaVoidRelay {

    /// <summary>Enter relays fire on the false→true edge; exit relays on true→false.</summary>
    protected abstract bool FiresOnInside { get; }

    private global::DreamPark.GameArea _zone;
    private bool _inside;
    private bool _hasState;
    private bool _subscribed;

    // The owning zone cannot always be resolved at OnEnable: the park spawner
    // parents spawned content AFTER Awake (the same reason `storage` is a lazy
    // proxy and net_send reads netId.Id at send time). Retry in Update until it
    // resolves, then stop doing any work. Bounded so a genuinely zone-less
    // object stops paying for the lookup.
    private int _resolveTries;
    private const int MaxResolveTries = 600;   // ~10s at 60fps, far longer than any load

    void OnEnable() {
        if (!_subscribed) {
            global::DreamPark.GameArea.OnContentZoneChanged += HandleZoneChanged;
            _subscribed = true;
        }
        Sync();
    }

    void OnDisable() {
        if (_subscribed) {
            global::DreamPark.GameArea.OnContentZoneChanged -= HandleZoneChanged;
            _subscribed = false;
        }
    }

    void Update() {
        if (_zone != null || _resolveTries >= MaxResolveTries) return;
        _resolveTries++;
        Sync();
    }

    private void HandleZoneChanged(global::DreamPark.GameArea previous, global::DreamPark.GameArea now) => Sync();

    private void Sync() {
        if (_zone == null) _zone = ResolveZone();
        if (_zone == null) return;

        bool inside = global::DreamPark.GameArea.currentGameArea == _zone;

        // First observation establishes the baseline. Deliver it only if it is
        // the state this relay reports — that is the sticky replay: a script
        // that loads while the player is ALREADY standing inside still gets its
        // onzoneenter, and one that loads outside is not spuriously told it left.
        if (!_hasState) {
            _hasState = true;
            _inside = inside;
            if (inside == FiresOnInside) Fire();
            return;
        }

        if (inside == _inside) return;
        _inside = inside;
        if (inside == FiresOnInside) Fire();
    }

    private void Fire() {
        // A throwing handler must not kill the subscription for everyone else.
        try { cb?.Invoke(); }
        catch (Exception e) { Debug.LogError($"[LuaZoneRelay] {name}.{(FiresOnInside ? "onzoneenter" : "onzoneexit")} threw: {e}"); }
    }

    /// <summary>
    /// The zone this script belongs to: the nearest ENABLED GameArea at or above
    /// this object. Disabled ones are skipped on purpose — a prop nested inside an
    /// attraction has its own GameArea suppressed (PropTemplate), and resolving to
    /// that instead of the containing attraction would mean the relay watched a
    /// zone that can never become current.
    /// </summary>
    private global::DreamPark.GameArea ResolveZone() {
        Transform t = transform;
        while (t != null) {
            var areas = t.GetComponents<global::DreamPark.GameArea>();
            for (int i = 0; i < areas.Length; i++)
                if (areas[i] != null && areas[i].enabled) return areas[i];
            t = t.parent;
        }
        return null;
    }
}

public sealed class LuaZoneEnterRelay : LuaZoneRelay { protected override bool FiresOnInside => true; }
public sealed class LuaZoneExitRelay  : LuaZoneRelay { protected override bool FiresOnInside => false; }
