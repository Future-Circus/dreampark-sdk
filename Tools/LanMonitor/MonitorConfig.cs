using System.Text.Json;

namespace LanMonitor;

/// <summary>
/// Monitor configuration. Mirrors the shape and loader conventions of
/// <c>Tools/DreamBoxServer/RelayConfig.cs</c> so the two tools feel the same,
/// but the surface is deliberately much smaller: the monitor has nothing to
/// host, nothing to advertise and nothing to tune about fan-out.
///
/// Note what is NOT here, and cannot be added by config: a relay port, a
/// connection-key-to-serve, a beacon interval. See <see cref="BeaconWatcher"/>.
/// </summary>
public sealed class MonitorConfig
{
    /// <summary>UDP port to listen for dream-pub beacons on. Must match the LAN (7700).</summary>
    public int DiscoveryPort { get; set; } = 7700;

    /// <summary>
    /// Auto-attach to the first live session whose parkId matches this, and
    /// follow that parkId across host re-elections. Empty = no auto-attach;
    /// pick a session by hand in the panel.
    /// </summary>
    public string ParkId { get; set; } = "";

    /// <summary>
    /// Only auto-attach to sessions on this channel ("prod" = core builds,
    /// "sdk" = creator projects). Empty = any channel. Kiosk beacons carry no
    /// channel and are always eligible.
    /// </summary>
    public string Channel { get; set; } = "";

    /// <summary>A session with no beacon for this long is shown as stale and stops being an auto-attach candidate.</summary>
    public int SessionStaleSeconds { get; set; } = 6;

    /// <summary>Ring-buffer depth for the live panel feed. The JSONL capture is unbounded.</summary>
    public int MaxEvents { get; set; } = 2000;

    /// <summary>Event-loop poll cadence, milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 15;

    /// <summary>Echo every observed message to stdout (and so to the Unity console).</summary>
    public bool Debug { get; set; } = false;

    /// <summary>
    /// "host:port[:key]" — skip discovery and attach straight to this host on
    /// startup. Set with --attach. For LANs that drop UDP broadcast, where a
    /// beacon will never arrive no matter how long you wait.
    /// </summary>
    public string DirectAttach { get; set; } = "";

    public CaptureConfig Capture { get; set; } = new();
    public WebPanelConfig WebPanel { get; set; } = new();

    // ── Loading ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_readerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public sealed record Resolution(string? Path, string ResolvedVia);

    /// <summary>
    /// Resolution order, matching the relay's:
    ///   1. --config &lt;path&gt;
    ///   2. --dev            → config/dev.json, else config/dev.example.json
    ///   3. DREAM_MONITOR_CONFIG env var
    ///   4. ./lan-monitor.json next to the binary
    ///   5. ./config/dev.json next to the binary
    /// Missing config is not fatal here — the defaults above are already the
    /// dev defaults, so the monitor runs with no config file at all.
    /// </summary>
    public static Resolution ResolveConfig(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config") return new Resolution(args[i + 1], "--config argument");
        }

        var baseDir = AppContext.BaseDirectory;

        if (args.Contains("--dev"))
        {
            var dev = Path.Combine(baseDir, "config", "dev.json");
            if (File.Exists(dev)) return new Resolution(dev, "--dev flag");
            var example = Path.Combine(baseDir, "config", "dev.example.json");
            if (File.Exists(example)) return new Resolution(example, "--dev flag (example fallback)");
        }

        var env = Environment.GetEnvironmentVariable("DREAM_MONITOR_CONFIG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return new Resolution(env, "DREAM_MONITOR_CONFIG env var");

        var local = Path.Combine(baseDir, "lan-monitor.json");
        if (File.Exists(local)) return new Resolution(local, "lan-monitor.json next to binary");

        var localDev = Path.Combine(baseDir, "config", "dev.json");
        if (File.Exists(localDev)) return new Resolution(localDev, "config/dev.json next to binary");

        return new Resolution(null, "built-in defaults");
    }

    public static MonitorConfig Load(string? path)
    {
        var config = new MonitorConfig();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<MonitorConfig>(json, s_readerOptions) ?? new MonitorConfig();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[config] failed to read {path}: {ex.Message} — using defaults.");
                config = new MonitorConfig();
            }
        }

        config.Clamp();
        return config;
    }

    /// <summary>Apply CLI overrides on top of the loaded file.</summary>
    public void ApplyArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--park" when i + 1 < args.Length:
                    ParkId = args[++i];
                    break;
                case "--attach" when i + 1 < args.Length:
                    DirectAttach = args[++i];
                    break;
                case "--channel" when i + 1 < args.Length:
                    Channel = args[++i];
                    break;
                case "--panel-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pp):
                    WebPanel.Port = pp; i++;
                    break;
                case "--no-panel":
                    WebPanel.Enabled = false;
                    break;
                case "--no-capture":
                    Capture.Enabled = false;
                    break;
                case "--capture-dir" when i + 1 < args.Length:
                    Capture.Directory = args[++i];
                    break;
                case "--debug":
                    Debug = true;
                    break;
            }
        }

        Clamp();
    }

    private void Clamp()
    {
        if (DiscoveryPort <= 0 || DiscoveryPort > 65535) DiscoveryPort = 7700;
        if (SessionStaleSeconds < 2) SessionStaleSeconds = 2;
        if (MaxEvents < 100) MaxEvents = 100;
        if (MaxEvents > 100_000) MaxEvents = 100_000;
        if (PollIntervalMs < 1) PollIntervalMs = 1;
        if (PollIntervalMs > 200) PollIntervalMs = 200;
        WebPanel.Clamp();
    }
}

public sealed class CaptureConfig
{
    /// <summary>Append every observed message to a timestamped .jsonl file.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where capture files land. Relative paths resolve against the binary's
    /// directory. Empty = "captures" next to the binary.
    /// </summary>
    public string Directory { get; set; } = "captures";
}

public sealed class WebPanelConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>7781 — one above the relay's 7780, so both panels can be open at once.</summary>
    public int Port { get; set; } = 7781;

    public string BindAddress { get; set; } = "127.0.0.1";

    public void Clamp()
    {
        if (Port <= 0 || Port > 65535) Port = 7781;
        if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "127.0.0.1";
    }
}
