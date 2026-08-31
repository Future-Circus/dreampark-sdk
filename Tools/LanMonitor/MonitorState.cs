using System.Collections.Concurrent;

namespace LanMonitor;

public enum AttachState
{
    /// <summary>Not attached and not trying. Either nothing matched, or you detached.</summary>
    Idle,
    Connecting,
    Attached,
    /// <summary>The host refused us — see <see cref="MonitorState.StatusMessage"/>.</summary>
    Rejected,
    Failed
}

/// <summary>
/// Everything the beacon thread, the observer and the web panel share.
///
/// Threading: the beacon thread writes <see cref="Sessions"/>, the main loop
/// writes attachment state, the panel reads both and enqueues commands. All
/// mutation of the LiteNetLib client happens on the main loop — the panel only
/// ever posts a <see cref="MonitorCommand"/>.
/// </summary>
public sealed class MonitorState
{
    public MonitorConfig Config { get; }
    public EventLog Log { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public readonly ConcurrentDictionary<string, SessionEntry> Sessions = new();

    private readonly ConcurrentQueue<MonitorCommand> _commands = new();

    private long _messagesObserved;
    private long _bytesObserved;

    // Attachment — written only by the main loop, read by everyone.
    public volatile AttachState State = AttachState.Idle;
    public volatile string? AttachedSessionId;
    public volatile string StatusMessage = "starting up";
    public long AttachedAtMs;

    /// <summary>Auto-attach filter. Empty = manual pick. Settable live from the panel.</summary>
    public volatile string PinnedParkId;
    public volatile string PinnedChannel;

    /// <summary>Set when the beacon socket could not bind; surfaced in the panel.</summary>
    public volatile string? DiscoveryError;

    public MonitorState(MonitorConfig config, EventLog log)
    {
        Config = config;
        Log = log;
        PinnedParkId = config.ParkId ?? "";
        PinnedChannel = config.Channel ?? "";
    }

    public long MessagesObserved => Interlocked.Read(ref _messagesObserved);
    public long BytesObserved => Interlocked.Read(ref _bytesObserved);

    public void RecordObserved(int bytes)
    {
        Interlocked.Increment(ref _messagesObserved);
        Interlocked.Add(ref _bytesObserved, bytes);
    }

    public void RecordBeacon(BeaconInfo beacon)
    {
        Sessions.AddOrUpdate(
            beacon.SessionId,
            _ => new SessionEntry
            {
                FirstSeenMs = beacon.ReceivedAtMs,
                BeaconCount = 1,
                Latest = beacon
            },
            (_, existing) =>
            {
                existing.BeaconCount++;
                existing.Latest = beacon;
                return existing;
            });
    }

    /// <summary>Sessions whose most recent beacon is within the staleness window.</summary>
    public bool IsLive(SessionEntry entry, long nowMs)
        => nowMs - entry.Latest.ReceivedAtMs <= Config.SessionStaleSeconds * 1000L;

    /// <summary>
    /// Drop sessions we haven't heard from in a long while, so the panel list
    /// doesn't accumulate every host that ever booted on this LAN. Generous
    /// versus the staleness window: a stale row is still useful ("it WAS here
    /// a minute ago" is a finding when a host vanishes).
    /// </summary>
    public void PruneSessions(long nowMs)
    {
        var cutoff = Config.SessionStaleSeconds * 1000L * 20;
        foreach (var kvp in Sessions)
        {
            if (kvp.Key == AttachedSessionId) continue;
            if (nowMs - kvp.Value.Latest.ReceivedAtMs > cutoff)
                Sessions.TryRemove(kvp.Key, out _);
        }
    }

    // ── Command queue (panel → main loop) ────────────────────────────

    public void Post(MonitorCommand command) => _commands.Enqueue(command);

    public bool TryTakeCommand(out MonitorCommand command) => _commands.TryDequeue(out command!);
}

public sealed class SessionEntry
{
    public long FirstSeenMs { get; init; }
    public long BeaconCount { get; set; }
    public BeaconInfo Latest { get; set; } = null!;
}

public enum MonitorCommandKind
{
    /// <summary>Attach to a session already in the discovered table, by id.</summary>
    Attach,
    /// <summary>Attach to a host typed in by hand — the fallback when beacons can't be heard.</summary>
    AttachDirect,
    Detach,
    Clear
}

public sealed record MonitorCommand(
    MonitorCommandKind Kind,
    string? SessionId = null,
    BeaconInfo? Beacon = null);
