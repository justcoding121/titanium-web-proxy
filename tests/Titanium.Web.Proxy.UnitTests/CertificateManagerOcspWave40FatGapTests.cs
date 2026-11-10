#pragma warning disable TWP001
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: SSL context creation after parallel mint and root intact.
/// </summary>
[TestClass]
public class CertificateManagerOcspWave40FatGapTests
{
    [TestMethod]
    [Timeout(90_000)]
    public async Task CreateSslCertificateContext_AfterParallelMint_TargetsLiveLeaf()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast,
        };
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("ctx-parallel.wave40.test");
        Assert.IsNotNull(leaf);
        var ctx = mgr.CreateSslCertificateContext(leaf!);
        Assert.AreSame(leaf, ctx.TargetCertificate);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ClearRoot_ThenRemint_SslContextUsesFreshLeaf()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);
        const string host = "remint-same-host.wave40.test";
        using var before = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(before);
        var thumbBefore = before!.Thumbprint;
        mgr.ClearRootCertificate();
        mgr.CreateRootCertificate(false);
        using var after = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(after);
        Assert.AreNotEqual(thumbBefore, after!.Thumbprint, "Clear+remint must sign a fresh leaf for the same host.");
        var ctxAfter = mgr.CreateSslCertificateContext(after);
        Assert.IsTrue(ctxAfter.TargetCertificate.HasPrivateKey);
    }
}
#pragma warning restore TWP001
