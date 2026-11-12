using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     CertificateStore.GetPem default arm: unknown TlsFileKind must fall back to the PFX bytes.
/// </summary>
[TestClass]
public sealed class CertificateStoreGetPemDefaultBehaviorTests
{
    [TestMethod]
    public void GetPem_UnknownKind_ReturnsPfxBytes()
    {
        var (cert, key) = SelfSigned("pem-default.example");
        var store = new CertificateStore();
        var secret = new SecretData("default", "tls", "kubernetes.io/tls", "1", cert, key);
        var pfx = store.GetPfx(secret);
        Assert.IsNotNull(pfx);

        // Bug class: a future TlsFileKind must not return null PEM and break data-plane file emission.
        var fallback = store.GetPem(secret, (TlsFileKind)999);
        Assert.AreSame(pfx, fallback);
        Assert.IsNull(store.GetPem(
            new SecretData("default", "empty", "kubernetes.io/tls", "1", null, null),
            (TlsFileKind)999));
    }

    private static (byte[] CertPem, byte[] KeyPem) SelfSigned(string dns)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + dns, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (Encoding.UTF8.GetBytes(cert.ExportCertificatePem()), Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
    }
}
