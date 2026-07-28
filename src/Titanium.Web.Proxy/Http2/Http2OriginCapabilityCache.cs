using System;
using System.Collections.Concurrent;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Caches, per upstream host:port, whether the real origin server negotiates HTTP/2 via TLS ALPN.
///     <para>
///         Titanium currently cannot transparently switch the protocol used for a decrypted connection once it
///         is open, so before it can decide which ALPN protocols to offer the client for a given CONNECT tunnel
///         it has to know in advance whether the real origin actually supports HTTP/2. Discovering that
///         requires a dedicated probe TLS handshake to the origin. Browsers commonly open many short-lived
///         tunnels to the very same host (connection racing/sharding), so without caching, every single one of
///         those tunnels pays for its own redundant probe handshake to the same host. This cache lets repeat
///         tunnels to the same host within <see cref="Ttl" /> reuse the most recent probe result instead.
///     </para>
/// </summary>
internal sealed class Http2OriginCapabilityCache
{
    private readonly ConcurrentDictionary<string, (bool Supported, DateTime ExpiresAtUtc)> cache = new();

    internal Http2OriginCapabilityCache(TimeSpan ttl)
    {
        Ttl = ttl;
    }

    /// <summary>
    ///     How long a probed result for a given host:port remains valid before a fresh probe is required.
    /// </summary>
    internal TimeSpan Ttl { get; }

    /// <summary>
    ///     Attempts to retrieve a still-valid, previously probed HTTP/2 support result for <paramref name="hostAndPort" />.
    /// </summary>
    /// <param name="hostAndPort">The connect target, e.g. "www.google.com:443".</param>
    /// <param name="supported">The cached result, when this method returns <c>true</c>.</param>
    /// <returns><c>true</c> if a still-valid cached result was found.</returns>
    internal bool TryGet(string hostAndPort, out bool supported)
    {
        if (cache.TryGetValue(hostAndPort, out var entry) && entry.ExpiresAtUtc > DateTime.UtcNow)
        {
            supported = entry.Supported;
            return true;
        }

        supported = false;
        return false;
    }

    /// <summary>
    ///     Records a freshly probed HTTP/2 support result for <paramref name="hostAndPort" />, valid for <see cref="Ttl" />.
    /// </summary>
    internal void Set(string hostAndPort, bool supported)
    {
        cache[hostAndPort] = (supported, DateTime.UtcNow.Add(Ttl));
    }

    /// <summary>
    ///     Removes entries whose TTL has elapsed. Called periodically from the connection-pool cleanup
    ///     loop to prevent unbounded growth of the dictionary in long-running proxies that handle many
    ///     unique origins.
    /// </summary>
    internal void TrimExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in cache.Keys)
            if (cache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc <= now)
                cache.TryRemove(key, out _);
    }
}
