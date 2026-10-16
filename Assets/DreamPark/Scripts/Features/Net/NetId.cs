using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;

[CustomEditor(typeof(NetId))]
public class NetIdEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        NetId netId = (NetId)target;

        EditorGUILayout.Space();
        GUI.enabled = false;
        EditorGUILayout.TextField("Net ID", Application.isPlaying ? netId.Id.ToString() : "(runtime only)");
        GUI.enabled = true;
    }
}
#endif

public class NetId : MonoBehaviour
{
    [Tooltip("Optional. If nonzero, this id is used verbatim instead of the " +
             "hierarchy-path hash. Use for scene props whose hierarchy may " +
             "differ between Editor and device builds (runtime-spawned roots " +
             "shift sibling indices). Must be unique per park and identical " +
             "on every client — set it on the shared prefab/scene object.")]
    public uint explicitId = 0;

    public uint Id { get; private set; }

    /// <summary>True once Id is final and this object is addressable (Start ran).
    /// Services that send on this NetId (SharedState) wait for it.</summary>
    public bool IsRegistered => _registered;

    private Action<string> _onNetEvent;

    /// <summary>
    /// Subscribe to receive network events targeting this object.
    /// Payload is the raw JSON string from the sender.
    ///
    /// Subscribing DRAINS any events that arrived before you existed. NetId
    /// registers in Start(), but a LuaBehaviour is forbidden from booting — and
    /// therefore from wiring onnet — while park content is parked, which is the
    /// entire load span and every Build→Play transition. Without this, a receiver
    /// that wires up late would never see the host's join-time state burst.
    ///
    /// Making subscription itself the trigger means no caller has to remember to
    /// ask for a replay, and the buffer cannot be stranded once someone is finally
    /// listening.
    /// </summary>
    public event Action<string> OnNetEvent {
        add {
            _onNetEvent += value;
            if (_registered) NetRegistry.TryFlushBuffered(this);
        }
        remove { _onNetEvent -= value; }
    }

    bool _registered;

    // Compute + register in Start, NOT Awake. The park spawner does
    // Instantiate(prefab) → SetParent → rename → stamp NetScope, and Awake
    // fires inside Instantiate — BEFORE parenting/rename/stamping — so an
    // Awake-time hash would be computed against a temporary hierarchy.
    // Start runs after the spawner's synchronous setup completes. Events
    // that arrive before Start are buffered by NetRegistry and flushed on
    // registration.
    void Start()
    {
        EnsureRegistered();
    }

    void EnsureRegistered()
    {
        if (_registered) return;
        Id = PreviewId();
        NetRegistry.Register(this);
        _registered = true;
    }

    /// <summary>
    /// Discriminate an authored id per spawned instance, using the same
    /// boundary the hash path already uses.
    ///
    /// explicitId is a hand-authored constant on a SHARED prefab, and it
    /// bypasses ComputeId entirely — so two live copies of one attraction both
    /// answer to the same number and routing becomes ambiguous. That is not a
    /// gap in the id scheme; it is an opt-out from the part of the scheme that
    /// already solves this. ComputeId stops at the first NetScope and mixes its
    /// scopeKey ("{levelId}|{objectIndex}|{resourceName}"), and objectIndex
    /// differs per park-doc entry, so two instances of one attraction hash
    /// apart today for every NetId that uses the hash.
    ///
    /// Mixing the same scopeKey into an authored id gives it the same property
    /// for free. Scene-placed props have no NetScope ancestor and are returned
    /// verbatim, so every shipped park keeps its current ids byte-for-byte.
    ///
    /// NOTE for anyone tempted to use a spawn ordinal instead: don't. Spawn
    /// order is async download completion order, which differs on every device
    /// — attraction N here is attraction M there. It would look correct in the
    /// Editor and on a single device and desync only in a real session, which
    /// is exactly what NetScope exists to prevent. objectIndex is park-doc
    /// data and is safe; the ordinal is not.
    /// </summary>
    uint ScopeExplicit(uint id)
    {
        for (Transform t = transform; t != null; t = t.parent)
            if (t.TryGetComponent<DreamPark.NetScope>(out var scope) && !string.IsNullOrEmpty(scope.scopeKey))
                return MixString(id, scope.scopeKey);
        return id;
    }

    void OnDestroy()
    {
        // Owner-checked: a duplicate that collided with a live object (the
        // PlayerRig dedup destroys one) must not take the live one's id with it.
        if (_registered) NetRegistry.Unregister(Id, this);
    }

    /// <summary>
    /// The id this object WILL register with, computed now without registering.
    /// Same rules as Start (explicitId scoped, else the hierarchy hash). For
    /// tests and diagnostics; only meaningful once the spawner has parented
    /// and stamped the object.
    /// </summary>
    public uint PreviewId() => MixContent(transform, explicitId != 0 ? ScopeExplicit(explicitId) : ComputeId());

    /// <summary>
    /// The hierarchy hash a NetId on <paramref name="t"/> would compute
    /// (NetScope-aware, scene roots by name). Lets SDK services address an
    /// object that carries no NetId with exactly the id one would get.
    /// </summary>
    public static uint HashPath(Transform t) => t == null ? 0u : MixContent(t, ComputeIdFor(t));

    /// <summary>
    /// Mix the owning title's content id into an id, hashed or explicit.
    ///
    /// The id space is shared by every title in a park and the wire carries no
    /// game id, so two titles that picked the same number (SuperAdventureLand
    /// and WizardsWay both put their rig bus on 8300001; Zombiez and ArcadeLand
    /// share 7310101 and 7310201) routed each other's traffic into the wrong
    /// scripts in a mixed park, and the registry gave one bus to the other.
    /// The content id comes from the nearest stamped content component — the
    /// same on every device. Objects outside any title (scene props) keep
    /// their id unchanged. Changing this changes every id on the wire, which is
    /// why BeaconBroadcaster.ProtocolVersion moved to 2 with it.
    /// </summary>
    static uint MixContent(Transform t, uint id)
    {
        string content = ContentIdOf(t);
        return string.IsNullOrEmpty(content) ? id : MixString(MixString(id, "|g|"), content);
    }

    /// <summary>The content id (gameId) the object belongs to, or null.</summary>
    public static string ContentIdOf(Transform t)
    {
        for (; t != null; t = t.parent)
        {
            if (t.TryGetComponent<global::DreamPark.GameArea>(out var area) && !string.IsNullOrEmpty(area.gameId)) return area.gameId;
            if (t.TryGetComponent<global::DreamPark.LevelTemplate>(out var level) && !string.IsNullOrEmpty(level.gameId)) return level.gameId;
            if (t.TryGetComponent<global::DreamPark.PropTemplate>(out var prop) && !string.IsNullOrEmpty(prop.gameId)) return prop.gameId;
            if (t.TryGetComponent<global::DreamPark.PlayerRig>(out var rig) && !string.IsNullOrEmpty(rig.gameId)) return rig.gameId;
        }
        return null;
    }

    /// <summary>
    /// Whether anything is actually listening. NetRegistry checks this BEFORE
    /// draining its replay buffer: a buffer that exists to cover the not-ready
    /// window must not throw the payload away during that window.
    /// </summary>
    public bool HasSubscribers => _onNetEvent != null;

    /// <summary>Delivers, and reports whether a subscriber actually took it.</summary>
    public bool ReceiveEvent(string payload)
    {
        // A delivered event with nobody listening must not vanish silently —
        // it means no TestNetObject/LuaBehaviour(onnet) is wired on this object.
        if (_onNetEvent == null)
        {
            Debug.LogWarning($"[NetId {Id}] Event delivered but NO subscribers on '{gameObject.name}' — is the receiving script (onnet/TestNetObject) attached on this client?");
            return false;
        }

        // Untrusted network input flows straight into creator Lua (onnet). Isolate
        // handler exceptions so a malformed/hostile payload can't crash the caller.
        try { _onNetEvent.Invoke(payload); }
        catch (Exception e) { Debug.LogWarning($"[NetId {Id}] onnet handler threw: {e.Message}"); }
        return true;
    }

    /// <summary>
    /// Deterministic id, stable across devices and sessions. Three rules:
    ///
    /// 1. Walking up, STOP at the first <see cref="DreamPark.NetScope"/> and mix
    ///    its scopeKey (park-doc-stable: levelId|objectIndex|resourceName).
    ///    Levels and objects spawn concurrently, so sibling order ABOVE an
    ///    attraction root reflects download completion order — different on
    ///    every device. It must never enter the hash. Below the scope, the
    ///    hierarchy is defined by the prefab asset — identical everywhere.
    ///
    /// 2. At a scene root (no parent, no scope), use the NAME ONLY — root
    ///    sibling order differs between Editor and device builds (runtime-
    ///    spawned roots). Keep scene-placed networked props uniquely named.
    ///
    /// 3. All string hashing is FNV over chars — string.GetHashCode() is not
    ///    guaranteed stable across runtimes (Mono Editor vs IL2CPP device).
    ///
    /// "(Clone)" suffixes are stripped so rename timing can't shift the hash.
    /// </summary>
    uint ComputeId() => ComputeIdFor(transform);

    static uint ComputeIdFor(Transform t)
    {
        uint hash = 2166136261; // FNV-1a offset basis

        while (t != null)
        {
            if (t.TryGetComponent<DreamPark.NetScope>(out var scope) && !string.IsNullOrEmpty(scope.scopeKey))
                return MixString(hash, scope.scopeKey);   // stable boundary — stop

            if (t.parent == null)
                return MixString(hash, CleanName(t.name)); // scene root — name only

            hash ^= (uint)t.GetSiblingIndex();
            hash *= 16777619; // FNV prime
            hash = MixString(hash, CleanName(t.name));

            t = t.parent;
        }
        return hash;
    }

    internal static uint MixString(uint hash, string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            hash ^= s[i];
            hash *= 16777619;
        }
        return hash;
    }

    static string CleanName(string name) =>
        name.EndsWith("(Clone)") ? name.Substring(0, name.Length - 7) : name;
}
