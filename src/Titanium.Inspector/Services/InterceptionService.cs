using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;
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
    private string? _rootPfxPath;

    public InterceptionService(ISystemProxyController? systemProxy = null)
    {
        _systemProxy = systemProxy ?? new WinInetSystemProxyController();
    }

    public bool IsRunning => _proxy?.ProxyRunning == true;

    /// <summary>When false, the listener stays up but sessions are not published to the grid.</summary>
    public bool Capturing { get; set; } = true;

    public bool SystemProxyEnabled => _systemProxyEnabled;
    public X509Certificate2? RootCertificate => _proxy?.CertificateManager.RootCertificate;
    public bool IsRootTrusted { get; private set; }
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
        _proxy.EnableHttpInterception = true;
        _proxy.EnableRequestTimingCapture = true;
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

        _endPoint = new ExplicitProxyEndPoint(address, port, decryptSsl: true);
        _endPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnect;
        _proxy.AddEndPoint(_endPoint);
        _proxy.Start();

        IsRootTrusted = IsRootPresentInStore(machineStore: false);

        if (AutoTrustRootOnStart)
        {
            InstallRootCertificate(machineStore: false);
        }

        Capturing = true;
        await Task.CompletedTask;
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
        }

        _proxy.Stop();
        _proxy.Dispose();
        _proxy = null;
        _endPoint = null;
        _live.Clear();
        IsRootTrusted = false;
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

        _proxy.CertificateManager.TrustRootCertificate(machineStore);
        IsRootTrusted = IsRootPresentInStore(machineStore);
        return IsRootTrusted;
    }

    public void UntrustRootCertificate(bool machineStore)
    {
        if (_proxy is null)
        {
            return;
        }

        _proxy.CertificateManager.RemoveTrustedRootCertificate(machineStore);
        IsRootTrusted = IsRootPresentInStore(machineStore);
    }

    public bool RefreshTrustState(bool machineStore = false)
    {
        IsRootTrusted = IsRootPresentInStore(machineStore);
        return IsRootTrusted;
    }

    public bool IsRootPresentInStore(bool machineStore)
    {
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
        if (MitmBypass.ShouldDisableSslDecrypt(e.HttpClient.Request.RequestUri.Host))
        {
            e.DecryptSsl = false;
        }

        return Task.CompletedTask;
    }

    private async Task OnBeforeRequest(object sender, SessionEventArgs e)
    {
        try
        {
            if (e.HttpClient.Request.HasBody)
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
            if (e.HttpClient.Response.HasBody)
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
        if (_live.TryGetValue(e.HttpClient, out var snap) && e.Timing is not null)
        {
            snap.DurationMs = e.Timing.TotalDuration.TotalMilliseconds;
            if (e.Timing.TimeToFirstByte is TimeSpan ttfb)
            {
                snap.TtfbMs = ttfb.TotalMilliseconds;
            }

            SessionUpdated?.Invoke(this, snap);
        }

        _live.TryRemove(e.HttpClient, out _);
        return Task.CompletedTask;
    }

    private Task OnServerCertValidation(object sender, CertificateValidationEventArgs e)
    {
        if (IgnoreServerCertificateErrors)
        {
            e.IsValid = true;
        }

        return Task.CompletedTask;
    }

    private SessionSnapshot CreatePreviewSnapshot(SessionEventArgs e)
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
            Protocol = FormatProtocol(req.HttpVersion),
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
        snap.Protocol = FormatProtocol(e.HttpClient.Request.HttpVersion) + " ↔ " + FormatProtocol(resp.HttpVersion);
        var bodyBytes = resp.IsBodyRead ? TruncateBytes(resp.Body) : null;
        snap.ResponseBodyBytes = bodyBytes;
        snap.ResponseBodyText = bodyBytes is null ? null : TruncateText(Encoding.UTF8.GetString(bodyBytes));
        snap.BodySize = bodyBytes?.LongLength
                        ?? (resp.ContentLength >= 0 ? resp.ContentLength : null);

        if (e.Timing is not null)
        {
            snap.DurationMs = e.Timing.TotalDuration.TotalMilliseconds;
            if (e.Timing.TimeToFirstByte is TimeSpan ttfb)
            {
                snap.TtfbMs = ttfb.TotalMilliseconds;
            }
        }

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

    private static string FormatProtocol(Version? version)
    {
        if (version is null || version.Major == 0)
        {
            return "unknown";
        }

        return version.Major >= 2 ? "HTTP/" + version.Major : $"HTTP/{version.Major}.{version.Minor}";
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

    public void Dispose() => Stop();
}
