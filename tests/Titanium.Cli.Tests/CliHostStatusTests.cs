using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>What the dashboard learns from the CLI: config identity, reload outcome, and certificate expiry.</summary>
[TestClass]
public class CliHostStatusTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        SecretRedactor.Clear();
        _dir = Path.Combine(Path.GetTempPath(), "twp-hoststatus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SecretRedactor.Clear();
        Directory.Delete(_dir, recursive: true);
    }

    [TestMethod]
    public void ConfigStatus_StartsWithTheLoadedConfig_AndNoReload()
    {
        var clock = new ManualClock();
        var status = new CliHostStatus(new TwpConfig { SchemaVersion = "7.1" }, "hash-1", clock);

        var s = status.GetConfigStatus();

        Assert.AreEqual("7.1", s.SchemaVersion);
        Assert.AreEqual("hash-1", s.ConfigHash);
        Assert.AreEqual(clock.GetUtcNow(), s.LoadedUtc);
        Assert.IsNull(s.LastReloadUtc);
        Assert.IsNull(s.LastReloadResult);
        Assert.IsNull(s.LastReloadError);
    }

    [TestMethod]
    public void Reload_Success_UpdatesTheHashAndTime_AndClearsTheError()
    {
        var clock = new ManualClock();
        var status = new CliHostStatus(new TwpConfig(), "hash-1", clock);
        status.MarkReloadFailed("boom");
        clock.Advance(TimeSpan.FromMinutes(5));

        status.MarkReloaded(new TwpConfig { SchemaVersion = "7.2" }, "hash-2");

        var s = status.GetConfigStatus();
        Assert.AreEqual("7.2", s.SchemaVersion);
        Assert.AreEqual("hash-2", s.ConfigHash);
        Assert.AreEqual("ok", s.LastReloadResult);
        Assert.IsNull(s.LastReloadError);
        Assert.AreEqual(clock.GetUtcNow(), s.LastReloadUtc);
    }

    [TestMethod]
    public void Reload_Failure_KeepsTheRunningConfig_AndRedactsTheMessage()
    {
        SecretRedactor.Register("super-secret-value");
        var clock = new ManualClock();
        var status = new CliHostStatus(new TwpConfig { SchemaVersion = "7.1" }, "hash-1", clock);
        clock.Advance(TimeSpan.FromMinutes(1));

        status.MarkReloadFailed("bad token super-secret-value in plus.options");

        var s = status.GetConfigStatus();
        Assert.AreEqual("hash-1", s.ConfigHash, "a failed reload leaves the running config in place");
        Assert.AreEqual("failed", s.LastReloadResult);
        Assert.AreEqual("bad token *** in plus.options", s.LastReloadError);
        Assert.AreEqual(clock.GetUtcNow(), s.LastReloadUtc);
    }

    [TestMethod]
    public void Certificates_ListPemAndPfx_WithSubjectIssuerAndExpiry()
    {
        var notAfter = DateTimeOffset.UtcNow.AddDays(45);
        var pem = WritePem("server.pem", "CN=pem-host", notAfter);
        var pfx = WritePfx("sni.pfx", "CN=pfx-host", notAfter.AddDays(10), "pfx-pass");
        var config = new TwpConfig
        {
            Certificates = new CertificatesConfig
            {
                CertificatePath = pem,
                Items = [new CertificateFileConfig { Path = pfx, Password = "pfx-pass" }],
            },
        };

        var certs = CliHostStatus.Collect(config, _ => null);

        Assert.AreEqual(2, certs.Count);
        var p = certs.Single(c => c.Name.Contains("server.pem", StringComparison.Ordinal));
        Assert.AreEqual("listener", p.Role);
        Assert.AreEqual("CN=pem-host", p.Subject);
        Assert.AreEqual("CN=pem-host", p.Issuer);
        Assert.IsNull(p.Error);
        AssertWithinAMinute(notAfter, p.NotAfterUtc);
        var f = certs.Single(c => c.Name.Contains("sni.pfx", StringComparison.Ordinal));
        Assert.AreEqual("CN=pfx-host", f.Subject);
        AssertWithinAMinute(notAfter.AddDays(10), f.NotAfterUtc);
    }

    private static void AssertWithinAMinute(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.IsNotNull(actual);
        Assert.IsTrue((expected - actual.Value).Duration() < TimeSpan.FromMinutes(1), $"{expected:O} vs {actual:O}");
    }

    [TestMethod]
    public void Certificates_Upstream_UseThePasswordFromTheNamedVariable_AndNeverPrintIt()
    {
        var pfx = WritePfx("client.pfx", "CN=client", DateTimeOffset.UtcNow.AddDays(100), "env-pass");
        var ca = WritePem("ca.pem", "CN=upstream-ca", DateTimeOffset.UtcNow.AddDays(200));
        var config = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 443 }],
                    HttpClient = new ClusterHttpClientConfig
                    {
                        ClientCertificatePath = pfx,
                        ClientCertificatePasswordEnv = "CLIENT_PFX_PASSWORD",
                        CaBundlePath = ca,
                    },
                },
            ],
        };

        var certs = CliHostStatus.Collect(config, name => name == "CLIENT_PFX_PASSWORD" ? "env-pass" : null);

        Assert.AreEqual(2, certs.Count);
        Assert.IsTrue(certs.All(c => c.Role == "upstream" && c.Error is null), string.Join(";", certs.Select(c => c.Error)));
        Assert.IsTrue(certs.Any(c => c.Subject == "CN=client"));
        Assert.IsFalse(certs.Any(c => c.Name.Contains("env-pass", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Certificates_MissingOrCorruptFile_BecomesAnEntryWithAnError()
    {
        var corrupt = Path.Combine(_dir, "corrupt.pfx");
        File.WriteAllBytes(corrupt, [1, 2, 3, 4]);
        var config = new TwpConfig
        {
            Certificates = new CertificatesConfig
            {
                CertificatePath = Path.Combine(_dir, "missing.pem"),
                Items = [new CertificateFileConfig { Path = corrupt, Password = "irrelevant-password" }],
            },
        };

        var certs = CliHostStatus.Collect(config, _ => null);

        Assert.AreEqual(2, certs.Count);
        Assert.IsTrue(certs.All(c => c.Error is not null && c.NotAfterUtc is null));
        Assert.IsTrue(certs.All(c => c.Error!.StartsWith("could not be read", StringComparison.Ordinal)));
        Assert.IsFalse(certs.Any(c => c.Error!.Contains("irrelevant-password", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Certificates_ABundle_ListsEachCertificate_AndBlankPathsAreSkipped()
    {
        var bundle = Path.Combine(_dir, "bundle.pem");
        File.WriteAllText(
            bundle,
            ToPem("CN=one", DateTimeOffset.UtcNow.AddDays(10)) + Environment.NewLine + ToPem("CN=two", DateTimeOffset.UtcNow.AddDays(20)));
        var config = new TwpConfig
        {
            Certificates = new CertificatesConfig { CertificatePath = "  " },
            Listeners = [new ListenerConfig { Port = 8080, ClientCertificateCaPath = bundle }],
        };

        var certs = CliHostStatus.Collect(config, _ => null);

        Assert.AreEqual(2, certs.Count);
        Assert.IsTrue(certs.Any(c => c.Name.EndsWith("(1/2)", StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(new[] { "CN=one", "CN=two" }, certs.Select(c => c.Subject).ToArray());
    }

    [TestMethod]
    public void Inventory_IsCachedBriefly_AndRefreshedAfterAReload()
    {
        var clock = new ManualClock();
        var path = WritePem("a.pem", "CN=first", DateTimeOffset.UtcNow.AddDays(30));
        var config = new TwpConfig { Certificates = new CertificatesConfig { CertificatePath = path } };
        var status = new CliHostStatus(config, "h1", clock);

        var first = status.GetCertificates();
        File.Delete(path);
        Assert.AreSame(first, status.GetCertificates(), "inside the cache window the files are not read again");

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.IsNotNull(status.GetCertificates().Single().Error, "after the window the missing file shows up");

        WritePem("a.pem", "CN=second", DateTimeOffset.UtcNow.AddDays(60));
        status.MarkReloaded(config, "h2");
        Assert.AreEqual("CN=second", status.GetCertificates().Single().Subject);
    }

    private string WritePem(string name, string subject, DateTimeOffset notAfter)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, ToPem(subject, notAfter));
        return path;
    }

    private string WritePfx(string name, string subject, DateTimeOffset notAfter, string password)
    {
        var path = Path.Combine(_dir, name);
        using var rsa = RSA.Create(2048);
        using var cert = Create(rsa, subject, notAfter);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static string ToPem(string subject, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        using var cert = Create(rsa, subject, notAfter);
        return cert.ExportCertificatePem();
    }

    private static X509Certificate2 Create(RSA rsa, string subject, DateTimeOffset notAfter)
    {
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
