#pragma warning disable TWP001
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>CertificateManager</c> + <see cref="HttpHelper.GetWildCardDomainName" /> edges: wildcard
///     leaf mint for multi-label hosts and short second-level TLD guard (issue #769).
/// </summary>
[TestClass]
public class CertificateManagerWildcardMintFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
            DisableWildCardCertificates = false,
        };

    /// <summary>
    ///     Bug class: wildcard disabled by default regression — www subdomains must mint *.domain CN.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task WildcardEnabled_WwwHost_MintsWildcardCn()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        const string host = "www.wildcard-mint-gap.example";
        var certName = HttpHelper.GetWildCardDomainName(host, mgr.DisableWildCardCertificates);
        Assert.AreEqual("*.wildcard-mint-gap.example", certName);

        using var leaf = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(leaf);
        StringAssert.Contains(leaf!.Subject, "wildcard-mint-gap.example");
    }

    /// <summary>
    ///     Bug class: short second-level labels (vn.ua) incorrectly receive wildcard subjects.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task WildcardEnabled_ShortSecondLevel_MintsExactHost()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        const string host = "api.pay.vn.ua";
        var certName = HttpHelper.GetWildCardDomainName(host, false);
        Assert.AreEqual(host, certName);

        using var leaf = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(leaf);
        StringAssert.Contains(leaf!.Subject, "api.pay.vn.ua");
        Assert.IsFalse(leaf.Subject.Contains('*'));
    }
}
#pragma warning restore TWP001
