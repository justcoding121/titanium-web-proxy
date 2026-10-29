using System;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// Behavior knobs for <see cref="HttpResponseCacheMiddleware"/>. Every default is the conservative choice:
/// heuristic freshness off, cache-status headers hidden, authenticated requests never stored.
/// </summary>
public sealed class ResponseCacheOptions
{
    /// <summary>Largest body stored for one response. Larger bodies stream through uncached. Default 1 MiB.</summary>
    public int MaxEntryBytes { get; init; } = MemoryHttpResponseCache.DefaultMaxEntryBytes;

    /// <summary>
    /// Freshness for a response that carries no explicit freshness information and no usable
    /// <c>Last-Modified</c>. Null (default) stores nothing for such responses.
    /// </summary>
    public TimeSpan? DefaultTtl { get; init; }

    /// <summary>
    /// Heuristic freshness (10 percent of the time since <c>Last-Modified</c>, capped at 24 hours), used only
    /// when the response has <c>Last-Modified</c> and no explicit freshness. Off by default.
    /// </summary>
    public bool HeuristicFreshness { get; init; }

    /// <summary>Add <c>X-Cache</c> and <c>Age</c> to responses the cache took part in. Off by default.</summary>
    public bool ExposeHeaders { get; init; }

    /// <summary>Store responses to requests carrying Authorization or Cookie. Off by default; never enable on a shared listener.</summary>
    public bool AllowAuthenticated { get; init; }

    /// <summary>How long a stale entry with a validator stays available for conditional revalidation. Default 5 minutes.</summary>
    public TimeSpan StaleRetention { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Coalesce concurrent misses for one key behind a single origin fetch. On by default.</summary>
    public bool Collapse { get; init; } = true;

    /// <summary>Requests that may wait behind one origin fetch. Further requests fetch independently. Default 256.</summary>
    public int CollapseMaxWaiters { get; init; } = 256;

    /// <summary>How long a request waits for the leading fetch before fetching on its own. Default 5 seconds.</summary>
    public TimeSpan CollapseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Distinct origin keys that may have an in-flight fetch at once. Beyond this, requests fetch independently.</summary>
    public int CollapseMaxFlights { get; init; } = 4096;
}
