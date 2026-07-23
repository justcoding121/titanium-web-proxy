using System;
using System.Threading;
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
    public void TryGet_AfterTtlElapses_NoLongerReturnsTheExpiredEntry()
    {
        var cache = new Http2OriginCapabilityCache(TimeSpan.FromMilliseconds(50));

        cache.Set("www.google.com:443", true);
        Assert.IsTrue(cache.TryGet("www.google.com:443", out _), "Entry should still be fresh immediately after Set.");

        Thread.Sleep(200);

        var found = cache.TryGet("www.google.com:443", out var supported);

        Assert.IsFalse(found, "Entry should have expired after the TTL elapsed.");
        Assert.IsFalse(supported);
    }
}
