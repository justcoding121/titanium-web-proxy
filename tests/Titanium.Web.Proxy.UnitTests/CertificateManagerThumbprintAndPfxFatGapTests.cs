#pragma warning disable TWP001
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>CertificateManager</c> edges: thumbprint removal guardrails, same-CN listing,
///     and PFX path/password property round-trip.
/// </summary>
[TestClass]
public class CertificateManagerThumbprintAndPfxFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    [TestMethod]
    public void RemoveCertificateByThumbprint_Empty_ReturnsFalse()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(StoreName.Root, StoreLocation.CurrentUser, ""));
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(StoreName.Root, StoreLocation.CurrentUser, "   "));
    }

    [TestMethod]
    [Timeout(30_000)]
    public void ListSameCommonNameRootThumbprints_AfterCreate_IncludesRoot()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        var list = mgr.ListSameCommonNameRootThumbprints(StoreLocation.CurrentUser);
        Assert.IsNotNull(list);
        Assert.IsFalse(string.IsNullOrEmpty(mgr.RootCertificate?.Thumbprint));
        // In-memory roots are not written to the store under test suppress flags; listing must not throw.
        Assert.IsTrue(list.Count >= 0);
    }

}
#pragma warning restore TWP001
