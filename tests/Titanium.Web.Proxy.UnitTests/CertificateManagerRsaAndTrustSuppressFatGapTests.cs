#pragma warning disable TWP001
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: explicit RSA leaf algorithm, trust/remove under
///     interactive-store suppression (no OS prompts), and certificate cache clear.
/// </summary>
[TestClass]
public class CertificateManagerRsaAndTrustSuppressFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(90_000)]
    public async Task Rsa2048_LeafAlgorithm_IssuesLeafWithPrivateKey()
    {
        using var mgr = NewMgr();
        mgr.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.Rsa2048;
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("rsa2048-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        Assert.AreEqual(CertificateKeyAlgorithm.Rsa2048, mgr.LeafCertificateKeyAlgorithm);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task EcdsaP256_LeafAlgorithm_IssuesLeafWithPrivateKey()
    {
        using var mgr = NewMgr();
        mgr.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256;
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("ecdsa-p256-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        Assert.AreEqual(CertificateKeyAlgorithm.EcdsaP256, mgr.LeafCertificateKeyAlgorithm);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void TrustAndRemoveRoot_UnderSuppress_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        // Tests set ShouldSuppressInteractiveRootStoreMutations; trust/remove must be no-ops.
        mgr.TrustRootCertificate(false);
        mgr.TrustRootCertificate(true);
        Assert.IsTrue(mgr.RemoveTrustedRootCertificateAsAdmin(false));
        Assert.IsTrue(mgr.RemoveTrustedRootCertificateAsAdmin(true));
        mgr.RemoveTrustedRootCertificate(false);
        mgr.RemoveTrustedRootCertificate(true);
        Assert.IsNotNull(mgr.RootCertificate);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ClearRootCertificate_ClearsLeafCache_AllowsReissueUnderNewRoot()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var first = await mgr.CreateServerCertificate("clear-cache.gap.test");
        Assert.IsNotNull(first);
        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);
        mgr.CreateRootCertificate(false);
        using var second = await mgr.CreateServerCertificate("clear-cache.gap.test");
        Assert.IsNotNull(second);
        Assert.IsTrue(second!.HasPrivateKey);
    }

    [TestMethod]
    public void LeafRsaKeyPairBufferSize_RoundTrip()
    {
        var prior = CertificateManager.LeafRsaKeyPairBufferSize;
        try
        {
            CertificateManager.LeafRsaKeyPairBufferSize = 4;
            Assert.AreEqual(4, CertificateManager.LeafRsaKeyPairBufferSize);
            CertificateManager.LeafRsaKeyPairBufferSize = 8;
            Assert.AreEqual(8, CertificateManager.LeafRsaKeyPairBufferSize);
        }
        finally
        {
            CertificateManager.LeafRsaKeyPairBufferSize = prior;
        }
    }
}
