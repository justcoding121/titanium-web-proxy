using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Services;

/// <summary>
/// Explicit MITM capture: full request/response bodies, system proxy, CA trust/export,
/// capture pause, AutoResponder, breakpoints, and light script hooks.
/// </summary>
public sealed class InterceptionService : IDisposable
{
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    public const int MaxBodyTextChars = 256 * 1024;

    private static long _nextId = 1;
    private readonly ConcurrentDictionary<object, SessionSnapshot> _live = new();
    private readonly ISystemProxyController _systemProxy;
    private ProxyServer? _proxy;
    private ExplicitProxyEndPoint? _endPoint;
    private bool _systemProxyEnabled;
    private int _shutdownStarted;
    private readonly ManualResetEventSlim _shutdownCompleted = new(false);
    private string? _rootPfxPath;
    private InspectorSettings? _loggingSettings;

    public InterceptionService(ISystemProxyController? systemProxy = null)
    {
        _systemProxy = systemProxy ?? new ProxyServerSystemProxyController();
    }

    public bool IsRunning => _proxy?.ProxyRunning == true;

    /// <summary>When false, the listener stays up but sessions are not published to the grid.</summary>
    public bool Capturing { get; set; } = true;

    /// <summary>
    /// When false, HTTPS CONNECT tunnels stay opaque (no MITM). Endpoint still has decrypt capability;
    /// <see cref="OnBeforeTunnelConnect"/> gates per-request <c>DecryptSsl</c>.
    /// </summary>
    public bool DecryptHttps { get; set; }

    /// <summary>Extra host patterns that skip HTTPS decryption (in addition to built-in bypasses).</summary>
    public List<string> DecryptSkipHosts { get; set; } = [];

    /// <summary>When non-empty, only these hosts are decrypted (built-in bypasses still never decrypt).</summary>
    public List<string> DecryptOnlyHosts { get; set; } = [];

    /// <summary>True when the OS can host QUIC (MsQuic / <c>QuicListener.IsSupported</c>).</summary>
    public static bool IsHttp3Supported => System.Net.Quic.QuicListener.IsSupported;

    /// <summary>
    /// When set (tests), skip the Windows certificate store and track trust in-memory.
    /// Avoids modal "Root Certificate Store" UI that hangs headless / CI runs.
    /// </summary>
    public bool UseInMemoryTrustState { get; set; }

    /// <summary>Test seam: next <see cref="InstallRootCertificate"/> returns false once (forces elevate path).</summary>
    public bool FailNextUserTrustInstall { get; set; }

    private bool _inMemoryTrusted;

    public bool SystemProxyEnabled => _systemProxyEnabled;
    public X509Certificate2? RootCertificate => _proxy?.CertificateManager.RootCertificate;
    public bool IsRootTrusted { get; private set; }

    /// <summary>True when the running proxy currently allows HTTP/2.</summary>
    public bool Http2Enabled { get; private set; } = true;

    /// <summary>True when this capture session has HTTP/3 enabled (MsQuic available).</summary>
    public bool Http3Enabled { get; private set; }
    public string? UpstreamProxyAddress { get; set; }
    public string? PacUrl { get; set; }
    public bool IgnoreServerCertificateErrors { get; set; }

    /// <summary>When true, call <see cref="InstallRootCertificate"/> after start (explicit trust).</summary>
    public bool AutoTrustRootOnStart { get; set; }

    public AutoResponderViewModel? AutoResponder { get; set; }
    public BreakpointViewModel? Breakpoints { get; set; }

    /// <summary>When true, breakpoints also fire on BeforeResponse.</summary>
    public bool BreakpointOnResponse { get; set; }

    /// <summary>Optional light request script (set-header / set-status / abort).</summary>
    public string? ScriptOnRequest { get; set; }

    /// <summary>Optional light response script (set-header / set-status / abort).</summary>
    public string? ScriptOnResponse { get; set; }

    public event EventHandler<SessionSnapshot>? SessionCaptured;
    public event EventHandler<SessionSnapshot>? SessionUpdated;

    public async Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_proxy is not null)
        {
            return;
        }

        // Explicit trust flags: do not silently install into the user store on start.
        // Callers must InstallRootCertificate (or set AutoTrustRootOnStart) so UI can report success/failure.
        _proxy = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false);
        ApplyLoggingOptions(_loggingSettings);
        _proxy.EnableHttpInterception = true;
        _proxy.EnableRequestTimingCapture = true;
        // Inspector eagerly buffers bodies for the session grid; 4 MiB trips too often on
        // normal browsing (images, JS bundles) and RST'd the H2 stream. 32 MiB still bounds
        // memory while covering typical inspected payloads.
        _proxy.MaxBufferedBodyBytes = 32 * 1024 * 1024;
        ApplyHttpProtocols();
        _proxy.BeforeRequest += OnBeforeRequest;
        _proxy.BeforeResponse += OnBeforeResponse;
        _proxy.AfterResponse += OnAfterResponse;
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
        _proxy.Start();

        IsRootTrusted = UseInMemoryTrustState ? _inMemoryTrusted : IsRootPresentInStore(machineStore: false);

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
            _shutdownCompleted.Wait(TimeSpan.FromSeconds(3));
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
        _proxy.ServerCertificateValidationCallback -= OnServerCertValidation;
        if (_endPoint is not null)
        {
            _endPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnect;
            _endPoint.BeforeTunnelConnectResponse -= OnBeforeTunnelConnectResponse;
        }

        _proxy.Stop();
        _proxy.Dispose();
        _proxy = null;
        _endPoint = null;
        _live.Clear();
        IsRootTrusted = false;
        _systemProxyEnabled = false;
        Http3Enabled = false;
    }

    /// <summary>
    /// Enable or disable system proxy. Returns false if the proxy is not running or the underlying call failed.
    /// </summary>
    public bool SetSystemProxy(bool enable)
    {
        if (_proxy is null || _endPoint is null || !_proxy.ProxyRunning)
        {
            return false;
        }

        try
        {
            if (enable)
            {
                _systemProxy.SetAsSystemProxy(_proxy, _endPoint);
                _systemProxyEnabled = true;
            }
            else
            {
                _systemProxy.RestoreOriginalProxySettings(_proxy);
                _systemProxyEnabled = false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Install root CA and refresh <see cref="IsRootTrusted"/> from the store.</summary>
    /// <returns>True when the cert is present in the target Root store after install.</returns>
    public bool InstallRootCertificate(bool machineStore)
    {
        if (_proxy is null)
        {
            return false;
        }

        if (FailNextUserTrustInstall)
        {
            FailNextUserTrustInstall = false;
            return false;
        }

        if (UseInMemoryTrustState)
        {
            _inMemoryTrusted = true;
            IsRootTrusted = true;
            return true;
        }

        _proxy.CertificateManager.TrustRootCertificate(machineStore);
        IsRootTrusted = IsRootPresentInStore(machineStore);
        return IsRootTrusted;
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
            return true;
        }

        var ok = _proxy.CertificateManager.TrustRootCertificateAsAdmin(machineStore);
        // On non-Windows, OS trust may succeed even when X509Store presence checks are incomplete.
        IsRootTrusted = ok && (IsRootPresentInStore(machineStore) || !OperatingSystem.IsWindows());
        if (ok && !IsRootTrusted)
            IsRootTrusted = true;
        return IsRootTrusted;
    }

    public void UntrustRootCertificate(bool machineStore)
    {
        if (_proxy is null)
        {
            return;
        }

        if (UseInMemoryTrustState)
        {
            _inMemoryTrusted = false;
            IsRootTrusted = false;
            return;
        }

        _proxy.CertificateManager.RemoveTrustedRootCertificate(machineStore);
        IsRootTrusted = IsRootPresentInStore(machineStore);
    }

    /// <summary>
    ///     Mint a new root CA: untrust same-CN store entries, delete Inspector PFX + local leaf cache,
    ///     recreate root. Always best-effort prunes the legacy shared <c>Titanium.Web.Proxy/crts</c> folder.
    ///     Does not install trust — caller should prompt Install CA.
    /// </summary>
    public bool RotateRootCertificate(bool machineStore)
    {
        if (_proxy is null)
            return false;

        EnsureRootPfxPath();
        var mgr = _proxy.CertificateManager;

        if (!UseInMemoryTrustState)
            mgr.RemoveTrustedRootCertificate(machineStore);
        else
        {
            _inMemoryTrusted = false;
            IsRootTrusted = false;
        }

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
        var ok = mgr.CreateRootCertificate(persistToFile: true);
        IsRootTrusted = UseInMemoryTrustState ? false : IsRootPresentInStore(machineStore);

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

        if (!force)
        {
            try
            {
                File.WriteAllText(LegacySharedCrtsMarkerPath(), DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // best-effort
            }
        }
        else
        {
            // Rotate always prunes; also ensure marker exists so Start won't re-hit aggressively.
            try
            {
                File.WriteAllText(LegacySharedCrtsMarkerPath(), DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // best-effort
            }
        }
    }

    public bool RefreshTrustState(bool machineStore = false)
    {
        IsRootTrusted = UseInMemoryTrustState ? _inMemoryTrusted : IsRootPresentInStore(machineStore);
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
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return path;
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
        e.DecryptSsl = DecryptHttps && !MitmBypass.ShouldDisableSslDecrypt(
            host,
            DecryptSkipHosts,
            DecryptOnlyHosts);

        if (!Capturing)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Opaque HTTPS (DecryptHttps=false) never hits BeforeRequest — publish CONNECT here
            // so the session list matches Fiddler when decryption is off.
            var snap = CreateTunnelSnapshot(e);
            AttachTunnelByteCounters(e, snap);
            _live[e.HttpClient] = snap;
            SessionCaptured?.Invoke(this, snap);
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

    private static SessionSnapshot CreateTunnelSnapshot(TunnelConnectSessionEventArgs e)
    {
        var req = e.HttpClient.Request;
        var processId = 0;
        string? processName = null;
        try
        {
            processId = e.HttpClient.ProcessId.Value;
            if (processId > 0)
            {
                processName = System.Diagnostics.Process.GetProcessById(processId).ProcessName;
            }
        }
        catch
        {
            // process may have exited
        }

        return new SessionSnapshot
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = "CONNECT",
            Url = req.RequestUriString ?? req.Url ?? "",
            Host = TryHost(req),
            StartedUtc = DateTimeOffset.UtcNow,
            RequestHeadersText = FormatHeaders(req.Headers),
            Protocol = SessionDisplayFormat.FormatHttpProtocol(req.HttpVersion),
            ProcessId = processId,
            ProcessName = processName,
            IsTunnel = true,
        };
    }

    private async Task OnBeforeRequest(object sender, SessionEventArgs e)
    {
        try
        {
            if (e.HttpClient.Request.HasBody && ShouldBufferBody(e.HttpClient.Request, e))
            {
                e.HttpClient.Request.KeepBody = true;
                await e.GetRequestBody();
            }

            if (SessionScriptHost.ApplyOnRequest(ScriptOnRequest, e))
            {
                return;
            }

            // AutoResponder before breakpoints / origin.
            if (AutoResponder is not null &&
                AutoResponder.TryMatch(e.HttpClient.Request.Url ?? "", out var rule) &&
                rule is not null)
            {
                var headers = new List<HttpHeader>
                {
                    new("Content-Type", rule.ContentType),
                };
                e.GenericResponse(rule.Body, (HttpStatusCode)rule.StatusCode, headers);
            }

            if (Breakpoints is not null &&
                Breakpoints.TryEnter(CreatePreviewSnapshot(e), out var hit))
            {
                var action = await hit.WaitAsync();
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

            var snap = CreatePreviewSnapshot(e);
            _live[e.HttpClient] = snap;
            SessionCaptured?.Invoke(this, snap);
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
            if (e.HttpClient.Response.HasBody && ShouldBufferBody(e.HttpClient.Response, e))
            {
                e.HttpClient.Response.KeepBody = true;
                await e.GetResponseBody();
            }

            SessionScriptHost.ApplyOnResponse(ScriptOnResponse, e);

            if (BreakpointOnResponse &&
                Breakpoints is not null &&
                Breakpoints.TryEnter(CreatePreviewSnapshot(e), out var hit))
            {
                var action = await hit.WaitAsync();
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

                snap = CreatePreviewSnapshot(e);
                _live[e.HttpClient] = snap;
                SessionCaptured?.Invoke(this, snap);
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

    private static SessionSnapshot CreatePreviewSnapshot(SessionEventArgs e)
    {
        var req = e.HttpClient.Request;
        var bodyBytes = req.IsBodyRead ? TruncateBytes(req.Body) : null;
        var bodyText = bodyBytes is null ? null : TruncateText(Encoding.UTF8.GetString(bodyBytes));
        var processId = 0;
        string? processName = null;
        try
        {
            processId = e.HttpClient.ProcessId.Value;
            if (processId > 0)
            {
                processName = System.Diagnostics.Process.GetProcessById(processId).ProcessName;
            }
        }
        catch
        {
            // process may have exited
        }

        return new SessionSnapshot
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = req.Method ?? "GET",
            Url = req.Url ?? "",
            Host = TryHost(req),
            StartedUtc = DateTimeOffset.UtcNow,
            RequestHeadersText = FormatHeaders(req.Headers),
            RequestBodyBytes = bodyBytes,
            RequestBodyText = bodyText,
            ContentType = req.ContentType,
            Protocol = SessionDisplayFormat.FormatHttpProtocol(req.HttpVersion),
            ProcessId = processId,
            ProcessName = processName,
            IsTunnel = req.Method?.Equals("CONNECT", StringComparison.OrdinalIgnoreCase) == true,
            IsWebSocket = req.UpgradeToWebSocket,
            IsGrpc = req.ContentType?.Contains("grpc", StringComparison.OrdinalIgnoreCase) == true,
            IsMultipart = req.ContentType?.Contains("multipart/", StringComparison.OrdinalIgnoreCase) == true,
        };
    }

    private static void FillResponse(SessionSnapshot snap, SessionEventArgs e)
    {
        var resp = e.HttpClient.Response;
        snap.StatusCode = resp.StatusCode;
        snap.ResponseHeadersText = FormatHeaders(resp.Headers);
        snap.Protocol = SessionDisplayFormat.FormatClientServer(
            e.HttpClient.Request.HttpVersion, resp.HttpVersion);
        var bodyBytes = resp.IsBodyRead ? TruncateBytes(resp.Body) : null;
        snap.ResponseBodyBytes = bodyBytes;
        snap.ResponseBodyText = bodyBytes is null ? null : TruncateText(Encoding.UTF8.GetString(bodyBytes));
        snap.BodySize = bodyBytes?.LongLength
                        ?? (resp.ContentLength >= 0 ? resp.ContentLength : null);

        ApplyTiming(snap, e.Timing, snap.StartedUtc);

        if (snap.IsWebSocket)
        {
            snap.WebSocketFrames = ProtocolFrameInspectors.ParseWebSocketFrames(bodyBytes ?? snap.RequestBodyBytes);
        }

        if (snap.IsGrpc && bodyBytes is { Length: > 0 })
        {
            snap.GrpcFrames = ProtocolFrameInspectors.ParseGrpcFrames(bodyBytes);
        }

        if (snap.IsMultipart && bodyBytes is { Length: > 0 })
        {
            snap.MultipartParts = ProtocolFrameInspectors.ParseMultipart(snap.ContentType, bodyBytes);
        }
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
    ///     with ENHANCE_YOUR_CALM and breaks the browser download. Unknown length still buffers
    ///     up to the limit (UI truncation via <see cref="MaxBodyBytes" /> applies afterward).
    /// </summary>
    private bool ShouldBufferBody(RequestResponseBase message, SessionEventArgs session)
    {
        var limit = session.MaxBufferedBodyBytes ?? _proxy?.MaxBufferedBodyBytes ?? (4 * 1024 * 1024);
        if (limit <= 0)
        {
            return true;
        }

        var contentLength = message.ContentLength;
        return contentLength < 0 || contentLength <= limit;
    }

    private static byte[]? TruncateBytes(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return body;
        }

        return body.Length <= MaxBodyBytes ? body : body.AsSpan(0, MaxBodyBytes).ToArray();
    }

    private static string TruncateText(string text)
        => text.Length <= MaxBodyTextChars ? text : text[..MaxBodyTextChars] + "…";

    public void Dispose() => EnsureShutdown();
}
