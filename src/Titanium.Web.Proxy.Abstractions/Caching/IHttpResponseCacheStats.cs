namespace Titanium.Web.Proxy.Abstractions.Caching;

/// <summary>
/// Byte total for an in-memory response cache. Entry count stays on <c>IHttpResponseCache</c>.
/// Gauges read this only when a cache object is already alive.
/// </summary>
public interface IHttpResponseCacheStats
{
    /// <summary>Bytes currently charged, headers and keys included.</summary>
    long Bytes { get; }
}
