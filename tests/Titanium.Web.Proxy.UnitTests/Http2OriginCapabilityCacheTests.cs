using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="Http2OriginCapabilityCache" />, the per-host TTL cache that avoids a
///     redundant HTTP/2-support probe TLS handshake for every repeat CONNECT tunnel to the same origin.
/// </summary>
[TestClass]
public class Http2OriginCapabilityCacheTests
{
    [TestMethod]
    public void TryGet_OnEmptyCache_ReturnsFalse()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromMinutes(5));

        var found = cache.TryGet("www.google.com:443", out var supported);

        Assert.IsFalse(found);
        Assert.IsFalse(supported);
    }

    [TestMethod]
    public void Set_ThenTryGet_ReturnsTheCachedValue()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromMinutes(5));

        cache.Set("www.google.com:443", true);
        var foundTrue = cache.TryGet("www.google.com:443", out var supportedTrue);

        cache.Set("example.com:443", false);
        var foundFalse = cache.TryGet("example.com:443", out var supportedFalse);

        Assert.IsTrue(foundTrue);
        Assert.IsTrue(supportedTrue);
        Assert.IsTrue(foundFalse);
        Assert.IsFalse(supportedFalse);
    }

    [TestMethod]
    public void TryGet_ForDifferentHostAndPort_DoesNotReturnAnUnrelatedEntry()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromMinutes(5));

        cache.Set("www.google.com:443", true);

        // same host, different port must not share the cached result.
        var found = cache.TryGet("www.google.com:8443", out var supported);

        Assert.IsFalse(found);
        Assert.IsFalse(supported);
    }

    [TestMethod]
    public void Set_Overwrites_APreviouslyCachedValue()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromMinutes(5));

        cache.Set("www.google.com:443", false);
        cache.Set("www.google.com:443", true);

        var found = cache.TryGet("www.google.com:443", out var supported);

        Assert.IsTrue(found);
        Assert.IsTrue(supported);
    }

    [TestMethod]
    public async Task TryGet_AfterTtlElapses_NoLongerReturnsTheExpiredEntry()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromMilliseconds(50));

        cache.Set("www.google.com:443", true);
        Assert.IsTrue(cache.TryGet("www.google.com:443", out _), "Entry should still be fresh immediately after Set.");

        await WaitUntilAsync(() => !cache.TryGet("www.google.com:443", out _), TimeSpan.FromSeconds(2));

        var found = cache.TryGet("www.google.com:443", out var supported);

        Assert.IsFalse(found, "Entry should have expired after the TTL elapsed.");
        Assert.IsFalse(supported);
    }

    [TestMethod]
    public async Task TrimExpired_RemovesExpiredEntries()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromMilliseconds(50));
        cache.Set("a.example.com:443", true);
        cache.Set("b.example.com:443", false);

        await WaitUntilAsync(() => !cache.TryGet("a.example.com:443", out _), TimeSpan.FromSeconds(2));

        cache.TrimExpired();

        // Expired entries must no longer be returned.
        Assert.IsFalse(cache.TryGet("a.example.com:443", out _));
        Assert.IsFalse(cache.TryGet("b.example.com:443", out _));
    }

    [TestMethod]
    public void TrimExpired_PreservesActiveEntries()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromSeconds(60));
        cache.Set("live.example.com:443", true);

        cache.TrimExpired();

        Assert.IsTrue(cache.TryGet("live.example.com:443", out var supported));
        Assert.IsTrue(supported);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}
