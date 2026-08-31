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

        WireVoid<LuaReadyRelay>(go, scope, "onready", outRelays);
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
        r.MarkWired();          // cb is live from here — safe to do startup work
        outRelays.Add(r);
    }

    static void WireCollider<T>(GameObject go, LuaTable scope, string fn, List<Behaviour> outRelays)
        where T : LuaColliderRelay {
        scope.Get(fn, out Action<Collider> cb);
        if (cb == null) return;
        var r = go.AddComponent<T>();
        r.cb = cb;
        r.MarkWired();          // cb is live from here — safe to do startup work
        outRelays.Add(r);
    }

    static void WireCollision<T>(GameObject go, LuaTable scope, string fn, List<Behaviour> outRelays)
        where T : LuaCollisionRelay {
        scope.Get(fn, out Action<Collision> cb);
        if (cb == null) return;
        var r = go.AddComponent<T>();
        r.cb = cb;
        r.MarkWired();          // cb is live from here — safe to do startup work
        outRelays.Add(r);
    }

    static void WireBool<T>(GameObject go, LuaTable scope, string fn, List<Behaviour> outRelays)
        where T : LuaBoolRelay {
        scope.Get(fn, out Action<bool> cb);
        if (cb == null) return;
        var r = go.AddComponent<T>();
        r.cb = cb;
        r.MarkWired();          // cb is live from here — safe to do startup work
        outRelays.Add(r);
    }
}

// ── Relay base classes (one delegate signature each) ───────────────────────
// cb is [NonSerialized]: relays are added at runtime, never authored/serialized.

/// <summary>
/// Common base for every relay. Exists for one reason: to make the wiring order
/// explicit and safe.
///
/// THE TRAP IT CLOSES. The Wire* helpers do:
///
///     var r = go.AddComponent&lt;T&gt;();   // Awake + OnEnable run SYNCHRONOUSLY
///     r.cb = cb;                       // ...only assigned afterwards
///
/// so a relay's OnEnable always runs while `cb` is still null. The message
/// relays never noticed, because each only fires from a Unity callback on a
/// later frame. The first relay that needed to act at startup — LuaZoneRelay,
/// which delivers the CURRENT zone state so a script loading in after the player
/// already walked in still gets its onzoneenter — did act in OnEnable, invoked a
/// null callback, and recorded the delivery as done. The event was then gone for
/// good: exactly the failure the relay was written to prevent, one layer down.
///
/// Rather than leave that as a comment for the next person to miss, wiring is
/// now a step: Wire* assigns cb and then calls MarkWired(), and any relay that
/// needs to do startup work overrides OnWired() instead of touching OnEnable.
/// By the time OnWired runs, cb is guaranteed non-null.
/// </summary>
public abstract class LuaRelay : MonoBehaviour {
    /// <summary>
    /// Called once, immediately after this relay's callback has been assigned.
    /// Override for anything that must happen at startup. Never do startup work
    /// in Awake or OnEnable — cb does not exist yet there.
    /// </summary>
    protected virtual void OnWired() { }

    internal void MarkWired() => OnWired();
}

public abstract class LuaVoidRelay : LuaRelay { [NonSerialized] public Action cb; }
public abstract class LuaColliderRelay : LuaRelay { [NonSerialized] public Action<Collider> cb; }
public abstract class LuaCollisionRelay : LuaRelay { [NonSerialized] public Action<Collision> cb; }
public abstract class LuaBoolRelay : LuaRelay { [NonSerialized] public Action<bool> cb; }

// ── Concrete relays (each declares exactly one Unity message) ──────────────

public sealed class LuaFixedUpdateRelay : LuaVoidRelay { void FixedUpdate() => cb?.Invoke(); }
public sealed class LuaLateUpdateRelay  : LuaVoidRelay { void LateUpdate()  => cb?.Invoke(); }

// NOTE — an initial-overlap sweep was tried here (July 2026) and REVERTED.
//
// The idea was sound: colliders are disabled during load, and Unity does not
// reliably raise OnTriggerEnter for something already overlapping at the moment
// a collider becomes enabled, so a pickup the player spawns standing on top of
// never fires. The implementation was not.
//
// The sweep invoked ontriggerenter for everything already overlapping and
// recorded each collider in a dedupe set so Unity's own event could not deliver
// it twice. But a Lua handler routinely NO-OPS on the frame the sweep runs —
// supply_pickup opens with `if zombiez and not zombiez.is_playing() then return
// end`, and the round has not started yet that early. The handler declined, the
// collider was marked as delivered anyway, and because the player was standing
// still no OnTriggerExit ever cleared it. The real enter could then never fire.
//
// That is the SAME failure it was written to fix — an edge consumed inside a
// window where nobody could act on it — just moved into core, where it broke
// the common case instead of a rare one.
//
// Any retry needs the handler to be able to say "not yet, ask me again" (a
// return value, or re-arming while the overlap persists). Firing blind and
// recording it as delivered cannot work. Left as a plain relay until then:
// missing a spawned-on-top pickup is a far smaller failure than silently
// killing every pickup in the park.
public sealed class LuaTriggerEnterRelay : LuaColliderRelay { void OnTriggerEnter(Collider c) => cb?.Invoke(c); }

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

    // First delivery happens the instant the callback is wired — see LuaRelay.
    // Never in Awake/OnEnable: cb does not exist yet there, and delivering into
    // null would consume the baseline and lose the event permanently.
    protected override void OnWired() => Sync();

    // Retry only while the zone is still unresolved. A script on an object the
    // park spawner has not parented yet cannot see its attraction at wire time,
    // so it may take a frame or two. Early-outs on the first line once settled.
    void Update() {
        if (_hasState && _zone != null) return;
        if (_resolveTries >= MaxResolveTries) return;
        _resolveTries++;
        Sync();
    }

    private void HandleZoneChanged(global::DreamPark.GameArea previous, global::DreamPark.GameArea now) => Sync();

    private void Sync() {
        // Not wired yet (see Update): never establish or consume state while the
        // callback is null, or the delivery is silently thrown away.
        if (cb == null) return;

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

/// <summary>
/// onready() — "the world is real now".
///
/// Fires once, when park content is live: everything spawned, parented, posed,
/// floors generated, colliders and components enabled. In a hand-authored scene
/// that is true from the start, so it fires immediately — which is the point.
/// A script behaves the SAME whether it was placed in a scene or spawned by the
/// park loader, and that is the whole contract.
///
/// WHAT THIS REPLACES. Content had no way to learn the park had finished, so it
/// approximated. From shipped Zombiez:
///
///     if level_tries > 300 then level_settled = true return end   -- ~5s, then give up
///     level_tries = level_tries + 1
///     level = find_level()
///
/// and, every frame forever, `if not registered then try_register() end`. None of
/// that is about the game. LevelAnchor knows the exact moment — it is the line
/// after WaitForFloors() — and now it says so.
///
/// STICKY, like every other DreamPark event: a script that boots after the park
/// went live still gets onready() the instant it is wired. Late is normal here;
/// missing it must not be possible. (In practice park content cannot boot early —
/// LuaBehaviour refuses while parked — so this usually fires at wire time. It is
/// written to survive the other ordering anyway rather than rely on that.)
/// </summary>
public sealed class LuaReadyRelay : LuaVoidRelay {
    private bool _fired;
    private bool _subscribed;

    protected override void OnWired() { Subscribe(); TryFire(); }

    void OnEnable() { Subscribe(); TryFire(); }

    void OnDisable() { Unsubscribe(); }
    void OnDestroy() { Unsubscribe(); }

    private void Subscribe() {
        if (_subscribed || _fired) return;
        DreamPark.ParkBuilder.LevelObjectManager.OnObjectsEnabledChanged += HandleChanged;
        _subscribed = true;
    }

    private void Unsubscribe() {
        if (!_subscribed) return;
        DreamPark.ParkBuilder.LevelObjectManager.OnObjectsEnabledChanged -= HandleChanged;
        _subscribed = false;
    }

    private void HandleChanged(bool enabled) { if (enabled) TryFire(); }

    private void TryFire() {
        if (_fired) return;
        // cb is null until MarkWired — never consume the one-shot before the
        // callback exists (see LuaRelay).
        if (cb == null) return;
        if (!DreamPark.ParkBuilder.LevelObjectManager.objectsEnabled) return;

        _fired = true;
        Unsubscribe();
        try { cb.Invoke(); }
        catch (Exception e) { Debug.LogError($"[LuaReadyRelay] {name}.onready threw: {e}"); }
    }
}
