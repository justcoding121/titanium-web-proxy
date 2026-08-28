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
    public string LoggingMinimumLevel { get; set; } = "Error";
    public bool LoggingEnableFile { get; set; }
    public string? LoggingFilePath { get; set; }

    public bool IgnoreServerCertificateErrors { get; set; }

    /// <summary>Start listener when the main window opens.</summary>
    public bool AutoStartCapture { get; set; } = true;

    /// <summary>Enable system proxy after auto-start (or first start when requested).</summary>
    public bool AutoSystemProxyOnStart { get; set; } = true;

    /// <summary>When false, HTTPS stays opaque CONNECT tunnels (Fiddler-like default).</summary>
    public bool DecryptHttps { get; set; }

    /// <summary>Show the session detail tab sidebar (headers, body, composer, …).</summary>
    public bool ShowSessionDetails { get; set; } = true;

    /// <summary>Allow HTTP/1.1 (CONNECT is always HTTP/1.1 regardless).</summary>
    public bool EnableHttp11 { get; set; } = true;

    /// <summary>Allow HTTP/2 (ALPN h2) on new connections.</summary>
    public bool EnableHttp2 { get; set; } = true;

    /// <summary>Allow HTTP/3 to origins when MsQuic is available.</summary>
    public bool EnableHttp3 { get; set; } = true;
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
            if (json.IndexOf("showSessionDetails", StringComparison.OrdinalIgnoreCase) < 0)
            {
                loaded.ShowSessionDetails = true;
            }

            // Older settings.json omitted these keys; default every protocol on.
            if (json.IndexOf("enableHttp11", StringComparison.OrdinalIgnoreCase) < 0)
            {
                loaded.EnableHttp11 = true;
            }

            if (json.IndexOf("enableHttp2", StringComparison.OrdinalIgnoreCase) < 0)
            {
                loaded.EnableHttp2 = true;
            }

            if (json.IndexOf("enableHttp3", StringComparison.OrdinalIgnoreCase) < 0)
            {
                loaded.EnableHttp3 = true;
            }

            return loaded;
        }
        catch
        {
            return new InspectorSettings();
        }
    }
}
