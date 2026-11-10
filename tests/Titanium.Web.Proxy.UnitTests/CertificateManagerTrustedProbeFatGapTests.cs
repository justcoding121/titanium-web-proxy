#pragma warning disable TWP001
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> edges: trust probe booleans under store-ui suppression.
/// </summary>
[TestClass]
public class CertificateManagerTrustedProbeFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(30_000)]
    public void IsRootCertificateUserTrusted_AfterCreate_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        _ = mgr.IsRootCertificateUserTrusted();
        Assert.IsNotNull(mgr.RootCertificate);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void IsRootCertificateMachineTrusted_AfterCreate_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        _ = mgr.IsRootCertificateMachineTrusted();
        Assert.IsNotNull(mgr.RootCertificate);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void TrustAndRemoveTrusted_UnderSuppress_DoesNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        mgr.TrustRootCertificate(machineTrusted: false);
        mgr.RemoveTrustedRootCertificate(machineTrusted: false);
        _ = mgr.IsRootCertificateUserTrusted();
        Assert.IsNotNull(mgr.RootCertificate);
    }
}
#pragma warning restore TWP001
