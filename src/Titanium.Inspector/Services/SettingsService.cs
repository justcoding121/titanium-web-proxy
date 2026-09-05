using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Web.Proxy;

namespace Titanium.Inspector.Services;

public enum ThemeMode
{
    Automatic,
    Light,
    Dark,
}

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

    /// <summary>Release tag last applied via in-app update (e.g. 7.0.4-beta). Null when unknown / manual install.</summary>
    public string? InstalledReleaseTag { get; set; }

    /// <summary>Channel of <see cref="InstalledReleaseTag"/> (Stable or Beta). Null when unknown.</summary>
    public string? InstalledReleaseChannel { get; set; }

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

    /// <summary>Accept upstream TLS that fails normal validation (lab / self-signed hosts). Off by default.</summary>
    public bool IgnoreServerCertificateErrors { get; set; }

    /// <summary>Start listener when the main window opens.</summary>
    public bool AutoStartCapture { get; set; } = true;

    /// <summary>Enable system proxy after auto-start (or first start when requested).</summary>
    public bool AutoSystemProxyOnStart { get; set; } = true;

    /// <summary>When false, HTTPS stays opaque CONNECT tunnels (Fiddler-like default).</summary>
    public bool DecryptHttps { get; set; }

    /// <summary>App color theme: follow OS (Automatic), Light, or Dark.</summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Automatic;

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

    /// <summary>Host patterns that skip HTTPS decryption (tunnel only). Supports *.example.com.</summary>
    public List<string> DecryptSkipHosts { get; set; } = new();

    /// <summary>
    /// Legacy decrypt-only allowlist. Inspector no longer edits or applies this; kept for settings back-compat.
    /// </summary>
    public List<string> DecryptOnlyHosts { get; set; } = new();

    /// <summary>OS system-proxy bypass patterns when System proxy is on (Replace mode — full list).</summary>
    public List<string> SystemProxyBypassHosts { get; set; } = new();

    /// <summary>When true, localhost uses the proxy (WinINET &lt;-loopback&gt; / Unix NO_PROXY parity).</summary>
    public bool ProxyLoopback { get; set; } = true;

    /// <summary>
    /// When true, <see cref="SystemProxyBypassHosts"/> and <see cref="DecryptSkipHosts"/> were seeded
    /// from factory defaults (or saved by the user). When false, load applies factory seed once.
    /// </summary>
    public bool ExclusionsInitialized { get; set; }

    /// <summary>User acknowledged PAC replace warning when enabling System proxy.</summary>
    public bool WarnedAboutPacReplace { get; set; }
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

    /// <summary>
    /// Seeds OS-bypass and tunnel-only lists from <see cref="MitmExclusionDefaults"/> when not yet initialized.
    /// Persists when seeding changes settings.
    /// </summary>
    public bool EnsureExclusionsSeeded()
    {
        if (Current.ExclusionsInitialized)
        {
            return false;
        }

        ApplyFactoryExclusionDefaults(Current);
        Current.ExclusionsInitialized = true;
        Save();
        return true;
    }

    /// <summary>Restores factory OS-bypass and tunnel-only lists (and loopback).</summary>
    public void ResetExclusionsToFactoryDefaults()
    {
        ApplyFactoryExclusionDefaults(Current);
        Current.DecryptOnlyHosts = [];
        Current.ExclusionsInitialized = true;
        Save();
    }

    public static void ApplyFactoryExclusionDefaults(InspectorSettings settings)
    {
        settings.SystemProxyBypassHosts = MitmExclusionDefaults.SystemProxyBypassRules.ToList();
        settings.DecryptSkipHosts = MitmExclusionDefaults.TunnelOnlyPinningDomains.ToList();
        settings.ProxyLoopback = true;
    }

    /// <summary>
    ///     Adds any factory OS-bypass hosts missing from the saved list (does not remove user entries).
    ///     Returns true when the list changed.
    /// </summary>
    internal static bool MergeMissingFactoryOsBypassHosts(InspectorSettings settings)
    {
        settings.SystemProxyBypassHosts ??= [];
        var changed = false;
        foreach (var rule in MitmExclusionDefaults.SystemProxyBypassRules)
        {
            if (settings.SystemProxyBypassHosts.Any(h =>
                    string.Equals(h, rule, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            settings.SystemProxyBypassHosts.Add(rule);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Replace preferences with factory defaults and write settings.json.
    /// Does not touch the root CA, OS trust stores, or captured sessions / disk body cache.
    /// </summary>
    public void ResetToFactoryDefaults()
    {
        Current = new InspectorSettings();
        ApplyFactoryExclusionDefaults(Current);
        Current.ExclusionsInitialized = true;
        Save();
    }

    private InspectorSettings LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            var fresh = new InspectorSettings();
            ApplyFactoryExclusionDefaults(fresh);
            fresh.ExclusionsInitialized = true;
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<InspectorSettings>(json, JsonOptions)
                         ?? new InspectorSettings();
            if (!loaded.ExclusionsInitialized)
            {
                // Migrate: empty lists previously relied on silent Merge of factory defaults.
                if (loaded.SystemProxyBypassHosts.Count == 0 && loaded.DecryptSkipHosts.Count == 0)
                {
                    ApplyFactoryExclusionDefaults(loaded);
                }

                loaded.ExclusionsInitialized = true;
                try
                {
                    File.WriteAllText(_path, JsonSerializer.Serialize(loaded, JsonOptions));
                }
                catch
                {
                    // best effort
                }
            }
            else if (MergeMissingFactoryOsBypassHosts(loaded))
            {
                // Additive: new factory forge/SSO hosts (e.g. github.com) must land in existing
                // settings.json or Inspector Replace-mode system proxy keeps MITM'ing git HTTPS.
                try
                {
                    File.WriteAllText(_path, JsonSerializer.Serialize(loaded, JsonOptions));
                }
                catch
                {
                    // best effort
                }
            }

            return loaded;
        }
        catch
        {
            var fallback = new InspectorSettings();
            ApplyFactoryExclusionDefaults(fallback);
            fallback.ExclusionsInitialized = true;
            return fallback;
        }
    }
}
