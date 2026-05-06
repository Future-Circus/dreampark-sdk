using System.Net;
using System.Text;
using System.Text.Json;

namespace DreamBoxRelay;

/// <summary>
/// Tiny HTTP server (stdlib HttpListener, no ASP.NET dependency) that serves
/// a static HTML control panel and a minimal JSON API exposing server state.
///
/// Intended for developer local testing only. Default bind is 127.0.0.1;
/// the panel is disabled entirely by default in config.
/// </summary>
public sealed class WebControlPanel : IDisposable
{
    private readonly ServerState _state;
    private readonly WebPanelConfig _panelConfig;
    private readonly string _wwwroot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public WebControlPanel(ServerState state)
    {
        _state = state;
        _panelConfig = state.Config.WebPanel;
        _wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }

    public void Start()
    {
        if (!_panelConfig.Enabled)
        {
            Console.WriteLine("[webpanel] disabled in config — skipping.");
            return;
        }

        var prefix = $"http://{_panelConfig.BindAddress}:{_panelConfig.Port}/";
        try
        {
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"[webpanel] could not bind {prefix}: {ex.Message}");
            Console.Error.WriteLine("[webpanel] on Windows you may need to grant the URL ACL: netsh http add urlacl url=" + prefix + " user=Everyone");
            return;
        }

        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "WebControlPanel" };
        _thread.Start();

        Console.WriteLine($"[webpanel] serving on {prefix}");
    }

    private void ListenLoop()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch (HttpListenerException)
            {
                return; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Handle per-request on a threadpool task so we don't block the accept loop.
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        Console.WriteLine($"[webpanel] {ctx.Request.HttpMethod} {path}");
        try
        {
            if (path == "/" || path == "/index.html" || path == "/index.htm")
            {
                ServeStaticFile(ctx, "index.html", "text/html; charset=utf-8");
            }
            else if (path == "/api/status")
            {
                WriteJson(ctx, BuildStatus());
            }
            else if (path == "/api/peers")
            {
                WriteJson(ctx, BuildPeers());
            }
            else if (path == "/api/messages")
            {
                WriteJson(ctx, BuildMessages());
            }
            else if (path == "/favicon.ico")
            {
                // Browsers always ask for this; return empty 204 so it doesn't
                // clutter logs with 404s or clobber the UI with "not found".
                ctx.Response.StatusCode = 204;
                ctx.Response.ContentLength64 = 0;
            }
            else if (path.StartsWith("/static/"))
            {
                // Optional future: static/css/js assets under wwwroot/static/
                var rel = path.Substring("/static/".Length);
                ServeStaticFile(ctx, Path.Combine("static", rel), GuessMime(rel));
            }
            else
            {
                ctx.Response.StatusCode = 404;
                WriteText(ctx, $"not found: {path}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[webpanel] request error: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = 500;
                WriteText(ctx, "internal error");
            }
            catch { /* response likely already closed */ }
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    private object BuildStatus() => new
    {
        dreamboxId = _state.Config.DreamboxId,
        port = _state.Config.Port,
        connectionKey = _state.Config.ConnectionKey,
        maxConnections = _state.Config.MaxConnections,
        debug = _state.Config.Debug,
        discoveryEnabled = _state.Config.DiscoveryEnabled,
        discoveryPort = _state.Config.DiscoveryPort,
        connectedCount = _state.ConnectedCount,
        totalMessagesRelayed = _state.TotalMessagesRelayed,
        totalBytesRelayed = _state.TotalBytesRelayed,
        startedAt = _state.StartedAt.ToUnixTimeSeconds(),
        lastActivityAt = _state.LastActivityAt,
        nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    private object BuildPeers()
    {
        var list = _state.Peers.Values
            .OrderBy(p => p.ConnectedAt)
            .Select(p => new
            {
                peerId = p.PeerId,
                endpoint = p.Endpoint,
                connectedAt = p.ConnectedAt.ToUnixTimeSeconds()
            })
            .ToList();
        return new { count = list.Count, peers = list };
    }

    private object BuildMessages()
    {
        var list = _state.Log.Snapshot()
            .Select(m => new
            {
                id = m.Id,
                peerId = m.PeerId,
                endpoint = m.Endpoint,
                size = m.Size,
                preview = m.Preview,
                timestampMs = m.TimestampMs
            })
            .ToList();
        return new { count = list.Count, messages = list };
    }

    private void ServeStaticFile(HttpListenerContext ctx, string relativePath, string mime)
    {
        var fullPath = Path.Combine(_wwwroot, relativePath);
        // prevent path traversal
        var normalized = Path.GetFullPath(fullPath);
        if (!normalized.StartsWith(Path.GetFullPath(_wwwroot), StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 403;
            WriteText(ctx, "forbidden");
            return;
        }

        if (!File.Exists(normalized))
        {
            ctx.Response.StatusCode = 404;
            WriteText(ctx, $"missing: {relativePath}");
            return;
        }

        var bytes = File.ReadAllBytes(normalized);
        ctx.Response.ContentType = mime;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    private static void WriteJson(HttpListenerContext ctx, object payload)
    {
        var json = JsonSerializer.Serialize(payload, s_json);
        var bytes = Encoding.UTF8.GetBytes(json);
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
