using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests.Support;

internal static class PlatformSsl
{
    private static readonly Lazy<bool> Tls13 = new(Probe);

    internal static void RequireTls13()
    {
        if (!Tls13.Value)
        {
            Assert.Inconclusive("SslStream on this runtime cannot enable TLS 1.3.");
        }
    }

    private static bool Probe()
    {
        using X509Certificate2 certificate = Certificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(() =>
        {
            using TcpClient accepted = listener.AcceptTcpClient();
            using var stream = new SslStream(accepted.GetStream());
            stream.AuthenticateAsServer(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
        });
        try
        {
            using var tcp = new TcpClient(IPAddress.Loopback.ToString(), port);
            using var stream = new SslStream(tcp.GetStream());
            stream.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            });
            server.GetAwaiter().GetResult();
            return stream.SslProtocol == SslProtocols.Tls13;
        }
        catch (AuthenticationException exception) when (exception.InnerException is PlatformNotSupportedException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
            try
            {
                server.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }
}
