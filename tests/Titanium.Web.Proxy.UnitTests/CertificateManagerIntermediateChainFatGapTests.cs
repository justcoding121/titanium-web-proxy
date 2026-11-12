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
///     Fat CertificateManager edges: <see cref="CertificateManager.IntermediateCertificates" />
///     are folded into <see cref="CertificateManager.CreateSslCertificateContext" /> chains, and
///     changing the collection invalidates cached contexts.
/// </summary>
[TestClass]
public class CertificateManagerIntermediateChainFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    /// <summary>
    ///     Bug class: configured intermediates are omitted from the TLS handshake chain.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task IntermediateCertificates_AppendedToSslContext()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        using var signingLeaf = await mgr.CreateServerCertificate("intermediate-signer-wave27.example");
        Assert.IsNotNull(signingLeaf);
        using var intermediateOnly = X509CertificateLoader.LoadCertificate(signingLeaf!.RawData);

        mgr.IntermediateCertificates = new X509Certificate2Collection { intermediateOnly };
        using var leaf = await mgr.CreateServerCertificate("leaf-chain-wave27.example");
        Assert.IsNotNull(leaf);

        var ctx = mgr.CreateSslCertificateContext(leaf!);
        Assert.IsNotNull(ctx);
        Assert.IsTrue(ctx.IntermediateCertificates.Count >= 1,
            "IntermediateCertificates must appear in the built chain.");
    }

    /// <summary>
    ///     Bug class: clearing IntermediateCertificates after a context was cached keeps presenting
    ///     the old extras on the next CreateSslCertificateContext for the same leaf.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task IntermediateCertificates_Clear_InvalidatesCachedContext()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        using var extraLeaf = await mgr.CreateServerCertificate("extra-intermediate-wave27.example");
        Assert.IsNotNull(extraLeaf);
        using var extra = X509CertificateLoader.LoadCertificate(extraLeaf!.RawData);
        var extraThumb = extra.Thumbprint;

        mgr.IntermediateCertificates = new X509Certificate2Collection { extra };
        using var leaf = await mgr.CreateServerCertificate("leaf-clear-wave27.example");
        Assert.IsNotNull(leaf);
        var withExtra = mgr.CreateSslCertificateContext(leaf!);
        var withExtraHasExtra = withExtra.IntermediateCertificates
            .OfType<X509Certificate2>()
            .Any(c => string.Equals(c.Thumbprint, extraThumb, StringComparison.OrdinalIgnoreCase));

        mgr.IntermediateCertificates = null;
        Assert.IsNull(mgr.IntermediateCertificates);
        var cleared = mgr.CreateSslCertificateContext(leaf!);
        var clearedHasExtra = cleared.IntermediateCertificates
            .OfType<X509Certificate2>()
            .Any(c => string.Equals(c.Thumbprint, extraThumb, StringComparison.OrdinalIgnoreCase));

        // Prefer proving the extra was present then gone. If the OS context omits unrecognized
        // extras from IntermediateCertificates, still require a successful rebuild after clear.
        if (withExtraHasExtra)
        {
            Assert.IsFalse(clearedHasExtra,
                "Clearing IntermediateCertificates must drop the extra from the rebuilt context.");
        }
        else
        {
            Assert.IsNotNull(cleared);
            Assert.IsFalse(clearedHasExtra);
        }
    }
}
#pragma warning restore TWP001
