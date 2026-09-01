using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Minimal HTTPS origin (self-signed) for MITM decryption E2E.</summary>
public sealed class HttpsEchoOrigin : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _cert;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public HttpsEchoOrigin()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        _cert.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _cert,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13,
            }, ct);

            using var reader = new StreamReader(ssl, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct)))
            {
                // drain headers
            }

            var path = "/";
            var parts = requestLine.Split(' ');
            if (parts.Length >= 2)
            {
                path = parts[1];
            }

            var body = Encoding.UTF8.GetBytes($"echo:{path}:GET");
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " +
                body.Length + "\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(header, ct);
            await ssl.WriteAsync(body, ct);
            await ssl.FlushAsync(ct);
        }
        catch
        {
            // ignore per-connection errors
        }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }
}
