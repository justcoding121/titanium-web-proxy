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

    [TestMethod]
    public void EmptySecretName_NacksWithNameFieldPath()
    {
        // Bug class: unnamed secrets overwrite each other's files under stateDirectory.
        var secret = new Secret
        {
            Name = "  ",
            TlsCertificate = new TlsCertificate { CertificateChain = "x", PrivateKey = "y" },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("name", result.Error!.FieldPath);
    }

    [TestMethod]
    public void ValidationContext_WritesTrustedCa()
    {
        // Bug class: validation_context trusted_ca is ignored / not written to disk.
        var secret = new Secret
        {
            Name = "trust",
            ValidationContext = new CertificateValidationContext
            {
                TrustedCa = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n",
            },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsTrue(File.Exists(result.Value!.TrustedCaPath));
        Assert.IsNull(result.Value.PfxPath);
    }

    [TestMethod]
    public void ValidationContext_WithoutTrustedCa_Nacks()
    {
        var secret = new Secret
        {
            Name = "empty-ca",
            ValidationContext = new CertificateValidationContext(),
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "trusted_ca");
    }

    [TestMethod]
    public void MismatchedPemPair_NacksWithCryptographicDetail()
    {
        // Bug class: unparseable PEM pairs are written as dead PFX files instead of NACK'd.
        var (certPem, _) = SelfSigned("a.example.com");
        var (_, otherKey) = SelfSigned("b.example.com");
        var secret = new Secret
        {
            Name = "mismatch",
            TlsCertificate = new TlsCertificate { CertificateChain = certPem, PrivateKey = otherKey },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "tls_certificate");
        StringAssert.Contains(result.Error.Message, "valid pair");
    }

    [TestMethod]
    public void CertificateWithoutSan_FallsBackToCommonName()
    {
        // Bug class: CN-only certs get Hosts=["*"] and over-bind SNI.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=cn-only.example", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var secret = new Secret
        {
            Name = "cn-only",
            TlsCertificate = new TlsCertificate
            {
                CertificateChain = cert.ExportCertificatePem(),
                PrivateKey = key.ExportPkcs8PrivateKeyPem(),
            },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        CollectionAssert.Contains(result.Value!.Hosts.ToArray(), "cn-only.example");
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
