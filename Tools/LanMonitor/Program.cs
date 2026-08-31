using System.Net;
using System.Net.Sockets;
using LanMonitor;

// ─────────────────────────────────────────────────────────────────────
// DreamPark LAN Monitor
//
// Passive-as-possible observer for headset-to-headset multiplayer traffic.
// Watches dream-pub beacons on UDP :7700, joins a session as a read-only
// peer, and streams every message it sees to a browser panel and a JSONL
// capture file.
//
// It hosts nothing and advertises nothing. See BeaconWatcher and
// SessionObserver for why that is a structural property of this tool and
// not a setting.
// ─────────────────────────────────────────────────────────────────────

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

var resolution = MonitorConfig.ResolveConfig(args);
var config = MonitorConfig.Load(resolution.Path);
config.ApplyArgs(args);

if (args.Contains("--check"))
{
    Console.WriteLine("=== DreamPark LAN Monitor — Environment Check ===");
    Console.WriteLine($".NET     : {Environment.Version}");
    Console.WriteLine($"OS       : {Environment.OSVersion}");
    Console.WriteLine($"Config   : {resolution.Path ?? "(none)"} — via {resolution.ResolvedVia}");
    Console.WriteLine();

    var beaconFree = CanBindUdp(config.DiscoveryPort);
    var panelFree = CanBindTcp(config.WebPanel.BindAddress, config.WebPanel.Port);

    Console.WriteLine($"UDP :{config.DiscoveryPort} (beacons)  : {(beaconFree ? "OK" : "shared/in use — degraded to manual attach")}");
    Console.WriteLine($"TCP :{config.WebPanel.Port} (panel)    : {(panelFree ? "OK" : "IN USE — another monitor is probably running")}");
    Console.WriteLine();
    Console.WriteLine(panelFree ? "Ready." : "Panel port is taken; stop the other instance or set webPanel.port.");
    return panelFree ? 0 : 1;
}

Console.WriteLine("=== DreamPark LAN Monitor ===");
Console.WriteLine($"[startup] config via: {resolution.ResolvedVia}");
Console.WriteLine("[startup] read-only observer — this process never hosts, beacons or sends.");

var log = new EventLog(config.MaxEvents);
if (config.Capture.Enabled) log.StartCapture(config.Capture.Directory);

var state = new MonitorState(config, log);
var observer = new SessionObserver(state);
observer.Start();

var beacons = new BeaconWatcher(state, config.DiscoveryPort);
if (!beacons.Start()) state.DiscoveryError = beacons.BindError;

var panel = new MonitorPanel(state, observer);
panel.Start();

if (!string.IsNullOrEmpty(state.PinnedParkId))
    Console.WriteLine($"[startup] auto-attaching to parkId '{state.PinnedParkId}' as soon as it appears.");
else
    Console.WriteLine("[startup] no parkId pinned — pick a session in the panel, or pass --park <id>.");

state.StatusMessage = string.IsNullOrEmpty(state.PinnedParkId)
    ? "listening for sessions"
    : $"waiting for parkId '{state.PinnedParkId}'";

// --attach host:port[:key] — skip discovery entirely.
if (!string.IsNullOrWhiteSpace(config.DirectAttach))
{
    var parts = config.DirectAttach.Split(':');
    if (parts.Length >= 2 && int.TryParse(parts[1], out var directPort))
    {
        state.Post(new MonitorCommand(MonitorCommandKind.AttachDirect, null, new BeaconInfo
        {
            Host = parts[0],
            Port = directPort,
            Key = parts.Length > 2 ? parts[2] : "",
            HostType = "peer",
            HostId = "(manual)",
            SourceAddress = parts[0],
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }));
        Console.WriteLine($"[startup] --attach {config.DirectAttach} — skipping discovery.");
    }
    else
    {
        Console.Error.WriteLine($"[startup] --attach '{config.DirectAttach}' is not host:port[:key] — ignoring.");
    }
}

// Graceful shutdown so the capture file is complete.
var shutdown = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[shutdown] stopping…");
    shutdown.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Set();

// ── Main loop ────────────────────────────────────────────────────────
// Everything that touches the LiteNetLib client happens here, on one
// thread. The panel and the beacon watcher only ever post state.

var director = new AttachDirector(state, observer);
var nextDirectorTick = DateTime.UtcNow;
var nextFlush = DateTime.UtcNow;

while (!shutdown.IsSet)
{
    observer.Poll();

    while (state.TryTakeCommand(out var command))
    {
        switch (command.Kind)
        {
            case MonitorCommandKind.Attach:
                director.AttachManually(command.SessionId!);
                break;
            case MonitorCommandKind.AttachDirect:
                director.AttachDirect(command.Beacon!);
                break;
            case MonitorCommandKind.Detach:
                Console.WriteLine("[observe] detach requested from panel.");
                observer.Detach();
                state.StatusMessage = "detached";
                break;
            case MonitorCommandKind.Clear:
                state.Log.Clear();
                break;
        }
    }

    var now = DateTime.UtcNow;
    if (now >= nextDirectorTick)
    {
        nextDirectorTick = now.AddMilliseconds(500);
        director.Tick();
        state.PruneSessions(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    if (now >= nextFlush)
    {
        nextFlush = now.AddMilliseconds(500);
        log.FlushCapture();
    }

    Thread.Sleep(config.PollIntervalMs);
}

Console.WriteLine("[shutdown] flushing capture…");
observer.Dispose();
beacons.Dispose();
panel.Dispose();
log.Dispose();
Console.WriteLine("[shutdown] done.");
return 0;

// ── Helpers ──────────────────────────────────────────────────────────

static bool CanBindUdp(int port)
{
    try
    {
        using var s = new UdpClient();
        s.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return true;
    }
    catch { return false; }
}

static bool CanBindTcp(string address, int port)
{
    try
    {
        var ip = IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Loopback;
        var listener = new TcpListener(ip, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch { return false; }
}

static void PrintUsage()
{
    Console.WriteLine("""
DreamPark LAN Monitor — read-only observer for LAN multiplayer sessions.

  --dev                  load config/dev.json (or the shipped example)
  --config <path>        load a specific config file
  --park <parkId>        auto-attach to this park, and follow it across host re-elections
  --attach host:port[:key]  skip discovery and join this host directly
                            (for LANs that drop UDP broadcast)
  --channel <prod|sdk>   only auto-attach to sessions on this discovery channel
  --panel-port <n>       web panel port (default 7781)
  --no-panel             headless: capture to JSONL only
  --no-capture           live panel only, nothing written to disk
  --capture-dir <path>   where JSONL captures land (default ./captures)
  --debug                echo every observed message to stdout
  --check                validate ports and config, then exit
  --help                 this text

This tool never hosts a relay and never broadcasts a beacon. It joins an
existing session as an ordinary peer that only listens.
""");
}
