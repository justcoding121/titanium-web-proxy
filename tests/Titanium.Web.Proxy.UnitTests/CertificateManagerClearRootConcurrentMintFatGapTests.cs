#pragma warning disable TWP001
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: concurrent mint during root rotation must not return disposed leaves.
/// </summary>
[TestClass]
public class CertificateManagerClearRootConcurrentMintFatGapTests
{
    /// <summary>
    ///     Bug class: after <see cref="CertificateManager.ClearRootCertificate" />, burst mint must not return invalid handles.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task ClearRoot_ThenParallelMint_AllLeavesUsable()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };
        mgr.CreateRootCertificate(false);
        _ = await mgr.CreateServerCertificate("warm.wave39.test");

        mgr.ClearRootCertificate();
        mgr.CreateRootCertificate(false);

        var mintTasks = Enumerable.Range(0, 10)
            .Select(i => mgr.CreateServerCertificate($"post-clear-{i}.wave39.test"))
            .ToArray();
        var leaves = await Task.WhenAll(mintTasks);

        try
        {
            Assert.IsTrue(leaves.All(l => l is not null && l.HasPrivateKey));
        }
        finally
        {
            foreach (var leaf in leaves)
                leaf?.Dispose();
        }
    }
}
#pragma warning restore TWP001
