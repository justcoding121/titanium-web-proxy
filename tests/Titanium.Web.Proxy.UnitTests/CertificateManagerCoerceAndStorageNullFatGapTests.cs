#pragma warning disable TWP001
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: CoerceEngineForPlatform, CertificateStorage null
///     fallback, empty thumbprint remove, and cache counters after leaf issue.
/// </summary>
[TestClass]
public class CertificateManagerCoerceAndStorageNullFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    public void CoerceEngineForPlatform_DefaultWindows_OffWindows_BecomesBouncyCastle()
    {
        Assert.AreEqual(
            CertificateEngine.BouncyCastle,
            CertificateManager.CoerceEngineForPlatform(CertificateEngine.DefaultWindows, isWindows: false));
        Assert.AreEqual(
            CertificateEngine.DefaultWindows,
            CertificateManager.CoerceEngineForPlatform(CertificateEngine.DefaultWindows, isWindows: true));
        Assert.AreEqual(
            CertificateEngine.BouncyCastleFast,
            CertificateManager.CoerceEngineForPlatform(CertificateEngine.BouncyCastleFast, isWindows: false));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CertificateStorage_SetNull_FallsBackAndStillIssuesLeaf()
    {
        using var mgr = NewMgr();
        mgr.CertificateStorage = null!;
        Assert.IsNotNull(mgr.CertificateStorage);
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("storage-null-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void RemoveCertificateByThumbprint_Empty_ReturnsFalse()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(
            System.Security.Cryptography.X509Certificates.StoreName.Root,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            string.Empty));
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(
            System.Security.Cryptography.X509Certificates.StoreName.Root,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            "   "));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_RaisesCachedCertificateCount_AndCertValidated()
    {
        using var mgr = NewMgr();
        Assert.IsFalse(mgr.CertValidated);
        Assert.AreEqual(0, mgr.CachedCertificateCount);
        mgr.CreateRootCertificate(false);
        Assert.IsTrue(mgr.CertValidated);
        using var leaf = await mgr.CreateServerCertificate("cache-count-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(mgr.CachedCertificateCount >= 1);
    }
}
#pragma warning restore TWP001
