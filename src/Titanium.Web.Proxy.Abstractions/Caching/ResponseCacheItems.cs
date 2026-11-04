namespace Titanium.Web.Proxy.Abstractions.Caching;

/// <summary>Keys a response-cache engine writes into middleware context items.</summary>
public static class ResponseCacheItems
{
    /// <summary>
    /// <c>ProxyMiddlewareContext.Items</c> key for the in-flight <see cref="ICacheRequestState"/>.
    /// Absent when the cache is off or declined the request, so a lookup that misses does no further work.
    /// </summary>
    public const string StateKey = "twp.cache.state";
}
