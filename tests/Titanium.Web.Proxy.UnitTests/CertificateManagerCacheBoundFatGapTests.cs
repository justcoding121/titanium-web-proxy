#pragma warning disable TWP001
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>CertificateManager</c> + <see cref="ProxyResourceLimits.MaxCertificateCacheEntries" />
///     suite: LRU eviction keeps the in-memory cache within the bound.
/// </summary>
[TestClass]
public class CertificateManagerCacheBoundFatGapTests
{
    private static ProxyServer NewProxy(int maxEntries)
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.ResourceLimits = ProxyResourceLimits.Default.WithCertificateCacheBounds(maxEntries, null);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        return proxy;
    }

    /// <summary>
    ///     Bug class: burst host minting grows cachedCertificates without bound.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task MintManyHosts_EvictsToResourceLimit()
    {
        const int cap = 3;
        using var proxy = NewProxy(cap);
        for (var i = 0; i < cap + 3; i++)
            _ = await proxy.CertificateManager.CreateServerCertificate($"cache-bound-{i}.gap.test");

        Assert.IsTrue(proxy.CertificateManager.CachedCertificateCount <= cap,
            $"Cache must stay at or below {cap}; saw {proxy.CertificateManager.CachedCertificateCount}.");
    }

    /// <summary>
    ///     Bug class: after LRU eviction, revisiting the same host fails to remint a leaf.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task EvictedHost_Remint_Succeeds()
    {
        const string host = "cache-bound-remint.gap.test";
        using var proxy = NewProxy(2);
        using var first = await proxy.CertificateManager.CreateServerCertificate(host);
        Assert.IsNotNull(first);
        await proxy.CertificateManager.CreateServerCertificate("cache-bound-other-a.gap.test");
        await proxy.CertificateManager.CreateServerCertificate("cache-bound-other-b.gap.test");
        await proxy.CertificateManager.CreateServerCertificate("cache-bound-other-c.gap.test");

        using var second = await proxy.CertificateManager.CreateServerCertificate(host);
        Assert.IsNotNull(second);
        Assert.IsTrue(second!.HasPrivateKey);
    }
}
#pragma warning restore TWP001
