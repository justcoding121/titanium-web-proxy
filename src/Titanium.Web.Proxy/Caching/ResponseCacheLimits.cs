namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// Body-size ceiling shared by the H1 terminate-lite coalesce path and the response cache.
/// </summary>
internal static class ResponseCacheLimits
{
    /// <summary>64 KiB. Same value as <see cref="HttpResponseCacheMiddleware.MaxCachedBodyBytes"/>.</summary>
    internal const int MaxCachedBodyBytes = 64 * 1024;
}
