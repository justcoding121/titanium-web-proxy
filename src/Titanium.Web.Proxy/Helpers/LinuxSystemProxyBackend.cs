using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Linux system proxy: GNOME gsettings + KDE kwriteconfig + this process's http(s)_proxy
///     + browser launch hooks. Session-wide environment.d and systemd --user proxy variables
///     are not written; they survive logout and point the next boot at a dead port.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemProxyBackend : ISystemProxyBackend
{
    private const string GnomeSystemProxySchema = "org.gnome.system.proxy";
    private const string GnomeSystemProxyHttpSchema = "org.gnome.system.proxy.http";
    private const string GnomeSystemProxyHttpsSchema = "org.gnome.system.proxy.https";
    private const string KdeProxyTypeKey = "ProxyType";
    private const string GsettingsCommand = "gsettings";
    private const string GsettingsEnabledKey = "enabled";
    private const string DbusSessionBusAddress = "DBUS_SESSION_BUS_ADDRESS";
    private const string SessionEnvDropInFileName = "90-titanium-inspector-proxy.conf";
    private const string ConfigDirName = ".config";
    private const string InspectorConfigDirName = "TitaniumInspector";
    private const string ProxySnapshotFileName = "proxy-snapshot.json";

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
    private Timer? _externalWatch;
    private string? _watchHost;
    private int _watchPort;
    private int _watchBusy;
    private readonly EventHandler _processExitHandler;
    private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;
    private readonly List<PosixSignalRegistration> _posixSignals = new();

    public LinuxSystemProxyBackend(IProcessRunner? runner = null, bool applyBrowserLaunchHooks = true)
    {
        _runner = runner ?? new ProcessRunner();
        _applyBrowserLaunchHooks = applyBrowserLaunchHooks;
        _processExitHandler = (_, _) => RestoreOriginalSettings();
        _unhandledExceptionHandler = (_, _) => RestoreOriginalSettings();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
        // SIGTERM/SIGINT skip Avalonia Exit; still must restore gsettings and drop Chrome proxy flags.
        try
        {
            _posixSignals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
            {
                RestoreOriginalSettings();
            }));
            _posixSignals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, _ =>
            {
                RestoreOriginalSettings();
            }));
        }
        catch
        {
            // older runtimes / non-POSIX
        }
    }

    public void SetProxy(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        EnsureSnapshot();

        var gnome = HasGnome();
        var kde = HasKde();

        // Write Chromium launch hooks before gsettings flips. Callers treat "proxy on"
        // as the signal to start a browser, and a stale desktop file still has the
        // bypass token that makes Chromium ignore --proxy-server.
        TryPrepareBrowserLaunchHooks(hostname, port, proxyOverride);

        var desktopError = ApplyDesktopProxies(gnome, kde, hostname, port, protocolType, proxyOverride);
        TryApplyProcessEnvironment(hostname, port, protocolType, proxyOverride);
        var sessionEnvApplied = TryApplySessionEnvironment(hostname, port, protocolType, proxyOverride);
        var hooksApplied = TryCommitBrowserLaunchHooks(hostname, port, proxyOverride);

        if (desktopError is not null && !hooksApplied)
            throw desktopError;

        if (!gnome && !kde && !hooksApplied && !sessionEnvApplied)
        {
            throw new InvalidOperationException(
                "Linux system proxy requires GNOME gsettings, KDE kwriteconfig, or writable " +
                "Chrome/Chromium desktop/policy files; only this process's http(s)_proxy " +
                "environment was updated.");
        }

        if (gnome || kde || hooksApplied)
            StartExternalProxyWatch(hostname, port);
    }

    private void TryPrepareBrowserLaunchHooks(string hostname, int port, string? proxyOverride)
    {
        if (!_applyBrowserLaunchHooks)
            return;

        try
        {
            LinuxBrowserLaunchProxy.Apply(hostname, port, proxyOverride, relaunch: false);
        }
        catch
        {
            // best-effort; the later call reports whether any hook stuck
        }
    }

    private Exception? ApplyDesktopProxies(
        bool gnome, bool kde, string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
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

        return desktopError;
    }

    private static void TryApplyProcessEnvironment(
        string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        // Process env alone does not affect Chrome/Firefox already running in the desktop session.
        try
        {
            ApplyProcessEnvironment(hostname, port, protocolType, proxyOverride);
        }
        catch
        {
            // best-effort
        }
    }

    private bool TryApplySessionEnvironment(
        string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        // Do not publish http_proxy into the user session. environment.d is loaded at
        // the next login, before Inspector starts, and systemd --user keeps the same
        // variables for every new process. GNOME/KDE plus browser hooks are the proxy.
        _ = hostname;
        _ = port;
        _ = protocolType;
        _ = proxyOverride;
        try
        {
            ClearUserSessionProxyEnvironment();
        }
        catch
        {
            // best-effort removal of a file written by an older build
        }

        return false;
    }

    private bool TryCommitBrowserLaunchHooks(string hostname, int port, string? proxyOverride)
    {
        // XFCE/i3/WSLg/RDP dock Chrome ignores GNOME gsettings; pin Chromium-family via policy + .desktop Exec.
        if (!_applyBrowserLaunchHooks)
            return false;

        try
        {
            return LinuxBrowserLaunchProxy.Apply(hostname, port, proxyOverride);
        }
        catch
        {
            return false;
        }
    }

    public void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
    {
        StopExternalProxyWatch();
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
                GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, "false");
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

        try
        {
            ClearUserSessionProxyEnvironment();
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

    /// <summary>
    ///     Drop GNOME/KDE/session proxy and browser launch hooks without a snapshot.
    ///     Package removal calls this after the Inspector process is already gone, so
    ///     <see cref="RestoreOriginalSettings"/> cannot run.
    /// </summary>
    public void ForceClearInstalledProxy()
    {
        StopExternalProxyWatch();
        // A crash leaves the pre-proxy values on disk. Put those back when the desktop
        // is still aimed at this machine; otherwise only the extra browser hooks go.
        var restored = false;
        try
        {
            if (TryLoadDiskSnapshot() && GnomeOrKdePointsAtLoopback())
            {
                ApplyLoadedSnapshot();
                restored = true;
            }
        }
        catch
        {
            // fall through to the loopback-only clear
        }

        _hasSnapshot = false;
        _gnome = null;
        _kde = null;
        DeleteDiskSnapshot();

        // Only flip GNOME/KDE off when they still point at this machine. A power loss
        // leaves that loopback entry behind; a proxy the user set to some other host
        // must stay.
        try
        {
            if (!restored && HasGnome())
            {
                var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? "none";
                var host = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? string.Empty;
                if (mode.Equals("manual", StringComparison.OrdinalIgnoreCase) && IsLoopbackHost(host))
                {
                    GsettingsSet(GnomeSystemProxySchema, "mode", "'none'");
                    GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, "false");
                }
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            if (!restored && HasKde())
            {
                var httpProxy = KdeRead("httpProxy") ?? string.Empty;
                if (httpProxy.Contains("127.0.0.1", StringComparison.Ordinal) ||
                    httpProxy.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    KdeWrite(KdeProxyTypeKey, "0");
                    KdeReload();
                }
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
                // Strip hooks only. Do not relaunch browsers or start fail-open — this path
                // runs after Inspector is already gone (login hook / package remove / clear binary).
                LinuxBrowserLaunchProxy.ClearInstalledHooks();
            }
            catch
            {
                // best-effort
            }
        }
        else
        {
            try
            {
                LinuxProxyFailOpen.Stop();
            }
            catch
            {
                // best-effort
            }
        }

        try
        {
            // Blank, do not unset. Unset reveals the login-time value an older
            // build stored as http://127.0.0.1:8866.
            ClearUserSessionProxyEnvironment();
        }
        catch
        {
            // best-effort
        }
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "";

    public void RestoreOriginalSettings() // NOSONAR S3776 -- Snapshot restore must apply gsettings/env together.
    {
        StopExternalProxyWatch();
        // Disk snapshot is for a later clear after this process is gone. Loading it
        // here would let a second window, which never turned the proxy on, put the
        // desktop back and delete the file out from under the instance that is listening.
        if (!_hasSnapshot) return;

        try
        {
            if (_gnome is not null && HasGnome())
            {
                GsettingsSet(GnomeSystemProxySchema, "mode", QuoteGsettings(_gnome.Mode));
                GsettingsSet(GnomeSystemProxyHttpSchema, "host", QuoteGsettings(_gnome.HttpHost));
                GsettingsSet(GnomeSystemProxyHttpSchema, "port", _gnome.HttpPort.ToString());
                GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, _gnome.HttpEnabled ? "true" : "false");
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

        try
        {
            ClearUserSessionProxyEnvironment();
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
        DeleteDiskSnapshot();
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

    public ProxyProtocolType GetOrphanedLocalProxyProtocols(Func<string, int, bool> isListening)
    {
        ArgumentNullException.ThrowIfNull(isListening);
        var orphaned = ProxyProtocolType.None;

        if (HasGnome())
            orphaned |= GetGnomeOrphanedLocalProxyProtocols(isListening);

        orphaned |= GetEnvOrphanedLocalProxyProtocols(
            isListening, ["http_proxy", "HTTP_PROXY"], ProxyProtocolType.Http);
        orphaned |= GetEnvOrphanedLocalProxyProtocols(
            isListening, ["https_proxy", "HTTPS_PROXY"], ProxyProtocolType.Https);

        return orphaned;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopExternalProxyWatch();
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;
        foreach (var reg in _posixSignals)
        {
            try { reg.Dispose(); } catch { /* ignore */ }
        }

        _posixSignals.Clear();
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
                ParseGsettingsBool(GsettingsGet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey)),
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

        // A leftover 127.0.0.1 entry is this proxy, not the user's previous setting.
        // Persisting it would put the dead port back after a crash.
        if (_gnome is not null &&
            _gnome.Mode.Equals("manual", StringComparison.OrdinalIgnoreCase) &&
            IsLoopbackHost(_gnome.HttpHost))
        {
            _gnome = _gnome with { Mode = "none", HttpEnabled = false };
        }

        if (_kde is not null &&
            (_kde.HttpProxy.Contains("127.0.0.1", StringComparison.Ordinal) ||
             _kde.HttpProxy.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            _kde = _kde with { ProxyType = "0" };
        }

        _hasSnapshot = true;
        PersistSnapshot();
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
            GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, "true");
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

            if (!ParseGsettingsBool(GsettingsGet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey)))
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
    ///     Clear poisoned session-bus addresses (e.g. Cursor/sandbox <c>disabled:</c>) and adopt the
    ///     graphical login bus (XFCE/GNOME/xrdp) so gsettings/dconf match what the desktop uses.
    /// </summary>
    private void EnsureUsableDbusSession()
    {
        if (_dbusSanitized)
            return;

        var address = Environment.GetEnvironmentVariable(DbusSessionBusAddress);
        if (IsUnusableDbusAddress(address))
        {
            Environment.SetEnvironmentVariable(DbusSessionBusAddress, null);
            var discovered = LinuxGraphicalSession.TryGetDbusSessionAddress();
            if (!IsUnusableDbusAddress(discovered))
                Environment.SetEnvironmentVariable(DbusSessionBusAddress, discovered);
        }

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(display))
        {
            var sessionDisplay = LinuxGraphicalSession.TryGetDisplay();
            if (!string.IsNullOrWhiteSpace(sessionDisplay))
                Environment.SetEnvironmentVariable("DISPLAY", sessionDisplay);
        }

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

    private void ClearUserSessionProxyEnvironment()
    {
        // An older build published these into the user manager's base environment.
        // unset-environment removes only an override and puts http://127.0.0.1:8866
        // back. An empty override hides that value until the next login, and
        // deleting environment.d keeps that login from loading it again.
        _runner.Run("systemctl",
            "--user set-environment http_proxy= https_proxy= HTTP_PROXY= HTTPS_PROXY= no_proxy= NO_PROXY=");
        TryDeleteSessionEnvDropIn();
    }

    private static void TryDeleteSessionEnvDropIn()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                return;
            File.Delete(Path.Combine(home, ConfigDirName, "environment.d", SessionEnvDropInFileName));
        }
        catch
        {
            // best-effort
        }
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
    private static Dictionary<string, string?>? DbusEnvironmentOverride()
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

    private ProxyProtocolType GetGnomeOrphanedLocalProxyProtocols(Func<string, int, bool> isListening)
    {
        var orphaned = ProxyProtocolType.None;
        var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? "none";
        if (!mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return orphaned;

        var httpHost = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? "";
        var httpPort = ParseInt(GsettingsGet(GnomeSystemProxyHttpSchema, "port"));
        if (httpPort > 0 && UnixProxyBypassMapper.IsLocalHost(httpHost) && !isListening(httpHost, httpPort))
            orphaned |= ProxyProtocolType.Http;

        var httpsHost = GsettingsGet(GnomeSystemProxyHttpsSchema, "host")?.Trim('\'', '"') ?? "";
        var httpsPort = ParseInt(GsettingsGet(GnomeSystemProxyHttpsSchema, "port"));
        if (httpsPort > 0 && UnixProxyBypassMapper.IsLocalHost(httpsHost) && !isListening(httpsHost, httpsPort))
            orphaned |= ProxyProtocolType.Https;

        return orphaned;
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

    private static ProxyProtocolType GetEnvOrphanedLocalProxyProtocols(
        Func<string, int, bool> isListening, string[] keys, ProxyProtocolType protocol)
    {
        var orphaned = ProxyProtocolType.None;
        foreach (var key in keys)
        {
            if (TryParseProxyUri(Environment.GetEnvironmentVariable(key), out var host, out var port) &&
                port > 0 &&
                UnixProxyBypassMapper.IsLocalHost(host) &&
                !isListening(host, port))
                orphaned |= protocol;
        }

        return orphaned;
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

    private void StartExternalProxyWatch(string hostname, int port)
    {
        // Unit tests inject a fake runner. A timer there would touch the real desktop.
        if (_runner is not ProcessRunner)
            return;

        _watchHost = hostname;
        _watchPort = port;
        _externalWatch?.Dispose();
        _externalWatch = new Timer(
            _ => OnExternalProxyWatch(),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
    }

    private void StopExternalProxyWatch()
    {
        var timer = _externalWatch;
        _externalWatch = null;
        if (timer is null)
            return;
        try
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch
        {
            // already disposed
        }

        try
        {
            timer.Dispose();
        }
        catch
        {
            // already disposed
        }
    }

    private void OnExternalProxyWatch()
    {
        if (Interlocked.Exchange(ref _watchBusy, 1) == 1)
            return;
        try
        {
            if (_disposed || _externalWatch is null)
                return;
            if (DesktopStillPointsAtInspector())
                return;

            StopExternalProxyWatch();
            ClearHooksAfterExternalDisable();
        }
        catch
        {
            // a failed gsettings read must not drop the hooks
        }
        finally
        {
            Interlocked.Exchange(ref _watchBusy, 0);
        }
    }

    private bool DesktopStillPointsAtInspector()
    {
        if (string.IsNullOrWhiteSpace(_watchHost) || _watchPort <= 0)
            return true;

        if (HasGnome())
        {
            var modeRaw = GsettingsGet(GnomeSystemProxySchema, "mode");
            if (string.IsNullOrWhiteSpace(modeRaw))
                return true;
            var mode = modeRaw.Trim('\'', '"');
            var host = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? string.Empty;
            var port = ParseInt(GsettingsGet(GnomeSystemProxyHttpSchema, "port"));
            return mode.Equals("manual", StringComparison.OrdinalIgnoreCase)
                   && host.Equals(_watchHost, StringComparison.OrdinalIgnoreCase)
                   && port == _watchPort;
        }

        if (HasKde())
        {
            var type = KdeRead(KdeProxyTypeKey)?.Trim();
            if (type is null)
                return true;
            var http = KdeRead("httpProxy") ?? string.Empty;
            return type == "1"
                   && http.Contains(_watchHost, StringComparison.OrdinalIgnoreCase)
                   && http.Contains(_watchPort.ToString(), StringComparison.Ordinal);
        }

        return true;
    }

    private void ClearHooksAfterExternalDisable()
    {
        try
        {
            ClearUserSessionProxyEnvironment();
        }
        catch
        {
            // best-effort
        }

        if (!_applyBrowserLaunchHooks)
            return;

        try
        {
            LinuxBrowserLaunchProxy.Clear();
        }
        catch
        {
            // best-effort
        }
    }

    private void PersistSnapshot()
    {
        if (_runner is not ProcessRunner)
            return;
        try
        {
            var path = ProxySnapshotPath();
            if (path is null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dto = new DiskSnapshotFile { Gnome = _gnome, Kde = _kde };
            File.WriteAllText(path, JsonSerializer.Serialize(dto));
        }
        catch
        {
            // snapshot is best-effort; in-memory restore still runs on a clean exit
        }
    }

    private bool TryLoadDiskSnapshot()
    {
        if (_hasSnapshot)
            return true;
        try
        {
            var path = ProxySnapshotPath();
            if (path is null || !File.Exists(path))
                return false;
            var dto = JsonSerializer.Deserialize<DiskSnapshotFile>(File.ReadAllText(path));
            if (dto is null || (dto.Gnome is null && dto.Kde is null))
                return false;
            _gnome = dto.Gnome;
            _kde = dto.Kde;
            _hasSnapshot = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyLoadedSnapshot()
    {
        if (_gnome is not null && HasGnome())
        {
            GsettingsSet(GnomeSystemProxySchema, "mode", QuoteGsettings(_gnome.Mode));
            GsettingsSet(GnomeSystemProxyHttpSchema, "host", QuoteGsettings(_gnome.HttpHost));
            GsettingsSet(GnomeSystemProxyHttpSchema, "port", _gnome.HttpPort.ToString());
            GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, _gnome.HttpEnabled ? "true" : "false");
            GsettingsSet(GnomeSystemProxyHttpsSchema, "host", QuoteGsettings(_gnome.HttpsHost));
            GsettingsSet(GnomeSystemProxyHttpsSchema, "port", _gnome.HttpsPort.ToString());
            GsettingsSet(GnomeSystemProxySchema, "ignore-hosts", _gnome.IgnoreHosts);
        }

        if (_kde is not null && HasKde())
        {
            KdeWrite(KdeProxyTypeKey, _kde.ProxyType);
            KdeWrite("httpProxy", _kde.HttpProxy);
            KdeWrite("httpsProxy", _kde.HttpsProxy);
            KdeWrite("NoProxyFor", _kde.NoProxyFor);
            KdeReload();
        }
    }

    private bool GnomeOrKdePointsAtLoopback()
    {
        if (HasGnome())
        {
            var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? "none";
            var host = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? string.Empty;
            if (mode.Equals("manual", StringComparison.OrdinalIgnoreCase) && IsLoopbackHost(host))
                return true;
        }

        if (HasKde())
        {
            var httpProxy = KdeRead("httpProxy") ?? string.Empty;
            if (httpProxy.Contains("127.0.0.1", StringComparison.Ordinal) ||
                httpProxy.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void DeleteDiskSnapshot()
    {
        try
        {
            var path = ProxySnapshotPath();
            if (path is not null)
                File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }

    private static string? ProxySnapshotPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;
        return Path.Combine(home, ConfigDirName, InspectorConfigDirName, ProxySnapshotFileName);
    }

    private sealed class DiskSnapshotFile
    {
        public GnomeSnapshot? Gnome { get; set; }
        public KdeSnapshot? Kde { get; set; }
    }

    private sealed record GnomeSnapshot(
        string Mode, string HttpHost, int HttpPort, bool HttpEnabled, string HttpsHost, int HttpsPort, string IgnoreHosts);

    private sealed record KdeSnapshot(string ProxyType, string HttpProxy, string HttpsProxy, string NoProxyFor);
}
