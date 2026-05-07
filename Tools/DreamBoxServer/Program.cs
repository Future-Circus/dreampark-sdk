using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using DreamBoxRelay;
using LiteNetLib;
using LiteNetLib.Utils;

// ── Environment check mode ────────────────────────────────────────
if (args.Contains("--check"))
{
    Console.WriteLine("=== DreamBox Dev Server — Environment Check ===");
    Console.WriteLine();
    PrintEnvironmentInfo();

    var checkResolution = RelayConfig.ResolveConfig(args.Where(a => a != "--check").ToArray());
    Console.WriteLine();
    Console.WriteLine($"Config: {checkResolution.Path ?? "(none)"}");
    Console.WriteLine($"  Resolved via: {checkResolution.ResolvedVia}");

    if (checkResolution.Path == null)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("FAIL: no config file found");
        Environment.Exit(1);
    }

    var checkConfig = RelayConfig.Load(checkResolution.Path);
    Console.WriteLine();
    var portsOk = CheckPorts(checkConfig);

    Console.WriteLine();
    if (portsOk)
    {
        Console.WriteLine("All checks passed.");
        Environment.Exit(0);
    }
    else
    {
        Console.Error.WriteLine("One or more port checks failed.");
        Environment.Exit(1);
    }
}

// ── Startup diagnostics ──────────────────────────────────────────
PrintEnvironmentInfo();

var resolution = RelayConfig.ResolveConfig(args);
var configPath = resolution.Path;
Console.WriteLine($"[startup] Config resolved via: {resolution.ResolvedVia}");

var config = RelayConfig.Load(configPath);

var state = new ServerState(config);

var listener = new EventBasedNetListener();
var server = new NetManager(listener)
{
    // LiteNetLib doesn't expose a MaxConnections property on NetManager.
    // The limit is enforced in the ConnectionRequestEvent handler below.
};

// ── Port availability checks ─────────────────────────────────────
CheckPorts(config);

// ── Start services with individual error handling ────────────────
try
{
    server.Start(config.Port);
    Console.WriteLine($"DreamBox relay running on :{config.Port} (max {config.MaxConnections} peers, debug={config.Debug})");
}
catch (Exception ex)
{
    PrintStartupFailed($"Failed to start relay on port {config.Port}: {ex.Message}",
        $"Check if port {config.Port} is already in use: lsof -i :{config.Port}");
    Environment.Exit(1);
}

DiscoveryBeacon? beacon = null;
try
{
    beacon = new DiscoveryBeacon(config);
    beacon.Start();
}
catch (Exception ex)
{
    PrintStartupFailed($"Failed to start discovery beacon on port {config.DiscoveryPort}: {ex.Message}",
        $"Check if port {config.DiscoveryPort} is already in use: lsof -i :{config.DiscoveryPort}");
    server.Stop();
    Environment.Exit(1);
}

WebControlPanel? webPanel = null;
try
{
    webPanel = new WebControlPanel(state);
    webPanel.Start();
}
catch (Exception ex)
{
    PrintStartupFailed($"Failed to start web panel on port {config.WebPanel.Port}: {ex.Message}",
        $"Check if port {config.WebPanel.Port} is already in use: lsof -i :{config.WebPanel.Port}");
    beacon.Dispose();
    server.Stop();
    Environment.Exit(1);
}

// Status file writer — writes every 5 seconds so session-manager can monitor idle state
Timer? statusTimer = null;
if (!string.IsNullOrEmpty(config.StatusFilePath))
{
    Console.WriteLine($"[relay] status file: {config.StatusFilePath}");
    statusTimer = new Timer(_ =>
    {
        try
        {
            var status = JsonSerializer.Serialize(new
            {
                connectedCount = state.ConnectedCount,
                lastActivityAt = state.LastActivityAt,
            });
            // Atomic write: write to temp file then rename to avoid partial reads
            var tmp = config.StatusFilePath + ".tmp";
            File.WriteAllText(tmp, status);
            File.Move(tmp, config.StatusFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[relay] failed to write status file: {ex.Message}");
        }
    }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
}

listener.ConnectionRequestEvent += request =>
{
    if (state.ConnectedCount >= config.MaxConnections)
    {
        request.Reject();
        if (config.Debug)
            Console.WriteLine($"[relay] rejected connection — at max ({config.MaxConnections})");
        return;
    }
    request.AcceptIfKey(config.ConnectionKey);
};

listener.PeerConnectedEvent += peer =>
{
    state.Peers[peer.Id] = new PeerInfo
    {
        PeerId = peer.Id,
        Endpoint = peer.ToString() ?? "?"
    };
    state.TouchActivity();
    Console.WriteLine($"Headset connected: {peer} | Total: {server.ConnectedPeersCount}");
};

listener.PeerDisconnectedEvent += (peer, info) =>
{
    state.Peers.TryRemove(peer.Id, out _);
    state.TouchActivity();
    Console.WriteLine($"Headset disconnected: {peer} | Reason: {info.Reason}");
};

listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
{
    var bytes = reader.GetRemainingBytes();
    state.RecordRelay(bytes.Length);
    state.Log.Record(peer.Id, peer.ToString() ?? "?", bytes);

    if (config.Debug)
    {
        string message = System.Text.Encoding.UTF8.GetString(bytes);
        Console.WriteLine($"[relay] received from {peer}: {message}");
    }

    var writer = new NetDataWriter();
    writer.Put(bytes);
    server.SendToAll(writer, DeliveryMethod.ReliableOrdered, peer);
    reader.Recycle();
};

while (true)
{
    server.PollEvents();
    Thread.Sleep(config.PollIntervalMs);
}

// ── Helper methods ───────────────────────────────────────────────

void PrintEnvironmentInfo()
{
    Console.WriteLine($"[startup] .NET Runtime: {Environment.Version}");
    Console.WriteLine($"[startup] OS: {Environment.OSVersion} ({RuntimeInformation.ProcessArchitecture})");
    Console.WriteLine($"[startup] Working dir: {Environment.CurrentDirectory}");
    Console.WriteLine($"[startup] Base dir: {AppContext.BaseDirectory}");
}

bool CheckPorts(RelayConfig cfg)
{
    var allOk = true;
    allOk &= CheckPort("Relay", cfg.Port);
    allOk &= CheckPort("Discovery", cfg.DiscoveryPort);
    if (cfg.WebPanel.Enabled)
        allOk &= CheckPort("WebPanel", cfg.WebPanel.Port);
    return allOk;
}

bool CheckPort(string name, int port)
{
    try
    {
        using var tcp = new TcpClient();
        var ar = tcp.BeginConnect("127.0.0.1", port, null, null);
        if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)))
        {
            try { tcp.EndConnect(ar); } catch { }
            if (tcp.Connected)
            {
                Console.Error.WriteLine($"[startup] WARNING: {name} port {port} is already in use!");
                return false;
            }
        }
        Console.WriteLine($"[startup] {name} port {port} — available");
        return true;
    }
    catch
    {
        Console.WriteLine($"[startup] {name} port {port} — available");
        return true;
    }
}

void PrintStartupFailed(string error, string hint)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("============================");
    Console.Error.WriteLine("=== STARTUP FAILED ===");
    Console.Error.WriteLine("============================");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  Hint: {hint}");
    Console.Error.WriteLine();
}
