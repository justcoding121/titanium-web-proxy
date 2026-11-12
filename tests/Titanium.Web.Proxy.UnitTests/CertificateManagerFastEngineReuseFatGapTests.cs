#pragma warning disable TWP001
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>CertificateManager</c> edges: <see cref="CertificateEngine.BouncyCastleFast" />
///     issues distinct host names while reusing the fast-path key pair.
/// </summary>
[TestClass]
public class CertificateManagerFastEngineReuseFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast,
            SaveFakeCertificates = false,
        };

    /// <summary>
    ///     Bug class: switching to BouncyCastleFast leaves CertificateEngine stuck on BouncyCastle.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task BouncyCastleFast_TwoHosts_BothIssueLeaves()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);

        using var a = await mgr.CreateServerCertificate("fast-a-wave29.example");
        using var b = await mgr.CreateServerCertificate("fast-b-wave29.example");
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(a.Thumbprint, b.Thumbprint);
        Assert.AreEqual(CertificateEngine.BouncyCastleFast, mgr.CertificateEngine);
    }
}
#pragma warning restore TWP001
