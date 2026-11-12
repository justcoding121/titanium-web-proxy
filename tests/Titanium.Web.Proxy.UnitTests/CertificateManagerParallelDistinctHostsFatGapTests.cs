#pragma warning disable TWP001
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: concurrent mint for distinct hosts must not cross-contaminate cache entries.
/// </summary>
[TestClass]
public class CertificateManagerParallelDistinctHostsFatGapTests
{
    /// <summary>
    ///     Bug class: parallel mint for different hosts races the storage layer and returns disposed leaves.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task CreateServerCertificate_ParallelDistinctHosts_AllUsable()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };
        mgr.CreateRootCertificate(false);

        var tasks = Enumerable.Range(0, 12)
            .Select(i => mgr.CreateServerCertificate($"parallel-{i}.wave37.test"))
            .ToArray();
        var leaves = await Task.WhenAll(tasks);
        try
        {
            Assert.AreEqual(12, leaves.Length);
            Assert.IsTrue(leaves.All(l => l is not null && l.HasPrivateKey));
            var distinctThumbs = leaves.Select(l => l!.Thumbprint).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Assert.AreEqual(12, distinctThumbs, "Each host must receive its own leaf.");
        }
        finally
        {
            foreach (var leaf in leaves)
                leaf?.Dispose();
        }
    }
}
#pragma warning restore TWP001
