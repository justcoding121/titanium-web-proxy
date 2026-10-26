#pragma warning disable TWP001
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

    [TestMethod]
    public void ApplySniItems_BindsHostsOnDecryptingListeners()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-sni-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pfx = Path.Combine(dir, "leaf.pfx");
        try
        {
            WritePfx(pfx, "CN=sni.example");
            using var proxy = new ProxyServer(false, false, false);
            var decrypting = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            var clear = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
            proxy.AddEndPoint(decrypting);
            proxy.AddEndPoint(clear);
            CertificateBootstrap.ApplySniItems(proxy, new CertificatesConfig
            {
                Items =
                [
                    new CertificateFileConfig
                    {
                        Path = pfx,
                        Password = "secret",
                        Hosts = ["sni.example"],
                    },
                ],
            });
            Assert.IsNotNull(decrypting.SniCertificates);
            Assert.AreEqual(1, decrypting.SniCertificates!.Count);
            Assert.AreEqual("sni.example", decrypting.SniCertificates[0].Hosts[0]);
            Assert.IsNull(clear.SniCertificates);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void WatchCertificateFiles_RebuildsSniBindingsAfterTheFileChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pfx = Path.Combine(dir, "leaf.pfx");
        try
        {
            WritePfx(pfx, "CN=before.example");
            using var proxy = new ProxyServer(false, false, false);
            var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            proxy.AddEndPoint(ep);
            var certificates = new CertificatesConfig
            {
                Items = [new CertificateFileConfig { Path = pfx, Password = "secret", Hosts = ["app.example"] }],
            };
            using var watch = CertificateBootstrap.WatchCertificateFiles(proxy, certificates);
            Assert.IsNotNull(watch);
            CertificateBootstrap.ApplySniItems(proxy, certificates);
            Assert.AreEqual("CN=before.example", ep.SniCertificates![0].Certificate.Subject);

            WritePfx(pfx, "CN=after.example");
            var saw = false;
            for (var i = 0; i < 30 && !saw; i++)
            {
                Thread.Sleep(100);
                saw = ep.SniCertificates![0].Certificate.Subject == "CN=after.example";
            }

            Assert.IsTrue(saw);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void ReloadClientCertificateAuthorities_MatchesAddressAndContinuesAfterABadFile()
    {
        using var proxy = new ProxyServer(false, false, false);
        var matched = new TransparentProxyEndPoint(IPAddress.Parse("127.0.0.1"), 8443, true);
        var other = new TransparentProxyEndPoint(IPAddress.Parse("127.0.0.2"), 8443, true);
        proxy.AddEndPoint(matched);
        proxy.AddEndPoint(other);

        var dir = Path.Combine(Path.GetTempPath(), "twp-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=reload-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            var pem = Path.Combine(dir, "ca.pem");
            File.WriteAllText(pem, new string(PemEncoding.Write("CERTIFICATE", ca.RawData)));
            var bad = Path.Combine(dir, "bad.pem");
            File.WriteAllText(bad, "not a certificate");

            CertificateBootstrap.ReloadClientCertificateAuthorities(proxy,
            [
                new ListenerConfig { Host = "127.0.0.2", Port = 8443, ClientCertificateCaPath = bad },
                new ListenerConfig { Host = "127.0.0.1", Port = 8443, ClientCertificateCaPath = pem },
            ]);

            Assert.IsNull(other.ClientCertificateTrustedRoots);
            Assert.AreEqual(1, matched.ClientCertificateTrustedRoots?.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void WritePfx(string path, string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "secret"));
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
