using System;
using System.Collections.Generic;
using UnityEngine;

public static class NetRegistry
{
    static readonly Dictionary<uint, NetId> _objects = new();
    static readonly Dictionary<uint, List<string>> _buffer = new();

    public static void Register(NetId netId)
    {
        // Verbose: one line per networked object — makes NetId mismatches
        // between two builds diagnosable by comparing logs side by side.
        global::DreamPark.NetLog.V($"[NetRegistry] Registered NetId {netId.Id} ({netId.gameObject.name})");

        // Two live objects hashing to the same id = ambiguous routing. Always
        // warn: usually duplicate scene-root names or duplicate NetScope keys.
        if (_objects.TryGetValue(netId.Id, out var existing) && existing != null && existing != netId)
            Debug.LogWarning($"[NetRegistry] NetId COLLISION on {netId.Id}: '{existing.gameObject.name}' vs '{netId.gameObject.name}' — give scene props unique names (or set explicitId).");

        _objects[netId.Id] = netId;

        // Flush buffered messages — but ONLY if something is actually listening.
        //
        // This buffer exists precisely to cover the window where a NetId is
        // registered and addressable but its receiver has not wired up yet. It
        // used to drain unconditionally and then Remove(), while NetId.ReceiveEvent
        // DECLINES delivery when OnNetEvent is null — so the payload was discarded
        // at the exact moment it was most likely to be undeliverable.
        //
        // That window is not rare, it is guaranteed: NetId registers in Start(),
        // while LuaBehaviour refuses to boot (and therefore refuses to subscribe
        // onnet) for the entire park-load span and every Build→Play transition
        // (LuaBehaviour.ParkContentIsParked). The message most likely to land in it
        // is the host's join-time state-sync burst — the one that seeds a late
        // joiner's world. Losing it silently de-syncs the attraction.
        //
        // Keeping the buffer until someone takes it means a receiver that boots
        // late still gets its backlog. The existing size/age caps below already
        // bound how long that can persist.
        if (netId.HasSubscribers && _buffer.TryGetValue(netId.Id, out var pending))
        {
            foreach (var payload in pending)
                netId.ReceiveEvent(payload);

            _buffer.Remove(netId.Id);
        }
    }

    /// <summary>
    /// Deliver any buffered backlog for this object, if it now has a listener.
    /// Called automatically when something subscribes to NetId.OnNetEvent, so a
    /// receiver that wired up after registration still gets what it missed.
    /// No-op when nothing is listening — the backlog stays buffered.
    /// </summary>
    public static void TryFlushBuffered(NetId netId)
    {
        if (netId == null || !netId.HasSubscribers) return;
        if (!_buffer.TryGetValue(netId.Id, out var pending)) return;

        foreach (var payload in pending)
            netId.ReceiveEvent(payload);

        _buffer.Remove(netId.Id);
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

        // object hasn't loaded yet — buffer it (bounded). Log the first buffer
        // per id: an inbound event for an id nothing registered is the
        // signature of a NetId mismatch between builds (different hierarchy
        // paths → different hashes) and must not fail silently.
        if (!_buffer.ContainsKey(id))
            Debug.LogWarning($"[NetRegistry] Event for UNREGISTERED NetId {id} — buffering. If this id never registers, the sender and receiver hierarchies don't match.");

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
