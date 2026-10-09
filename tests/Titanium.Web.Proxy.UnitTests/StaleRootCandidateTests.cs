using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression (2026-10-07): leftover roots from earlier CA regenerations break chain building. The cleanup
///     lists same-CN roots except the current one; this pins the predicate that decides what is "stale".
/// </summary>
[TestClass]
public class StaleRootCandidateTests
{
    private const string CommonName = "Titanium Stale Root Predicate CA";

    private static CertificateManager NewManager(string cn)
    {
        var mgr = new CertificateManager(cn, "TitaniumStaleRoot", false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        return mgr;
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void IsSameCommonNameStoreCandidate_ReturnsAllButCurrentThumbprint()
    {
        using var current = NewManager(CommonName);
        using var stale = NewManager(CommonName);
        var currentCert = current.RootCertificate!;
        var staleCert = stale.RootCertificate!;
        Assert.AreNotEqual(currentCert.Thumbprint, staleCert.Thumbprint);

        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(staleCert, CommonName, currentCert.Thumbprint),
            "A same-CN root that is not the current one is stale and must be listed.");
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(currentCert, CommonName, currentCert.Thumbprint),
            "The current root must never be listed for removal.");
        Assert.IsFalse(
            CertificateManager.IsSameCommonNameStoreCandidate(currentCert, CommonName, currentCert.Thumbprint.ToLowerInvariant()),
            "Thumbprint comparison must be case-insensitive.");
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(currentCert, CommonName, null),
            "Without a keep thumbprint every same-CN root is a candidate.");
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void IsSameCommonNameStoreCandidate_IgnoresOtherCommonNames()
    {
        using var other = NewManager("Some Unrelated Root CA");
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(other.RootCertificate!, CommonName, null),
            "Roots with a different CN must never be touched by the cleanup.");
    }
}
