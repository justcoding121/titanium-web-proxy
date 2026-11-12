#pragma warning disable TWP001
using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: IntermediateCertificates bag, IPv6 host mint,
///     StorageFlag Exportable, and EnsureRootCertificate after CreateRoot.
/// </summary>
[TestClass]
public class CertificateManagerValidDaysAndClearFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(60_000)]
    public async Task IntermediateCertificates_EmptyBag_StillIssuesLeaf()
    {
        using var mgr = NewMgr();
        mgr.IntermediateCertificates = new X509Certificate2Collection();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("intermediate-bag-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        Assert.IsNotNull(mgr.IntermediateCertificates);
        Assert.AreEqual(0, mgr.IntermediateCertificates.Count);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_Ipv6Literal_HasPrivateKey()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("::1");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task StorageFlag_Exportable_IssuesLeafWithPrivateKey()
    {
        using var mgr = NewMgr();
        mgr.StorageFlag = X509KeyStorageFlags.Exportable;
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("storage-exportable-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        Assert.AreEqual(X509KeyStorageFlags.Exportable, mgr.StorageFlag);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void EnsureRootCertificate_AfterCreate_IsIdempotent()
    {
        using var mgr = NewMgr();
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        var thumb = mgr.RootCertificate!.Thumbprint;
        mgr.EnsureRootCertificate();
        Assert.AreEqual(thumb, mgr.RootCertificate!.Thumbprint);
        Assert.IsTrue(mgr.CertValidated);
    }
}
#pragma warning restore TWP001
