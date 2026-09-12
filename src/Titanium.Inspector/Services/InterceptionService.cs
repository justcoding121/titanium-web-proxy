using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Services;

/// <summary>
/// Explicit MITM capture: full request/response bodies, system proxy, CA trust/export,
/// capture pause, AutoResponder, breakpoints, and light script hooks.
/// </summary>
public sealed class InterceptionService : IDisposable
{
    public const int MaxBodyBytes = InspectorBodyLimits.MaxBodyBytes;
    public const int MaxBodyTextChars = InspectorBodyLimits.MaxBodyTextChars;

    private long _nextId;
    private readonly ConcurrentDictionary<object, SessionSnapshot> _live = new();
    private readonly ISystemProxyController _systemProxy;
    private ProxyServer? _proxy;
    private ExplicitProxyEndPoint? _endPoint;
    private bool _systemProxyEnabled;
    private int _shutdownStarted;
    private readonly ManualResetEventSlim _shutdownCompleted = new(false);
    private string? _rootPfxPath;
    private InspectorSettings? _loggingSettings;
    private Channel<ProcessResolveWork>? _processResolveChannel;
    private CancellationTokenSource? _processResolveCts;

    /// <summary>
    ///     Serializes fire-and-forget trust cleanup: Firefox prefs/HKCU/policies and Personal-store
    ///     prune. Rapid Clear+Install / Install / Untrust must not interleave ClearRootTrust,
    ///     EnableEnterpriseRoots, and My-store prune (prefs locks + Crypt32 contention).
    /// </summary>
    private readonly object _firefoxTrustBgGate = new();
    private readonly Queue<FirefoxTrustBgQueued> _firefoxTrustBgQueue = new();
    private bool _firefoxTrustBgRunning;
    private TaskCompletionSource _firefoxTrustBgIdle = CreateCompletedFirefoxTrustIdle();

    private enum FirefoxTrustBgKind
    {
        Clear,
        Enable,
        Prune,
    }

    private readonly record struct FirefoxTrustBgQueued(FirefoxTrustBgKind Kind, Action Work);

    private static TaskCompletionSource CreateCompletedFirefoxTrustIdle()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private readonly record struct ProcessResolveWork(SessionSnapshot Snap, Lazy<int> ProcessId);

    public InterceptionService(ISystemProxyController? systemProxy = null)
    {
        _systemProxy = systemProxy ?? new ProxyServerSystemProxyController();
    }

    public bool IsRunning => _proxy?.ProxyRunning == true;

    /// <summary>OS-assigned listen port after <see cref="StartAsync"/> (supports <c>port == 0</c>).</summary>
    public int BoundPort { get; private set; }

    /// <summary>When false, the listener stays up but sessions are not published to the grid.</summary>
    public bool Capturing { get; set; } = true;

    /// <summary>
    /// When false, HTTPS CONNECT tunnels stay opaque (no MITM). Endpoint still has decrypt capability;
    /// <see cref="OnBeforeTunnelConnect"/> gates per-request <c>DecryptSsl</c>.
    /// </summary>
    public bool DecryptHttps { get; set; }

    /// <summary>
    /// When true, origin TLS handshake failures under MITM train an in-memory host bypass.
    /// Default on for Inspector; wired to <see cref="ProxyServer.EnableDecryptFailureBypass"/>.
    /// </summary>
    public bool EnableDecryptFailureBypass { get; set; } = true;

    /// <summary>Extra host patterns that skip HTTPS decryption (in addition to built-in bypasses).</summary>
    public List<string> DecryptSkipHosts { get; set; } = [];

    /// <summary>When non-empty, only these hosts are decrypted (built-in bypasses still never decrypt).</summary>
    public List<string> DecryptOnlyHosts { get; set; } = [];

    /// <summary>User WinINET bypass patterns when System proxy is on.</summary>
    public List<string> SystemProxyBypassHosts { get; set; } = [];

    /// <summary>Proxy localhost through the system proxy when enabled.</summary>
    public bool ProxyLoopback { get; set; } = true;

    public InspectorSettings? SystemProxySettings { get; set; }

    /// <summary>True when the OS can host QUIC (MsQuic / <c>QuicListener.IsSupported</c>).</summary>
    public static bool IsHttp3Supported => System.Net.Quic.QuicListener.IsSupported;

    /// <summary>
    /// When set (tests), skip the Windows certificate store and track trust in-memory.
    /// Avoids modal "Root Certificate Store" UI that hangs headless / CI runs.
    /// Also suppresses CertificateManager Root-store CryptUI when the proxy is started.
    /// </summary>
    public bool UseInMemoryTrustState
    {
        get => _useInMemoryTrustState;
        set
        {
            _useInMemoryTrustState = value;
            if (value)
                CertificateManager.SuppressInteractiveRootStoreMutations = true;
        }
    }

    private bool _useInMemoryTrustState;

    /// <summary>Test seam: next <see cref="InstallRootCertificate"/> returns false once (forces elevate path).</summary>
    public bool FailNextUserTrustInstall { get; set; }

    private bool _inMemoryTrusted;

    public bool SystemProxyEnabled => _systemProxyEnabled;
    public X509Certificate2? RootCertificate => _proxy?.CertificateManager.RootCertificate;
    public bool IsRootTrusted { get; private set; }

    /// <summary>Last OS trust outcome from Install root CA (Keychain / NSS / package hints).</summary>
    public CertificateOsTrustResult? LastOsTrustResult { get; private set; }

    /// <summary>Last system-proxy enable/disable failure message (null after success).</summary>
    public string? LastSystemProxyError { get; private set; }

    /// <summary>Root certificate display name used as NSS nickname.</summary>
    public string RootCertificateName =>
        _proxy?.CertificateManager.RootCertificateName ?? "Titanium Inspector Root Certificate";

    /// <summary>True when a Firefox profiles.ini is present on this machine.</summary>
    public static bool IsFirefoxProfilePresent => FirefoxCertificateTrust.IsFirefoxProfilePresent();

    /// <summary>True when the running proxy currently allows HTTP/2.</summary>
    public bool Http2Enabled { get; private set; } = true;

    /// <summary>True when this capture session has HTTP/3 enabled (MsQuic available).</summary>
    public bool Http3Enabled { get; private set; }
    public string? UpstreamProxyAddress { get; set; }
    public string? PacUrl { get; set; }

    public bool IgnoreServerCertificateErrors { get; set; }

    private bool _addViaHeader = true;

    /// <summary>
    /// When true, inject <see cref="ProxyServer.DefaultViaHeaderPseudonym"/> on intercepted traffic.
    /// Applied on start and when toggled while the proxy is running.
    /// </summary>
    public bool AddViaHeader
    {
        get => _addViaHeader;
        set
        {
            _addViaHeader = value;
            ApplyViaHeaderOption();
        }
    }

    /// <summary>When true, call <see cref="InstallRootCertificate"/> after start (explicit trust).</summary>
    public bool AutoTrustRootOnStart { get; set; }

    public AutoResponderViewModel? AutoResponder { get; set; }
    public MapRemoteViewModel? MapRemote { get; set; }
    public BreakpointViewModel? Breakpoints { get; set; }

    /// <summary>When true, breakpoints also fire on BeforeResponse.</summary>
    public bool BreakpointOnResponse { get; set; }

    /// <summary>Optional light request script (set-header / set-status / abort).</summary>
    public string? ScriptOnRequest { get; set; }

    /// <summary>Optional light response script (set-header / set-status / abort).</summary>
    public string? ScriptOnResponse { get; set; }

    /// <summary>Active network throttle profile (null / None = off).</summary>
    public NetworkThrottleProfile? ThrottleProfile { get; set; }

    /// <summary>Optional FileDescriptorSet path for protobuf decode hints.</summary>
    public string? ProtobufDescriptorSetPath { get; set; }

    public event EventHandler<SessionSnapshot>? SessionCaptured;
    public event EventHandler<SessionSnapshot>? SessionUpdated;
    public event EventHandler<DecryptFailureBypassEntry>? DecryptFailureBypassLearned;

    /// <summary>Applies the learning toggle to a running proxy (no-op when not started).</summary>
    public void ApplyDecryptFailureBypassSetting()
    {
        if (_proxy is null)
            return;
        _proxy.EnableDecryptFailureBypass = EnableDecryptFailureBypass;
    }

    public IReadOnlyList<DecryptFailureBypassEntry> GetDecryptFailureBypassEntries() =>
        _proxy?.GetDecryptFailureBypassEntries() ?? Array.Empty<DecryptFailureBypassEntry>();

    public bool RemoveDecryptFailureBypass(string host) =>
        _proxy?.RemoveDecryptFailureBypass(host) ?? false;

    public void ClearDecryptFailureBypass() => _proxy?.ClearDecryptFailureBypass();

    private bool IsLearnedDecryptBypass(string? host)
    {
        if (!EnableDecryptFailureBypass || _proxy is null || string.IsNullOrWhiteSpace(host))
            return false;
        // O(1) cache consult — do not Snapshot the full list on every CONNECT.
        return _proxy.ShouldBypassDecryptForLearnedHost(host);
    }

    private void OnDecryptFailureBypassChanged(object? sender, DecryptFailureBypassEntry e) =>
        DecryptFailureBypassLearned?.Invoke(this, e);

    public async Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        using var scope = InspectorUxTrace.Scope("Interception.StartAsync", $"{address}:{port}");
        cancellationToken.ThrowIfCancellationRequested();
        if (_proxy is not null)
        {
            return;
        }

        Interlocked.Exchange(ref _nextId, 0);

        // Explicit trust flags: do not silently install into the user store on start.
        // Callers must InstallRootCertificate (or set AutoTrustRootOnStart) so UI can report success/failure.
        _proxy = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false);
        ApplyLoggingOptions(_loggingSettings);
        _proxy.EnableHttpInterception = true;
        _proxy.EnableRequestTimingCapture = true;
        _proxy.EnableDecryptFailureBypass = EnableDecryptFailureBypass;
        _proxy.DecryptFailureBypassChanged += OnDecryptFailureBypassChanged;
        ApplyViaHeaderOption();
        // Inspector eagerly buffers bodies for the session grid; 4 MiB trips too often on
        // normal browsing (images, JS bundles) and RST'd the H2 stream. 32 MiB still bounds
        // memory while covering typical inspected payloads.
        _proxy.MaxBufferedBodyBytes = 32 * 1024 * 1024;
        ApplyHttpProtocols();
        _proxy.BeforeRequest += OnBeforeRequest;
        _proxy.BeforeResponse += OnBeforeResponse;
        _proxy.AfterResponse += OnAfterResponse;
        _proxy.OnRequestBodyWrite += OnRequestBodyWriteThrottle;
        _proxy.OnResponseBodyWrite += OnResponseBodyWriteThrottle;
        _proxy.ServerCertificateValidationCallback += OnServerCertValidation;

        if (!string.IsNullOrWhiteSpace(UpstreamProxyAddress) &&
            Uri.TryCreate(UpstreamProxyAddress, UriKind.Absolute, out var upstream))
        {
            _proxy.UpStreamHttpProxy = new ExternalProxy(upstream.Host, upstream.Port)
            {
                ProxyType = ExternalProxyType.Http,
            };
        }

        EnsureRootPfxPath();
        _proxy.CertificateManager.PfxFilePath = _rootPfxPath!;
        // Browser MITM: ECDSA leaves + disk cache (RSA first-visit stampede is the cold google.com tax).
        _proxy.CertificateManager.ApplyFastColdStartLeafSettings();

        _endPoint = new ExplicitProxyEndPoint(address, port, decryptSsl: true);
        _endPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnect;
        _endPoint.BeforeTunnelConnectResponse += OnBeforeTunnelConnectResponse;
        _proxy.AddEndPoint(_endPoint);
        using (InspectorUxTrace.Scope("Interception.ProxyServer.Start"))
            _proxy.Start();
        BoundPort = _endPoint.Port;
        StartProcessResolveWorker();

        // Do not treat Unix store/Keychain presence as SSL trust (see RefreshTrustState).
        using (InspectorUxTrace.Scope("Interception.RefreshTrustState.OnStart"))
        {
            IsRootTrusted = UseInMemoryTrustState
                ? _inMemoryTrusted
                : RefreshTrustState(machineStore: false);
        }

        TryPruneLegacySharedCrtsOnce();

        if (AutoTrustRootOnStart)
        {
            InstallRootCertificate(machineStore: false);
        }

        Capturing = true;
        Interlocked.Exchange(ref _shutdownStarted, 0);
        _shutdownCompleted.Reset();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Enable HTTP/2 and HTTP/3 (when MsQuic is available). Safe while capturing: new connections
    /// pick up the change; in-flight sessions keep the protocol they already negotiated.
    /// Inspector is an explicit TCP proxy, so HTTP/3 here is origin-side only.
    /// </summary>
    public void ApplyHttpProtocols()
    {
        if (_proxy is null)
        {
            Http2Enabled = true;
            Http3Enabled = IsHttp3Supported;
            return;
        }

        _proxy.EnableHttp2 = true;
        Http2Enabled = _proxy.EnableHttp2;
        Http3Enabled = _proxy.SetHttp3Enabled(true);
    }

    /// <summary>Apply or refresh logging from Inspector settings (safe while running).</summary>
    public void ConfigureLogging(InspectorSettings settings)
    {
        _loggingSettings = settings;
        if (_proxy is null)
        {
            return;
        }

        ApplyLoggingOptions(settings);
        _proxy.ApplyLoggingConfiguration();
    }

    private void ApplyViaHeaderOption()
    {
        if (_proxy is null)
        {
            return;
        }

        _proxy.ViaHeaderPseudonym = AddViaHeader
            ? ProxyServer.DefaultViaHeaderPseudonym
            : string.Empty;
    }

    /// <summary>
    /// Idempotent shutdown: restore system proxy (even if already stopped) and dispose the proxy.
    /// Matches WPF example <c>EnsureProxyShutdown</c> semantics.
    /// Must not run on the Avalonia UI thread — WinINET <c>InternetSetOption</c> broadcasts
    /// back to the closing window and deadlocks (title-bar Close hangs; taskbar Close often
    /// terminates the process instead).
    /// </summary>
    public void EnsureShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            _shutdownCompleted.Wait(TimeSpan.FromSeconds(3), CancellationToken.None);
            return;
        }

        try
        {
            if (_proxy is not null && _proxy.ProxyRunning)
            {
                Stop();
            }
            else if (_proxy is not null)
            {
                try
                {
                    _systemProxy.RestoreOriginalProxySettings(_proxy);
                }
                catch
                {
                    // best-effort
                }

                try
                {
                    _proxy.Dispose();
                }
                catch
                {
                    // best-effort
                }

                _proxy = null;
                _endPoint = null;
                _systemProxyEnabled = false;
                Http3Enabled = false;
            }
        }
        catch
        {
            // never throw from teardown
        }
        finally
        {
            _shutdownCompleted.Set();
        }
    }

    /// <summary>
    /// Persist-safe close path: queue <see cref="EnsureShutdown"/> on the thread pool so the
    /// window can disappear immediately.
    /// </summary>
    public void BeginBackgroundShutdown()
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                EnsureShutdown();
            }
            catch
            {
                // never throw from teardown
            }
        }, null);
    }

    private void ApplyLoggingOptions(InspectorSettings? settings)
    {
        if (_proxy is null)
        {
            return;
        }

        var s = settings ?? new InspectorSettings();
        _proxy.Logging.Enabled = s.LoggingEnabled;
        if (Enum.TryParse<LogLevel>(s.LoggingMinimumLevel, ignoreCase: true, out var level))
        {
            _proxy.Logging.MinimumLevel = level;
        }

        _proxy.Logging.EnableConsole = false;
        _proxy.Logging.EnableFile = s.LoggingEnableFile;
        EnsureRootPfxPath();
        var dir = Path.GetDirectoryName(_rootPfxPath!)!;
        var defaultLog = Path.Combine(dir, "logs", "titanium-inspector.log");
        _proxy.Logging.FilePath = string.IsNullOrWhiteSpace(s.LoggingFilePath) ? defaultLog : s.LoggingFilePath;
        _proxy.ApplyLoggingConfiguration();
    }

    public void Stop()
    {
        if (_proxy is null)
        {
            return;
        }

        if (_systemProxyEnabled)
        {
            SetSystemProxy(false);
        }

        _proxy.BeforeRequest -= OnBeforeRequest;
        _proxy.BeforeResponse -= OnBeforeResponse;
        _proxy.AfterResponse -= OnAfterResponse;
        _proxy.OnRequestBodyWrite -= OnRequestBodyWriteThrottle;
        _proxy.OnResponseBodyWrite -= OnResponseBodyWriteThrottle;
        _proxy.ServerCertificateValidationCallback -= OnServerCertValidation;
        _proxy.DecryptFailureBypassChanged -= OnDecryptFailureBypassChanged;
        if (_endPoint is not null)
        {
            _endPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnect;
            _endPoint.BeforeTunnelConnectResponse -= OnBeforeTunnelConnectResponse;
        }

        _proxy.Stop();
        _proxy.Dispose();
        _proxy = null;
        _endPoint = null;
        BoundPort = 0;
        _live.Clear();
        StopProcessResolveWorker();
        IsRootTrusted = false;
        _systemProxyEnabled = false;
        Http3Enabled = false;
    }

    private readonly object _systemProxyGate = new();

    /// <summary>
    /// Enable or disable system proxy. Returns false if the proxy is not running or the underlying call failed.
    /// </summary>
    /// <param name="stillWanted">
    /// Optional gate evaluated under the system-proxy lock before mutating OS settings.
    /// Used to cancel a superseded optimistic enable/disable (e.g. Stop while enable is in flight).
    /// </param>
    public bool SetSystemProxy(bool enable, InspectorSettings? settings = null, Func<bool>? stillWanted = null)
    {
        LastSystemProxyError = null;
        lock (_systemProxyGate)
        {
            if (stillWanted is not null && !stillWanted())
            {
                return false;
            }

            if (enable)
            {
                if (_proxy is null || _endPoint is null || !_proxy.ProxyRunning)
                {
                    LastSystemProxyError = "Proxy is not running";
                    return false;
                }
            }
            else if (!_systemProxyEnabled)
            {
                // Already restored — common when Stop raced an optimistic enable that never landed.
                return true;
            }

            if (_proxy is null)
            {
                LastSystemProxyError = "Proxy is not running";
                return false;
            }

            try
            {
                if (enable)
                {
                    var effective = settings ?? SystemProxySettings ?? new InspectorSettings();
                    SystemProxySettings = effective;
                    var result = _systemProxy.SetAsSystemProxy(_proxy, _endPoint!, effective);
                    if (!result.Succeeded)
                    {
                        LastSystemProxyError = result.Message;
                        _proxy.Logger.LogWarning("System proxy enable failed: {Message}", result.Message);
                        return false;
                    }

                    _systemProxyEnabled = true;
                }
                else
                {
                    var result = _systemProxy.RestoreOriginalProxySettings(_proxy);
                    if (!result.Succeeded)
                    {
                        LastSystemProxyError = result.Message;
                        _proxy.Logger.LogWarning("System proxy disable failed: {Message}", result.Message);
                        return false;
                    }

                    _systemProxyEnabled = false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LastSystemProxyError = ex.Message;
                try
                {
                    _proxy.Logger.LogWarning(ex, "System proxy {Action} failed", enable ? "enable" : "disable");
                }
                catch
                {
                    // logging must not hide the original failure
                }

                return false;
            }
        }
    }

    /// <summary>Re-applies system proxy bypass rules when already enabled (after settings change).</summary>
    public bool ReapplySystemProxyIfEnabled()
    {
        if (!_systemProxyEnabled || SystemProxySettings is null)
        {
            return true;
        }

        return SetSystemProxy(true, SystemProxySettings);
    }

    /// <summary>Install root CA and refresh <see cref="IsRootTrusted"/> from the store.</summary>
    /// <returns>True when the cert is present in the target Root store after install (or Unix SSL trust succeeded / needs Keychain confirm).</returns>
    /// <remarks>
    ///     Combined API for tests/E2E. Inspector UI uses <see cref="InstallRootStoresOnly"/> +
    ///     <see cref="FinalizeTrustAfterStoreMutation"/> so CryptUI Yes is not followed by store
    ///     sweeps on the Avalonia dispatcher.
    /// </remarks>
    public bool InstallRootCertificate(bool machineStore)
    {
        if (_proxy is null)
        {
            return false;
        }

        if (FailNextUserTrustInstall)
        {
            FailNextUserTrustInstall = false;
            LastOsTrustResult = CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed, "Forced user-trust failure (test)");
            return false;
        }

        if (UseInMemoryTrustState)
        {
            _inMemoryTrusted = true;
            IsRootTrusted = true;
            LastOsTrustResult = CertificateOsTrustResult.Ok("Root CA trusted (in-memory)");
            return true;
        }

        if (IsRootPresentInStore(machineStore))
        {
            if (OperatingSystem.IsWindows())
            {
                IsRootTrusted = true;
                LastOsTrustResult = CertificateOsTrustResult.Ok("Root CA already trusted");
                return CompleteRootTrustInstall(true);
            }

            if (_proxy.CertificateManager.VerifyOsUserSslTrust())
            {
                IsRootTrusted = true;
                LastOsTrustResult = CertificateOsTrustResult.Ok("Root CA already trusted for SSL");
                return CompleteRootTrustInstall(true);
            }

            _proxy.CertificateManager.TrustRootCertificate(machineStore);
            LastOsTrustResult = _proxy.CertificateManager.LastOsTrustResult;
            IsRootTrusted = EvaluateUnixTrustSuccess(LastOsTrustResult) ||
                            _proxy.CertificateManager.VerifyOsUserSslTrust();
            return CompleteRootTrustInstall(
                IsRootTrusted ||
                LastOsTrustResult?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm);
        }

        // Full trust (stores + orphan prune + Unix) — non-UI callers only.
        _proxy.CertificateManager.TrustRootCertificate(machineStore);
        LastOsTrustResult = _proxy.CertificateManager.LastOsTrustResult;

        if (OperatingSystem.IsWindows())
        {
            IsRootTrusted = IsRootPresentInStore(machineStore);
            return CompleteRootTrustInstall(IsRootTrusted);
        }

        IsRootTrusted = EvaluateUnixTrustSuccess(LastOsTrustResult) ||
                        _proxy.CertificateManager.VerifyOsUserSslTrust();
        return CompleteRootTrustInstall(
            IsRootTrusted ||
            LastOsTrustResult?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm);
    }

    /// <summary>CryptUI Root Add only — must run on a pumping UI thread.</summary>
    /// <returns>True when the Root entry was newly added.</returns>
    public bool InstallRootStoresOnly(bool machineStore)
    {
        if (_proxy is null)
            return false;

        if (FailNextUserTrustInstall)
        {
            FailNextUserTrustInstall = false;
            LastOsTrustResult = CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed, "Forced user-trust failure (test)");
            return false;
        }

        if (UseInMemoryTrustState)
        {
            _inMemoryTrusted = true;
            IsRootTrusted = true;
            LastOsTrustResult = CertificateOsTrustResult.Ok("Root CA trusted (in-memory)");
            return true;
        }

        var added = _proxy.CertificateManager.InstallRootIntoCertificateStores(machineStore);
        LastOsTrustResult = _proxy.CertificateManager.LastOsTrustResult;
        return added;
    }

    /// <summary>macOS/Linux Keychain/NSS trust — may show auth UI; pumping thread required.</summary>
    public void ApplyUnixSslTrustOnUi(bool machineStore)
    {
        if (_proxy is null || UseInMemoryTrustState || OperatingSystem.IsWindows())
            return;

        _proxy.CertificateManager.ApplyUnixSslTrustAfterStoreInstall(machineStore);
        LastOsTrustResult = _proxy.CertificateManager.LastOsTrustResult;
    }

    /// <summary>
    ///     After CryptUI Add / Unix trust: verify trust + My-store prune. Safe off the UI thread.
    ///     Skips Root orphan CryptUI sweeps (those freeze Avalonia after Yes).
    /// </summary>
    /// <param name="machineStore">CurrentUser vs LocalMachine.</param>
    /// <param name="rootStoreAdded">
    ///     When <see langword="true"/>, CryptUI just added the Root entry — skip an immediate
    ///     Root-store Find (Crypt32 is hot after Yes and routinely stalls ~10–15s, especially on a
    ///     second Clear+Install). Trust is assumed; My prune runs best-effort afterward.
    /// </param>
    public bool FinalizeTrustAfterStoreMutation(bool machineStore, bool? rootStoreAdded = null)
    {
        using var scope = InspectorUxTrace.Scope(
            "FinalizeTrustAfterStoreMutation",
            $"machine={machineStore} added={rootStoreAdded}");
        if (_proxy is null)
            return false;

        if (UseInMemoryTrustState)
        {
            IsRootTrusted = _inMemoryTrusted;
            return IsRootTrusted;
        }

        if (rootStoreAdded == true && OperatingSystem.IsWindows())
        {
            IsRootTrusted = true;
            if (LastOsTrustResult is null)
                LastOsTrustResult = CertificateOsTrustResult.Ok("Root CA trusted in current-user store");

            InspectorUxTrace.Event("FinalizeTrust.SkipRootFind", "assumeInstalled=true");
            // Prune on the serial trust background lane — never Task.Run beside Firefox prefs work.
            SchedulePruneOrphanedPersonalCertificates(machineStore);
            return CompleteRootTrustInstall(true);
        }

        try
        {
            using (InspectorUxTrace.Scope("FinalizeTrust.PrunePersonal"))
            {
                _proxy.CertificateManager.PruneOrphanedPersonalCertificates(
                    machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser,
                    keepCurrentThumbprint: true);
            }
        }
        catch
        {
            // best-effort
        }

        if (OperatingSystem.IsWindows())
        {
            using (InspectorUxTrace.Scope("FinalizeTrust.IsRootPresentInStore"))
                IsRootTrusted = IsRootPresentInStore(machineStore);
            if (IsRootTrusted && LastOsTrustResult is null)
                LastOsTrustResult = CertificateOsTrustResult.Ok("Root CA trusted in current-user store");
            return CompleteRootTrustInstall(IsRootTrusted);
        }

        using (InspectorUxTrace.Scope("FinalizeTrust.VerifyOsUserSslTrust"))
        {
            IsRootTrusted = EvaluateUnixTrustSuccess(LastOsTrustResult) ||
                            _proxy.CertificateManager.VerifyOsUserSslTrust();
        }
        return CompleteRootTrustInstall(
            IsRootTrusted ||
            LastOsTrustResult?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm);
    }

    private bool CompleteRootTrustInstall(bool installed)
    {
        // Do not write Firefox prefs/policies here — schedule via RunOffUiAsync after success.
        return installed;
    }

    /// <summary>
    /// Installs the root CA with an OS admin prompt when required (UAC / macOS auth / polkit).
    /// </summary>
    public bool InstallRootCertificateAsAdmin(bool machineStore)
    {
        if (_proxy is null)
        {
            return false;
        }

        if (UseInMemoryTrustState)
        {
            _inMemoryTrusted = true;
            IsRootTrusted = true;
            LastOsTrustResult = CertificateOsTrustResult.Ok("Root CA trusted (in-memory)");
            return true;
        }

        var ok = _proxy.CertificateManager.TrustRootCertificateAsAdmin(machineStore);
        LastOsTrustResult = _proxy.CertificateManager.LastOsTrustResult;
        if (OperatingSystem.IsWindows())
        {
            IsRootTrusted = ok && IsRootPresentInStore(machineStore);
            return CompleteRootTrustInstall(IsRootTrusted);
        }

        IsRootTrusted = ok && (EvaluateUnixTrustSuccess(LastOsTrustResult) ||
                               _proxy.CertificateManager.VerifyOsUserSslTrust());
        return CompleteRootTrustInstall(
            IsRootTrusted ||
            LastOsTrustResult?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm);
    }

    /// <summary>
    ///     After UAC/admin install: re-verify off the UI thread (store Find can stall Crypt32).
    /// </summary>
    public bool FinalizeTrustAfterAdminInstall(bool machineStore)
    {
        if (_proxy is null)
            return false;
        if (UseInMemoryTrustState)
            return IsRootTrusted;

        try
        {
            _proxy.CertificateManager.PruneOrphanedPersonalCertificates(
                machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser,
                keepCurrentThumbprint: true);
        }
        catch
        {
            // best-effort
        }

        if (OperatingSystem.IsWindows())
        {
            IsRootTrusted = IsRootPresentInStore(machineStore);
            return CompleteRootTrustInstall(IsRootTrusted);
        }

        IsRootTrusted = EvaluateUnixTrustSuccess(LastOsTrustResult) ||
                        _proxy.CertificateManager.VerifyOsUserSslTrust();
        return CompleteRootTrustInstall(
            IsRootTrusted ||
            LastOsTrustResult?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm);
    }

    /// <summary>Installs certutil (package/brew) then retries user SSL trust.</summary>
    public CertificateOsTrustResult InstallNssToolsAndRetryTrust()
    {
        if (_proxy is null)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed, "Start the proxy first");
        }

        var result = _proxy.CertificateManager.InstallNssCertutilAndRetryUserTrust();
        LastOsTrustResult = result;
        if (EvaluateUnixTrustSuccess(result))
        {
            IsRootTrusted = true;
        }
        else if (result.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
        {
            // Cert may be present; SSL trust still requires Always Trust confirmation.
            IsRootTrusted = _proxy.CertificateManager.VerifyOsUserSslTrust();
        }

        return result;
    }

    /// <summary>Opens Keychain Access for Always Trust guidance.</summary>
    public string? OpenMacKeychainGuidance() => _proxy?.CertificateManager.OpenMacKeychainGuidance();

    /// <summary>Best-effort: root is present in the macOS login keychain (not necessarily SSL-trusted).</summary>
    public bool IsRootInLoginKeychain() =>
        _proxy?.CertificateManager.IsRootInLoginKeychain() == true;

    /// <summary>Re-verifies macOS/Linux user SSL trust and updates <see cref="IsRootTrusted"/>.</summary>
    /// <remarks>
    /// Does not write Firefox prefs — that is Install / Trust Firefox only (verify-only must stay cheap).
    /// </remarks>
    public bool VerifyOsUserSslTrust()
    {
        if (_proxy is null) return false;
        if (UseInMemoryTrustState) return IsRootTrusted;
        // Windows: Root store presence is trust. Unix: require real SSL trust verification —
        // Keychain/NSS can hold the CA without trusting it for SSL (Chrome MITM fails).
        var ok = OperatingSystem.IsWindows()
            ? IsRootPresentInStore(false)
            : _proxy.CertificateManager.VerifyOsUserSslTrust();
        IsRootTrusted = ok;
        return ok;
    }

    /// <summary>
    ///     Trust CA for Firefox: enable OS-root import (Windows policy / macOS Keychain via
    ///     <c>user.js</c>) first; otherwise import into the default Firefox profile via certutil.
    /// </summary>
    public CertificateOsTrustResult TrustFirefox()
    {
        if (UseInMemoryTrustState)
        {
            return CertificateOsTrustResult.Ok("Firefox trust recorded (in-memory)");
        }

        if (_proxy is null)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed, "Start the proxy first");
        }

        var cert = _proxy.CertificateManager.RootCertificate;
        if (cert is null)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed, "Root certificate is not loaded");
        }

        if (OperatingSystem.IsWindows())
        {
            var policy = FirefoxCertificateTrust.TryEnableWindowsEnterpriseRoots();
            if (policy.Succeeded)
                return policy;
            // Fall through to profile NSS import.
        }
        else
        {
            var pref = FirefoxCertificateTrust.TryEnableEnterpriseRootsUserPref();
            if (pref.Succeeded)
                return pref;
        }

        return FirefoxCertificateTrust.TrustDefaultProfile(cert, RootCertificateName);
    }

    /// <summary>
    ///     Best-effort: if a Firefox profile exists, enable OS-root trust so Install root CA
    ///     is enough after a Firefox restart (no extra menu, no certutil).
    /// </summary>
    public static void TryEnableFirefoxEnterpriseRootsBestEffort()
    {
        try
        {
            // Unit tests set TITANIUM_SKIP_ROOT_STORE_UI=1 — never touch live Firefox profiles
            // (prefs.js locks hang / balloon memory when Firefox is open).
            if (string.Equals(Environment.GetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI"), "1",
                    StringComparison.Ordinal))
                return;

            if (!FirefoxCertificateTrust.IsFirefoxProfilePresent())
                return;

            // Windows: HKCU ImportEnterpriseRoots first (cheap). user.js/prefs.js only as fallback
            // inside TryEnableWindowsEnterpriseRoots — never prefs-first on the install path.
            if (OperatingSystem.IsWindows())
                FirefoxCertificateTrust.TryEnableWindowsEnterpriseRoots();
            else
                FirefoxCertificateTrust.TryEnableEnterpriseRootsUserPref();
        }
        catch
        {
            // install path must not fail because Firefox prefs were locked
        }
    }

    /// <summary>
    ///     Queue Firefox enable work on the serial background lane (coalesces consecutive enables).
    /// </summary>
    public void ScheduleFirefoxEnterpriseRootsBestEffort() =>
        EnqueueFirefoxTrustBackground(FirefoxTrustBgKind.Enable, TryEnableFirefoxEnterpriseRootsBestEffort);

    /// <summary>
    ///     Queue Firefox clear work on the serial background lane (coalesces consecutive clears).
    /// </summary>
    public void ScheduleClearPendingFirefoxRootTrust() =>
        EnqueueFirefoxTrustBackground(FirefoxTrustBgKind.Clear, ClearPendingFirefoxRootTrust);

    /// <summary>
    ///     Queue Personal (My) store same-CN prune on the serial trust background lane.
    ///     Used after CryptUI Root Add so install returns immediately while Crypt32 settles.
    /// </summary>
    public void SchedulePruneOrphanedPersonalCertificates(bool machineStore)
    {
        if (_proxy is null || UseInMemoryTrustState)
            return;

        var location = machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        var mgr = _proxy.CertificateManager;
        EnqueueFirefoxTrustBackground(FirefoxTrustBgKind.Prune, () =>
        {
            try
            {
                mgr.PruneOrphanedPersonalCertificates(location, keepCurrentThumbprint: true);
            }
            catch
            {
                // best-effort
            }
        });
    }

    /// <summary>
    ///     Await idle trust background lane (Firefox prefs + My prune) so the next trust mutation
    ///     does not collide with prior fire-and-forget work.
    /// </summary>
    public Task WaitForFirefoxTrustBackgroundIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_firefoxTrustBgGate)
            idle = _firefoxTrustBgIdle.Task;

        if (idle.IsCompleted)
            return Task.CompletedTask;

        return idle.WaitAsync(cancellationToken);
    }

    private void EnqueueFirefoxTrustBackground(FirefoxTrustBgKind kind, Action work)
    {
        lock (_firefoxTrustBgGate)
        {
            // Coalesce consecutive same-kind ops (double Enable from Ensure+SetOsTrustSuccess,
            // double Clear, double Prune after rapid Install).
            if (_firefoxTrustBgQueue.Count > 0)
            {
                var items = _firefoxTrustBgQueue.ToArray();
                if (items[^1].Kind == kind)
                {
                    _firefoxTrustBgQueue.Clear();
                    for (var i = 0; i < items.Length - 1; i++)
                        _firefoxTrustBgQueue.Enqueue(items[i]);
                }
            }

            _firefoxTrustBgQueue.Enqueue(new FirefoxTrustBgQueued(kind, work));
            InspectorUxTrace.Event(
                "TrustBg.Enqueue",
                $"kind={kind} depth={_firefoxTrustBgQueue.Count} running={_firefoxTrustBgRunning}");

            if (_firefoxTrustBgRunning)
                return;

            _firefoxTrustBgRunning = true;
            _firefoxTrustBgIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(DrainFirefoxTrustBackground);
        }
    }

    private void DrainFirefoxTrustBackground()
    {
        InspectorUxTrace.Event("TrustBg.Drain.Start");
        try
        {
            while (true)
            {
                FirefoxTrustBgQueued next;
                lock (_firefoxTrustBgGate)
                {
                    if (_firefoxTrustBgQueue.Count == 0)
                    {
                        _firefoxTrustBgRunning = false;
                        _firefoxTrustBgIdle.TrySetResult();
                        InspectorUxTrace.Event("TrustBg.Drain.Idle");
                        return;
                    }

                    next = _firefoxTrustBgQueue.Dequeue();
                }

                using (InspectorUxTrace.Scope("TrustBg.Job", $"kind={next.Kind}"))
                {
                    try
                    {
                        next.Work();
                    }
                    catch
                    {
                        // best-effort lane — never fail the proxy / UI on prefs I/O
                    }
                }
            }
        }
        catch
        {
            lock (_firefoxTrustBgGate)
            {
                _firefoxTrustBgRunning = false;
                _firefoxTrustBgIdle.TrySetResult();
            }
            InspectorUxTrace.Event("TrustBg.Drain.Fault");
        }
    }

    private static bool EvaluateUnixTrustSuccess(CertificateOsTrustResult? result) =>
        result is { Succeeded: true };

    /// <summary>Marks the last trust attempt as user-cancelled (recovery dialog dismissed).</summary>
    public void SetLastOsTrustCancelled()
    {
        LastOsTrustResult = CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.Cancelled,
            "Root CA install cancelled");
    }

    public void UntrustRootCertificate(bool machineStore)
    {
        // Combined API for E2E / non-UI callers. Inspector ViewModel uses RemoveOsRootStoreOnly
        // + ClearPendingFirefoxRootTrust off-UI so CryptUI does not freeze on Firefox prefs.
        RemoveOsRootStoreOnly(machineStore);
        ClearPendingFirefoxRootTrust();
    }

    /// <summary>Nickname to clear from Firefox after OS untrust; consumed by <see cref="ClearPendingFirefoxRootTrust"/>.</summary>
    internal string? PendingFirefoxRootClearName { get; private set; }

    /// <summary>Best-effort Firefox cleanup after OS Root remove (call off the UI thread).</summary>
    public void ClearPendingFirefoxRootTrust()
    {
        var name = PendingFirefoxRootClearName;
        PendingFirefoxRootClearName = null;
        FirefoxCertificateTrust.ClearRootTrustBestEffort(name);
    }

    /// <summary>
    ///     Mint a new root CA: untrust same-CN store entries, delete Inspector PFX + local leaf cache,
    ///     recreate root. Always best-effort prunes the legacy shared <c>Titanium.Web.Proxy/crts</c> folder.
    ///     Does not install trust — caller should prompt Install CA.
    /// </summary>
    /// <remarks>
    ///     CryptUI Remove must run on a pumping UI thread; Firefox clear + PFX recreate should run
    ///     off-UI via <see cref="RemoveOsRootStoreOnly"/> + <see cref="MintNewRootCertificateCore"/>.
    ///     This combined method remains for tests / non-UI callers.
    /// </remarks>
    public bool RotateRootCertificate(bool machineStore)
    {
        RemoveOsRootStoreOnly(machineStore);
        return MintNewRootCertificateCore();
    }

    /// <summary>
    ///     CryptUI Root Removes for every same-CN thumbprint, then My/Unix finalize off-UI via
    ///     <see cref="FinalizeAfterRootRemove"/>. Inspector ViewModel lists thumbs off-UI first.
    /// </summary>
    public void RemoveOsRootStoreOnly(bool machineStore)
    {
        if (_proxy is null)
            return;

        if (UseInMemoryTrustState)
        {
            _inMemoryTrusted = false;
            IsRootTrusted = false;
            PendingFirefoxRootClearName = null;
            return;
        }

        PendingFirefoxRootClearName = RootCertificateName;
        var location = machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        // Combined path for tests: full CN sweep (may CryptUI). UI callers use List + RemoveByThumb.
        _proxy.CertificateManager.PruneOrphanedSameCommonNameCertificates(
            machineStore, keepCurrentThumbprint: false);
        RefreshTrustAfterRootRemove(machineStore);
    }

    /// <summary>Read-only list of same-CN Root thumbprints to delete. Safe off the UI thread.</summary>
    public IReadOnlyList<string> ListRootThumbprintsToRemove(bool machineStore)
    {
        if (_proxy is null || UseInMemoryTrustState)
            return Array.Empty<string>();

        PendingFirefoxRootClearName = RootCertificateName;
        var location = machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        return _proxy.CertificateManager.ListSameCommonNameRootThumbprints(location, keepThumbprint: null);
    }

    /// <summary>One Root Remove by thumbprint (CryptUI). Must run on a pumping UI thread.</summary>
    public void RemoveRootThumbprintOnUi(bool machineStore, string thumbprint)
    {
        if (_proxy is null || UseInMemoryTrustState)
            return;

        var location = machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        _proxy.CertificateManager.RemoveCertificateByThumbprint(StoreName.Root, location, thumbprint);
    }

    /// <summary>
    ///     After CryptUI Root Removes: drop matching Personal-store entries + refresh IsRootTrusted.
    ///     Safe off the UI thread (no Root CryptUI). Does not touch Firefox.
    /// </summary>
    public void FinalizeAfterRootRemove(bool machineStore)
    {
        if (_proxy is null)
            return;

        if (UseInMemoryTrustState)
        {
            _inMemoryTrusted = false;
            IsRootTrusted = false;
            return;
        }

        var location = machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        try
        {
            // Thumbprint remove only — avoid another subject scan of a large Personal store.
            var thumb = RootCertificate?.Thumbprint;
            if (!string.IsNullOrEmpty(thumb))
                _proxy.CertificateManager.RemoveCertificateByThumbprint(StoreName.My, location, thumb);
            else
                _proxy.CertificateManager.PruneOrphanedPersonalCertificates(
                    location, keepCurrentThumbprint: false);
        }
        catch
        {
            // best-effort
        }

        RefreshTrustAfterRootRemove(machineStore);
    }

    /// <summary>macOS/Linux Keychain/NSS untrust — may prompt; pumping UI thread.</summary>
    public void ApplyUnixUntrustOnUi()
    {
        if (_proxy is null || UseInMemoryTrustState || OperatingSystem.IsWindows())
            return;
        if (CertificateManager.AreInteractiveRootStoreMutationsSuppressed)
            return;
        if (RootCertificate is null)
            return;

        try
        {
            _proxy.CertificateManager.ApplyUnixSslUntrust();
        }
        catch
        {
            // best-effort
        }
    }

    private void RefreshTrustAfterRootRemove(bool machineStore)
    {
        if (OperatingSystem.IsWindows())
            IsRootTrusted = IsRootPresentInStore(machineStore);
        else if (OperatingSystem.IsMacOS())
            IsRootTrusted = _proxy!.CertificateManager.IsOsRootStillPresent();
        else
            IsRootTrusted = _proxy!.CertificateManager.VerifyOsUserSslTrust();
    }

    /// <summary>
    ///     After OS Root remove: optionally clear Firefox prefs, delete PFX/leaf cache, mint new root.
    ///     Safe off the UI thread (no CryptUI).
    /// </summary>
    public bool MintNewRootCertificateCore(bool clearFirefox = true)
    {
        using var scope = InspectorUxTrace.Scope("MintNewRoot", $"clearFirefox={clearFirefox}");
        if (_proxy is null)
            return false;

        if (clearFirefox)
            ClearPendingFirefoxRootTrust();
        else
            PendingFirefoxRootClearName = null;

        EnsureRootPfxPath();
        var mgr = _proxy.CertificateManager;
        using (InspectorUxTrace.Scope("MintNewRoot.ClearRootCertificate"))
            mgr.ClearRootCertificate();

        try
        {
            if (File.Exists(_rootPfxPath))
                File.Delete(_rootPfxPath);
        }
        catch
        {
            // best-effort
        }

        try
        {
            var localCrts = Path.Combine(Path.GetDirectoryName(_rootPfxPath!)!, "crts");
            if (Directory.Exists(localCrts))
                Directory.Delete(localCrts, recursive: true);
        }
        catch
        {
            // best-effort
        }

        mgr.PfxFilePath = _rootPfxPath!;
        bool ok;
        using (InspectorUxTrace.Scope("MintNewRoot.CreateRootCertificate"))
            ok = mgr.CreateRootCertificate(persistToFile: true);
        // Brand-new thumbprint cannot be in the Root store yet — do not open Crypt32 here
        // (after Remove the store is hot; a useless Find routinely stalls Clear+Install).
        IsRootTrusted = UseInMemoryTrustState && _inMemoryTrusted;

        using (InspectorUxTrace.Scope("MintNewRoot.PruneLegacySharedCrts"))
            PruneLegacySharedCrts(force: true);
        return ok && mgr.RootCertificate != null;
    }

    /// <summary>Test seam: override marker + shared-crts paths under a temp directory.</summary>
    public string? LegacyCrtsTestRoot { get; set; }

    private string LegacySharedCrtsMarkerPath()
    {
        EnsureRootPfxPath();
        var dir = LegacyCrtsTestRoot ?? Path.GetDirectoryName(_rootPfxPath!)!;
        return Path.Combine(dir, "legacy-shared-crts-cleared");
    }

    private string ResolveLegacySharedCrtsDirectory()
    {
        if (LegacyCrtsTestRoot != null)
            return Path.Combine(LegacyCrtsTestRoot, "shared-crts");
        return Titanium.Web.Proxy.Network.DefaultCertificateDiskCache.GetSharedLeafCertificateDirectory();
    }

    private void TryPruneLegacySharedCrtsOnce()
    {
        var marker = LegacySharedCrtsMarkerPath();
        if (File.Exists(marker))
            return;
        PruneLegacySharedCrts(force: false);
    }

    /// <summary>
    ///     Best-effort delete of shared <c>Titanium.Web.Proxy/crts</c> (never the shared root PFX).
    ///     When <paramref name="force"/> is false, writes the one-time Start marker.
    /// </summary>
    public void PruneLegacySharedCrts(bool force)
    {
        _ = force; // Callers pass Start vs rotate; marker write is identical.
        try
        {
            var sharedCrts = ResolveLegacySharedCrtsDirectory();
            if (Directory.Exists(sharedCrts))
                Directory.Delete(sharedCrts, recursive: true);
        }
        catch
        {
            // best-effort
        }

        // Start (force=false) and rotate (force=true) both ensure the one-time marker exists.
        try
        {
            File.WriteAllText(LegacySharedCrtsMarkerPath(), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // best-effort
        }
    }

    public bool RefreshTrustState(bool machineStore = false)
    {
        if (UseInMemoryTrustState)
        {
            IsRootTrusted = _inMemoryTrusted;
            return IsRootTrusted;
        }

        // Windows Root store presence == trust. On macOS/Linux, presence is not enough —
        // VerifyOsUserSslTrust checks Keychain/NSS SSL trust (security verify-cert / certutil).
        if (OperatingSystem.IsWindows())
        {
            IsRootTrusted = IsRootPresentInStore(machineStore);
            return IsRootTrusted;
        }

        if (_proxy is null)
        {
            IsRootTrusted = false;
            return false;
        }

        IsRootTrusted = _proxy.CertificateManager.VerifyOsUserSslTrust();
        return IsRootTrusted;
    }

    public bool IsRootPresentInStore(bool machineStore)
    {
        if (UseInMemoryTrustState)
        {
            return _inMemoryTrusted;
        }

        var cert = RootCertificate;
        if (cert is null)
        {
            return false;
        }

        try
        {
            using var store = new X509Store(StoreName.Root,
                machineStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
            return found.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public string? ExportRootCertificate(string? destinationPath = null)
    {
        var cert = RootCertificate;
        if (cert is null)
        {
            return null;
        }

        EnsureRootPfxPath();
        var path = destinationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "TitaniumInspector-RootCA.cer");
        var der = cert.Export(X509ContentType.Cert);
        if (IsPemExportPath(path))
        {
            File.WriteAllText(path, EncodeCertificatePem(der), Encoding.ASCII);
        }
        else
        {
            File.WriteAllBytes(path, der);
        }

        return path;
    }

    internal static bool IsPemExportPath(string path) =>
        Path.GetExtension(path).Equals(".pem", StringComparison.OrdinalIgnoreCase);

    internal static string EncodeCertificatePem(byte[] der)
    {
        var b64 = Convert.ToBase64String(der);
        var sb = new StringBuilder(b64.Length + 64);
        sb.Append("-----BEGIN CERTIFICATE-----\n");
        for (var i = 0; i < b64.Length; i += 64)
        {
            var len = Math.Min(64, b64.Length - i);
            sb.Append(b64, i, len);
            sb.Append('\n');
        }

        sb.Append("-----END CERTIFICATE-----\n");
        return sb.ToString();
    }

    private void EnsureRootPfxPath()
    {
        if (_rootPfxPath is not null)
        {
            return;
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector");
        Directory.CreateDirectory(dir);
        _rootPfxPath = Path.Combine(dir, "rootCert.pfx");
    }

    private Task OnBeforeTunnelConnect(object sender, TunnelConnectSessionEventArgs e)
    {
        var host = e.HttpClient.Request.RequestUri?.Host
                   ?? TryHost(e.HttpClient.Request);
        var disableDecrypt = MitmBypass.ShouldDisableSslDecrypt(
            host,
            DecryptSkipHosts,
            userOnlyHosts: null);
        var learnedBypass = !disableDecrypt && DecryptHttps && IsLearnedDecryptBypass(host);
        e.DecryptSsl = DecryptHttps && !disableDecrypt && !learnedBypass;
        var opaqueReason = learnedBypass
            ? OpaqueTunnelReason.LearnedFailure
            : disableDecrypt || !DecryptHttps
                ? MitmBypass.ResolveOpaqueReason(host, DecryptHttps, DecryptSkipHosts, userOnlyHosts: null)
                : OpaqueTunnelReason.None;

        if (!Capturing)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Opaque HTTPS (DecryptHttps=false) never hits BeforeRequest — publish CONNECT here
            // so the session list matches Fiddler when decryption is off.
            var snap = CreateTunnelSnapshot(e, opaqueReason);
            AttachTunnelByteCounters(e, snap);
            _live[e.HttpClient] = snap;
            SessionCaptured?.Invoke(this, snap);
            ScheduleProcessResolve(snap, e.HttpClient.ProcessId);
        }
        catch
        {
            // never break the proxy pipeline for capture failures
        }

        return Task.CompletedTask;
    }

    private Task OnBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
    {
        if (!_live.TryGetValue(e.HttpClient, out var snap))
        {
            return Task.CompletedTask;
        }

        try
        {
            ApplyConnectCompletion(snap, e);
            SessionUpdated?.Invoke(this, snap);
        }
        catch
        {
            // ignore
        }
        finally
        {
            // Tunnel sessions are complete after CONNECT response (no AfterResponse for opaque tunnels).
            if (!e.DecryptSsl)
            {
                _live.TryRemove(e.HttpClient, out _);
            }
        }

        return Task.CompletedTask;
    }

    private SessionSnapshot CreateTunnelSnapshot(TunnelConnectSessionEventArgs e, OpaqueTunnelReason opaqueReason)
    {
        var req = e.HttpClient.Request;

        return new SessionSnapshot
        {
            Id = NextSessionId(),
            Method = "CONNECT",
            Url = req.RequestUriString ?? req.Url ?? "",
            Host = TryHost(req),
            StartedUtc = DateTimeOffset.UtcNow,
            RequestHeadersText = FormatHeaders(req.Headers),
            Protocol = SessionDisplayFormat.FormatHttpProtocol(req.HttpVersion),
            IsTunnel = true,
            OpaqueReason = opaqueReason,
        };
    }

    private async Task OnBeforeRequest(object sender, SessionEventArgs e) // NOSONAR S3776 -- Capture pipeline (scripts, AutoResponder, breakpoints) shares session state; splitting would hide ordering.
    {
        try
        {
            // Buffer when safe. GraphQL tools must NOT force GetRequestBody past the skip
            // (huge POST would RST HTTP/2 with ENHANCE_YOUR_CALM).
            if (e.HttpClient.Request.HasBody && ShouldBufferBody(e.HttpClient.Request, e, isRequest: true))
            {
                e.HttpClient.Request.KeepBody = true;
                await e.GetRequestBody(CancellationToken.None);
            }

            if (SessionScriptHost.ApplyOnRequest(ScriptOnRequest, e))
            {
                return;
            }

            string? requestBody = null;
            var needsBodyForTools =
                (AutoResponder is { Enabled: true } && AutoResponder.Rules.Any(r => r.Enabled && !string.IsNullOrWhiteSpace(r.GraphQlOperationName))) ||
                (MapRemote is { Enabled: true } && MapRemote.Rules.Any(r => r.Enabled && !string.IsNullOrWhiteSpace(r.GraphQlOperationName))) ||
                (Breakpoints is { Enabled: true } && !string.IsNullOrWhiteSpace(Breakpoints.GraphQlOperationName));
            if (needsBodyForTools && e.HttpClient.Request.IsBodyRead)
            {
                requestBody = await e.GetRequestBodyAsString(CancellationToken.None);
            }

            var requestUrl = e.HttpClient.Request.Url ?? "";

            // AutoResponder / Map Local before breakpoints / origin.
            var autoResponded = false;
            if (AutoResponder is not null &&
                AutoResponder.TryMatch(requestUrl, requestBody, out var rule) &&
                rule is not null &&
                AutoResponderViewModel.TryResolveResponse(rule, out var inlineBody, out var mapLocalPath, out _, out _))
            {
                if (mapLocalPath is not null)
                {
                    e.RespondStreaming(
                        ProxyResults.File(mapLocalPath, rule.ContentType, (HttpStatusCode)rule.StatusCode),
                        closeServerConnection: false);
                }
                else
                {
                    var headers = new List<HttpHeader>
                    {
                        new("Content-Type", rule.ContentType),
                    };
                    e.GenericResponse(inlineBody ?? Array.Empty<byte>(), (HttpStatusCode)rule.StatusCode, headers);
                }

                autoResponded = true;
            }

            // Map Remote: rewrite URL before origin (only when not already answered).
            if (!autoResponded &&
                MapRemote is not null &&
                MapRemote.TryRewrite(requestUrl, requestBody, out var rewritten, out _) &&
                !string.IsNullOrEmpty(rewritten))
            {
                e.HttpClient.Request.Url = rewritten;
            }

            if (Breakpoints is { Enabled: true } &&
                (string.IsNullOrWhiteSpace(Breakpoints.GraphQlOperationName) ||
                 GraphQlOperationMatcher.MatchesOperation(requestBody, Breakpoints.GraphQlOperationName)) &&
                Breakpoints.TryEnter(CreatePreviewSnapshot(e, assignId: false), out var hit))
            {
                var action = await hit.WaitAsync(CancellationToken.None);
                if (action == BreakpointAction.Abort)
                {
                    e.GenericResponse("Aborted by Titanium Inspector breakpoint", HttpStatusCode.Forbidden);
                    return;
                }

                if (hit.EditedBody is not null)
                {
                    e.SetRequestBodyString(hit.EditedBody);
                }
            }

            if (!Capturing)
            {
                return;
            }

            var snap = CreatePreviewSnapshot(e, assignId: true);
            _live[e.HttpClient] = snap;
            SessionCaptured?.Invoke(this, snap);
            ScheduleProcessResolve(snap, e.HttpClient.ProcessId);
        }
        catch (Exception)
        {
            // never break the proxy pipeline for capture failures
        }
    }

    private async Task OnBeforeResponse(object sender, SessionEventArgs e)
    {
        try
        {
            if (e.HttpClient.Response.HasBody && ShouldBufferBody(e.HttpClient.Response, e, isRequest: false))
            {
                e.HttpClient.Response.KeepBody = true;
                await e.GetResponseBody(CancellationToken.None);
            }

            SessionScriptHost.ApplyOnResponse(ScriptOnResponse, e);

            if (BreakpointOnResponse &&
                Breakpoints is { Enabled: true } &&
                Breakpoints.TryEnter(CreatePreviewSnapshot(e, assignId: false), out var hit))
            {
                var action = await hit.WaitAsync(CancellationToken.None);
                if (action == BreakpointAction.Abort)
                {
                    e.GenericResponse("Aborted by Titanium Inspector response breakpoint", HttpStatusCode.Forbidden);
                    return;
                }

                if (hit.EditedBody is not null)
                {
                    e.SetResponseBodyString(hit.EditedBody);
                }
            }

            if (!_live.TryGetValue(e.HttpClient, out var snap))
            {
                if (!Capturing)
                {
                    return;
                }

                snap = CreatePreviewSnapshot(e, assignId: true);
                _live[e.HttpClient] = snap;
                SessionCaptured?.Invoke(this, snap);
                ScheduleProcessResolve(snap, e.HttpClient.ProcessId);
            }

            FillResponse(snap, e);
            SessionUpdated?.Invoke(this, snap);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private Task OnAfterResponse(object sender, SessionEventArgs e)
    {
        if (_live.TryGetValue(e.HttpClient, out var snap))
        {
            FinalizeStreamingBody(snap);
            ApplyTiming(snap, e.Timing, snap.StartedUtc);
            SessionUpdated?.Invoke(this, snap);
        }

        _live.TryRemove(e.HttpClient, out _);
        return Task.CompletedTask;
    }

    private Task OnServerCertValidation(object sender, CertificateValidationEventArgs e)
    {
        // When a callback is subscribed, Core returns args.IsValid only (default false).
        // Accept valid public chains; optionally ignore all errors when the user opts in.
        e.IsValid = IgnoreServerCertificateErrors
                    || e.SslPolicyErrors == SslPolicyErrors.None;
        return Task.CompletedTask;
    }

    private SessionSnapshot CreatePreviewSnapshot(SessionEventArgs e, bool assignId)
    {
        var req = e.HttpClient.Request;
        var originalBody = req.IsBodyRead ? req.Body : null;
        var bodyBytes = InspectorBodyLimits.TruncateBytes(originalBody);
        var bodyText = bodyBytes is null ? null : InspectorBodyLimits.TruncateText(Encoding.UTF8.GetString(bodyBytes));
        GrpcJsonTranscodeSessionMark.TryGet(e.UserData, out var mark);

        var snap = new SessionSnapshot
        {
            Id = assignId ? NextSessionId() : 0,
            Method = mark?.ClientMethod ?? req.Method ?? "GET",
            Url = BuildDisplayUrl(req, mark),
            Host = TryHost(req),
            StartedUtc = DateTimeOffset.UtcNow,
            RequestHeadersText = FormatHeaders(req.Headers),
            RequestBodyBytes = bodyBytes,
            RequestBodyText = bodyText,
            ContentType = mark?.ClientContentType ?? req.ContentType,
            Protocol = SessionDisplayFormat.FormatHttpProtocol(req.HttpVersion),
            IsTunnel = req.Method?.Equals("CONNECT", StringComparison.OrdinalIgnoreCase) == true,
            IsWebSocket = req.UpgradeToWebSocket,
            IsGrpc = req.ContentType?.Contains("grpc", StringComparison.OrdinalIgnoreCase) == true ||
                     mark is not null,
            IsTranscoded = mark is not null,
            IsMultipart = req.ContentType?.Contains("multipart/", StringComparison.OrdinalIgnoreCase) == true,
            IsServerSentEvents =
                (req.Headers.GetFirstHeader("Accept")?.Value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true),
        };

        ApplyRequestBodyCapture(snap, req, originalBody);
        ApplyTranscodeMark(snap, mark);
        if (mark?.ClientRequestBody is { Length: > 0 } clientBody)
        {
            snap.RequestBodyBytes = InspectorBodyLimits.TruncateBytes(clientBody);
            snap.RequestBodyText = InspectorBodyLimits.TruncateText(Encoding.UTF8.GetString(clientBody));
            snap.RequestBodyOriginalSize = clientBody.LongLength;
            snap.RequestBodyCapture = clientBody.Length > MaxBodyBytes
                ? BodyCaptureState.Truncated
                : BodyCaptureState.Complete;
        }

        if (mark?.UpstreamRequestBody is { Length: > 0 } upstreamReq)
        {
            snap.UpstreamRequestBodyBytes = InspectorBodyLimits.TruncateBytes(upstreamReq);
            snap.GrpcFrames = ProtocolFrameInspectors.ParseGrpcFrames(snap.UpstreamRequestBodyBytes);
            snap.ProtobufDecodedText = ProtobufMessageDecoder.DecodeWireFormat(snap.UpstreamRequestBodyBytes);
        }

        if (assignId && snap.IsWebSocket)
        {
            AttachLiveWebSocketFrames(e, snap);
        }

        return snap;
    }

    private static void ApplyTranscodeMark(SessionSnapshot snap, GrpcJsonTranscodeSessionMark? mark)
    {
        if (mark is null) return;
        snap.IsTranscoded = true;
        snap.ClientMethod = mark.ClientMethod;
        snap.ClientPathAndQuery = mark.ClientPathAndQuery;
        snap.ClientContentType = mark.ClientContentType;
        snap.UpstreamMethod = mark.UpstreamMethod;
        snap.UpstreamPath = mark.UpstreamPath;
        snap.UpstreamContentType = mark.UpstreamContentType;
    }

    private static string BuildDisplayUrl(Request req, GrpcJsonTranscodeSessionMark? mark)
    {
        if (mark is null)
            return req.Url ?? "";

        // Prefer absolute URL with client path when available.
        var url = req.Url ?? "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            var builder = new UriBuilder(abs)
            {
                Path = mark.ClientPathAndQuery.Split('?', 2)[0],
                Query = mark.ClientPathAndQuery.Contains('?', StringComparison.Ordinal)
                    ? mark.ClientPathAndQuery.Split('?', 2)[1]
                    : string.Empty
            };
            return builder.Uri.ToString();
        }

        return mark.ClientPathAndQuery;
    }

    private long NextSessionId() => Interlocked.Increment(ref _nextId);

    /// <summary>Reset the session ID sequence (tests / clear-sessions).</summary>
    public void ResetSessionIdSequence() => Interlocked.Exchange(ref _nextId, 0);

    private void StartProcessResolveWorker()
    {
        StopProcessResolveWorker();
        if (!ClientProcessId.IsSupported)
        {
            return;
        }

        var channel = Channel.CreateUnbounded<ProcessResolveWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        var cts = new CancellationTokenSource();
        _processResolveChannel = channel;
        _processResolveCts = cts;
        _ = Task.Run(() => ProcessResolveLoopAsync(channel.Reader, cts.Token), cts.Token);
    }

    private void StopProcessResolveWorker()
    {
        var cts = _processResolveCts;
        var channel = _processResolveChannel;
        _processResolveCts = null;
        _processResolveChannel = null;

        try
        {
            channel?.Writer.TryComplete();
        }
        catch
        {
            // ignore
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        cts?.Dispose();
    }

    private void ScheduleProcessResolve(SessionSnapshot snap, Lazy<int> processId)
    {
        var channel = _processResolveChannel;
        if (channel is null)
        {
            return;
        }

        channel.Writer.TryWrite(new ProcessResolveWork(snap, processId));
    }

    private async Task ProcessResolveLoopAsync(
        ChannelReader<ProcessResolveWork> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    ApplyResolvedProcess(work);
                }
                catch
                {
                    // never break the resolve loop for a single session
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }

    private void ApplyResolvedProcess(ProcessResolveWork work)
    {
        var processId = work.ProcessId.Value;
        if (processId <= 0)
            return;

        string? processName = null;
        try
        {
            processName = System.Diagnostics.Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            // process may have exited; keep pid when known
        }

        if (work.Snap.ProcessId == processId &&
            string.Equals(work.Snap.ProcessName, processName, StringComparison.Ordinal))
        {
            return;
        }

        work.Snap.ProcessId = processId;
        work.Snap.ProcessName = processName;
        SessionUpdated?.Invoke(this, work.Snap);
    }

    private static void FillResponse(SessionSnapshot snap, SessionEventArgs e) // NOSONAR S3776 -- Snapshot fill walks protocol-specific body/header branches in one place.
    {
        var resp = e.HttpClient.Response;
        snap.StatusCode = resp.StatusCode;
        snap.ResponseHeadersText = FormatHeaders(resp.Headers);
        snap.Protocol = SessionDisplayFormat.FormatClientServer(
            e.HttpClient.Request.HttpVersion, resp.HttpVersion);
        var originalBody = resp.IsBodyRead ? resp.Body : null;
        var bodyBytes = InspectorBodyLimits.TruncateBytes(originalBody);
        snap.ResponseBodyBytes = bodyBytes;
        snap.ResponseBodyText = bodyBytes is null ? null : InspectorBodyLimits.TruncateText(Encoding.UTF8.GetString(bodyBytes));
        ApplyResponseBodyCapture(snap, resp, e.HttpClient.Request, originalBody);

        ApplyTiming(snap, e.Timing, snap.StartedUtc);

        if (GrpcJsonTranscodeSessionMark.TryGet(e.UserData, out var mark) && mark is not null)
        {
            ApplyTranscodeMark(snap, mark);
            if (mark.UpstreamResponseBody is { Length: > 0 } upstreamResp)
            {
                snap.UpstreamResponseBodyBytes = InspectorBodyLimits.TruncateBytes(upstreamResp);
                snap.GrpcFrames = ProtocolFrameInspectors.ParseGrpcFrames(snap.UpstreamResponseBodyBytes);
            }
        }

        if (snap.IsWebSocket)
        {
            // Prefer live frames when present; otherwise best-effort parse.
            snap.WebSocketFrames ??= ProtocolFrameInspectors.ParseWebSocketFrames(bodyBytes ?? snap.RequestBodyBytes);
        }

        var contentType = resp.ContentType ?? snap.ContentType ?? "";
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
            snap.IsServerSentEvents)
        {
            snap.IsServerSentEvents = true;
            snap.SseEvents = SseEventParser.Parse(snap.ResponseBodyText);
        }

        if (snap.IsGrpc && !snap.IsTranscoded && bodyBytes is { Length: > 0 })
        {
            snap.GrpcFrames = ProtocolFrameInspectors.ParseGrpcFrames(bodyBytes);
            snap.ProtobufDecodedText = ProtobufMessageDecoder.DecodeWireFormat(bodyBytes);
        }

        if (snap.IsTranscoded && snap.UpstreamResponseBodyBytes is { Length: > 0 })
        {
            snap.ProtobufDecodedText = ProtobufMessageDecoder.DecodeWireFormat(snap.UpstreamResponseBodyBytes);
        }

        if (snap.IsMultipart && bodyBytes is { Length: > 0 })
        {
            snap.MultipartParts = ProtocolFrameInspectors.ParseMultipart(snap.ContentType, bodyBytes);
        }
    }

    private void AttachLiveWebSocketFrames(SessionEventArgs e, SessionSnapshot snap)
    {
        var frames = new List<WebSocketFrameSnapshot>();
        snap.WebSocketFrames = frames;
        e.BeforeWebSocketFrame += (_, args) =>
        {
            var direction = args.Direction == WebSocketFrameDirection.ClientToServer ? "Client" : "Server";
            var opcode = args.OpCode.ToString();
            frames.Add(ProtocolFrameInspectors.FromLiveFrame(direction, opcode, args.Data));
            var profile = ThrottleProfile;
            if (profile is { IsEnabled: true })
            {
                args.Delay = NetworkThrottle.DelayFor(profile, args.Data.Length, applyLatency: true);
            }

            SessionUpdated?.Invoke(this, snap);
            return Task.CompletedTask;
        };
    }

    private async Task OnRequestBodyWriteThrottle(object sender, BeforeBodyWriteEventArgs e)
    {
        var profile = ThrottleProfile;
        if (profile is { IsEnabled: true })
        {
            var delay = NetworkThrottle.DelayFor(profile, e.BodyBytes?.Length ?? 0, applyLatency: !e.IsChunked || e.BodyBytes?.Length > 0);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _processResolveCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task OnResponseBodyWriteThrottle(object sender, BeforeBodyWriteEventArgs e)
    {
        var profile = ThrottleProfile;
        if (profile is { IsEnabled: true })
        {
            var delay = NetworkThrottle.DelayFor(profile, e.BodyBytes?.Length ?? 0, applyLatency: true);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _processResolveCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Preview tee only for streamed SSE (not buffered). Do not tee NotCaptured huge downloads.
        if (e.Session.HttpClient.Response.IsBodyRead)
        {
            return;
        }

        if (!_live.TryGetValue(e.Session.HttpClient, out var snap))
        {
            return;
        }

        if (snap.ResponseBodyCapture != BodyCaptureState.Streaming)
        {
            return;
        }

        TeeResponseChunk(snap, e);
    }

    private void TeeResponseChunk(SessionSnapshot snap, BeforeBodyWriteEventArgs e)
    {
        var chunk = e.BodyBytes;
        var len = chunk?.Length ?? 0;
        if (len > 0)
        {
            snap.ResponseBytesSeen += len;
            snap.ResponseBodyOriginalSize = snap.ResponseBytesSeen;
            snap.BodySize = snap.ResponseBytesSeen;

            var tee = snap.ResponseTeeStream;
            if (tee is null)
            {
                tee = new MemoryStream(Math.Min(MaxBodyBytes, Math.Max(len, 4096)));
                snap.ResponseTeeStream = tee;
            }

            if (tee.Length < MaxBodyBytes)
            {
                var toWrite = (int)Math.Min(len, MaxBodyBytes - tee.Length);
                tee.Write(chunk!, 0, toWrite);
            }
        }

        if (e.IsLastChunk)
        {
            FinalizeStreamingBody(snap);
            SessionUpdated?.Invoke(this, snap);
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        var last = snap.LastTeeUiUtcTicks;
        if (last != 0 && (now - last) < TimeSpan.FromMilliseconds(InspectorBodyLimits.TeeUiCoalesceMs).Ticks)
        {
            return;
        }

        snap.LastTeeUiUtcTicks = now;
        PublishTeePreview(snap);
        SessionUpdated?.Invoke(this, snap);
    }

    private static void PublishTeePreview(SessionSnapshot snap)
    {
        var tee = snap.ResponseTeeStream;
        if (tee is null || tee.Length == 0)
        {
            return;
        }

        var bytes = tee.ToArray();
        snap.ResponseBodyBytes = bytes;
        snap.ResponseBodyText = InspectorBodyLimits.TruncateText(Encoding.UTF8.GetString(bytes));
        if (snap.IsServerSentEvents)
        {
            snap.SseEvents = SseEventParser.Parse(snap.ResponseBodyText);
        }
    }

    private static void FinalizeStreamingBody(SessionSnapshot snap)
    {
        if (snap.ResponseBodyCapture != BodyCaptureState.Streaming)
        {
            snap.ResponseTeeStream?.Dispose();
            snap.ResponseTeeStream = null;
            return;
        }

        PublishTeePreview(snap);
        snap.ResponseBodyStreamOpen = false;
        var captured = snap.ResponseBodyBytes?.LongLength ?? 0;
        if (snap.ResponseBytesSeen > captured && captured >= MaxBodyBytes)
        {
            snap.ResponseBodyCapture = BodyCaptureState.Truncated;
        }
        else if (captured > 0 && snap.ResponseBytesSeen <= MaxBodyBytes)
        {
            snap.ResponseBodyCapture = BodyCaptureState.Complete;
        }

        snap.ResponseBodyOriginalSize = snap.ResponseBytesSeen > 0
            ? snap.ResponseBytesSeen
            : snap.ResponseBodyOriginalSize;
        if (snap.ResponseBytesSeen > 0)
        {
            snap.BodySize = snap.ResponseBytesSeen;
        }

        snap.ResponseTeeStream?.Dispose();
        snap.ResponseTeeStream = null;
    }

    private static void ApplyRequestBodyCapture(SessionSnapshot snap, Request req, byte[]? originalBody)
    {
        if (originalBody is { Length: >= 0 } && req.IsBodyRead)
        {
            snap.RequestBodyOriginalSize = originalBody.LongLength;
            snap.RequestBodyCapture = originalBody.Length > MaxBodyBytes
                ? BodyCaptureState.Truncated
                : BodyCaptureState.Complete;
            return;
        }

        if (!req.HasBody)
        {
            snap.RequestBodyCapture = BodyCaptureState.None;
            return;
        }

        var limit = InspectorBodyLimits.MaxMapLocalFileBytes;
        if (req.ContentLength > limit)
        {
            snap.RequestBodyCapture = BodyCaptureState.NotCaptured;
            snap.RequestBodyOriginalSize = req.ContentLength;
            return;
        }

        snap.RequestBodyCapture = BodyCaptureState.None;
    }

    private static void ApplyResponseBodyCapture(
        SessionSnapshot snap,
        Response resp,
        Request req,
        byte[]? originalBody)
    {
        var contentType = resp.ContentType ?? snap.ContentType ?? "";
        var isSse = InspectorBodyLimits.LooksLikeSseContentType(contentType)
                    || snap.IsServerSentEvents;
        if (isSse)
        {
            snap.IsServerSentEvents = true;
        }

        if (originalBody is not null && resp.IsBodyRead)
        {
            snap.ResponseBodyOriginalSize = originalBody.LongLength;
            snap.ResponseBodyCapture = originalBody.Length > MaxBodyBytes
                ? BodyCaptureState.Truncated
                : BodyCaptureState.Complete;
            snap.BodySize = originalBody.LongLength;
            snap.ResponseBodyStreamOpen = false;
            return;
        }

        if (req.UpgradeToWebSocket)
        {
            snap.ResponseBodyCapture = BodyCaptureState.None;
            snap.BodySize ??= resp.ContentLength >= 0 ? resp.ContentLength : null;
            return;
        }

        if (isSse)
        {
            snap.ResponseBodyCapture = BodyCaptureState.Streaming;
            snap.ResponseBodyStreamOpen = true;
            snap.BodySize = snap.ResponseBytesSeen > 0 ? snap.ResponseBytesSeen : null;
            return;
        }

        if (resp.HasBody && resp.ContentLength > InspectorBodyLimits.MaxMapLocalFileBytes)
        {
            snap.ResponseBodyCapture = BodyCaptureState.NotCaptured;
            snap.ResponseBodyOriginalSize = resp.ContentLength;
            snap.BodySize = resp.ContentLength;
            return;
        }

        if (resp.HasBody && !resp.IsBodyRead)
        {
            // Should not happen for finite bodies we chose to buffer; treat as not captured.
            snap.ResponseBodyCapture = BodyCaptureState.NotCaptured;
            snap.ResponseBodyOriginalSize = resp.ContentLength >= 0 ? resp.ContentLength : null;
            snap.BodySize = snap.ResponseBodyOriginalSize;
            return;
        }

        snap.ResponseBodyCapture = BodyCaptureState.None;
        snap.BodySize ??= resp.ContentLength >= 0 ? resp.ContentLength : null;
    }

    private static string? TryHost(Request req)
    {
        try
        {
            return req.RequestUri?.Host ?? req.Host;
        }
        catch
        {
            return req.Host;
        }
    }

    private static void ApplyConnectCompletion(SessionSnapshot snap, TunnelConnectSessionEventArgs e)
    {
        var resp = e.HttpClient.Response;
        snap.StatusCode = resp.StatusCode != 0 ? resp.StatusCode : 200;
        snap.ResponseHeadersText = FormatHeaders(resp.Headers);
        snap.Protocol = SessionDisplayFormat.FormatClientServer(
            e.HttpClient.Request.HttpVersion, resp.HttpVersion);
        ApplyTiming(snap, e.Timing, snap.StartedUtc);
        snap.TtfbMs ??= snap.DurationMs;
        snap.BodySize ??= 0;
    }

    private static void AttachTunnelByteCounters(TunnelConnectSessionEventArgs e, SessionSnapshot snap)
    {
        e.DataSent += (_, args) => AddTunnelBytes(snap, sent: args.Count, received: 0);
        e.DataReceived += (_, args) => AddTunnelBytes(snap, sent: 0, received: args.Count);
    }

    private static void AddTunnelBytes(SessionSnapshot snap, int sent, int received)
    {
        if (sent != 0)
        {
            snap.SentBytes += sent;
        }

        if (received != 0)
        {
            snap.ReceivedBytes += received;
        }

        snap.BodySize = snap.SentBytes + snap.ReceivedBytes;
    }

    private static void ApplyTiming(SessionSnapshot snap, HttpRequestTiming? timing, DateTimeOffset startedUtc)
    {
        if (timing is not null)
        {
            snap.DurationMs = SessionDisplayFormat.RoundMs(timing.TotalDuration.TotalMilliseconds);
            if (timing.TimeToFirstByte is TimeSpan ttfb)
            {
                snap.TtfbMs = SessionDisplayFormat.RoundMs(ttfb.TotalMilliseconds);
            }

            return;
        }

        snap.DurationMs = SessionDisplayFormat.RoundMs(
            Math.Max(0, (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds));
    }

    private static string FormatHeaders(HeaderCollection headers)
    {
        var sb = new StringBuilder();
        foreach (var h in headers)
        {
            sb.Append(h.Name).Append(": ").Append(h.Value).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Whole-body buffering for the session grid must not run when Content-Length already
    ///     exceeds <see cref="ProxyServer.MaxBufferedBodyBytes" /> — that path RSTs HTTP/2 streams
    ///     with ENHANCE_YOUR_CALM and breaks the browser download. SSE and WebSocket upgrades are
    ///     never buffered (relay + optional 2 MiB tee). Finite unknown-length (chunked) bodies still
    ///     buffer up to the limit so gzip JSON can be inspected.
    /// </summary>
    private bool ShouldBufferBody(RequestResponseBase message, SessionEventArgs session, bool isRequest)
    {
        if (LooksLikeEndlessStream(message, session, isRequest))
        {
            return false;
        }

        var limit = session.MaxBufferedBodyBytes ?? _proxy?.MaxBufferedBodyBytes ?? (4 * 1024 * 1024);
        if (limit <= 0)
        {
            return true;
        }

        var contentLength = message.ContentLength;
        return contentLength < 0 || contentLength <= limit;
    }

    private static bool LooksLikeEndlessStream(RequestResponseBase message, SessionEventArgs session, bool isRequest)
    {
        if (session.HttpClient.Request.UpgradeToWebSocket)
        {
            return true;
        }

        if (!isRequest && InspectorBodyLimits.LooksLikeSseContentType(message.ContentType))
        {
            return true;
        }

        return false;
    }

    private static byte[]? TruncateBytes(byte[]? body) => InspectorBodyLimits.TruncateBytes(body);

    private static string TruncateText(string text) => InspectorBodyLimits.TruncateText(text);

    public void Dispose() => EnsureShutdown();
}
