using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class CertificateBootstrapTests
{
    [TestMethod]
    public void Apply_NullCertificates_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        CertificateBootstrap.Apply(proxy, null);
        Assert.AreEqual(0, proxy.ProxyEndPoints.Count);
    }

    [TestMethod]
    public void SetChallengeToken_DoesNotThrow()
    {
        var token = "tok-" + Guid.NewGuid().ToString("N");
        Assert.IsFalse(string.IsNullOrEmpty(token));
        CertificateBootstrap.SetChallengeToken(token, "key-auth");

        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);
        CertificateBootstrap.Apply(proxy, new CertificatesConfig
        {
            AcmeDomain = "example.test",
            AcmeEmail = "ops@example.test",
        });
        Assert.IsTrue(proxy.ProxyEndPoints.Count >= 1);
    }

    [TestMethod]
    public void ReplaceCertificate_LoadsPemOntoDecryptSslEndpoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        try
        {
            WriteSelfSignedPem(certPath, keyPath);

            using var proxy = new ProxyServer(false, false, false);
            var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            proxy.AddEndPoint(ep);

            CertificateBootstrap.ReplaceCertificate(proxy, certPath, keyPath);
            Assert.IsNotNull(ep.GenericCertificate);
            Assert.AreEqual("CN=twp-test", ep.GenericCertificate!.Subject);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void ReplaceCertificate_MissingFile_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CertificateBootstrap.ReplaceCertificate(proxy, Path.Combine(Path.GetTempPath(), "missing-leaf.pem"), null));
    }

    [TestMethod]
    public void Apply_WithAcmeDomain_RegistersChallengeHandler()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);

        var token = "acme-" + Guid.NewGuid().ToString("N");
        CertificateBootstrap.SetChallengeToken(token, "key-authorization-value");
        CertificateBootstrap.Apply(proxy, new CertificatesConfig
        {
            AcmeDomain = "example.test",
            AcmeEmail = "ops@example.test",
        });

        proxy.Start(changeSystemProxySettings: false);
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{ep.Port}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var resp = http.GetAsync($"http://example.test/.well-known/acme-challenge/{token}")
                .GetAwaiter().GetResult();
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            Assert.AreEqual("key-authorization-value", body);
        }
        finally
        {
            proxy.Stop();
        }
    }

    private static void WriteSelfSignedPem(string certPath, string keyPath)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=twp-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
    }
}
