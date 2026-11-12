#pragma warning disable TWP001
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: custom issuer name and storage flags on in-memory mint.
/// </summary>
[TestClass]
public class CertificateManagerIssuerNameWave41FatGapTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task CustomIssuerName_AppearsOnRootAndLeaf()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast,
            SaveFakeCertificates = false,
        };
        mgr.RootCertificateIssuerName = "Wave41 Test CA";
        mgr.RootCertificateName = "Wave41 Root";
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.IsTrue(mgr.RootCertificate!.Subject.Contains("Wave41 Root", StringComparison.Ordinal),
            mgr.RootCertificate.Subject);
        using var leaf = await mgr.CreateServerCertificate("issuer.wave41.test");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task EphemeralStorageFlag_StillExportsLeafPrivateKey()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            StorageFlag = System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet,
        };
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("ephemeral.wave41.test");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        var ctx = mgr.CreateSslCertificateContext(leaf);
        Assert.IsTrue(ctx.TargetCertificate.HasPrivateKey);
    }
}
#pragma warning restore TWP001
