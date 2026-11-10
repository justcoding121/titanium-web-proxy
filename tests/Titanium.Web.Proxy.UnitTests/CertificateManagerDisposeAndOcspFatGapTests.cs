#pragma warning disable TWP001
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> edges: <see cref="CertificateManager.Dispose" />,
///     <see cref="CertificateManager.OcspStapling" /> context invalidation, and pending-disposal drain.
/// </summary>
[TestClass]
public class CertificateManagerDisposeAndOcspFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    /// <summary>
    ///     Bug class: <see cref="CertificateManager.Dispose" /> leaves cached leaf handles live.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task Dispose_AfterMint_DrainsCache_AndSecondDisposeIsSafe()
    {
        var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        _ = await mgr.CreateServerCertificate("dispose-wave34.test");
        Assert.IsTrue(mgr.CachedCertificateCount >= 1);
        mgr.Dispose();
        Assert.AreEqual(0, mgr.CachedCertificateCount);
        mgr.Dispose();
    }

    /// <summary>
    ///     Bug class: toggling <see cref="CertificateManager.OcspStapling" /> keeps stale
    ///     <c>SslStreamCertificateContext</c> entries.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public void OcspStapling_FalseToTrueToFalse_RebuildsSslContext()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = mgr.CreateCertificate("ocsp-wave34.test", false)!;
        var offline = mgr.CreateSslCertificateContext(leaf);
        mgr.OcspStapling = true;
        mgr.OcspStapling = false;
        var rebuilt = mgr.CreateSslCertificateContext(leaf);
        Assert.IsNotNull(rebuilt);
        Assert.AreNotSame(offline, rebuilt);
    }
}
#pragma warning restore TWP001
