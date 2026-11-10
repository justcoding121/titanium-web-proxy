#pragma warning disable TWP001
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: parallel mint during root rotation must not return dead handles.
/// </summary>
[TestClass]
public class CertificateManagerClearRootDuringParallelMintFatGapTests
{
    /// <summary>
    ///     Bug class: <see cref="CertificateManager.ClearRootCertificate" /> during burst mint surfaces
    ///     CryptographicException (invalid handle) on newly returned leaves.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task ParallelMint_WhileClearRoot_ReturnsOnlyLiveHandles()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };
        mgr.CreateRootCertificate(false);
        _ = await mgr.CreateServerCertificate("warm.wave40.test");

        using var cts = new CancellationTokenSource();
        var clearLoop = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                mgr.ClearRootCertificate();
                mgr.CreateRootCertificate(false);
                await Task.Delay(8, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        var mintTasks = Enumerable.Range(0, 20)
            .Select(async i =>
            {
                var leaf = await mgr.CreateServerCertificate($"mid-clear-{i}.wave40.test").ConfigureAwait(false);
                if (leaf is null) return null;
                _ = leaf.Thumbprint;
                Assert.IsTrue(leaf.HasPrivateKey);
                return leaf;
            })
            .ToArray();

        await Task.Delay(2_500).ConfigureAwait(false);
        cts.Cancel();
        try { await clearLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        var invalidOnReturn = 0;
        foreach (var task in mintTasks)
        {
            try
            {
                _ = await task.ConfigureAwait(false);
            }
            catch (CryptographicException)
            {
                invalidOnReturn++;
            }
        }

        Assert.AreEqual(0, invalidOnReturn,
            "CreateServerCertificate must not hand back disposed leaves at return time.");

        foreach (var task in mintTasks)
        {
            var leaf = await task.ConfigureAwait(false);
            leaf?.Dispose();
        }
    }
}
#pragma warning restore TWP001
