using System.Text.Json;
using System.Text.Json.Serialization;

namespace Titanium.Inspector.Services;

public sealed class AutoResponderRuleDto
{
    public string MatchUrl { get; set; } = "*";
    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text/plain";
    public bool Enabled { get; set; } = true;
}

public sealed class InspectorSettings
{
    public bool CheckForUpdatesOnStartup { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string UpdateChannel { get; set; } = "Stable";

    public string BindAddress { get; set; } = "127.0.0.1";
    public int BindPort { get; set; } = 8866;

    public bool AutoResponderEnabled { get; set; }
    public List<AutoResponderRuleDto> AutoResponderRules { get; set; } = new();

    public bool BreakpointEnabled { get; set; }
    public string BreakpointUrlFilter { get; set; } = "*";
    public bool BreakpointOnResponse { get; set; }

    public string? ScriptOnRequest { get; set; }
    public string? ScriptOnResponse { get; set; }

    public bool LoggingEnabled { get; set; } = true;
#if DEBUG
    public string LoggingMinimumLevel { get; set; } = "Debug";
    public bool LoggingEnableFile { get; set; } = true;
#else
    public string LoggingMinimumLevel { get; set; } = "Error";
    public bool LoggingEnableFile { get; set; }
#endif
    public string? LoggingFilePath { get; set; }

    public bool IgnoreServerCertificateErrors { get; set; }

    /// <summary>Start listener when the main window opens.</summary>
    public bool AutoStartCapture { get; set; } = true;

    /// <summary>Enable system proxy after auto-start (or first start when requested).</summary>
    public bool AutoSystemProxyOnStart { get; set; } = true;

    /// <summary>When false, HTTPS stays opaque CONNECT tunnels (Fiddler-like default).</summary>
    public bool DecryptHttps { get; set; }

    /// <summary>Session grid column widths, order, and sort across launches.</summary>
    public SessionGridLayoutDto? SessionGridLayout { get; set; }

    /// <summary>Hard cap on sessions retained in the Inspector grid.</summary>
    public int MaxSessionsInMemory { get; set; } = 10_000;

    /// <summary>Soft budget for in-RAM body bytes+text across retained sessions.</summary>
    public long MaxCaptureBytesInMemory { get; set; } = 512L * 1024 * 1024;

    /// <summary>Newest N sessions keep bodies in RAM; older ones spill to disk.</summary>
    public int HotBodySessions { get; set; } = 2_000;

    /// <summary>When true, cold session bodies are written under LocalAppData session-cache.</summary>
    public bool SpillBodiesToDisk { get; set; } = true;

    /// <summary>Max size of the on-disk session body cache.</summary>
    public long DiskCacheMaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Delete spill files older than this many days on startup.</summary>
    public int DiskCacheMaxAgeDays { get; set; } = 7;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsService(string? path = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "settings.json");
        Current = LoadFromDisk();
    }

    public InspectorSettings Current { get; private set; }

    public static SettingsService Load() => new();

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
    }

    private InspectorSettings LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return new InspectorSettings();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<InspectorSettings>(json, JsonOptions)
                         ?? new InspectorSettings();
            return loaded;
        }
        catch
        {
            return new InspectorSettings();
        }
    }
}
