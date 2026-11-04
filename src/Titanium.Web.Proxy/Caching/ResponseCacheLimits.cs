namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// Body-size ceiling shared by the H1 terminate-lite coalesce path and the response cache.
/// </summary>
internal static class ResponseCacheLimits
{
    /// <summary>64 KiB. The Plus response cache stores the same ceiling on its middleware.</summary>
    internal const int MaxCachedBodyBytes = 64 * 1024;
}
