using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Cli.QaProbe;

public sealed class EchoOrigin : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public EchoOrigin()
    {
        (_listener, Port) = CliSpawn.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch { return; }

            _ = Task.Run(() =>
            {
                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    var body = Encoding.UTF8.GetBytes($"echo:{path}:{ctx.Request.HttpMethod}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.OutputStream.Write(body);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { /* ignore */ } }
            }, ct);
        }
    }
}

/// <summary>
/// HTTPS origin whose leaf is signed by a temp CA installed in CurrentUser\Root,
/// so CLI MITM (default ValidateServerCertificate) accepts the upstream without a
/// ServerCertificateValidationCallback (CLI has no ignore-upstream-cert flag).
/// </summary>
public sealed class HttpsEchoOrigin : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _leaf;
    private readonly X509Certificate2 _root;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _rootThumbprint;

    public int Port { get; }

    public HttpsEchoOrigin()
    {
        using var rootRsa = RSA.Create(2048);
        var rootReq = new CertificateRequest(
            "CN=Titanium CliQaProbe Temp Root", rootRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var rootCreated = rootReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        _root = X509CertificateLoader.LoadPkcs12(
            rootCreated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        _rootThumbprint = _root.Thumbprint;

        using var leafRsa = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            "CN=127.0.0.1", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafReq.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        leafReq.CertificateExtensions.Add(san.Build());
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using var leafCreated = leafReq.Create(
            _root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), serial);
        using var leafWithKey = leafCreated.CopyWithPrivateKey(leafRsa);
        _leaf = X509CertificateLoader.LoadPkcs12(
            leafWithKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);

        TryInstallRoot(_root);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        TryRemoveRoot(_rootThumbprint);
        _leaf.Dispose();
        _root.Dispose();
    }

    private static void TryInstallRoot(X509Certificate2 root)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(root);
        }
        catch
        {
            // MITM step may fail if trust install is blocked
        }
    }

    private static void TryRemoveRoot(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            foreach (var c in matches)
                store.Remove(c);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }

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
                ServerCertificate = _leaf,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13,
            }, ct).ConfigureAwait(false);

            using var reader = new StreamReader(ssl, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct).ConfigureAwait(false))) { }

            var path = "/";
            var parts = requestLine.Split(' ');
            if (parts.Length >= 2)
                path = parts[1];

            var body = Encoding.UTF8.GetBytes($"echo:{path}:GET");
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " +
                body.Length + "\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(header, ct).ConfigureAwait(false);
            await ssl.WriteAsync(body, ct).ConfigureAwait(false);
            await ssl.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }
}
