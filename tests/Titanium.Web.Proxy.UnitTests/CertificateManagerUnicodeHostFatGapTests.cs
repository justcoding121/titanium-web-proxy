#pragma warning disable TWP001
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>CertificateManager</c> suite: internationalized domain labels mint usable server leaves.
/// </summary>
[TestClass]
public class CertificateManagerUnicodeHostFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    /// <summary>
    ///     Bug class: punycode-adjacent Unicode host labels fail leaf minting.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_UnicodeLabel_HasPrivateKey()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("tëst-gap.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }
}
#pragma warning restore TWP001
