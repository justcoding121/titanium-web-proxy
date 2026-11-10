#pragma warning disable TWP001
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: LoadRootCertificate miss, ListSameCommonName,
///     ApplyUnixSslTrust under suppress, and CertificateStorage round-trip.
/// </summary>
[TestClass]
public class CertificateManagerLoadFailAndListCnFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    public void LoadRootCertificate_MissingFile_ReturnsFalse()
    {
        using var mgr = NewMgr();
        var missing = Path.Combine(Path.GetTempPath(), "twp-missing-" + Guid.NewGuid().ToString("N") + ".pfx");
        Assert.IsFalse(File.Exists(missing));
        Assert.IsFalse(mgr.LoadRootCertificate(missing, "pw", overwritePfXFile: false));
        Assert.IsNull(mgr.RootCertificate);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void ApplyUnixSslTrust_UnderSuppress_SetsCancelledOrWindowsOk()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        mgr.ApplyUnixSslTrustAfterStoreInstall(false);
        Assert.IsNotNull(mgr.LastOsTrustResult);
        if (RunTime.IsWindows)
        {
            Assert.IsTrue(mgr.LastOsTrustResult!.Succeeded,
                "Windows ApplyUnixSslTrust short-circuits as Ok.");
        }
        else
        {
            Assert.AreEqual(CertificateOsTrustKind.Cancelled, mgr.LastOsTrustResult!.Kind);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public void ListSameCommonNameRootThumbprints_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        var listed = mgr.ListSameCommonNameRootThumbprints(StoreLocation.CurrentUser);
        Assert.IsNotNull(listed);
        // May be empty when Root store has no matching CN (tests suppress installs).
        _ = mgr.ListSameCommonNameRootThumbprints(StoreLocation.CurrentUser,
            keepThumbprint: mgr.RootCertificate!.Thumbprint);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CertificateStorage_DefaultDiskCache_RoundTripAssign()
    {
        using var mgr = NewMgr();
        var prior = mgr.CertificateStorage;
        Assert.IsNotNull(prior);
        mgr.CertificateStorage = prior;
        Assert.AreSame(prior, mgr.CertificateStorage);
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("storage-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void TrustRootCertificate_UnderSuppress_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        mgr.TrustRootCertificate(false);
        mgr.TrustRootCertificate(true);
        Assert.IsNotNull(mgr.RootCertificate);
    }
}
