using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Linux system proxy: GNOME gsettings + KDE kwriteconfig + process http(s)_proxy / no_proxy.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemProxyBackend : ISystemProxyBackend
{
    private static readonly string[] EnvKeys =
    [
        "http_proxy", "https_proxy", "no_proxy", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"
    ];

    private readonly IProcessRunner _runner;
    private readonly Dictionary<string, string?> _originalEnv = new(StringComparer.Ordinal);
    private GnomeSnapshot? _gnome;
    private KdeSnapshot? _kde;
    private bool _hasSnapshot;
    private bool _disposed;
    private readonly EventHandler _processExitHandler;
    private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;

    public LinuxSystemProxyBackend(IProcessRunner? runner = null)
    {
        _runner = runner ?? new ProcessRunner();
        _processExitHandler = (_, _) => RestoreOriginalSettings();
        _unhandledExceptionHandler = (_, _) => RestoreOriginalSettings();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
    }

    public void SetProxy(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        EnsureSnapshot();

        if (HasGnome())
            ApplyGnome(hostname, port, protocolType, proxyOverride);

        if (HasKde())
            ApplyKde(hostname, port, protocolType, proxyOverride);

        ApplyProcessEnvironment(hostname, port, protocolType, proxyOverride);
    }

    public void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
    {
        if (saveOriginalConfig) EnsureSnapshot();

        // Full disable is the practical Linux equivalent of removing http/https entries.
        if (HasGnome())
        {
            GsettingsSet("org.gnome.system.proxy", "mode", "'none'");
        }

        if (HasKde())
        {
            KdeWrite("ProxyType", "0");
            KdeReload();
        }

        ClearProcessProxyEnv();
    }

    public void DisableAllProxy()
    {
        EnsureSnapshot();
        RemoveProxy(ProxyProtocolType.AllHttp, saveOriginalConfig: false);
    }

    public void RestoreOriginalSettings()
    {
        if (!_hasSnapshot) return;

        if (_gnome is not null && HasGnome())
        {
            GsettingsSet("org.gnome.system.proxy", "mode", QuoteGsettings(_gnome.Mode));
            GsettingsSet("org.gnome.system.proxy.http", "host", QuoteGsettings(_gnome.HttpHost));
            GsettingsSet("org.gnome.system.proxy.http", "port", _gnome.HttpPort.ToString());
            GsettingsSet("org.gnome.system.proxy.https", "host", QuoteGsettings(_gnome.HttpsHost));
            GsettingsSet("org.gnome.system.proxy.https", "port", _gnome.HttpsPort.ToString());
            GsettingsSet("org.gnome.system.proxy", "ignore-hosts", _gnome.IgnoreHosts);
        }

        if (_kde is not null && HasKde())
        {
            KdeWrite("ProxyType", _kde.ProxyType);
            KdeWrite("httpProxy", _kde.HttpProxy);
            KdeWrite("httpsProxy", _kde.HttpsProxy);
            KdeWrite("NoProxyFor", _kde.NoProxyFor);
            KdeReload();
        }

        foreach (var key in EnvKeys)
        {
            if (_originalEnv.TryGetValue(key, out var value))
            {
                if (value is null)
                    Environment.SetEnvironmentVariable(key, null);
                else
                    Environment.SetEnvironmentVariable(key, value);
            }
        }

        _hasSnapshot = false;
        _gnome = null;
        _kde = null;
        _originalEnv.Clear();
    }

    public string? GetCurrentProxyOverride()
    {
        if (HasGnome())
        {
            var result = _runner.Run("gsettings", "get org.gnome.system.proxy ignore-hosts");
            if (result is { Succeeded: true })
                return ParseGsettingsArray(result.StandardOutput);
        }

        var noProxy = Environment.GetEnvironmentVariable("no_proxy")
                      ?? Environment.GetEnvironmentVariable("NO_PROXY");
        return string.IsNullOrWhiteSpace(noProxy) ? null : noProxy.Replace(',', ';');
    }

    public ProxyProtocolType GetStaleLocalProxyProtocols(IReadOnlyCollection<int> ownedPorts)
    {
        var stale = ProxyProtocolType.None;

        if (HasGnome())
        {
            var mode = GsettingsGet("org.gnome.system.proxy", "mode")?.Trim('\'', '"') ?? "none";
            if (mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
            {
                var httpHost = GsettingsGet("org.gnome.system.proxy.http", "host")?.Trim('\'', '"') ?? "";
                var httpPort = ParseInt(GsettingsGet("org.gnome.system.proxy.http", "port"));
                if (UnixProxyBypassMapper.IsLocalHost(httpHost) && ownedPorts.Contains(httpPort))
                    stale |= ProxyProtocolType.Http;

                var httpsHost = GsettingsGet("org.gnome.system.proxy.https", "host")?.Trim('\'', '"') ?? "";
                var httpsPort = ParseInt(GsettingsGet("org.gnome.system.proxy.https", "port"));
                if (UnixProxyBypassMapper.IsLocalHost(httpsHost) && ownedPorts.Contains(httpsPort))
                    stale |= ProxyProtocolType.Https;
            }
        }

        foreach (var key in new[] { "http_proxy", "HTTP_PROXY" })
        {
            if (TryParseProxyUri(Environment.GetEnvironmentVariable(key), out var host, out var port) &&
                UnixProxyBypassMapper.IsLocalHost(host) && ownedPorts.Contains(port))
                stale |= ProxyProtocolType.Http;
        }

        foreach (var key in new[] { "https_proxy", "HTTPS_PROXY" })
        {
            if (TryParseProxyUri(Environment.GetEnvironmentVariable(key), out var host, out var port) &&
                UnixProxyBypassMapper.IsLocalHost(host) && ownedPorts.Contains(port))
                stale |= ProxyProtocolType.Https;
        }

        return stale;
    }

    public void Dispose()
    {
        if (_disposed) return;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;
        _disposed = true;
    }

    private void EnsureSnapshot()
    {
        if (_hasSnapshot) return;

        foreach (var key in EnvKeys)
            _originalEnv[key] = Environment.GetEnvironmentVariable(key);

        if (HasGnome())
        {
            _gnome = new GnomeSnapshot(
                GsettingsGet("org.gnome.system.proxy", "mode")?.Trim('\'', '"') ?? "none",
                GsettingsGet("org.gnome.system.proxy.http", "host")?.Trim('\'', '"') ?? string.Empty,
                ParseInt(GsettingsGet("org.gnome.system.proxy.http", "port")),
                GsettingsGet("org.gnome.system.proxy.https", "host")?.Trim('\'', '"') ?? string.Empty,
                ParseInt(GsettingsGet("org.gnome.system.proxy.https", "port")),
                GsettingsGet("org.gnome.system.proxy", "ignore-hosts") ?? "[]");
        }

        if (HasKde())
        {
            _kde = new KdeSnapshot(
                KdeRead("ProxyType") ?? "0",
                KdeRead("httpProxy") ?? string.Empty,
                KdeRead("httpsProxy") ?? string.Empty,
                KdeRead("NoProxyFor") ?? string.Empty);
        }

        _hasSnapshot = true;
    }

    private void ApplyGnome(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        GsettingsSet("org.gnome.system.proxy", "mode", "'manual'");
        if ((protocolType & ProxyProtocolType.Http) != 0)
        {
            GsettingsSet("org.gnome.system.proxy.http", "host", QuoteGsettings(hostname));
            GsettingsSet("org.gnome.system.proxy.http", "port", port.ToString());
        }

        if ((protocolType & ProxyProtocolType.Https) != 0)
        {
            GsettingsSet("org.gnome.system.proxy.https", "host", QuoteGsettings(hostname));
            GsettingsSet("org.gnome.system.proxy.https", "port", port.ToString());
        }

        if (proxyOverride != null)
            GsettingsSet("org.gnome.system.proxy", "ignore-hosts",
                UnixProxyBypassMapper.ToGsettingsArray(proxyOverride));
    }

    private void ApplyKde(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        KdeWrite("ProxyType", "1");
        // Local forward proxies are plain HTTP endpoints; https:// is not valid for these settings.
        var url = $"http://{hostname}:{port}"; // NOSONAR S5332
        if ((protocolType & ProxyProtocolType.Http) != 0)
            KdeWrite("httpProxy", url);
        if ((protocolType & ProxyProtocolType.Https) != 0)
            KdeWrite("httpsProxy", url);
        if (proxyOverride != null)
            KdeWrite("NoProxyFor", UnixProxyBypassMapper.ToCommaSeparated(proxyOverride));
        KdeReload();
    }

    private void ApplyProcessEnvironment(string hostname, int port, ProxyProtocolType protocolType,
        string? proxyOverride)
    {
        // Process proxy env vars for a local listener always use the http scheme.
        var url = $"http://{hostname}:{port}"; // NOSONAR S5332
        if ((protocolType & ProxyProtocolType.Http) != 0)
        {
            Environment.SetEnvironmentVariable("http_proxy", url);
            Environment.SetEnvironmentVariable("HTTP_PROXY", url);
        }

        if ((protocolType & ProxyProtocolType.Https) != 0)
        {
            Environment.SetEnvironmentVariable("https_proxy", url);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", url);
        }

        var noProxy = UnixProxyBypassMapper.ToNoProxyEnv(proxyOverride);
        Environment.SetEnvironmentVariable("no_proxy", noProxy);
        Environment.SetEnvironmentVariable("NO_PROXY", noProxy);
    }

    private void ClearProcessProxyEnv()
    {
        foreach (var key in EnvKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    private bool HasGnome()
    {
        var which = _runner.Run("sh", "-c \"command -v gsettings\"");
        if (which is not { Succeeded: true } || string.IsNullOrWhiteSpace(which.StandardOutput))
            return false;

        var schema = _runner.Run("gsettings", "list-schemas");
        return schema is { Succeeded: true } &&
               schema.StandardOutput.Contains("org.gnome.system.proxy", StringComparison.Ordinal);
    }

    private bool HasKde()
    {
        var k6 = _runner.Run("sh", "-c \"command -v kwriteconfig6\"");
        if (k6 is { Succeeded: true } && !string.IsNullOrWhiteSpace(k6.StandardOutput))
            return true;
        var k5 = _runner.Run("sh", "-c \"command -v kwriteconfig5\"");
        return k5 is { Succeeded: true } && !string.IsNullOrWhiteSpace(k5.StandardOutput);
    }

    private string KdeTool() =>
        _runner.Run("sh", "-c \"command -v kwriteconfig6\"") is { Succeeded: true } r &&
        !string.IsNullOrWhiteSpace(r.StandardOutput)
            ? "kwriteconfig6"
            : "kwriteconfig5";

    private string KdeReadTool() =>
        KdeTool().Replace("kwriteconfig", "kreadconfig");

    private void KdeWrite(string key, string value) =>
        _runner.Run(KdeTool(), $"--file kioslaverc --group \"Proxy Settings\" --key {key} {QuoteShell(value)}");

    private string? KdeRead(string key)
    {
        var result = _runner.Run(KdeReadTool(), $"--file kioslaverc --group \"Proxy Settings\" --key {key}");
        return result is { Succeeded: true } ? result.StandardOutput.Trim() : null;
    }

    private void KdeReload()
    {
        // Best-effort: notify KIO that proxy settings changed.
        _runner.Run("sh",
            "-c \"qdbus org.kde.kioslave /KIO/Scheduler reparseSlaveConfiguration \\\"\\\" 2>/dev/null || " +
            "dbus-send --type=method_call --dest=org.kde.kioslave /KIO/Scheduler " +
            "org.kde.KIO.Scheduler.reparseSlaveConfiguration string: 2>/dev/null || true\"");
    }

    private string? GsettingsGet(string schema, string key)
    {
        var result = _runner.Run("gsettings", $"get {schema} {key}");
        return result is { Succeeded: true } ? result.StandardOutput.Trim() : null;
    }

    private void GsettingsSet(string schema, string key, string value) =>
        _runner.Run("gsettings", $"set {schema} {key} {value}");

    private static string QuoteGsettings(string value) => $"'{value.Replace("'", @"'\''")}'";

    private static string QuoteShell(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static int ParseInt(string? text) =>
        int.TryParse(text?.Trim(), out var n) ? n : 0;

    private static string ParseGsettingsArray(string output)
    {
        // e.g. ['localhost', '127.0.0.1']
        var inner = output.Trim();
        if (inner.StartsWith('[')) inner = inner.Trim('[', ']');
        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('\'', '"'))
            .Where(p => p.Length > 0);
        return string.Join(";", parts);
    }

    private static bool TryParseProxyUri(string? value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        host = uri.Host;
        port = uri.Port;
        return port > 0;
    }

    private sealed record GnomeSnapshot(
        string Mode, string HttpHost, int HttpPort, string HttpsHost, int HttpsPort, string IgnoreHosts);

    private sealed record KdeSnapshot(string ProxyType, string HttpProxy, string HttpsProxy, string NoProxyFor);
}
