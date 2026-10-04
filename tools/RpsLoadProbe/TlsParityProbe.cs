using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// One throw-away TLS handshake per arm, made with the same client defaults as the
/// <see cref="EmbeddedLoadGenerator"/> SocketsHttpHandler (OS-default protocols and cipher policy,
/// ALPN from the arm's request version). Logs the negotiated protocol, cipher suite and ALPN so a
/// peer that negotiated a different (cheaper/dearer) suite than Titanium shows up in the artifact
/// instead of hiding inside an RPS delta. Observation only: it never changes any arm configuration.
/// </summary>
internal static class TlsParityProbe
{
    private static readonly object SidecarLock = new();
    private static string? sidecarPath;

    public static void SetSidecarPath(string path)
    {
        sidecarPath = path;
        lock (SidecarLock)
        {
            File.WriteAllText(path, "arm\ttls_version\tcipher_suite\talpn\tserver_cert_key\tnote\n");
        }
    }

    public static async Task ProbeAsync(string armName, Uri target, Version httpVersion, HttpVersionPolicy policy,
        string? explicitProxyUrl, bool measuredWithBombardier, CancellationToken cancellationToken)
    {
        if (!target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            Record(armName, "-", "-", "-", "-", "cleartext client leg");
            return;
        }

        if (httpVersion.Major >= 3)
        {
            Record(armName, "-", "-", "h3", "-", "QUIC (TLS 1.3 inside MsQuic; not observable via SslStream)");
            return;
        }

        if (!string.IsNullOrEmpty(explicitProxyUrl))
        {
            Record(armName, "-", "-", "-", "-", "explicit-proxy CONNECT; client TLS is to the MITM leaf");
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(10));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(target.Host, target.Port, linked.Token);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var alpn = new List<SslApplicationProtocol>();
            if (httpVersion.Major >= 2)
                alpn.Add(SslApplicationProtocol.Http2);
            if (policy != HttpVersionPolicy.RequestVersionExact || httpVersion.Major < 2)
                alpn.Add(SslApplicationProtocol.Http11);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = target.Host,
                ApplicationProtocols = alpn,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, linked.Token);

            var certKey = "-";
            if (ssl.RemoteCertificate is { } remote)
            {
                using var cert = new X509Certificate2(remote);
                var name = cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "?";
                var bits = cert.PublicKey.GetRSAPublicKey()?.KeySize
                           ?? cert.PublicKey.GetECDsaPublicKey()?.KeySize ?? 0;
                certKey = bits > 0 ? $"{name}-{bits}" : name;
            }

            var negotiated = ssl.NegotiatedApplicationProtocol.Protocol.IsEmpty
                ? "none"
                : Encoding.ASCII.GetString(ssl.NegotiatedApplicationProtocol.Protocol.Span);
            Record(armName, ssl.SslProtocol.ToString(),
                ssl.NegotiatedCipherSuite.ToString(), negotiated, certKey,
                measuredWithBombardier ? "arm is measured with bombardier (Go TLS); this is the .NET client view" : string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Record(armName, "-", "-", "-", "-", $"probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Record(string arm, string tls, string cipher, string alpn, string certKey, string note)
    {
        ProbeLog.Info($"  tls-parity: arm={arm} tls={tls} cipher={cipher} alpn={alpn} cert_key={certKey}" +
                      (note.Length > 0 ? $" ({note})" : string.Empty));
        var path = sidecarPath;
        if (path == null)
            return;
        lock (SidecarLock)
        {
            File.AppendAllText(path,
                $"{arm}\t{tls}\t{cipher}\t{alpn}\t{certKey}\t{note.Replace('\t', ' ').Replace('\n', ' ')}\n");
        }
    }
}
