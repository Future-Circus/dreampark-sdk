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
    public uint Id { get; private set; }

    /// <summary>
    /// Subscribe to receive network events targeting this object.
    /// Payload is the raw JSON string from the sender.
    /// </summary>
    public event Action<string> OnNetEvent;

    void Awake()
    {
        Id = ComputeId();
        NetRegistry.Register(this);
    }

    void OnDestroy()
    {
        NetRegistry.Unregister(Id);
    }

    public void ReceiveEvent(string payload)
    {
        OnNetEvent?.Invoke(payload);
    }

    /// <summary>
    /// Builds a deterministic ID from the sibling-index path up to the level root.
    /// All clients loading the same prefab get the same hierarchy, so the IDs match.
    /// </summary>
    uint ComputeId()
    {
        // walk up collecting sibling indices until we hit a root
        // (no parent, or parent is the scene root)
        uint hash = 2166136261; // FNV-1a offset basis
        Transform t = transform;

        while (t != null)
        {
            uint index = (uint)t.GetSiblingIndex();
            hash ^= index;
            hash *= 16777619; // FNV prime

            // also mix in the name to handle dynamically spawned siblings
            // that might shift indices — but for prefab children this is stable
            int nameHash = t.name.GetHashCode();
            hash ^= (uint)nameHash;
            hash *= 16777619;

            t = t.parent;
        }

        return hash;
    }
}
