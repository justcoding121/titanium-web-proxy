using System.Net;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Services;

/// <summary>Root CA trust, system proxy, upstream/PAC, and certificate validation UI helpers.</summary>
public sealed class InterceptionService : IDisposable
{
    private ProxyServer? _proxy;
    private ExplicitProxyEndPoint? _endPoint;

    public bool IsRunning => _proxy?.ProxyRunning == true;
    public X509Certificate2? RootCertificate => _proxy?.CertificateManager.RootCertificate;
    public string? UpstreamProxyAddress { get; set; }
    public string? PacUrl { get; set; }
    public bool TrustRootCertificate { get; set; } = true;
    public bool IgnoreServerCertificateErrors { get; set; }

    public event EventHandler<SessionSnapshot>? SessionCaptured;

    public async Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_proxy is not null)
        {
            return;
        }

        _proxy = new ProxyServer();
        _proxy.BeforeRequest += OnBeforeRequest;
        _proxy.BeforeResponse += OnBeforeResponse;

        if (!string.IsNullOrWhiteSpace(UpstreamProxyAddress) &&
            Uri.TryCreate(UpstreamProxyAddress, UriKind.Absolute, out var upstream))
        {
            _proxy.UpStreamHttpProxy = new ExternalProxy(upstream.Host, upstream.Port)
            {
                ProxyType = ExternalProxyType.Http,
            };
        }

        _endPoint = new ExplicitProxyEndPoint(address, port, decryptSsl: true);
        _proxy.AddEndPoint(_endPoint);
        _proxy.Start();

        if (TrustRootCertificate)
        {
            _proxy.CertificateManager.TrustRootCertificate(true);
        }

        await Task.CompletedTask;
    }

    public void Stop()
    {
        if (_proxy is null)
        {
            return;
        }

        _proxy.BeforeRequest -= OnBeforeRequest;
        _proxy.BeforeResponse -= OnBeforeResponse;
        _proxy.Stop();
        _proxy.Dispose();
        _proxy = null;
        _endPoint = null;
    }

    public void SetSystemProxy(bool enable)
    {
        if (_proxy is null || _endPoint is null)
        {
            return;
        }

        if (enable)
        {
            _proxy.SetAsSystemHttpProxy(_endPoint);
            _proxy.SetAsSystemHttpsProxy(_endPoint);
        }
        else
        {
            _proxy.RestoreOriginalProxySettings();
        }
    }

    private Task OnBeforeRequest(object sender, SessionEventArgs e)
    {
        var snap = new SessionSnapshot
        {
            Id = e.HttpClient.Request.GetHashCode(),
            Method = e.HttpClient.Request.Method ?? "GET",
            Url = e.HttpClient.Request.Url ?? "",
            StartedUtc = DateTimeOffset.UtcNow,
            RequestHeadersText = FormatHeaders(e.HttpClient.Request.Headers),
            ContentType = e.HttpClient.Request.ContentType,
            IsTunnel = e.HttpClient.Request.Method?.Equals("CONNECT", StringComparison.OrdinalIgnoreCase) == true,
            IsWebSocket = e.HttpClient.Request.UpgradeToWebSocket,
            IsGrpc = e.HttpClient.Request.ContentType?.Contains("grpc", StringComparison.OrdinalIgnoreCase) == true,
            IsMultipart = e.HttpClient.Request.ContentType?.Contains("multipart/", StringComparison.OrdinalIgnoreCase) == true,
        };
        SessionCaptured?.Invoke(this, snap);
        return Task.CompletedTask;
    }

    private Task OnBeforeResponse(object sender, SessionEventArgs e)
    {
        return Task.CompletedTask;
    }

    private static string FormatHeaders(HeaderCollection headers)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var h in headers)
        {
            sb.Append(h.Name).Append(": ").Append(h.Value).AppendLine();
        }

        return sb.ToString();
    }

    public void Dispose() => Stop();
}
