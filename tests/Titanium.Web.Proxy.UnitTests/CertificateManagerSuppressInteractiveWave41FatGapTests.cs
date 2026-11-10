#pragma warning disable TWP001
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: interactive root-store suppression flags under test host.
/// </summary>
[TestClass]
public class CertificateManagerSuppressInteractiveWave41FatGapTests
{
    [TestMethod]
    public void AreInteractiveRootStoreMutationsSuppressed_UnderTestHost_IsTrue()
    {
        Assert.IsTrue(CertificateManager.AreInteractiveRootStoreMutationsSuppressed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void EnsureRoot_WhileSuppressed_DoesNotThrow()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance);
        mgr.EnsureRootCertificate();
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.IsFalse(mgr.IsRootCertificateUserTrusted());
        Assert.IsFalse(mgr.IsRootCertificateMachineTrusted());
    }
}
#pragma warning restore TWP001
