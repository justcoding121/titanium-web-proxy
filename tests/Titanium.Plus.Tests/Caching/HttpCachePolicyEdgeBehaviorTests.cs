using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Caching;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Tests;

/// <summary>
///     Pure <see cref="HttpCachePolicy"/> leftovers outside the RFC 9111 vector table.
///     Bug classes: must-revalidate blocks stale-if-error; ServeStale ignored when must-revalidate is set.
/// </summary>
[TestClass]
public class HttpCachePolicyEdgeBehaviorTests
{
    [TestMethod]
    public void Decide_FreshHit_VsMustRevalidateStale()
    {
        var fresh = Entry(expiresInSeconds: 30, mustRevalidate: false, staleWhile: 0, etag: null);
        var ageFresh = new HttpCachePolicy.EntryAge(5, 25, Fresh: true, StalenessSeconds: 0);
        Assert.AreEqual(HttpCachePolicy.CacheDecision.Hit, HttpCachePolicy.Decide(fresh, ageFresh, HttpCachePolicy.RequestDirectives.None));

        var stale = Entry(expiresInSeconds: -10, mustRevalidate: true, staleWhile: 60, etag: "\"v1\"");
        var ageStale = new HttpCachePolicy.EntryAge(40, 0, Fresh: false, StalenessSeconds: 10);
        Assert.AreEqual(HttpCachePolicy.CacheDecision.Revalidate,
            HttpCachePolicy.Decide(stale, ageStale, HttpCachePolicy.RequestDirectives.None));
    }

    [TestMethod]
    public void Decide_ServeStale_WhenInsideStaleWhileRevalidate()
    {
        var entry = Entry(expiresInSeconds: -5, mustRevalidate: false, staleWhile: 30, etag: null);
        var age = new HttpCachePolicy.EntryAge(35, 0, Fresh: false, StalenessSeconds: 5);
        Assert.AreEqual(HttpCachePolicy.CacheDecision.ServeStale,
            HttpCachePolicy.Decide(entry, age, HttpCachePolicy.RequestDirectives.None));
    }

    [TestMethod]
    public void MayServeStaleOnError_RespectsMustRevalidateAndNoCache()
    {
        var age = new HttpCachePolicy.EntryAge(40, 0, Fresh: false, StalenessSeconds: 5);
        var allowed = Entry(expiresInSeconds: -5, mustRevalidate: false, staleWhile: 0, etag: null, staleIfError: 60);
        Assert.IsTrue(HttpCachePolicy.MayServeStaleOnError(allowed, age, HttpCachePolicy.RequestDirectives.None));

        var mustRevalidate = Entry(expiresInSeconds: -5, mustRevalidate: true, staleWhile: 0, etag: null, staleIfError: 60);
        Assert.IsFalse(HttpCachePolicy.MayServeStaleOnError(mustRevalidate, age, HttpCachePolicy.RequestDirectives.None));

        Assert.IsFalse(HttpCachePolicy.MayServeStaleOnError(allowed, age, new HttpCachePolicy.RequestDirectives
        {
            MaxAge = -1,
            MinFresh = -1,
            NoCache = true,
        }));
    }

    [TestMethod]
    public void IsCacheableStatus_CoversHeuristicSet()
    {
        Assert.IsTrue(HttpCachePolicy.IsCacheableStatus(200));
        Assert.IsTrue(HttpCachePolicy.IsCacheableStatus(404));
        Assert.IsFalse(HttpCachePolicy.IsCacheableStatus(206));
        Assert.IsFalse(HttpCachePolicy.IsCacheableStatus(500));
    }

    private static CachedHttpResponse Entry(int expiresInSeconds, bool mustRevalidate, int staleWhile, string? etag, int staleIfError = 0)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (etag is not null)
        {
            headers.Add(new("ETag", etag));
        }

        return new CachedHttpResponse
        {
            StatusCode = 200,
            Body = [1],
            Headers = headers,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
            MustRevalidate = mustRevalidate,
            StaleWhileRevalidateSeconds = staleWhile,
            StaleIfErrorSeconds = staleIfError,
        };
    }
}
