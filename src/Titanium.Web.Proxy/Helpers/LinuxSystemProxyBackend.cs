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
    private const string GsettingsDisabled = "false";
    private const string ManualProxyMode = "manual";
    private const string KdeHttpProxyKey = "httpProxy";
    private const string KdeHttpsProxyKey = "httpsProxy";
    private const string KdeNoProxyForKey = "NoProxyFor";
    private const string GnomeIgnoreHostsKey = "ignore-hosts";
    private const string LoopbackAddress = "127.0.0.1";
    private const string LocalhostName = "localhost";
    private const string HttpProxyEnv = "http_proxy";
    private const string HttpsProxyEnv = "https_proxy";
    private const string HttpProxyEnvUpper = "HTTP_PROXY";
    private const string HttpsProxyEnvUpper = "HTTPS_PROXY";
    private const string NoProxyEnv = "no_proxy";
    private const string NoProxyEnvUpper = "NO_PROXY";

    private static readonly string[] EnvKeys =
    [
        HttpProxyEnv, HttpsProxyEnv, NoProxyEnv, HttpProxyEnvUpper, HttpsProxyEnvUpper, NoProxyEnvUpper
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

        // A GNOME or KDE session follows the desktop proxy when policy and Preferences say
        // mode=system. A --proxy-server launcher flag would pin that process past File → Exit.
        // XFCE, i3, and RDP still need the flag: Chrome there ignores gsettings even when
        // the org.gnome.system.proxy schema package is installed.
        var pinProcess = ShouldPinBrowserProcess(_applyBrowserLaunchHooks, ReadSessionDesktop());
        TryPrepareBrowserLaunchHooks(hostname, port, proxyOverride, pinProcess);

        var desktopError = ApplyDesktopProxies(gnome, kde, hostname, port, protocolType, proxyOverride);
        TryApplyProcessEnvironment(hostname, port, protocolType, proxyOverride);
        var sessionEnvApplied = TryApplySessionEnvironment(hostname, port, protocolType, proxyOverride);
        var hooksApplied = TryCommitBrowserLaunchHooks(hostname, port, proxyOverride, pinProcess);
        if (desktopError is not null && !pinProcess && _applyBrowserLaunchHooks)
        {
            // Session desktop looked like GNOME/KDE but gsettings/kwrite failed (common under
            // xvfb / dbus-run-session). Pin Chromium so ProxyMode=system is not left as DIRECT.
            hooksApplied = TryCommitBrowserLaunchHooks(hostname, port, proxyOverride, pinProcess: true)
                           || hooksApplied;
        }

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

    /// <summary>
    ///     Process pins (<c>--proxy-server</c>) are for sessions that do not follow the
    ///     desktop proxy live. Schema packages alone are not enough: XFCE often has
    ///     <c>org.gnome.system.proxy</c> installed, and Chrome still ignores it there.
    ///     The hook flag itself is what unit tests turn off so
    ///     <see cref="LinuxBrowserLaunchProxy.Apply"/> is never called.
    /// </summary>
    internal static bool ShouldPinBrowserProcess(bool applyHooks, string? sessionDesktop) =>
        applyHooks && !SessionFollowsLiveProxy(sessionDesktop);

    /// <summary>
    ///     True when <paramref name="sessionDesktop"/> is a session whose desktop proxy
    ///     Chrome follows live (gsettings-based or KDE kioslaverc).
    ///     Matches Chromium's <c>proxy_config_service_linux.cc</c> switch:
    ///     Cinnamon, Deepin, GNOME, Pantheon, UKUI, Unity, COSMIC → gsettings path;
    ///     KDE variants → KDE kioslaverc path.
    ///     XFCE, LXQt, MATE, DDE, tiling compositors → OTHER, not watched → need pin.
    ///     <c>XDG_CURRENT_DESKTOP</c> may be a colon-separated list such as
    ///     <c>ubuntu:GNOME</c> or <c>Budgie:GNOME</c>.
    /// </summary>
    internal static bool SessionFollowsLiveProxy(string? sessionDesktop)
    {
        if (string.IsNullOrWhiteSpace(sessionDesktop))
            return false;

        foreach (var part in sessionDesktop.Split(':', ';', ' '))
        {
            if (part.Length == 0)
                continue;
            // gsettings watchers: matches DESKTOP_ENVIRONMENT_GNOME, _UNITY, _CINNAMON,
            // _DEEPIN, _PANTHEON, _UKUI, _COSMIC in Chromium xdg_util.h.
            // X-Cinnamon is used on some Linux Mint versions alongside Cinnamon.
            // "Deepin" only — DDE (older Deepin) falls through to OTHER in Chromium
            // and Chrome does not follow gsettings for it, so DDE stays pinned.
            if (IsLiveProxyDesktop(part))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Desktop names Chromium watches for a live system proxy (gsettings or KDE kioslaverc).
    /// </summary>
    private static bool IsLiveProxyDesktop(string part) =>
        IsGnomeFamilyDesktop(part) || IsKdeFamilyDesktop(part);

    /// <summary>
    ///     gsettings watchers: GNOME, Unity, Cinnamon, X-Cinnamon, Deepin, Pantheon, UKUI, COSMIC.
    ///     "Deepin" only — older DDE is not watched, so it stays pinned.
    /// </summary>
    private static bool IsGnomeFamilyDesktop(string part) =>
        part.Equals("GNOME", StringComparison.OrdinalIgnoreCase)
        || part.Equals("Unity", StringComparison.OrdinalIgnoreCase)
        || part.Equals("Cinnamon", StringComparison.OrdinalIgnoreCase)
        || part.Equals("X-Cinnamon", StringComparison.OrdinalIgnoreCase)
        || part.Equals("Deepin", StringComparison.OrdinalIgnoreCase)
        || part.Equals("Pantheon", StringComparison.OrdinalIgnoreCase)
        || part.Equals("UKUI", StringComparison.OrdinalIgnoreCase)
        || part.Equals("COSMIC", StringComparison.OrdinalIgnoreCase);

    /// <summary>KDE kioslaverc watchers: KDE3–6 and the plasma desktop name.</summary>
    private static bool IsKdeFamilyDesktop(string part) =>
        part.Equals("KDE", StringComparison.OrdinalIgnoreCase)
        || part.Equals("plasma", StringComparison.OrdinalIgnoreCase);

    private static string? ReadSessionDesktop()
    {
        var parts = new[]
        {
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"),
            Environment.GetEnvironmentVariable("DESKTOP_SESSION"),
        };
        var combined = string.Join(':', parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
        if (combined.Length > 0)
            return combined;

        // Inspector may be launched via a systemd user service or bare terminal that
        // does not inherit the graphical session variables. Fall back to reading the
        // environment of the session daemon process for the same user (best-effort).
        return TryReadSessionDesktopFromGraphicalProcess();
    }

    /// <summary>
    ///     Looks for a well-known graphical session daemon owned by the current user and
    ///     reads its <c>XDG_CURRENT_DESKTOP</c> from <c>/proc/[pid]/environ</c>.
    ///     Returns null on any failure — the caller keeps pin-by-default as the safe path.
    /// </summary>
    private static string? TryReadSessionDesktopFromGraphicalProcess()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            var uid = GetCurrentUid();
            // Ordered by prevalence. xfce4-session is last: its environ usually has no
            // XDG_CURRENT_DESKTOP, so it returns nothing and the caller still pins.
            string[] sessionDaemons = ["gnome-shell", "plasmashell", "cinnamon", "xfce4-session"];
            foreach (var daemon in sessionDaemons)
            {
                var desktop = TryReadDesktopFromProcess(daemon, uid);
                if (!string.IsNullOrWhiteSpace(desktop))
                    return desktop;
            }
        }
        catch
        {
            // best-effort — do not propagate
        }
        return null;
    }

    private static string? TryReadDesktopFromProcess(string processName, int uid)
    {
        const int MaxCandidates = 5;
        var candidates = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc", "*"))
            {
                if (!IsSessionDaemonComm(dir, processName))
                    continue;
                candidates++;
                if (candidates > MaxCandidates)
                    break;
                var desktop = ReadDesktopIfOwnedBy(dir, uid);
                if (!string.IsNullOrWhiteSpace(desktop))
                    return desktop;
            }
        }
        catch
        {
            // best-effort
        }

        return null;
    }

    /// <summary>
    ///     True when <paramref name="dir"/> is a /proc pid whose comm matches
    ///     <paramref name="processName"/>. comm is truncated to 15 characters;
    ///     a prefix match would also hit gnome-shell-calendar-server.
    /// </summary>
    private static bool IsSessionDaemonComm(string dir, string processName)
    {
        if (!int.TryParse(Path.GetFileName(dir), out _))
            return false;
        var commPath = Path.Combine(dir, "comm");
        if (!File.Exists(commPath))
            return false;
        var comm = File.ReadAllText(commPath).Trim();
        var expected = processName.Length > 15 ? processName[..15] : processName;
        return comm.Equals(expected, StringComparison.Ordinal);
    }

    /// <summary>
    ///     XDG_CURRENT_DESKTOP from this pid when it belongs to <paramref name="uid"/>.
    /// </summary>
    private static string? ReadDesktopIfOwnedBy(string dir, int uid)
    {
        var statusPath = Path.Combine(dir, "status");
        if (!File.Exists(statusPath))
            return null;
        var status = File.ReadAllText(statusPath);
        if (!StatusContainsUid(status, uid))
            return null;
        var environPath = Path.Combine(dir, "environ");
        if (!File.Exists(environPath))
            return null;
        return ParseDesktopFromEnviron(File.ReadAllBytes(environPath));
    }

    private static int GetCurrentUid()
    {
        // Read from /proc/self/status — works on all Linux versions.
        var status = File.ReadAllText("/proc/self/status");
        foreach (var line in status.Split('\n'))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                continue;
            var fields = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && int.TryParse(fields[1], out var uid))
                return uid;
        }
        return -1;
    }

    private static bool StatusContainsUid(string status, int uid)
    {
        foreach (var line in status.Split('\n'))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                continue;
            var fields = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            // Uid: real effective saved fs
            return fields.Length >= 2 && int.TryParse(fields[1], out var procUid) && procUid == uid;
        }
        return false;
    }

    private static string? ParseDesktopFromEnviron(byte[] raw)
    {
        const string key = "XDG_CURRENT_DESKTOP=";
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var span = raw.AsSpan();
        var start = 0;
        while (start < span.Length)
        {
            var nul = span[start..].IndexOf((byte)0);
            var end = nul < 0 ? span.Length : start + nul;
            var entry = span[start..end];
            if (entry.Length > keyBytes.Length && entry[..keyBytes.Length].SequenceEqual(keyBytes))
                return System.Text.Encoding.UTF8.GetString(entry[keyBytes.Length..]);
            start = end + 1;
        }
        return null;
    }

    private void TryPrepareBrowserLaunchHooks(string hostname, int port, string? proxyOverride, bool pinProcess)
    {
        if (!_applyBrowserLaunchHooks)
            return;

        try
        {
            LinuxBrowserLaunchProxy.Apply(hostname, port, proxyOverride, relaunch: false, pinProcess: pinProcess);
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

    private static bool TryApplySessionEnvironment(
        string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        // Do not publish http_proxy into the user session. environment.d is loaded at
        // the next login, before Inspector starts, and systemd --user keeps the same
        // variables for every new process. GNOME/KDE plus browser hooks are the proxy.
        // Only delete a leftover drop-in from an older build — do not call
        // ClearUserSessionProxyEnvironment here: that runs systemctl --user and can
        // burn the ProcessRunner timeout before gsettings runs on SetProxy.
        _ = hostname;
        _ = port;
        _ = protocolType;
        _ = proxyOverride;
        try
        {
            TryDeleteSessionEnvDropIn();
        }
        catch
        {
            // best-effort removal of a file written by an older build
        }

        return false;
    }

    private bool TryCommitBrowserLaunchHooks(string hostname, int port, string? proxyOverride, bool pinProcess)
    {
        // XFCE/i3/WSLg/RDP dock Chrome ignores GNOME gsettings; pin Chromium-family via policy + .desktop Exec.
        // GNOME/KDE pass pinProcess false: policy and Preferences stay mode=system, with no process flag.
        if (!_applyBrowserLaunchHooks)
            return false;

        try
        {
            return LinuxBrowserLaunchProxy.Apply(hostname, port, proxyOverride, pinProcess: pinProcess);
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
                GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, GsettingsDisabled);
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
        var restored = TryRestoreSnapshotWhenDesktopPointsAtLoopback();
        _hasSnapshot = false;
        _gnome = null;
        _kde = null;
        DeleteDiskSnapshot();

        // Only flip GNOME/KDE off when they still point at this machine. A power loss
        // leaves that loopback entry behind; a proxy the user set to some other host
        // must stay.
        if (!restored)
            TryClearLoopbackDesktopProxy();

        TryBestEffort(ClearProcessProxyEnv);
        TryClearHooksOrFailOpen();
        // Blank, do not unset. Unset reveals the login-time value an older
        // build stored as http://127.0.0.1:8866.
        TryBestEffort(ClearUserSessionProxyEnvironment);
    }

    private bool TryRestoreSnapshotWhenDesktopPointsAtLoopback()
    {
        try
        {
            if (TryLoadDiskSnapshot() && GnomeOrKdePointsAtLoopback())
            {
                ApplyLoadedSnapshot();
                return true;
            }
        }
        catch
        {
            // fall through to the loopback-only clear
        }

        return false;
    }

    private void TryClearLoopbackDesktopProxy()
    {
        TryBestEffort(ClearGnomeLoopbackManualProxy);
        TryBestEffort(ClearKdeLoopbackProxy);
    }

    private void ClearGnomeLoopbackManualProxy()
    {
        if (!HasGnome())
            return;

        var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? "none";
        var host = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? string.Empty;
        if (!mode.Equals(ManualProxyMode, StringComparison.OrdinalIgnoreCase) || !IsLoopbackHost(host))
            return;

        GsettingsSet(GnomeSystemProxySchema, "mode", "'none'");
        GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, GsettingsDisabled);
    }

    private void ClearKdeLoopbackProxy()
    {
        if (!HasKde())
            return;

        var httpProxy = KdeRead(KdeHttpProxyKey) ?? string.Empty;
        if (!KdeProxyPointsAtLoopback(httpProxy))
            return;

        KdeWrite(KdeProxyTypeKey, "0");
        KdeReload();
    }

    private void TryClearHooksOrFailOpen()
    {
        if (_applyBrowserLaunchHooks)
        {
            // Strip hooks only. Do not relaunch browsers or start fail-open — this path
            // runs after Inspector is already gone (login hook / package remove / clear binary).
            TryBestEffort(LinuxBrowserLaunchProxy.ClearInstalledHooks);
            return;
        }

        TryBestEffort(LinuxProxyFailOpen.Stop);
    }

    private static void TryBestEffort(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // best-effort
        }
    }

    private static bool KdeProxyPointsAtLoopback(string httpProxy) =>
        httpProxy.Contains(LoopbackAddress, StringComparison.Ordinal)
        || httpProxy.Contains(LocalhostName, StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackHost(string host) =>
        host is LoopbackAddress or LocalhostName or "::1" or "";

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
                GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, _gnome.HttpEnabled ? "true" : GsettingsDisabled);
                GsettingsSet(GnomeSystemProxyHttpsSchema, "host", QuoteGsettings(_gnome.HttpsHost));
                GsettingsSet(GnomeSystemProxyHttpsSchema, "port", _gnome.HttpsPort.ToString());
                GsettingsSet(GnomeSystemProxySchema, GnomeIgnoreHostsKey, _gnome.IgnoreHosts);
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
                KdeWrite(KdeHttpProxyKey, _kde.HttpProxy);
                KdeWrite(KdeHttpsProxyKey, _kde.HttpsProxy);
                KdeWrite(KdeNoProxyForKey, _kde.NoProxyFor);
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

        var noProxy = Environment.GetEnvironmentVariable(NoProxyEnv)
                      ?? Environment.GetEnvironmentVariable(NoProxyEnvUpper);
        return string.IsNullOrWhiteSpace(noProxy) ? null : noProxy.Replace(',', ';');
    }

    public ProxyProtocolType GetStaleLocalProxyProtocols(IReadOnlyCollection<int> ownedPorts)
    {
        var stale = ProxyProtocolType.None;

        if (HasGnome())
            stale |= GetGnomeStaleLocalProxyProtocols(ownedPorts);

        stale |= GetEnvStaleLocalProxyProtocols(ownedPorts, [HttpProxyEnv, HttpProxyEnvUpper], ProxyProtocolType.Http);
        stale |= GetEnvStaleLocalProxyProtocols(ownedPorts, [HttpsProxyEnv, HttpsProxyEnvUpper], ProxyProtocolType.Https);

        return stale;
    }

    public ProxyProtocolType GetOrphanedLocalProxyProtocols(Func<string, int, bool> isListening)
    {
        ArgumentNullException.ThrowIfNull(isListening);
        var orphaned = ProxyProtocolType.None;

        if (HasGnome())
            orphaned |= GetGnomeOrphanedLocalProxyProtocols(isListening);

        orphaned |= GetEnvOrphanedLocalProxyProtocols(
            isListening, [HttpProxyEnv, HttpProxyEnvUpper], ProxyProtocolType.Http);
        orphaned |= GetEnvOrphanedLocalProxyProtocols(
            isListening, [HttpsProxyEnv, HttpsProxyEnvUpper], ProxyProtocolType.Https);

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
                GsettingsGet(GnomeSystemProxySchema, GnomeIgnoreHostsKey) ?? "[]");
        }

        if (HasKde())
        {
            _kde = new KdeSnapshot(
                KdeRead(KdeProxyTypeKey) ?? "0",
                KdeRead(KdeHttpProxyKey) ?? string.Empty,
                KdeRead(KdeHttpsProxyKey) ?? string.Empty,
                KdeRead(KdeNoProxyForKey) ?? string.Empty);
        }

        // A leftover 127.0.0.1 entry is this proxy, not the user's previous setting.
        // Persisting it would put the dead port back after a crash.
        if (_gnome is not null &&
            _gnome.Mode.Equals(ManualProxyMode, StringComparison.OrdinalIgnoreCase) &&
            IsLoopbackHost(_gnome.HttpHost))
        {
            _gnome = _gnome with { Mode = "none", HttpEnabled = false };
        }

        if (_kde is not null &&
            (_kde.HttpProxy.Contains(LoopbackAddress, StringComparison.Ordinal) ||
             _kde.HttpProxy.Contains(LocalhostName, StringComparison.OrdinalIgnoreCase)))
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
            GsettingsSet(GnomeSystemProxySchema, GnomeIgnoreHostsKey,
                UnixProxyBypassMapper.ToGsettingsArray(proxyOverride));

        // gsettings often exits 0 even when dconf cannot commit (e.g. DBUS_SESSION_BUS_ADDRESS=disabled:).
        // Verify so Inspector does not show System proxy on while Chrome still sees mode=none.
        VerifyGnomeApplied(hostname, port, protocolType);
    }

    private void VerifyGnomeApplied(string hostname, int port, ProxyProtocolType protocolType)
    {
        var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? string.Empty;
        if (!mode.Equals(ManualProxyMode, StringComparison.OrdinalIgnoreCase))
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
            KdeWrite(KdeHttpProxyKey, url);
        if ((protocolType & ProxyProtocolType.Https) != 0)
            KdeWrite(KdeHttpsProxyKey, url);
        if (proxyOverride != null)
            KdeWrite(KdeNoProxyForKey, UnixProxyBypassMapper.ToCommaSeparated(proxyOverride));
        KdeReload();
    }

    private static void ApplyProcessEnvironment(string hostname, int port, ProxyProtocolType protocolType,
        string? proxyOverride)
    {
        // Process proxy env vars for a local listener always use the http scheme.
        var url = $"http://{hostname}:{port}"; // NOSONAR S5332
        if ((protocolType & ProxyProtocolType.Http) != 0)
        {
            Environment.SetEnvironmentVariable(HttpProxyEnv, url);
            Environment.SetEnvironmentVariable(HttpProxyEnvUpper, url);
        }

        if ((protocolType & ProxyProtocolType.Https) != 0)
        {
            Environment.SetEnvironmentVariable(HttpsProxyEnv, url);
            Environment.SetEnvironmentVariable(HttpsProxyEnvUpper, url);
        }

        var noProxy = UnixProxyBypassMapper.ToNoProxyEnv(proxyOverride);
        Environment.SetEnvironmentVariable(NoProxyEnv, noProxy);
        Environment.SetEnvironmentVariable(NoProxyEnvUpper, noProxy);
    }

    private void ClearUserSessionProxyEnvironment()
    {
        // An older build published these into the user manager's base environment.
        // unset-environment removes only an override and puts http://127.0.0.1:8866
        // back. Blank only keys whose current value is loopback so a corporate
        // http_proxy the user set themselves is left alone.
        var toBlank = CollectLoopbackSessionProxyKeys(
            _runner.Run("systemctl", "--user show-environment")?.StandardOutput);
        if (toBlank.Count > 0)
        {
            toBlank.Add(NoProxyEnv);
            toBlank.Add(NoProxyEnvUpper);
            var args = "--user set-environment " + string.Join(' ',
                toBlank.Distinct(StringComparer.Ordinal).Select(static k => k + "="));
            _runner.Run("systemctl", args);
        }

        TryDeleteSessionEnvDropIn();
    }

    /// <summary>Parse <c>systemctl --user show-environment</c> output for loopback-valued proxy keys.</summary>
    internal static List<string> CollectLoopbackSessionProxyKeys(string? showEnvironmentOutput)
    {
        var toBlank = new List<string>();
        if (string.IsNullOrEmpty(showEnvironmentOutput))
            return toBlank;

        foreach (var raw in showEnvironmentOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq];
            if (!IsSessionProxyEnvKey(key))
                continue;
            var value = line[(eq + 1)..];
            if (TryParseProxyUri(value, out var host, out _) && IsLoopbackHost(host))
                toBlank.Add(key);
        }

        return toBlank;
    }

    private static bool IsSessionProxyEnvKey(string key) =>
        key is HttpProxyEnv or HttpsProxyEnv or HttpProxyEnvUpper or HttpsProxyEnvUpper;

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
        if (!mode.Equals(ManualProxyMode, StringComparison.OrdinalIgnoreCase))
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
        if (!mode.Equals(ManualProxyMode, StringComparison.OrdinalIgnoreCase))
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
            return mode.Equals(ManualProxyMode, StringComparison.OrdinalIgnoreCase)
                   && host.Equals(_watchHost, StringComparison.OrdinalIgnoreCase)
                   && port == _watchPort;
        }

        if (HasKde())
        {
            var type = KdeRead(KdeProxyTypeKey)?.Trim();
            if (type is null)
                return true;
            var http = KdeRead(KdeHttpProxyKey) ?? string.Empty;
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
            GsettingsSet(GnomeSystemProxyHttpSchema, GsettingsEnabledKey, _gnome.HttpEnabled ? "true" : GsettingsDisabled);
            GsettingsSet(GnomeSystemProxyHttpsSchema, "host", QuoteGsettings(_gnome.HttpsHost));
            GsettingsSet(GnomeSystemProxyHttpsSchema, "port", _gnome.HttpsPort.ToString());
            GsettingsSet(GnomeSystemProxySchema, GnomeIgnoreHostsKey, _gnome.IgnoreHosts);
        }

        if (_kde is not null && HasKde())
        {
            KdeWrite(KdeProxyTypeKey, _kde.ProxyType);
            KdeWrite(KdeHttpProxyKey, _kde.HttpProxy);
            KdeWrite(KdeHttpsProxyKey, _kde.HttpsProxy);
            KdeWrite(KdeNoProxyForKey, _kde.NoProxyFor);
            KdeReload();
        }
    }

    private bool GnomeOrKdePointsAtLoopback()
    {
        if (HasGnome())
        {
            var mode = GsettingsGet(GnomeSystemProxySchema, "mode")?.Trim('\'', '"') ?? "none";
            var host = GsettingsGet(GnomeSystemProxyHttpSchema, "host")?.Trim('\'', '"') ?? string.Empty;
            if (mode.Equals(ManualProxyMode, StringComparison.OrdinalIgnoreCase) && IsLoopbackHost(host))
                return true;
        }

        if (HasKde())
        {
            var httpProxy = KdeRead(KdeHttpProxyKey) ?? string.Empty;
            if (httpProxy.Contains(LoopbackAddress, StringComparison.Ordinal) ||
                httpProxy.Contains(LocalhostName, StringComparison.OrdinalIgnoreCase))
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
