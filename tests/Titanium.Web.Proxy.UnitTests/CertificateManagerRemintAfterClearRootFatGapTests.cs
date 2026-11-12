#pragma warning disable TWP001
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: remint after ClearRoot must recreate a live leaf.
/// </summary>
[TestClass]
public class CertificateManagerRemintAfterClearRootFatGapTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task ClearRoot_ThenMint_ReturnsLiveLeafWithPrivateKey()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };
        mgr.CreateRootCertificate(false);
        var first = await mgr.CreateServerCertificate("remint-before.wave44.test");
        Assert.IsNotNull(first);
        Assert.IsTrue(first!.HasPrivateKey);
        first.Dispose();

        mgr.ClearRootCertificate();
        mgr.CreateRootCertificate(false);
        var second = await mgr.CreateServerCertificate("remint-after.wave44.test");
        Assert.IsNotNull(second);
        Assert.IsTrue(second!.HasPrivateKey);
        _ = second.Thumbprint;
        second.Dispose();
    }
}
#pragma warning restore TWP001
