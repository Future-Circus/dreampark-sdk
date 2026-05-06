using System.Collections.Concurrent;

namespace DreamBoxRelay;

/// <summary>
/// Shared in-memory view of the relay state. The UDP loop writes into it,
/// the web control panel reads from it.
/// </summary>
public sealed class ServerState
{
    public RelayConfig Config { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    private long _lastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long _totalMessagesRelayed;
    private long _totalBytesRelayed;

    public readonly ConcurrentDictionary<int, PeerInfo> Peers = new();
    public readonly MessageLog Log = new();

    public ServerState(RelayConfig config)
    {
        Config = config;
    }

    public long LastActivityAt => Interlocked.Read(ref _lastActivityAt);
    public long TotalMessagesRelayed => Interlocked.Read(ref _totalMessagesRelayed);
    public long TotalBytesRelayed => Interlocked.Read(ref _totalBytesRelayed);
    public int ConnectedCount => Peers.Count;

    public void TouchActivity()
        => Interlocked.Exchange(ref _lastActivityAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public void RecordRelay(int bytes)
    {
        Interlocked.Increment(ref _totalMessagesRelayed);
        Interlocked.Add(ref _totalBytesRelayed, bytes);
        TouchActivity();
    }
}

public sealed class PeerInfo
{
    public int PeerId { get; init; }
    public string Endpoint { get; init; } = "";
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
}
