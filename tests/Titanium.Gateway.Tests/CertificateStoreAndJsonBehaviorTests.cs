using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     CertificateStore PEM→PFX conversion and GatewayJson time formatting.
///     Bug classes: mismatched key pairs still yield PFX; resourceVersion bumps reuse stale cache; microtime loses fractional seconds.
/// </summary>
[TestClass]
public sealed class CertificateStoreAndJsonBehaviorTests
{
    [TestMethod]
    public void CertificateStore_ConvertsMatchingPem_AndInvalidatesOnVersionBump()
    {
        var (cert, key) = SelfSigned("store.example");
        var store = new CertificateStore();
        var secret = new SecretData("default", "tls", "kubernetes.io/tls", "1", cert, key);

        var pfx = store.GetPfx(secret);
        Assert.IsNotNull(pfx);
        Assert.IsTrue(pfx!.Length > 0);
        Assert.AreSame(pfx, store.GetPfx(secret), "same resourceVersion must reuse the cache entry");
        Assert.IsNotNull(store.GetPem(secret, TlsFileKind.CertificatePem));
        Assert.IsNotNull(store.GetPem(secret, TlsFileKind.PrivateKeyPem));

        var bumped = secret with { ResourceVersion = "2" };
        var again = store.GetPfx(bumped);
        Assert.IsNotNull(again);
        Assert.AreNotSame(pfx, again, "a resourceVersion bump must rebuild the PFX");
        Assert.IsTrue(again!.Length > 0);

        store.Retain([bumped]);
        Assert.IsNotNull(store.GetPfx(bumped));
        store.Retain([]);
        Assert.IsNotNull(store.GetPfx(bumped with { ResourceVersion = "3" }), "retain drop must force a rebuild on next get");
    }

    [TestMethod]
    public void CertificateStore_MismatchedKey_ReturnsNull_AndHasCaRequiresParseablePem()
    {
        var (cert, _) = SelfSigned("a.example");
        var (_, otherKey) = SelfSigned("b.example");
        var store = new CertificateStore();
        Assert.IsNull(store.GetPfx(new SecretData("ns", "bad", "kubernetes.io/tls", "1", cert, otherKey)));

        Assert.IsFalse(store.HasCa(new SecretData("ns", "noca", null, "1", null, null)));
        Assert.IsFalse(store.HasCa(new SecretData("ns", "badca", null, "1", null, null, Encoding.UTF8.GetBytes("not-a-cert"))));
        Assert.IsTrue(store.HasCa(new SecretData("ns", "ca", null, "1", null, null, cert)));
        Assert.IsNotNull(store.GetPem(new SecretData("ns", "ca", null, "1", null, null, cert), TlsFileKind.CaPem));
    }

    [TestMethod]
    public void GatewayJson_FormatsTimeAndMicroTime_AndRoundTripsDateTimeOffset()
    {
        var value = new DateTimeOffset(2024, 5, 6, 7, 8, 9, 123, TimeSpan.Zero).AddTicks(4560);
        Assert.AreEqual("2024-05-06T07:08:09Z", GatewayJson.FormatTime(value));
        StringAssert.StartsWith(GatewayJson.FormatMicroTime(value), "2024-05-06T07:08:09.");
        StringAssert.EndsWith(GatewayJson.FormatMicroTime(value), "Z");

        var json = JsonSerializer.Serialize(new Wrapper { When = value }, GatewayJson.Options);
        var back = JsonSerializer.Deserialize<Wrapper>(json, GatewayJson.Options);
        Assert.IsNotNull(back);
        Assert.AreEqual(value.ToUnixTimeSeconds(), back!.When.ToUnixTimeSeconds());
    }

    private sealed class Wrapper
    {
        public DateTimeOffset When { get; set; }
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
