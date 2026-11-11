#pragma warning disable TWP001
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="CertificateManager" /> suite: disposed cache entries, ClearRoot generation
///     races during mint, and Dispose after the mint CTS is already torn down.
/// </summary>
[TestClass]
public class CertificateManagerDeadHandleAndGenerationFatGapTests
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    [Timeout(60_000)]
    public async Task DisposedCachedLeaf_IsEvictedAndReminted()
    {
        using var mgr = NewManager();
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        const string host = "dead-cache.wave44.test";
        var first = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(first);
        var firstThumb = first!.Thumbprint;
        // Caller dispose of the returned instance also kills the cache entry (same object).
        first.Dispose();

        var second = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(second);
        Assert.IsTrue(second!.HasPrivateKey);
        Assert.AreNotEqual(firstThumb, second.Thumbprint,
            "disposed cache entry must be reminted rather than served");
        second.Dispose();
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task InjectedDeadPendingMint_IsDiscardedAndRetried()
    {
        using var mgr = NewManager();
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        const string host = "dead-pending.wave44.test";
        var dead = MakeDisposedSelfSigned(host);

        var pendingLock = (SemaphoreSlim)typeof(CertificateManager)
            .GetField("pendingCertificateCreationTaskLock", PrivateInstance)!.GetValue(mgr)!;
        var pending = (Dictionary<string, Task<X509Certificate2?>>)typeof(CertificateManager)
            .GetField("pendingCertificateCreationTasks", PrivateInstance)!.GetValue(mgr)!;

        await pendingLock.WaitAsync();
        try
        {
            pending[host] = Task.FromResult<X509Certificate2?>(dead);
        }
        finally
        {
            pendingLock.Release();
        }

        var leaf = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        _ = leaf.Thumbprint;
        leaf.Dispose();
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ClearRoot_WhileMintBlockedOnThrottle_RetriesAfterGenerationBump()
    {
        using var mgr = NewManager();
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        _ = await mgr.CreateServerCertificate("warm-gen.wave44.test");

        var throttle = (SemaphoreSlim)typeof(CertificateManager)
            .GetField("certificateCreationThrottle", PrivateInstance)!.GetValue(mgr)!;

        var held = 0;
        while (throttle.Wait(0))
            held++;

        try
        {
            var mint = mgr.CreateServerCertificate("gen-race.wave44.test");
            // Let CreateServerCertificateCore park inside Task.Run on throttle.Wait.
            await Task.Delay(300).ConfigureAwait(false);

            mgr.ClearRootCertificate();
            Assert.IsTrue(mgr.CreateRootCertificate(false));

            for (var i = 0; i < held; i++)
                throttle.Release();
            held = 0;

            var leaf = await mint.ConfigureAwait(false);
            Assert.IsNotNull(leaf);
            Assert.IsTrue(leaf!.HasPrivateKey);
            _ = leaf.Thumbprint;
            leaf.Dispose();
        }
        finally
        {
            for (var i = 0; i < held; i++)
                throttle.Release();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public void ClearRootCertificate_AfterDispose_DoesNotThrow()
    {
        var mgr = NewManager();
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        mgr.Dispose();

        mgr.ClearRootCertificate();

        Assert.IsNull(mgr.RootCertificate);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void Dispose_AfterMintCtsAlreadyDisposed_SwallowsObjectDisposedException()
    {
        var mgr = NewManager();
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        var cts = (CancellationTokenSource)typeof(CertificateManager)
            .GetField("clearCertificatesTokenSource", PrivateInstance)!.GetValue(mgr)!;
        cts.Dispose();

        mgr.Dispose();
        // Second dispose is a no-op once the disposed flag is set.
        mgr.Dispose();
    }

    [TestMethod]
    [Timeout(30_000)]
    public void TryGetValidCachedCertificate_EvictsDisposedInjectedEntry()
    {
        using var mgr = NewManager();
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        const string host = "injected-dead.wave44.test";
        var dead = MakeDisposedSelfSigned(host);

        var cache = (ConcurrentDictionary<string, CachedCertificate>)typeof(CertificateManager)
            .GetField("cachedCertificates", PrivateInstance)!.GetValue(mgr)!;
        cache[host] = new CachedCertificate(dead) { LastAccess = DateTime.UtcNow };

        var tryGet = typeof(CertificateManager).GetMethod("TryGetValidCachedCertificate", PrivateInstance)!;
        var args = new object?[] { host, null };
        Assert.IsFalse((bool)tryGet.Invoke(mgr, args)!);
        Assert.IsFalse(cache.ContainsKey(host), "disposed cache entry must be removed");
    }

    private static CertificateManager NewManager() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    private static X509Certificate2 MakeDisposedSelfSigned(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        cert.Dispose();
        return cert;
    }
}
#pragma warning restore TWP001
