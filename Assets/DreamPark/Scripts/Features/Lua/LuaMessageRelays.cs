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

public sealed class LuaTriggerEnterRelay : LuaColliderRelay { void OnTriggerEnter(Collider c) => cb?.Invoke(c); }
public sealed class LuaTriggerExitRelay  : LuaColliderRelay { void OnTriggerExit(Collider c)  => cb?.Invoke(c); }
public sealed class LuaTriggerStayRelay  : LuaColliderRelay { void OnTriggerStay(Collider c)  => cb?.Invoke(c); }

public sealed class LuaCollisionEnterRelay : LuaCollisionRelay { void OnCollisionEnter(Collision c) => cb?.Invoke(c); }
public sealed class LuaCollisionExitRelay  : LuaCollisionRelay { void OnCollisionExit(Collision c)  => cb?.Invoke(c); }
public sealed class LuaCollisionStayRelay  : LuaCollisionRelay { void OnCollisionStay(Collision c)  => cb?.Invoke(c); }

public sealed class LuaAppPauseRelay : LuaBoolRelay { void OnApplicationPause(bool paused)  => cb?.Invoke(paused); }
public sealed class LuaAppFocusRelay : LuaBoolRelay { void OnApplicationFocus(bool focused) => cb?.Invoke(focused); }
