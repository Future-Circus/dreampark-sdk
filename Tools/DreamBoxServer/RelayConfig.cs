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
    /// Resolve config file path from CLI args or well-known locations.
    /// Priority: --config arg > DREAM_PUB_CONFIG env > ./dream-pub.json > ./config/dev.json > /etc/dream-pub/dream-pub.json
    /// </summary>
    public static string? ResolveConfigPath(string[] args)
    {
        // Check --config CLI argument
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
                return args[i + 1];
        }

        // --dev flag: use the shipped dev config
        foreach (var a in args)
        {
            if (a == "--dev")
            {
                var devPath = Path.Combine(AppContext.BaseDirectory, "config", "dev.json");
                if (File.Exists(devPath)) return devPath;
                var devExamplePath = Path.Combine(AppContext.BaseDirectory, "config", "dev.example.json");
                if (File.Exists(devExamplePath)) return devExamplePath;
            }
        }

        // Check environment variable
        var envPath = Environment.GetEnvironmentVariable("DREAM_PUB_CONFIG");
        if (!string.IsNullOrEmpty(envPath))
            return envPath;

        // Check local directory
        var localPath = Path.Combine(AppContext.BaseDirectory, "dream-pub.json");
        if (File.Exists(localPath))
            return localPath;

        // Check shipped config folder
        var shippedDev = Path.Combine(AppContext.BaseDirectory, "config", "dev.json");
        if (File.Exists(shippedDev))
            return shippedDev;

        // Check /etc/dream-pub/ (Pi deployment)
        const string etcPath = "/etc/dream-pub/dream-pub.json";
        if (File.Exists(etcPath))
            return etcPath;

        return null;
    }
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
