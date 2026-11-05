using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     A decrypting transparent listener that carries per-host (SNI) certificate bindings must complete the
///     client handshake and present the bound certificate — on both the fixed-certificate HTTP/1.1 path and
///     the ClientHello-peek path.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TransparentSniCertificateSelectionTests
{
    private const string BoundHost = "sni-bound.titanium.test";

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task FixedCertificatePath_PresentsBoundCertificateForMatchingSni_AndFallbackOtherwise()
    {
        await AssertSniSelectionAsync(enableHttp2: false);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ClientHelloPeekPath_PresentsBoundCertificateForMatchingSni_AndFallbackOtherwise()
    {
        await AssertSniSelectionAsync(enableHttp2: true);
    }

    private static async Task AssertSniSelectionAsync(bool enableHttp2)
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp2 = enableHttp2 };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;

        // Self-signed leaves: the client accepts any certificate here, so the test does not depend on the
        // machine stores (which accumulate same-subject test roots and make Windows chain building flaky).
        var boundCertificate = CreateSelfSigned(BoundHost);
        var fallbackCertificate = CreateSelfSigned("localhost");

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = fallbackCertificate,
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
            ForwardCleartext = true,
            SniCertificates = new List<SniCertificateBinding>
            {
                new() { Hosts = [BoundHost], Certificate = boundCertificate },
            },
        };
proxy.AddEndPoint(endPoint);
        proxy.Start();
        try
        {
            var fallback = await HandshakeSubjectAsync(endPoint.Port, "localhost");
            StringAssert.Contains(fallback, "localhost");

            var matched = await HandshakeSubjectAsync(endPoint.Port, BoundHost);
            StringAssert.Contains(matched, BoundHost);
        }
        finally
        {
            proxy.Stop();
            boundCertificate.Dispose();
            fallbackCertificate.Dispose();
        }
    }

    private static X509Certificate2 CreateSelfSigned(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static async Task<string> HandshakeSubjectAsync(int port, string targetHost)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });
        return ssl.RemoteCertificate?.Subject ?? string.Empty;
    }
}