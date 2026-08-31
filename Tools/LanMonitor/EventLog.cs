using System.Text;
using System.Text.Json;

namespace LanMonitor;

/// <summary>
/// Bounded ring buffer of observed messages, plus the JSONL capture file.
///
/// Every entry is parsed out of the wire format DreamPark actually uses —
/// <c>{"type":"...","payload":{"netId":N,...}}</c> — so the feed reads as a
/// table of events rather than a wall of JSON blobs. The raw text is kept
/// alongside, because the parse is a convenience and the bytes are the truth.
/// </summary>
public sealed class EventLog : IDisposable
{
    private readonly int _maxEntries;
    private readonly Queue<ObservedEvent> _entries = new();
    private readonly object _lock = new();
    private long _counter;

    private StreamWriter? _capture;
    private string? _capturePath;
    private readonly object _captureLock = new();

    /// <summary>Full raw text kept per panel row. The capture file gets the whole message regardless.</summary>
    private const int MaxPanelRawChars = 4096;

    public EventLog(int maxEntries)
    {
        _maxEntries = Math.Max(100, maxEntries);
    }

    public string? CapturePath => _capturePath;
    public long TotalObserved => Interlocked.Read(ref _counter);

    /// <summary>
    /// Open the JSONL capture. Called once at startup when capture is enabled;
    /// a failure here is logged and downgraded — losing the capture file must
    /// never cost you the live feed.
    /// </summary>
    public void StartCapture(string directory)
    {
        try
        {
            var dir = Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(AppContext.BaseDirectory, directory);
            Directory.CreateDirectory(dir);

            var name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl";
            var path = Path.Combine(dir, name);

            lock (_captureLock)
            {
                // UTF8Encoding(false), not Encoding.UTF8 — the latter emits a BOM,
                // and a BOM on the first line makes the file fail to parse in jq,
                // Python's json module and most other JSONL readers. A capture you
                // cannot pipe into jq is not a capture.
                _capture = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = false
                };
                _capturePath = path;
            }

            Console.WriteLine($"[capture] writing to {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[capture] disabled — could not open capture file: {ex.Message}");
            _capture = null;
            _capturePath = null;
        }
    }

    /// <summary>
    /// Record one observed message.
    /// </summary>
    /// <param name="raw">
    /// The decoded JSON string exactly as it came off the wire.
    /// </param>
    /// <param name="wireBytes">Size of the LiteNetLib payload, for the size column.</param>
    /// <param name="session">Which session this arrived on, stamped into the capture.</param>
    public ObservedEvent Record(string raw, int wireBytes, string session)
    {
        var (type, netId, user, parsed) = ParseWire(raw);

        var entry = new ObservedEvent
        {
            Id = Interlocked.Increment(ref _counter),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = type,
            NetId = netId,
            User = user,
            Parsed = parsed,
            Bytes = wireBytes,
            Raw = raw.Length > MaxPanelRawChars ? raw[..MaxPanelRawChars] + "…" : raw
        };

        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _maxEntries) _entries.Dequeue();
        }

        WriteCapture(entry, raw, session);
        return entry;
    }

    /// <summary>
    /// Pull the wire shape apart. Three fields carry almost all the debugging
    /// value:
    ///   type    — the event name the sender passed to net_send
    ///   netId   — the ADDRESS the event is routed to (see the NetId rules in
    ///             the SDK CLAUDE.md; the same id on every headset is the point)
    ///   u       — the sender's own handle, by convention. The relay strips no
    ///             framing but adds none either, so this payload field is the
    ///             ONLY sender attribution a joined observer can have: every
    ///             message reaches us over the single connection to the host.
    /// </summary>
    internal static (string type, long? netId, string? user, bool parsed) ParseWire(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", null, null, false);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", null, null, false);

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";

            long? netId = null;
            string? user = null;

            if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("netId", out var n))
                {
                    if (n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var ni)) netId = ni;
                    else if (n.ValueKind == JsonValueKind.String && long.TryParse(n.GetString(), out var ns)) netId = ns;
                }

                if (payload.TryGetProperty("u", out var u)) user = Scalar(u);
            }

            return (type, netId, user, true);
        }
        catch (JsonException)
        {
            // Not JSON, or truncated. Keep it in the feed — an unparseable
            // message on this wire is itself a finding.
            return ("", null, null, false);
        }
    }

    private static string? Scalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private void WriteCapture(ObservedEvent entry, string raw, string session)
    {
        if (_capture == null) return;

        // The line is assembled by hand rather than via a serializer so the
        // original message can be spliced in verbatim when it is valid JSON —
        // `jq '.msg.payload'` then works on a capture without a second decode
        // step. When it isn't JSON it lands as a string under rawText instead.
        var sb = new StringBuilder(raw.Length + 256);
        sb.Append("{\"seq\":").Append(entry.Id)
          .Append(",\"t\":").Append(entry.TimestampMs)
          .Append(",\"session\":").Append(JsonEncode(session))
          .Append(",\"bytes\":").Append(entry.Bytes);

        if (!string.IsNullOrEmpty(entry.Type)) sb.Append(",\"type\":").Append(JsonEncode(entry.Type));
        if (entry.NetId.HasValue) sb.Append(",\"netId\":").Append(entry.NetId.Value);
        if (entry.User != null) sb.Append(",\"u\":").Append(JsonEncode(entry.User));

        if (entry.Parsed) sb.Append(",\"msg\":").Append(raw);
        else sb.Append(",\"rawText\":").Append(JsonEncode(raw));

        sb.Append('}');

        lock (_captureLock)
        {
            try { _capture?.WriteLine(sb.ToString()); }
            catch (Exception ex) { Console.Error.WriteLine($"[capture] write failed: {ex.Message}"); }
        }
    }

    private static string JsonEncode(string value) => JsonSerializer.Serialize(value);

    /// <summary>Called from the main loop; keeps the capture durable without paying a flush per message.</summary>
    public void FlushCapture()
    {
        lock (_captureLock)
        {
            try { _capture?.Flush(); } catch { /* best effort */ }
        }
    }

    /// <summary>Entries newer than <paramref name="sinceId"/>, oldest first, capped at <paramref name="limit"/>.</summary>
    public List<ObservedEvent> Since(long sinceId, int limit)
    {
        lock (_lock)
        {
            var result = new List<ObservedEvent>();
            foreach (var e in _entries)
            {
                if (e.Id <= sinceId) continue;
                result.Add(e);
                if (result.Count >= limit) break;
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    public void Dispose()
    {
        lock (_captureLock)
        {
            try { _capture?.Flush(); _capture?.Dispose(); } catch { }
            _capture = null;
        }
    }
}

public sealed class ObservedEvent
{
    public long Id { get; init; }
    public long TimestampMs { get; init; }
    public string Type { get; init; } = "";
    public long? NetId { get; init; }
    public string? User { get; init; }
    public int Bytes { get; init; }
    public string Raw { get; init; } = "";

    /// <summary>False when the message wasn't valid JSON — surfaced in the panel rather than hidden.</summary>
    public bool Parsed { get; init; }
}
