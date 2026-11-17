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
    public void TryApplyExistingFreshCertificate_SkipsWhenMoreThanThirtyDaysRemain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        try
        {
            WriteSelfSignedPem(certPath, keyPath, validDays: 90);
            using var proxy = new ProxyServer(false, false, false);
            var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            proxy.AddEndPoint(ep);

            var applied = CertificateBootstrap.TryApplyExistingFreshCertificate(proxy, new CertificatesConfig
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                AcmeDomain = "example.test",
                AcmeEmail = "ops@example.test",
            });

            Assert.IsTrue(applied);
            Assert.IsNotNull(ep.GenericCertificate);
            Assert.IsFalse(CertificateBootstrap.NeedsRenewal(new CertificatesConfig
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
            }));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void NeedsRenewal_TrueWhenWithinThirtyDays()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-renew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        try
        {
            WriteSelfSignedPem(certPath, keyPath, validDays: 10);
            Assert.IsTrue(CertificateBootstrap.NeedsRenewal(new CertificatesConfig
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
            }));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task RunAcmeRenewalLoop_UsesIssueOverrideAndStopsOnCancel()
    {
        var calls = 0;
        CertificateBootstrap.IssueOverride = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        };
        CertificateBootstrap.DelayAsync = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        CertificateBootstrap.RenewalCheckInterval = TimeSpan.FromMilliseconds(1);

        var dir = Path.Combine(Path.GetTempPath(), "twp-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        try
        {
            WriteSelfSignedPem(certPath, keyPath, validDays: 5);
            using var proxy = new ProxyServer(false, false, false);
            using var cts = new CancellationTokenSource();
            var loop = CertificateBootstrap.RunAcmeRenewalLoopAsync(proxy, new CertificatesConfig
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                AcmeDomain = "example.test",
                AcmeEmail = "ops@example.test",
                AcmeDirectory = "https://acme.example/directory",
            }, cts.Token);

            await Task.Delay(100).ConfigureAwait(false);
            cts.Cancel();
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }

            Assert.IsTrue(calls >= 1, "expected at least one ACME issue attempt");
        }
        finally
        {
            CertificateBootstrap.IssueOverride = null;
            CertificateBootstrap.DelayAsync = null;
            CertificateBootstrap.RenewalCheckInterval = TimeSpan.FromHours(12);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
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
    public async Task WatchCertificateFiles_RebuildsSniBindingsAfterTheFileChanges()
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
                await Task.Delay(100);
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

    [TestMethod]
    public void ReportServedCertificateExpiry_RecordsExpiringAndExpiredButNotFresh()
    {
        var fresh = "fresh-" + Guid.NewGuid().ToString("N");
        var soon = "soon-" + Guid.NewGuid().ToString("N");
        var gone = "gone-" + Guid.NewGuid().ToString("N");
        using var freshCert = MakeCert(90);
        using var soonCert = MakeCert(10);
        using var goneCert = MakeCert(-2);

        CertificateBootstrap.ReportServedCertificateExpiry(fresh, freshCert);
        CertificateBootstrap.ReportServedCertificateExpiry(soon, soonCert);
        CertificateBootstrap.ReportServedCertificateExpiry(gone, goneCert);

        var reported = (System.Collections.IDictionary)typeof(CertificateBootstrap)
            .GetField("ReportedExpiry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        Assert.IsFalse(reported.Contains(fresh), "more than 30 days left is not reported");
        Assert.IsTrue(reported.Contains(soon), "30 days or less is reported");
        Assert.IsTrue(reported.Contains(gone), "an expired certificate is reported");

        static X509Certificate2 MakeCert(int days)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=report", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(days));
        }
    }

    [TestMethod]
    public void ResolveAcmePaths_PrefersEnvironmentThenConfigThenDefault()
    {
        var oldCert = Environment.GetEnvironmentVariable("TITANIUM_ACME_CERT_PATH");
        var oldKey = Environment.GetEnvironmentVariable("TITANIUM_ACME_KEY_PATH");
        try
        {
            Environment.SetEnvironmentVariable("TITANIUM_ACME_CERT_PATH", null);
            Environment.SetEnvironmentVariable("TITANIUM_ACME_KEY_PATH", null);

            var (defCert, defKey) = CertificateBootstrap.ResolveAcmePaths(new CertificatesConfig());
            Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "acme-cert.pem"), defCert);
            Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "acme-key.pem"), defKey);

            var (cfgCert, cfgKey) = CertificateBootstrap.ResolveAcmePaths(
                new CertificatesConfig { CertificatePath = "c.pem", PrivateKeyPath = "k.pem" });
            Assert.AreEqual("c.pem", cfgCert);
            Assert.AreEqual("k.pem", cfgKey);

            Environment.SetEnvironmentVariable("TITANIUM_ACME_CERT_PATH", "env-c.pem");
            Environment.SetEnvironmentVariable("TITANIUM_ACME_KEY_PATH", "env-k.pem");
            var (envCert, envKey) = CertificateBootstrap.ResolveAcmePaths(
                new CertificatesConfig { CertificatePath = "c.pem", PrivateKeyPath = "k.pem" });
            Assert.AreEqual("env-c.pem", envCert);
            Assert.AreEqual("env-k.pem", envKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_ACME_CERT_PATH", oldCert);
            Environment.SetEnvironmentVariable("TITANIUM_ACME_KEY_PATH", oldKey);
        }
    }

    [TestMethod]
    public async Task RunAcmeRenewalLoop_FreshCertificate_DoesNotContactAcmeAndWaitsTheCheckInterval()
    {
        var issueCalls = 0;
        var delays = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        CertificateBootstrap.IssueOverride = (_, _, _) =>
        {
            Interlocked.Increment(ref issueCalls);
            return Task.CompletedTask;
        };
        CertificateBootstrap.DelayAsync = (ts, ct) =>
        {
            delays.Add(ts);
            if (delays.Count >= 2) cts.Cancel();
            return Task.CompletedTask;
        };

        var dir = Path.Combine(Path.GetTempPath(), "twp-loop-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        try
        {
            WriteSelfSignedPem(certPath, keyPath, validDays: 90);
            using var proxy = new ProxyServer(false, false, false);
            var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            proxy.AddEndPoint(ep);

            await CertificateBootstrap.RunAcmeRenewalLoopAsync(proxy, new CertificatesConfig
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                AcmeDomain = "example.test",
                AcmeEmail = "ops@example.test",
                AcmeDirectory = "https://acme.example/directory",
            }, cts.Token).ConfigureAwait(false);

            Assert.AreEqual(0, issueCalls, "a certificate with more than 30 days left must not trigger issuance");
            Assert.IsNotNull(ep.GenericCertificate, "the existing fresh certificate is still applied");
            Assert.IsTrue(delays.All(d => d >= CertificateBootstrap.RenewalCheckInterval), string.Join(",", delays));
        }
        finally
        {
            CertificateBootstrap.IssueOverride = null;
            CertificateBootstrap.DelayAsync = null;
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task RunAcmeRenewalLoop_RenewsWithinThirtyDays_AndHotSwapsTheCertificate()
    {
        var issueCalls = 0;
        using var cts = new CancellationTokenSource();
        var dir = Path.Combine(Path.GetTempPath(), "twp-loop-renew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        CertificateBootstrap.IssueOverride = (_, _, _) =>
        {
            Interlocked.Increment(ref issueCalls);
            WriteSelfSignedPem(certPath, keyPath, validDays: 90);
            return Task.CompletedTask;
        };
        CertificateBootstrap.DelayAsync = (_, _) =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };
        try
        {
            WriteSelfSignedPem(certPath, keyPath, validDays: 5);
            using var proxy = new ProxyServer(false, false, false);
            var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            proxy.AddEndPoint(ep);
            CertificateBootstrap.ReplaceCertificate(proxy, certPath, keyPath);
            var before = ep.GenericCertificate!.NotAfter;

            await CertificateBootstrap.RunAcmeRenewalLoopAsync(proxy, new CertificatesConfig
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                AcmeDomain = "example.test",
                AcmeEmail = "ops@example.test",
                AcmeDirectory = "https://acme.example/directory",
            }, cts.Token).ConfigureAwait(false);

            Assert.AreEqual(1, issueCalls);
            Assert.IsTrue(ep.GenericCertificate!.NotAfter > before.AddDays(60),
                $"served certificate was not swapped: {ep.GenericCertificate.NotAfter:u}");
        }
        finally
        {
            CertificateBootstrap.IssueOverride = null;
            CertificateBootstrap.DelayAsync = null;
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task RunAcmeRenewalLoop_Failure_KeepsTheCurrentCertificateAndBacksOff()
    {
        var delays = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        CertificateBootstrap.IssueOverride = (_, _, _) => throw new InvalidOperationException("acme down");
        CertificateBootstrap.DelayAsync = (ts, _) =>
        {
            delays.Add(ts);
            if (delays.Count >= 3) cts.Cancel();
            return Task.CompletedTask;
        };

        var dir = Path.Combine(Path.GetTempPath(), "twp-loop-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        try
        {
            WriteSelfSignedPem(certPath, keyPath, validDays: 5);
            using var proxy = new ProxyServer(false, false, false);
            var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            proxy.AddEndPoint(ep);
            CertificateBootstrap.ReplaceCertificate(proxy, certPath, keyPath);
            var current = ep.GenericCertificate;

            await CertificateBootstrap.RunAcmeRenewalLoopAsync(proxy, new CertificatesConfig
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                AcmeDomain = "example.test",
                AcmeEmail = "ops@example.test",
                AcmeDirectory = "https://acme.example/directory",
            }, cts.Token).ConfigureAwait(false);

            Assert.AreSame(current, ep.GenericCertificate, "a failed renewal must not replace the served certificate");
            CollectionAssert.AreEqual(
                new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4) },
                delays.ToArray());
        }
        finally
        {
            CertificateBootstrap.IssueOverride = null;
            CertificateBootstrap.DelayAsync = null;
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void WriteSelfSignedPem(string certPath, string keyPath, int validDays = 30)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=twp-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(validDays));
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
    }
}
