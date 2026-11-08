using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Envoy.Extensions.TransportSockets.Tls.V3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Translation;

namespace Titanium.Xds.Tests;

[TestClass]
public class SecretTranslatorGoldenTests
{
    private string _stateDir = string.Empty;

    [TestInitialize]
    public void Setup() => _stateDir = Directory.CreateTempSubdirectory("twp-xds-secret-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_stateDir, recursive: true);

    [TestMethod]
    public void UsableTlsCertificate_MapsPemAndPfx()
    {
        var (certPem, keyPem) = SelfSigned("app.example.com");
        var secret = new Secret
        {
            Name = "server-cert",
            TlsCertificate = new TlsCertificate { CertificateChain = certPem, PrivateKey = keyPem },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsTrue(File.Exists(result.Value!.CertificateChainPath));
        Assert.IsTrue(File.Exists(result.Value.PrivateKeyPath));
        Assert.IsTrue(File.Exists(result.Value.PfxPath));
        CollectionAssert.Contains(result.Value.Hosts.ToArray(), "app.example.com");
    }

    [TestMethod]
    public void EmptyTlsCertificate_NacksWithFieldPath()
    {
        var secret = new Secret
        {
            Name = "empty",
            TlsCertificate = new TlsCertificate(),
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "tls_certificate");
    }

    [TestMethod]
    public void UnusableSecretType_NacksWithFieldPath()
    {
        var secret = new Secret { Name = "none" };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "type");
    }

    private static (string CertPem, string KeyPem) SelfSigned(string dnsName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + dnsName, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }
}
