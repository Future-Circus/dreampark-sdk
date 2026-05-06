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

    public static void Dispatch(uint id, string payload)
    {
        if (_objects.TryGetValue(id, out var netId))
        {
            netId.ReceiveEvent(payload);
            return;
        }

        // object hasn't loaded yet — buffer it
        if (!_buffer.TryGetValue(id, out var list))
        {
            list = new List<string>();
            _buffer[id] = list;
        }
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
