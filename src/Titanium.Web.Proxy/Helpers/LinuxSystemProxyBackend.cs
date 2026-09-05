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
    private const string GnomeSystemProxySchema = "org.gnome.system.proxy";
    private const string GnomeSystemProxyHttpSchema = "org.gnome.system.proxy.http";
    private const string GnomeSystemProxyHttpsSchema = "org.gnome.system.proxy.https";
    private const string KdeProxyTypeKey = "ProxyType";
    private const string GsettingsCommand = "gsettings";
    private const string DbusSessionBusAddress = "DBUS_SESSION_BUS_ADDRESS";

    private static readonly string[] EnvKeys =
    [
        "http_proxy", "https_proxy", "no_proxy", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"
    ];

    private readonly IProcessRunner _runner;
    private readonly bool _applyBrowserLaunchHooks;
    private readonly Dictionary<string, string?> _originalEnv = new(StringComparer.Ordinal);
    private GnomeSnapshot? _gnome;
    private KdeSnapshot? _kde;
    private bool _hasSnapshot;
    private bool _disposed;
    private bool _dbusSanitized;
    private readonly EventHandler _processExitHandler;
    private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;

    public LinuxSystemProxyBackend(IProcessRunner? runner = null, bool applyBrowserLaunchHooks = true)
    {
        _runner = runner ?? new ProcessRunner();
        _applyBrowserLaunchHooks = applyBrowserLaunchHooks;
        _processExitHandler = (_, _) => RestoreOriginalSettings();
        _unhandledExceptionHandler = (_, _) => RestoreOriginalSettings();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
    }

    public void SetProxy(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        EnsureSnapshot();

        var gnome = HasGnome();
        var kde = HasKde();
        Exception? desktopError = null;

        // Apply every desktop path that exists. GNOME verify must not skip XFCE/KDE/Chrome hooks.
        if (gnome)
        {
            try
            {
                ApplyGnome(hostname, port, protocolType, proxyOverride);
            }
            catch (Exception ex)
            {
                desktopError = ex;
            }
        }

        if (kde)
        {
            try
            {
                ApplyKde(hostname, port, protocolType, proxyOverride);
                desktopError = null;
            }
            catch (Exception ex)
            {
                desktopError ??= ex;
            }
        }

        // Process env alone does not affect Chrome/Firefox already running in the desktop session.
        try
        {
            ApplyProcessEnvironment(hostname, port, protocolType, proxyOverride);
        }
        catch
        {
            // best-effort
        }

        // XFCE/i3/WSLg dock Chrome ignores GNOME gsettings; pin Chromium-family via policy + .desktop Exec.
        var hooksApplied = false;
        if (_applyBrowserLaunchHooks)
        {
            try
            {
                LinuxBrowserLaunchProxy.Apply(hostname, port);
                hooksApplied = true;
            }
            catch
            {
                // best-effort
            }
        }

        if (desktopError is not null && !hooksApplied)
            throw desktopError;

        if (!gnome && !kde && !hooksApplied)
        {
            throw new InvalidOperationException(
                "Linux system proxy requires GNOME gsettings, KDE kwriteconfig, or writable " +
                "Chrome/Chromium desktop/policy files; only this process's http(s)_proxy " +
                "environment was updated.");
        }
    }

    public void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
    {
        try
        {
            if (saveOriginalConfig) EnsureSnapshot();
        }
        catch
        {
            // continue disable even if snapshot fails
        }

        // Full disable is the practical Linux equivalent of removing http/https entries.
        try
        {
            if (HasGnome())
            {
                GsettingsSet(GnomeSystemProxySchema, "mode", "'none'");
                GsettingsSet(GnomeSystemProxyHttpSchema, "enabled", "false");
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            if (HasKde())
            {
                KdeWrite(KdeProxyTypeKey, "0");
                KdeReload();
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            ClearProcessProxyEnv();
        }
        catch
        {
            // best-effort
        }

        if (_applyBrowserLaunchHooks)
        {
            try
            {
                LinuxBrowserLaunchProxy.Clear();
            }
            catch
            {
                // best-effort
            }
        }
    }

    public void DisableAllProxy()
    {
        EnsureSnapshot();
        RemoveProxy(ProxyProtocolType.AllHttp, saveOriginalConfig: false);
    }

    public void RestoreOriginalSettings()
    {
        if (!_hasSnapshot) return;

        try
        {
            if (_gnome is not null && HasGnome())
            {
                GsettingsSet(GnomeSystemProxySchema, "mode", QuoteGsettings(_gnome.Mode));
                GsettingsSet(GnomeSystemProxyHttpSchema, "host", QuoteGsettings(_gnome.HttpHost));
                GsettingsSet(GnomeSystemProxyHttpSchema, "port", _gnome.HttpPort.ToString());
                GsettingsSet(GnomeSystemProxyHttpSchema, "enabled", _gnome.HttpEnabled ? "true" : "false");
                GsettingsSet(GnomeSystemProxyHttpsSchema, "host", QuoteGsettings(_gnome.HttpsHost));
                GsettingsSet(GnomeSystemProxyHttpsSchema, "port", _gnome.HttpsPort.ToString());
                GsettingsSet(GnomeSystemProxySchema, "ignore-hosts", _gnome.IgnoreHosts);
            }
        }
        catch
        {
            // process-exit restore must not throw
        }

        try
        {
            if (_kde is not null && HasKde())
            {
                KdeWrite(KdeProxyTypeKey, _kde.ProxyType);
                KdeWrite("httpProxy", _kde.HttpProxy);
                KdeWrite("httpsProxy", _kde.HttpsProxy);
                KdeWrite("NoProxyFor", _kde.NoProxyFor);
                KdeReload();
            }
        }
        catch
        {
            // process-exit restore must not throw
        }

        try
        {
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
        }
        catch
        {
            // process-exit restore must not throw
        }

        if (_applyBrowserLaunchHooks)
        {
            try
            {
                LinuxBrowserLaunchProxy.Clear();
            }
            catch
            {
                // process-exit restore must not throw
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
            EnsureUsableDbusSession();
            var result = _runner.Run(GsettingsCommand, $"get {GnomeSystemProxySchema} ignore-hosts",
                DbusEnvironmentOverride());
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
            stale |= GetGnomeStaleLocalProxyProtocols(ownedPorts);

        stale |= GetEnvStaleLocalProxyProtocols(ownedPorts, ["http_proxy", "HTTP_PROXY"], ProxyProtocolType.Http);
        stale |= GetEnvStaleLocalProxyProtocols(ownedPorts, ["https_proxy", "HTTPS_PROXY"], ProxyProtocolType.Https);

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
                GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? "none",
                GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? string.Empty,
                ParseInt(GsettingsGet(GnomeSystemProxyHttpSchema, "port")),
                ParseGsettingsBool(GsettingsGet(GnomeSystemProxyHttpSchema, "enabled")),
                GsettingsGet(GnomeSystemProxyHttpsSchema, "host")?.Trim('\'', '"') ?? string.Empty,
                ParseInt(GsettingsGet(GnomeSystemProxyHttpsSchema, "port")),
                GsettingsGet(GnomeSystemProxySchema, "ignore-hosts") ?? "[]");
        }

        if (HasKde())
        {
            _kde = new KdeSnapshot(
                KdeRead(KdeProxyTypeKey) ?? "0",
                KdeRead("httpProxy") ?? string.Empty,
                KdeRead("httpsProxy") ?? string.Empty,
                KdeRead("NoProxyFor") ?? string.Empty);
        }

        _hasSnapshot = true;
    }

    private void ApplyGnome(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        EnsureUsableDbusSession();

        GsettingsSet(GnomeSystemProxySchema, "mode", "'manual'");
        if ((protocolType & ProxyProtocolType.Http) != 0)
        {
            GsettingsSet(GnomeSystemProxyHttpSchema, "host", QuoteGsettings(hostname));
            GsettingsSet(GnomeSystemProxyHttpSchema, "port", port.ToString());
            // GIO/Chrome treat mode=manual with enabled=false as DIRECT (no sessions in Inspector).
            GsettingsSet(GnomeSystemProxyHttpSchema, "enabled", "true");
        }

        if ((protocolType & ProxyProtocolType.Https) != 0)
        {
            GsettingsSet(GnomeSystemProxyHttpsSchema, "host", QuoteGsettings(hostname));
            GsettingsSet(GnomeSystemProxyHttpsSchema, "port", port.ToString());
        }

        if (proxyOverride != null)
            GsettingsSet(GnomeSystemProxySchema, "ignore-hosts",
                UnixProxyBypassMapper.ToGsettingsArray(proxyOverride));

        // gsettings often exits 0 even when dconf cannot commit (e.g. DBUS_SESSION_BUS_ADDRESS=disabled:).
        // Verify so Inspector does not show System proxy on while Chrome still sees mode=none.
        VerifyGnomeApplied(hostname, port, protocolType);
    }

    private void VerifyGnomeApplied(string hostname, int port, ProxyProtocolType protocolType)
    {
        var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? string.Empty;
        if (!mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Failed to apply GNOME system proxy (gsettings mode is still " +
                $"'{mode}' — is a D-Bus session available?).");
        }

        if ((protocolType & ProxyProtocolType.Http) != 0)
        {
            var host = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? string.Empty;
            var appliedPort = ParseInt(GsettingsGet(GnomeSystemProxyHttpSchema, "port"));
            if (!host.Equals(hostname, StringComparison.OrdinalIgnoreCase) || appliedPort != port)
            {
                throw new InvalidOperationException(
                    $"Failed to apply GNOME HTTP proxy (got {host}:{appliedPort}, expected {hostname}:{port}).");
            }

            if (!ParseGsettingsBool(GsettingsGet(GnomeSystemProxyHttpSchema, "enabled")))
            {
                throw new InvalidOperationException(
                    "Failed to apply GNOME HTTP proxy (org.gnome.system.proxy.http enabled is still false; " +
                    "Chrome/GIO treat that as DIRECT and no sessions appear).");
            }
        }

        if ((protocolType & ProxyProtocolType.Https) != 0)
        {
            var host = GsettingsGet(GnomeSystemProxyHttpsSchema, "host")?.Trim('\'', '"') ?? string.Empty;
            var appliedPort = ParseInt(GsettingsGet(GnomeSystemProxyHttpsSchema, "port"));
            if (!host.Equals(hostname, StringComparison.OrdinalIgnoreCase) || appliedPort != port)
            {
                throw new InvalidOperationException(
                    $"Failed to apply GNOME HTTPS proxy (got {host}:{appliedPort}, expected {hostname}:{port}).");
            }
        }
    }

    /// <summary>
    ///     Clear poisoned session-bus addresses (e.g. Cursor/sandbox <c>disabled:</c>) so gsettings/dconf
    ///     can discover the real X11/user session bus that Chrome already uses.
    /// </summary>
    private void EnsureUsableDbusSession()
    {
        if (_dbusSanitized)
            return;

        var address = Environment.GetEnvironmentVariable(DbusSessionBusAddress);
        if (IsUnusableDbusAddress(address))
            Environment.SetEnvironmentVariable(DbusSessionBusAddress, null);

        _dbusSanitized = true;
    }

    internal static bool IsUnusableDbusAddress(string? address) =>
        string.IsNullOrWhiteSpace(address) ||
        address.StartsWith("disabled", StringComparison.OrdinalIgnoreCase);

    private void ApplyKde(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        KdeWrite(KdeProxyTypeKey, "1");
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

    private static void ApplyProcessEnvironment(string hostname, int port, ProxyProtocolType protocolType,
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

    private static void ClearProcessProxyEnv()
    {
        foreach (var key in EnvKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    private bool HasGnome()
    {
        EnsureUsableDbusSession();
        var which = _runner.Run("sh", "-c \"command -v gsettings\"", DbusEnvironmentOverride());
        if (which is not { Succeeded: true } || string.IsNullOrWhiteSpace(which.StandardOutput))
            return false;

        var schema = _runner.Run(GsettingsCommand, "list-schemas", DbusEnvironmentOverride());
        return schema is { Succeeded: true } &&
               schema.StandardOutput.Contains(GnomeSystemProxySchema, StringComparison.Ordinal);
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
        EnsureUsableDbusSession();
        var result = _runner.Run(GsettingsCommand, $"get {schema} {key}", DbusEnvironmentOverride());
        return result is { Succeeded: true } ? result.StandardOutput.Trim() : null;
    }

    private void GsettingsSet(string schema, string key, string value)
    {
        EnsureUsableDbusSession();
        _runner.Run(GsettingsCommand, $"set {schema} {key} {value}", DbusEnvironmentOverride());
    }

    /// <summary>
    ///     Ensure child gsettings processes do not inherit a poisoned bus address even if something
    ///     re-set <c>DBUS_SESSION_BUS_ADDRESS</c> after <see cref="EnsureUsableDbusSession"/>.
    /// </summary>
    private static IDictionary<string, string?>? DbusEnvironmentOverride()
    {
        var address = Environment.GetEnvironmentVariable(DbusSessionBusAddress);
        if (!IsUnusableDbusAddress(address))
            return null;

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DbusSessionBusAddress] = null
        };
    }

    private static string QuoteGsettings(string value) => $"'{value.Replace("'", @"'\''")}'";

    private static string QuoteShell(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static int ParseInt(string? text) =>
        int.TryParse(text?.Trim(), out var n) ? n : 0;

    private static bool ParseGsettingsBool(string? text)
    {
        var value = text?.Trim().Trim('\'', '"');
        return value is "true" or "True" or "1";
    }

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

    private ProxyProtocolType GetGnomeStaleLocalProxyProtocols(IReadOnlyCollection<int> ownedPorts)
    {
        var stale = ProxyProtocolType.None;
        var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? "none";
        if (!mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return stale;

        var httpHost = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? "";
        var httpPort = ParseInt(GsettingsGet(GnomeSystemProxyHttpSchema, "port"));
        if (UnixProxyBypassMapper.IsLocalHost(httpHost) && ownedPorts.Contains(httpPort))
            stale |= ProxyProtocolType.Http;

        var httpsHost = GsettingsGet(GnomeSystemProxyHttpsSchema, "host")?.Trim('\'', '"') ?? "";
        var httpsPort = ParseInt(GsettingsGet(GnomeSystemProxyHttpsSchema, "port"));
        if (UnixProxyBypassMapper.IsLocalHost(httpsHost) && ownedPorts.Contains(httpsPort))
            stale |= ProxyProtocolType.Https;

        return stale;
    }

    private static ProxyProtocolType GetEnvStaleLocalProxyProtocols(
        IReadOnlyCollection<int> ownedPorts, string[] keys, ProxyProtocolType protocol)
    {
        var stale = ProxyProtocolType.None;
        foreach (var key in keys)
        {
            if (TryParseProxyUri(Environment.GetEnvironmentVariable(key), out var host, out var port) &&
                UnixProxyBypassMapper.IsLocalHost(host) && ownedPorts.Contains(port))
                stale |= protocol;
        }

        return stale;
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
        string Mode, string HttpHost, int HttpPort, bool HttpEnabled, string HttpsHost, int HttpsPort, string IgnoreHosts);

    private sealed record KdeSnapshot(string ProxyType, string HttpProxy, string HttpsProxy, string NoProxyFor);
}
