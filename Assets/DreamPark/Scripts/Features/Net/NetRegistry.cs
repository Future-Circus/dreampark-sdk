using System;
using System.Collections.Generic;
using UnityEngine;

public static class NetRegistry
{
    static readonly Dictionary<uint, NetId> _objects = new();
    // Replay buffer for ids nothing has registered yet: (payload, unscaled time).
    // Bounded on every axis — see Dispatch.
    static readonly Dictionary<uint, List<(string payload, float at)>> _buffer = new();
    static readonly List<uint> _bufferOrder = new();   // ids, least recently buffered first

    /// <summary>A buffered message older than this is stale and is never replayed.</summary>
    public const float BufferTtlSeconds = 5f;
    // Live registrants that collided with the current holder, oldest first.
    static readonly Dictionary<uint, List<NetId>> _waiting = new();
    // Ids whose holder was torn down (level unloaded / object destroyed), with
    // the time. Traffic for such an id is addressed to the visit that just
    // ended: it is dropped, never buffered, until the id registers again or
    // BufferTtlSeconds pass — so a quick re-entry never replays it.
    static readonly Dictionary<uint, float> _released = new();

    public static void Register(NetId netId)
    {
        // Verbose: one line per networked object — makes NetId mismatches
        // between two builds diagnosable by comparing logs side by side.
        global::DreamPark.NetLog.V($"[NetRegistry] Registered NetId {netId.Id} ({netId.gameObject.name})");

        // Two live objects hashing to the same id = ambiguous routing. Always
        // warn: usually duplicate scene-root names or duplicate NetScope keys.
        //
        // The FIRST live registrant keeps the id; the newcomer waits and is
        // promoted when the holder unregisters. Overwriting (the old rule) let
        // a short-lived duplicate steal a live object's traffic — the PlayerRig
        // dedup destroys a duplicate rig whose NetIds had just registered over
        // the worn rig's, and its OnDestroy then wiped the id, so the live rig
        // went deaf (Birthday Bash, Oct 2026). A level that is re-entered while
        // its old instance is still being destroyed is promoted the same frame
        // the old one goes.
        if (_objects.TryGetValue(netId.Id, out var existing) && existing != null && existing != netId)
        {
            Debug.LogWarning($"[NetRegistry] NetId COLLISION on {netId.Id}: '{existing.gameObject.name}' already holds it; '{netId.gameObject.name}' waits until it is released — give scene props unique names (or check NetScope keys).");
            if (!_waiting.TryGetValue(netId.Id, out var queue))
                _waiting[netId.Id] = queue = new List<NetId>();
            if (!queue.Contains(netId)) queue.Add(netId);
            return;
        }

        _objects[netId.Id] = netId;
        _released.Remove(netId.Id);

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
        if (netId.HasSubscribers) DeliverBuffered(netId);
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
        if (Holder(netId.Id) != netId) return;   // a waiting registrant gets nothing yet
        DeliverBuffered(netId);
    }

    static void DeliverBuffered(NetId netId)
    {
        if (!_buffer.TryGetValue(netId.Id, out var pending)) return;
        RemoveBuffer(netId.Id);
        float now = Time.unscaledTime;
        foreach (var m in pending)
            if (now - m.at <= BufferTtlSeconds)   // stale state is worse than none
                netId.ReceiveEvent(m.payload);
    }

    static void RemoveBuffer(uint id)
    {
        if (_buffer.Remove(id)) _bufferOrder.Remove(id);
    }

    /// <summary>
    /// Release <paramref name="id"/> if <paramref name="owner"/> holds it, and
    /// promote the oldest live object waiting on the same id. A non-holder
    /// only leaves the waiting list — it can never take the id from the live
    /// holder (see Register).
    /// </summary>
    public static void Unregister(uint id, NetId owner)
    {
        if (_waiting.TryGetValue(id, out var queue))
        {
            queue.Remove(owner);
            queue.RemoveAll(n => n == null);
            if (queue.Count == 0) _waiting.Remove(id);
        }

        if (!_objects.TryGetValue(id, out var cur)) return;
        if (cur != null && cur != owner) return;   // someone else holds it (fake-null holders are released)
        _objects.Remove(id);
        // Anything buffered for this id predates a re-registration (a re-entered
        // Sequence level hashes to the same id): never replay it into the next one.
        RemoveBuffer(id);
        // ...and nothing that arrives before it does, either (see Dispatch).
        MarkReleased(id);

        if (_waiting.TryGetValue(id, out queue))
        {
            while (queue.Count > 0)
            {
                var next = queue[0];
                queue.RemoveAt(0);
                if (next == null) continue;
                if (queue.Count == 0) _waiting.Remove(id);
                global::DreamPark.NetLog.V($"[NetRegistry] NetId {id} promoted to waiting '{next.gameObject.name}'");
                Register(next);
                return;
            }
            _waiting.Remove(id);
        }
    }

    /// <summary>Unconditional removal. Prefer Unregister(id, owner): this one
    /// lets a dying duplicate deafen the live holder.</summary>
    [Obsolete("Use Unregister(id, owner) — the owner check keeps a destroyed duplicate from removing the live holder.")]
    public static void Unregister(uint id)
    {
        _objects.Remove(id);
    }

    // Defensive caps so a malicious/buggy peer can't grow memory without bound by
    // spraying events at unregistered (or guessed) NetIds. All three axes are
    // bounded and every one keeps the NEWEST data: a message older than
    // BufferTtlSeconds is never replayed, a full per-id list drops its oldest
    // message, and a full table evicts the least recently buffered id. The old
    // caps kept the OLDEST 32 messages forever and, once 256 ids that would
    // never register (another title, an old park) had filled the table, dropped
    // every new unknown id for the rest of the session.
    const int MaxBufferedIds = 256;
    const int MaxBufferedPerId = 32;

    public static void Dispatch(uint id, string payload)
    {
        if (_objects.TryGetValue(id, out var netId) && netId != null)
        {
            // Isolate handler exceptions — one bad message must not break the
            // network poll loop or other objects' dispatch.
            try { netId.ReceiveEvent(payload); }
            catch (Exception e) { Debug.LogWarning($"[NetRegistry] handler for {id} threw: {e.Message}"); }
            return;
        }

        float now = Time.unscaledTime;

        // The holder of this id was just torn down (this headset left the level,
        // a Sequence moved on). Peers still in it keep sending, and replaying
        // that into the next instance — a quick re-entry within the TTL hashes
        // to the same id — delivers the previous visit's events (ArcadeLand's
        // replay guard, Birthday Bash's stale hand-guards). The new instance
        // asks for current state itself (SharedState requests on register).
        if (_released.TryGetValue(id, out float releasedAt))
        {
            if (now - releasedAt <= BufferTtlSeconds)
            {
                global::DreamPark.NetLog.V($"[NetRegistry] Dropped an event for NetId {id}: its holder left {now - releasedAt:0.0}s ago (not replayed into a re-entry).");
                return;
            }
            _released.Remove(id);
        }

        // object hasn't loaded yet — buffer it (bounded). Log the first buffer
        // per id: an inbound event for an id nothing registered is the
        // signature of a NetId mismatch between builds (different hierarchy
        // paths → different hashes) and must not fail silently.
        if (!_buffer.TryGetValue(id, out var list))
        {
            Debug.LogWarning($"[NetRegistry] Event for UNREGISTERED NetId {id} — buffering ({BufferTtlSeconds:0}s). If this id never registers, the sender and receiver hierarchies don't match.");
            if (_buffer.Count >= MaxBufferedIds && _bufferOrder.Count > 0)
                RemoveBuffer(_bufferOrder[0]);
            list = new List<(string, float)>();
            _buffer[id] = list;
        }
        else _bufferOrder.Remove(id);
        _bufferOrder.Add(id);

        list.RemoveAll(m => now - m.at > BufferTtlSeconds);
        if (list.Count >= MaxBufferedPerId) list.RemoveAt(0);
        list.Add((payload, now));
    }

    static void MarkReleased(uint id)
    {
        float now = Time.unscaledTime;
        if (_released.Count >= MaxBufferedIds)
        {
            var stale = new List<uint>();
            foreach (var kv in _released) if (now - kv.Value > BufferTtlSeconds) stale.Add(kv.Key);
            foreach (var k in stale) _released.Remove(k);
            if (_released.Count >= MaxBufferedIds) _released.Clear();   // bounded; worst case = the old behaviour
        }
        _released[id] = now;
    }

    /// <summary>Messages currently buffered for an unregistered id (diagnostics/tests).</summary>
    public static int BufferedCount(uint id) => _buffer.TryGetValue(id, out var l) ? l.Count : 0;

    public static bool TryGet(uint id, out NetId netId)
    {
        return _objects.TryGetValue(id, out netId);
    }

    /// <summary>The object currently holding <paramref name="id"/> (null if none).</summary>
    public static NetId Holder(uint id) => _objects.TryGetValue(id, out var n) ? n : null;

    public static void Clear()
    {
        _objects.Clear();
        _buffer.Clear();
        _bufferOrder.Clear();
        _waiting.Clear();
        _released.Clear();
    }
}
