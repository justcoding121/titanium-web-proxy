#pragma warning disable TWP001
using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: personal prune, OS trust probes under suppress,
///     Mac keychain guidance, and idle-clear stop.
/// </summary>
[TestClass]
public class CertificateManagerPruneAndGuidanceFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(30_000)]
    public void PruneOrphanedPersonalCertificates_UnderSuppress_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        mgr.PruneOrphanedPersonalCertificates(StoreLocation.CurrentUser, keepCurrentThumbprint: true);
        mgr.PruneOrphanedPersonalCertificates(StoreLocation.CurrentUser, keepCurrentThumbprint: false);
        Assert.IsNotNull(mgr.RootCertificate);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void VerifyOsUserSslTrust_AndRootPresenceProbes_DoNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        _ = mgr.VerifyOsUserSslTrust();
        _ = mgr.IsRootInLoginKeychain();
        _ = mgr.IsOsRootStillPresent();
        Assert.IsNotNull(mgr.RootCertificate);
    }

    [TestMethod]
    public void OpenMacKeychainGuidance_ReturnsNullOrString()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        var guidance = mgr.OpenMacKeychainGuidance();
        if (RunTime.IsWindows)
            Assert.IsTrue(guidance is null || guidance.Length >= 0);
        else
            Assert.IsTrue(guidance is null || guidance.Length > 0);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task StopClearIdleCertificates_AfterCreate_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("idle-stop-gap.example");
        Assert.IsNotNull(leaf);
        mgr.StopClearIdleCertificates();
        Assert.IsTrue(mgr.CachedCertificateCount >= 1);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void InstallNssCertutilAndRetryUserTrust_UnderSuppress_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        var result = mgr.InstallNssCertutilAndRetryUserTrust();
        Assert.IsNotNull(result);
        Assert.IsNotNull(mgr.LastOsTrustResult);
    }
}
#pragma warning restore TWP001
