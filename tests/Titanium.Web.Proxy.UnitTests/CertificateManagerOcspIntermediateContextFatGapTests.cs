#pragma warning disable TWP001
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: intermediate bag + <see cref="CertificateManager.OcspStapling" />
///     rebuilds <c>SslStreamCertificateContext</c> without throwing.
/// </summary>
[TestClass]
public class CertificateManagerOcspIntermediateContextFatGapTests
{
  /// <summary>
  ///     Bug class: OCSP stapling toggle with <see cref="CertificateManager.IntermediateCertificates" /> keeps a stale offline context.
  /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public void OcspStapling_WithIntermediate_RebuildsSslContext()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };
        mgr.CreateRootCertificate(false);
        mgr.IntermediateCertificates = new X509Certificate2Collection { mgr.RootCertificate! };
        using var leaf = mgr.CreateCertificate("ocsp-intermediate.wave39.test", false)!;

        var offline = mgr.CreateSslCertificateContext(leaf);
        mgr.OcspStapling = true;
        var online = mgr.CreateSslCertificateContext(leaf);
        mgr.OcspStapling = false;
        var restored = mgr.CreateSslCertificateContext(leaf);

        Assert.IsNotNull(offline);
        Assert.IsNotNull(online);
        Assert.IsNotNull(restored);
        Assert.AreNotSame(offline, online);
        Assert.AreNotSame(online, restored);
    }
}
#pragma warning restore TWP001
