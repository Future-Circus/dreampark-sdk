using System.Net;
using System.Text;
using System.Text.Json;

namespace LanMonitor;

/// <summary>
/// The browser front end. Same approach as the relay's WebControlPanel —
/// stdlib HttpListener, no ASP.NET — with two differences: the feed endpoint
/// is incremental (<c>?since=</c>) so the page can poll cheaply forever, and
/// there are a few write endpoints so you can attach, detach and pin a parkId
/// without restarting the process.
///
/// Those write endpoints move monitor state only. Nothing the browser can do
/// puts a byte on the LAN.
/// </summary>
public sealed class MonitorPanel : IDisposable
{
    private readonly MonitorState _state;
    private readonly SessionObserver _observer;
    private readonly WebPanelConfig _config;
    private readonly string _wwwroot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public MonitorPanel(MonitorState state, SessionObserver observer)
    {
        _state = state;
        _observer = observer;
        _config = state.Config.WebPanel;
        _wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }

    public bool Start()
    {
        if (!_config.Enabled)
        {
            Console.WriteLine("[panel] disabled — running headless (capture only).");
            return false;
        }

        var prefix = $"http://{_config.BindAddress}:{_config.Port}/";
        try
        {
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"[panel] could not bind {prefix}: {ex.Message}");
            Console.Error.WriteLine($"[panel] is another LAN Monitor already running? On Windows you may need: netsh http add urlacl url={prefix} user=Everyone");
            return false;
        }

        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "MonitorPanel" };
        _thread.Start();
        Console.WriteLine($"[panel] serving on {prefix}");
        return true;
    }

    private void ListenLoop()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        // HttpListenerRequest parses this for us — no System.Web dependency needed.
        var query = ctx.Request.QueryString;

        try
        {
            switch (path)
            {
                case "/":
                case "/index.html":
                    ServeStaticFile(ctx, "index.html", "text/html; charset=utf-8");
                    break;

                case "/api/state":
                    WriteJson(ctx, BuildState());
                    break;

                case "/api/events":
                    WriteJson(ctx, BuildEvents(query));
                    break;

                case "/api/attach":
                {
                    var session = query["session"] ?? "";
                    if (string.IsNullOrEmpty(session))
                    {
                        ctx.Response.StatusCode = 400;
                        WriteJson(ctx, new { ok = false, error = "missing session" });
                        break;
                    }
                    _state.Post(new MonitorCommand(MonitorCommandKind.Attach, session));
                    WriteJson(ctx, new { ok = true });
                    break;
                }

                case "/api/attach-direct":
                {
                    var host = (query["host"] ?? "").Trim();
                    if (!int.TryParse(query["port"], out var port) || string.IsNullOrEmpty(host) || port <= 0)
                    {
                        ctx.Response.StatusCode = 400;
                        WriteJson(ctx, new { ok = false, error = "need host and port" });
                        break;
                    }

                    var beacon = new BeaconInfo
                    {
                        Host = host,
                        Port = port,
                        Key = (query["key"] ?? "").Trim(),
                        HostType = "peer",
                        HostId = "(manual)",
                        ParkId = "",
                        Channel = "",
                        SourceAddress = host,
                        ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };

                    _state.Post(new MonitorCommand(MonitorCommandKind.AttachDirect, beacon.SessionId, beacon));
                    WriteJson(ctx, new { ok = true, sessionId = beacon.SessionId });
                    break;
                }

                case "/api/detach":
                    // Detaching by hand also clears the pin — otherwise the
                    // director would immediately re-attach and it would look
                    // like the button did nothing.
                    _state.PinnedParkId = "";
                    _state.Post(new MonitorCommand(MonitorCommandKind.Detach));
                    WriteJson(ctx, new { ok = true });
                    break;

                case "/api/pin":
                    _state.PinnedParkId = (query["parkId"] ?? "").Trim();
                    if (query["channel"] != null) _state.PinnedChannel = query["channel"]!.Trim();
                    WriteJson(ctx, new { ok = true, parkId = _state.PinnedParkId, channel = _state.PinnedChannel });
                    break;

                case "/api/clear":
                    _state.Post(new MonitorCommand(MonitorCommandKind.Clear));
                    WriteJson(ctx, new { ok = true });
                    break;

                case "/favicon.ico":
                    ctx.Response.StatusCode = 204;
                    ctx.Response.ContentLength64 = 0;
                    break;

                default:
                    ctx.Response.StatusCode = 404;
                    WriteText(ctx, $"not found: {path}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel] request error on {path}: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = 500;
                WriteText(ctx, "internal error");
            }
            catch { /* response probably already closed */ }
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    private object BuildState()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var sessions = _state.Sessions.Values
            .OrderByDescending(s => s.Latest.ReceivedAtMs)
            .Select(s => new
            {
                sessionId = s.Latest.SessionId,
                host = s.Latest.Host,
                port = s.Latest.Port,
                // The address the beacon claims, versus the address it actually
                // arrived from. Both have been in this table all along and only
                // the claim was ever shown — which is the half that can be wrong,
                // and the half every joining headset acts on. When they disagree,
                // headsets on the real Wi-Fi are being sent somewhere that does
                // not route, and the host cannot notice because a host can always
                // reach itself.
                sourceAddress = s.Latest.SourceAddress,
                addressMismatch = !string.IsNullOrEmpty(s.Latest.SourceAddress)
                                  && !string.IsNullOrEmpty(s.Latest.Host)
                                  && s.Latest.SourceAddress != s.Latest.Host,
                hostType = s.Latest.HostType,
                hostId = s.Latest.HostId,
                parkId = s.Latest.ParkId,
                channel = s.Latest.Channel,
                dreamboxId = s.Latest.DreamboxId,
                protocolVersion = s.Latest.ProtocolVersion,
                msgCap = s.Latest.MsgCap,
                seq = s.Latest.Seq,
                beaconCount = s.BeaconCount,
                firstSeenMs = s.FirstSeenMs,
                lastSeenMs = s.Latest.ReceivedAtMs,
                ageMs = now - s.Latest.ReceivedAtMs,
                live = _state.IsLive(s, now),
                attached = s.Latest.SessionId == _state.AttachedSessionId
            })
            .ToList();

        return new
        {
            state = _state.State.ToString().ToLowerInvariant(),
            statusMessage = _state.StatusMessage,
            attachedSessionId = _state.AttachedSessionId,
            attachedAtMs = _state.AttachedAtMs,
            connected = _observer.IsConnected,
            ping = _observer.Ping,
            pinnedParkId = _state.PinnedParkId,
            pinnedChannel = _state.PinnedChannel,
            discoveryError = _state.DiscoveryError,
            capturePath = _state.Log.CapturePath,
            messagesObserved = _state.MessagesObserved,
            bytesObserved = _state.BytesObserved,
            startedAt = _state.StartedAt.ToUnixTimeMilliseconds(),
            nowMs = now,
            sessions
        };
    }

    private object BuildEvents(System.Collections.Specialized.NameValueCollection query)
    {
        long since = 0;
        long.TryParse(query["since"], out since);

        var limit = 500;
        if (int.TryParse(query["limit"], out var l) && l is > 0 and <= 2000) limit = l;

        var events = _state.Log.Since(since, limit)
            .Select(e => new
            {
                id = e.Id,
                tMs = e.TimestampMs,
                type = e.Type,
                netId = e.NetId,
                u = e.User,
                bytes = e.Bytes,
                parsed = e.Parsed,
                raw = e.Raw
            })
            .ToList();

        return new
        {
            events,
            latestId = _state.Log.TotalObserved,
            totalObserved = _state.Log.TotalObserved
        };
    }

    private void ServeStaticFile(HttpListenerContext ctx, string relativePath, string mime)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_wwwroot, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(_wwwroot), StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 403;
            WriteText(ctx, "forbidden");
            return;
        }

        if (!File.Exists(fullPath))
        {
            ctx.Response.StatusCode = 404;
            WriteText(ctx, $"missing: {relativePath} (expected under {_wwwroot})");
            return;
        }

        var bytes = File.ReadAllBytes(fullPath);
        ctx.Response.ContentType = mime;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteJson(HttpListenerContext ctx, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, s_json));
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteText(HttpListenerContext ctx, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _thread?.Join(2000);
        _cts.Dispose();
    }
}
