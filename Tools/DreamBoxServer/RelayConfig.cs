using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamBoxRelay;

public class RelayConfig
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 7777;

    [JsonPropertyName("connectionKey")]
    public string ConnectionKey { get; set; } = "dreambox";

    [JsonPropertyName("debug")]
    public bool Debug { get; set; } = true;

    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMs { get; set; } = 15;

    [JsonPropertyName("maxConnections")]
    public int MaxConnections { get; set; } = 32;

    [JsonPropertyName("discoveryPort")]
    public int DiscoveryPort { get; set; } = 7700;

    [JsonPropertyName("discoveryEnabled")]
    public bool DiscoveryEnabled { get; set; } = true;

    [JsonPropertyName("discoveryIntervalMs")]
    public int DiscoveryIntervalMs { get; set; } = 2000;

    [JsonPropertyName("dreamboxId")]
    public string DreamboxId { get; set; } = "dreambox-local";

    [JsonPropertyName("statusFilePath")]
    public string? StatusFilePath { get; set; }

    /// <summary>
    /// Optional web-based control panel. Off by default so production
    /// Pi deployments are unchanged. Developers enable this for local testing.
    /// </summary>
    [JsonPropertyName("webPanel")]
    public WebPanelConfig WebPanel { get; set; } = new();

    private static readonly JsonSerializerOptions s_options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Load config from a JSON file. Returns defaults if the file doesn't exist.
    /// </summary>
    public static RelayConfig Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Console.Error.WriteLine($"[config] FATAL: config file not found: {path ?? "(null)"}");
            Environment.Exit(1);
            return new RelayConfig(); // unreachable, keeps compiler happy
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<RelayConfig>(json, s_options) ?? new RelayConfig();
            Console.WriteLine($"[config] Loaded from {path} (maxConnections={config.MaxConnections}, port={config.Port})");
            return config;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] FATAL: error reading {path}: {ex.Message}");
            Environment.Exit(1);
            return new RelayConfig(); // unreachable
        }
    }

    /// <summary>
    /// Result of config path resolution — includes both the path and how it was found.
    /// </summary>
    public class ConfigResolution
    {
        public string? Path { get; set; }
        public string ResolvedVia { get; set; } = "not found";
    }

    /// <summary>
    /// Resolve config file path from CLI args or well-known locations.
    /// Priority: --config arg > --dev flag > DREAM_PUB_CONFIG env > ./dream-pub.json > ./config/dev.json > /etc/dream-pub/dream-pub.json
    /// </summary>
    public static ConfigResolution ResolveConfig(string[] args)
    {
        // Check --config CLI argument
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
            {
                Console.WriteLine($"[config] checking --config arg... found: {args[i + 1]}");
                return new ConfigResolution { Path = args[i + 1], ResolvedVia = "--config CLI argument" };
            }
        }
        Console.WriteLine("[config] checking --config arg... not provided");

        // --dev flag: use the shipped dev config
        foreach (var a in args)
        {
            if (a == "--dev")
            {
                var devPath = Path.Combine(AppContext.BaseDirectory, "config", "dev.json");
                if (File.Exists(devPath))
                {
                    Console.WriteLine($"[config] checking --dev flag... found {devPath}");
                    return new ConfigResolution { Path = devPath, ResolvedVia = "--dev flag → config/dev.json" };
                }
                Console.WriteLine($"[config] checking --dev flag... {devPath} not found");

                var devExamplePath = Path.Combine(AppContext.BaseDirectory, "config", "dev.example.json");
                if (File.Exists(devExamplePath))
                {
                    Console.WriteLine($"[config] checking --dev flag fallback... found {devExamplePath}");
                    return new ConfigResolution { Path = devExamplePath, ResolvedVia = "--dev flag → config/dev.example.json (fallback)" };
                }
                Console.WriteLine($"[config] checking --dev flag fallback... {devExamplePath} not found");
            }
        }
        if (!args.Contains("--dev"))
            Console.WriteLine("[config] checking --dev flag... not provided");

        // Check environment variable
        var envPath = Environment.GetEnvironmentVariable("DREAM_PUB_CONFIG");
        if (!string.IsNullOrEmpty(envPath))
        {
            Console.WriteLine($"[config] checking DREAM_PUB_CONFIG env... found: {envPath}");
            return new ConfigResolution { Path = envPath, ResolvedVia = "DREAM_PUB_CONFIG environment variable" };
        }
        Console.WriteLine("[config] checking DREAM_PUB_CONFIG env... not set");

        // Check local directory
        var localPath = Path.Combine(AppContext.BaseDirectory, "dream-pub.json");
        if (File.Exists(localPath))
        {
            Console.WriteLine($"[config] checking local dream-pub.json... found: {localPath}");
            return new ConfigResolution { Path = localPath, ResolvedVia = "local dream-pub.json" };
        }
        Console.WriteLine($"[config] checking local dream-pub.json... not found at {localPath}");

        // Check shipped config folder
        var shippedDev = Path.Combine(AppContext.BaseDirectory, "config", "dev.json");
        if (File.Exists(shippedDev))
        {
            Console.WriteLine($"[config] checking config/dev.json... found: {shippedDev}");
            return new ConfigResolution { Path = shippedDev, ResolvedVia = "shipped config/dev.json" };
        }
        Console.WriteLine($"[config] checking config/dev.json... not found at {shippedDev}");

        // Check /etc/dream-pub/ (Pi deployment)
        const string etcPath = "/etc/dream-pub/dream-pub.json";
        if (File.Exists(etcPath))
        {
            Console.WriteLine($"[config] checking /etc/dream-pub/... found: {etcPath}");
            return new ConfigResolution { Path = etcPath, ResolvedVia = "/etc/dream-pub/ (Pi deployment)" };
        }
        Console.WriteLine($"[config] checking /etc/dream-pub/... not found at {etcPath}");

        Console.Error.WriteLine("[config] no config file found in any of the checked locations");
        return new ConfigResolution();
    }

    /// <summary>
    /// Convenience wrapper that returns just the path string (backward compat).
    /// </summary>
    public static string? ResolveConfigPath(string[] args) => ResolveConfig(args).Path;
}

public class WebPanelConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 7780;

    /// <summary>
    /// Address the panel binds to. "127.0.0.1" keeps it dev-machine-only;
    /// "0.0.0.0" exposes it on the LAN. Never expose on public networks.
    /// </summary>
    [JsonPropertyName("bindAddress")]
    public string BindAddress { get; set; } = "127.0.0.1";
}
