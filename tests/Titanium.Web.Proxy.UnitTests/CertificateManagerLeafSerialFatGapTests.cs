#pragma warning disable TWP001
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: distinct hosts mint distinct leaf serials.
/// </summary>
[TestClass]
public class CertificateManagerLeafSerialFatGapTests
{
    /// <summary>
    ///     Bug class: cache collisions reuse the same serial across different host mints.
    /// </summary>
    [TestMethod]
    public async Task DistinctHosts_DistinctSerialNumbers()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
            CertificateValidDays = 7,
        };
        mgr.CreateRootCertificate(false);

        using var a = await mgr.CreateServerCertificate("host-a.gap.test");
        using var b = await mgr.CreateServerCertificate("host-b.gap.test");
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(a!.SerialNumber, b!.SerialNumber);
    }
}
#pragma warning restore TWP001
