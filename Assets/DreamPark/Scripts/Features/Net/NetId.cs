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

    /// <summary>
    /// Subscribe to receive network events targeting this object.
    /// Payload is the raw JSON string from the sender.
    /// </summary>
    public event Action<string> OnNetEvent;

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
        Id = explicitId != 0 ? explicitId : ComputeId();
        NetRegistry.Register(this);
        _registered = true;
    }

    void OnDestroy()
    {
        if (_registered) NetRegistry.Unregister(Id);
    }

    public void ReceiveEvent(string payload)
    {
        // A delivered event with nobody listening must not vanish silently —
        // it means no TestNetObject/LuaBehaviour(onnet) is wired on this object.
        if (OnNetEvent == null)
        {
            Debug.LogWarning($"[NetId {Id}] Event delivered but NO subscribers on '{gameObject.name}' — is the receiving script (onnet/TestNetObject) attached on this client?");
            return;
        }

        // Untrusted network input flows straight into creator Lua (onnet). Isolate
        // handler exceptions so a malformed/hostile payload can't crash the caller.
        try { OnNetEvent.Invoke(payload); }
        catch (Exception e) { Debug.LogWarning($"[NetId {Id}] onnet handler threw: {e.Message}"); }
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
    uint ComputeId()
    {
        uint hash = 2166136261; // FNV-1a offset basis
        Transform t = transform;

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

    static uint MixString(uint hash, string s)
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
