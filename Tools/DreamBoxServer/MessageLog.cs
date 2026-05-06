using System.Text;

namespace DreamBoxRelay;

/// <summary>
/// Bounded, thread-safe ring buffer of the most recent messages relayed.
/// Used by the web control panel to render a live feed. Kept small and
/// self-contained — no external deps, no allocation in the hot path beyond
/// the entry struct.
/// </summary>
public sealed class MessageLog
{
    private const int MaxEntries = 200;
    private const int PreviewBytes = 160;

    private readonly Queue<MessageEntry> _entries = new();
    private readonly object _lock = new();
    private long _counter;

    public void Record(int peerId, string endpoint, byte[] bytes)
    {
        var entry = new MessageEntry
        {
            Id = Interlocked.Increment(ref _counter),
            PeerId = peerId,
            Endpoint = endpoint ?? "",
            Size = bytes?.Length ?? 0,
            Preview = BuildPreview(bytes),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>
    /// Snapshot copy of the current buffer. Most recent last.
    /// </summary>
    public List<MessageEntry> Snapshot()
    {
        lock (_lock)
        {
            return new List<MessageEntry>(_entries);
        }
    }

    private static string BuildPreview(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";

        // LiteNetLib's NetDataWriter.Put(string) — which DreamBoxClient uses —
        // prefixes the payload with a 7-bit-encoded varint length. Detect and
        // skip that so the preview shows the actual JSON instead of framing.
        int offset = DetectVarintStringPrefix(bytes);

        int available = bytes.Length - offset;
        int take = Math.Min(PreviewBytes, available);
        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(bytes, offset, take);
        }
        catch
        {
            raw = BitConverter.ToString(bytes, offset, Math.Min(32, take));
        }

        // Any remaining control chars get replaced so the UI never gets
        // surprised by a rogue NUL or escape sequence in a binary payload.
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(c < 0x20 && c != '\n' && c != '\t' ? '·' : c);
        }

        if (available > PreviewBytes) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>
    /// DreamPark's wire format is JSON-over-LiteNetLib. LiteNetLib adds its
    /// own length prefix (1-4 bytes depending on version and payload size)
    /// which is meaningless in the preview. We don't try to decode the
    /// prefix — instead we scan the first few bytes for the JSON start
    /// marker and begin the preview there.
    ///
    /// Non-JSON payloads simply show from byte 0, which is fine.
    /// </summary>
    private static int DetectVarintStringPrefix(byte[] bytes)
    {
        int maxScan = Math.Min(bytes.Length, 6);
        for (int i = 0; i < maxScan; i++)
        {
            if (bytes[i] == (byte)'{' || bytes[i] == (byte)'[')
            {
                return i;
            }
        }
        return 0;
    }
}

public sealed class MessageEntry
{
    public long Id { get; init; }
    public int PeerId { get; init; }
    public string Endpoint { get; init; } = "";
    public int Size { get; init; }
    public string Preview { get; init; } = "";
    public long TimestampMs { get; init; }
}
