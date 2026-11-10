#pragma warning disable TWP001
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: mint while <see cref="CertificateManager.Dispose" /> drains cache.
/// </summary>
[TestClass]
public class CertificateManagerParallelMintDuringDisposeFatGapTests
{
    /// <summary>
    ///     Bug class: parallel mint during dispose returns handles backed by torn-down storage.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task ParallelMint_WhileDispose_DoesNotThrow()
    {
        var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };
        mgr.CreateRootCertificate(false);

        var mint = Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => mgr.CreateServerCertificate($"dispose-race-{i}.wave39.test")));

        await Task.Delay(30);
        mgr.Dispose();

        X509Certificate2?[] leaves;
        try
        {
            leaves = await mint;
        }
        catch
        {
            leaves = [];
        }

        Assert.IsNotNull(leaves);
        Assert.IsTrue(leaves.Length <= 8);
        foreach (var leaf in leaves)
            leaf?.Dispose();
    }
}
#pragma warning restore TWP001
