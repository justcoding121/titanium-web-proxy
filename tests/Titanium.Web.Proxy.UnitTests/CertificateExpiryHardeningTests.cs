#pragma warning disable TWP001
using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Certificates;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class CertificateExpiryHardeningTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(60_000)]
    public void CreateRootCertificate_UsesRootCertificateValidDays()
    {
        using var mgr = NewMgr();
        mgr.RootCertificateValidDays = 3650;
        mgr.CertificateValidDays = 396;
        Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: false));
        Assert.IsNotNull(mgr.RootCertificate);
        var remaining = mgr.RootCertificate!.NotAfter - DateTime.Now;
        Assert.IsTrue(remaining.TotalDays > 3600, remaining.ToString());
        Assert.IsTrue(remaining.TotalDays < 3700, remaining.ToString());
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_LeafStaysNearCertificateValidDays()
    {
        using var mgr = NewMgr();
        mgr.RootCertificateValidDays = 3650;
        mgr.CertificateValidDays = 396;
        Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: false));
        using var leaf = await mgr.CreateServerCertificate("leaf-days.example");
        Assert.IsNotNull(leaf);
        var remaining = leaf!.NotAfter - DateTime.Now;
        Assert.IsTrue(remaining.TotalDays > 390, remaining.ToString());
        Assert.IsTrue(remaining.TotalDays < 400, remaining.ToString());
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_ClampsLeafToRootNotAfter()
    {
        using var mgr = NewMgr();
        mgr.RootCertificateValidDays = 5;
        mgr.CertificateValidDays = 396;
        Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: false));
        using var leaf = await mgr.CreateServerCertificate("clamped.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.NotAfter <= mgr.RootCertificate!.NotAfter.AddMinutes(1),
            $"leaf={leaf.NotAfter:u} root={mgr.RootCertificate.NotAfter:u}");
        var remaining = leaf.NotAfter - DateTime.Now;
        Assert.IsTrue(remaining.TotalDays < 10, remaining.ToString());
    }

    [TestMethod]
    public void IsUsableLeaf_RejectsWithinRenewalMargin()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=margin", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddHours(12));
        Assert.IsFalse(CertificateManager.IsUsableLeaf(cert, DateTime.Now));
    }

    [TestMethod]
    public void IsUsableLeaf_AcceptsBeyondRenewalMargin()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=ok", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        Assert.IsTrue(CertificateManager.IsUsableLeaf(cert, DateTime.Now));
    }

    [TestMethod]
    public void ClampLeafNotAfter_NeverExceedsRoot()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=root", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var root = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(3));
        var proposed = DateTime.UtcNow.AddDays(100);
        var clamped = BcCertificateMakerFast.ClampLeafNotAfter(proposed, root);
        Assert.IsTrue(clamped <= root.NotAfter.ToUniversalTime().AddSeconds(1));
    }

    [TestMethod]
    public void SniCertificateSelector_PrefersValidBindingOverExpired()
    {
        using var expired = MakeLeaf("CN=expired", days: -1);
        using var valid = MakeLeaf("CN=valid", days: 60);
        var bindings = new[]
        {
            SniCertificateBinding.Create(["app.example"], expired),
            SniCertificateBinding.Create(["app.example"], valid),
        };

        var selected = SniCertificateSelector.Select("app.example", bindings, DateTime.UtcNow);
        Assert.AreSame(valid, selected);
    }

    [TestMethod]
    public void SniCertificateSelector_FallsBackWhenAllExpired()
    {
        using var first = MakeLeaf("CN=first", days: -2);
        using var second = MakeLeaf("CN=second", days: -1);
        var bindings = new[]
        {
            SniCertificateBinding.Create(["app.example"], first),
            SniCertificateBinding.Create(["app.example"], second),
        };

        var selected = SniCertificateSelector.Select("app.example", bindings, DateTime.UtcNow);
        Assert.AreSame(first, selected);
    }

    [TestMethod]
    public void SniCertificateSelector_ExactBeatsWildcard()
    {
        using var wildcard = MakeLeaf("CN=wild", days: 60);
        using var exact = MakeLeaf("CN=exact", days: 60);
        var bindings = new[]
        {
            SniCertificateBinding.Create(["*.example"], wildcard),
            SniCertificateBinding.Create(["app.example"], exact),
        };

        var selected = SniCertificateSelector.Select("app.example", bindings, DateTime.UtcNow);
        Assert.AreSame(exact, selected);
    }

    private static X509Certificate2 MakeLeaf(string subject, int days)
    {
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow.AddDays(-2);
        var notAfter = DateTimeOffset.UtcNow.AddDays(days);
        if (notAfter <= notBefore)
        {
            notBefore = notAfter.AddDays(-2);
        }

        return req.CreateSelfSigned(notBefore, notAfter);
    }
}
#pragma warning restore TWP001
