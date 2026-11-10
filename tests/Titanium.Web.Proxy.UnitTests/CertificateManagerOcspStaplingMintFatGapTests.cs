#pragma warning disable TWP001
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> OCSP stapling and SSL context mint edges.
/// </summary>
[TestClass]
public class CertificateManagerOcspStaplingMintFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    /// <summary>
    ///     Bug class: <see cref="CertificateManager.OcspStapling" /> true breaks in-memory context creation.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task OcspStapling_On_CreateSslContext_Succeeds()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        mgr.OcspStapling = true;
        using var leaf = await mgr.CreateServerCertificate("ocsp-on-wave37.test");
        Assert.IsNotNull(leaf);
        var ctx = mgr.CreateSslCertificateContext(leaf!);
        Assert.IsNotNull(ctx);
    }

    /// <summary>
    ///     Bug class: <see cref="CertificateManager.ConfigureServerCertificate" /> leaves context set after stapling toggle.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task ConfigureServerCertificate_AfterOcspToggle_PresentsLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("ocsp-cfg-wave37.test");
        Assert.IsNotNull(leaf);
        mgr.OcspStapling = true;
        var options = new System.Net.Security.SslServerAuthenticationOptions();
        mgr.ConfigureServerCertificate(options, leaf!);
        Assert.IsTrue(options.ServerCertificateContext is not null || options.ServerCertificate is not null);
        mgr.OcspStapling = false;
        var options2 = new System.Net.Security.SslServerAuthenticationOptions();
        mgr.ConfigureServerCertificate(options2, leaf!);
        Assert.IsTrue(options2.ServerCertificateContext is not null || options2.ServerCertificate is not null);
    }
}
#pragma warning restore TWP001
