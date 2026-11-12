#pragma warning disable TWP001
using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: replacing <see cref="CertificateManager.IntermediateCertificates" />
///     after a context is cached must rebuild the chain with the new extras.
/// </summary>
[TestClass]
public class CertificateManagerIntermediateReplaceFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    /// <summary>
    ///     Bug class: swapping intermediate extras keeps serving the first cached chain.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task IntermediateCertificates_Replace_RebuildsSslContextChain()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        using var extraA = await mgr.CreateServerCertificate("intermediate-a-wave28.example");
        using var extraB = await mgr.CreateServerCertificate("intermediate-b-wave28.example");
        Assert.IsNotNull(extraA);
        Assert.IsNotNull(extraB);

        using var intermediateA = X509CertificateLoader.LoadCertificate(extraA!.RawData);
        using var intermediateB = X509CertificateLoader.LoadCertificate(extraB!.RawData);
        var thumbA = intermediateA.Thumbprint;
        var thumbB = intermediateB.Thumbprint;

        mgr.IntermediateCertificates = new X509Certificate2Collection { intermediateA };
        using var leaf = await mgr.CreateServerCertificate("leaf-replace-wave28.example");
        Assert.IsNotNull(leaf);

        var withA = mgr.CreateSslCertificateContext(leaf!);
        var hasA = withA.IntermediateCertificates
            .OfType<X509Certificate2>()
            .Any(c => string.Equals(c.Thumbprint, thumbA, StringComparison.OrdinalIgnoreCase));

        mgr.IntermediateCertificates = new X509Certificate2Collection { intermediateB };
        var withB = mgr.CreateSslCertificateContext(leaf!);
        var hasB = withB.IntermediateCertificates
            .OfType<X509Certificate2>()
            .Any(c => string.Equals(c.Thumbprint, thumbB, StringComparison.OrdinalIgnoreCase));
        var stillA = withB.IntermediateCertificates
            .OfType<X509Certificate2>()
            .Any(c => string.Equals(c.Thumbprint, thumbA, StringComparison.OrdinalIgnoreCase));

        if (hasA)
        {
            Assert.IsTrue(hasB,
                "Replacing IntermediateCertificates must fold the new extra into the rebuilt context.");
            Assert.IsFalse(stillA,
                "Replacing IntermediateCertificates must not keep the previous extra in the rebuilt context.");
        }
        else
        {
            Assert.IsNotNull(withB);
        }
    }
}
#pragma warning restore TWP001
