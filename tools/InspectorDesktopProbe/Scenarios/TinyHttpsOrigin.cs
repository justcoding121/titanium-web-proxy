using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>Local scratch origin: self-signed HTTPS/1.1 keep-alive, tiny fixed response.</summary>
internal sealed class TinyHttpsOrigin : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private X509Certificate2? _cert;

    public int Start()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        _listener.Start(512);
        _ = Task.Run(AcceptLoop);
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using var _ = c;
        try
        {
            using var ssl = new SslStream(c.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(_cert!).ConfigureAwait(false);
            var buf = new byte[8192];
            var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello");
            while (!_cts.IsCancellationRequested)
            {
                var n = await ssl.ReadAsync(buf).ConfigureAwait(false);
                if (n <= 0) return;
                await ssl.WriteAsync(resp).ConfigureAwait(false);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cert?.Dispose();
    }
}
