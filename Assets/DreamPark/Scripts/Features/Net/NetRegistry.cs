using System;
using System.Collections.Generic;
using UnityEngine;

public static class NetRegistry
{
    static readonly Dictionary<uint, NetId> _objects = new();
    static readonly Dictionary<uint, List<string>> _buffer = new();

    public static void Register(NetId netId)
    {
        _objects[netId.Id] = netId;

        // flush buffered messages
        if (_buffer.TryGetValue(netId.Id, out var pending))
        {
            foreach (var payload in pending)
                netId.ReceiveEvent(payload);

            _buffer.Remove(netId.Id);
        }
    }

    public static void Unregister(uint id)
    {
        _objects.Remove(id);
    }

    // Defensive caps so a malicious/buggy peer can't grow memory without bound by
    // spraying events at unregistered (or guessed) NetIds.
    const int MaxBufferedIds = 256;
    const int MaxBufferedPerId = 32;

    public static void Dispatch(uint id, string payload)
    {
        if (_objects.TryGetValue(id, out var netId))
        {
            // Isolate handler exceptions — one bad message must not break the
            // network poll loop or other objects' dispatch.
            try { netId.ReceiveEvent(payload); }
            catch (Exception e) { Debug.LogWarning($"[NetRegistry] handler for {id} threw: {e.Message}"); }
            return;
        }

        // object hasn't loaded yet — buffer it (bounded)
        if (!_buffer.TryGetValue(id, out var list))
        {
            if (_buffer.Count >= MaxBufferedIds) return; // drop — too many unknown ids
            list = new List<string>();
            _buffer[id] = list;
        }
        if (list.Count >= MaxBufferedPerId) return; // drop — too many pending for this id
        list.Add(payload);
    }

    public static bool TryGet(uint id, out NetId netId)
    {
        return _objects.TryGetValue(id, out netId);
    }

    public static void Clear()
    {
        _objects.Clear();
        _buffer.Clear();
    }
}
