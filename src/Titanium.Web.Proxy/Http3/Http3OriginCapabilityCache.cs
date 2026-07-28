using System;
using System.Collections.Concurrent;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Caches, per upstream host:port, whether the real origin server supports HTTP/3 (QUIC).
///     <para>
///         Discovery happens in two ways:
///         <list type="bullet">
///           <item>
///             <description>
///               <b>Alt-Svc</b>: a response header such as <c>Alt-Svc: h3=":443"; ma=86400</c> is parsed
///               and the result is stored here for the header-advertised <c>ma</c> (max-age) duration.
///             </description>
///           </item>
///           <item>
///             <description>
///               <b>HTTPS/SVCB DNS RR</b>: future extension point; callers may <see cref="Set" /> a result
///               after resolving DNS and parsing SVCB records.
///             </description>
///           </item>
///         </list>
///     </para>
/// </summary>
internal sealed class Http3OriginCapabilityCache
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    /// <summary>
    ///     Returns <see langword="true" /> and fills <paramref name="altPort" /> when there is a still-valid
    ///     cached HTTP/3 capability for <paramref name="hostAndPort" />.
    ///     <paramref name="altPort" /> is <see cref="int.MinValue" /> when the entry was stored without an
    ///     alternative port (i.e. the caller should use the same port as the current HTTPS connection).
    /// </summary>
    internal bool TryGet(string hostAndPort, out int altPort)
    {
        if (_cache.TryGetValue(hostAndPort, out var entry) && entry.ExpiresAtUtc > DateTime.UtcNow)
        {
            altPort = entry.AltPort;
            return true;
        }

        altPort = int.MinValue;
        return false;
    }

    /// <summary>
    ///     Records a freshly discovered HTTP/3 capability for <paramref name="hostAndPort" />.
    /// </summary>
    /// <param name="hostAndPort">The origin key, e.g. "example.com:443".</param>
    /// <param name="altPort">
    ///     An alternative port advertised by the origin (<c>h3=":8443"</c>), or <see cref="int.MinValue" />
    ///     when the same port applies.
    /// </param>
    /// <param name="ttl">How long the entry should remain valid. Defaults to <see cref="DefaultTtl" />.</param>
    internal void Set(string hostAndPort, int altPort = int.MinValue, TimeSpan? ttl = null)
    {
        _cache[hostAndPort] = new Entry(altPort, DateTime.UtcNow.Add(ttl ?? DefaultTtl));
    }

    /// <summary>Removes a stale or negative entry so the next request re-probes.</summary>
    internal void Evict(string hostAndPort) => _cache.TryRemove(hostAndPort, out _);

    /// <summary>
    ///     Removes entries whose TTL has elapsed. Called periodically from the connection-pool cleanup
    ///     loop to prevent unbounded growth of the dictionary in long-running proxies that visit many
    ///     unique origins.
    /// </summary>
    internal void TrimExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _cache.Keys)
            if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc <= now)
                _cache.TryRemove(key, out _);
    }

    private readonly record struct Entry(int AltPort, DateTime ExpiresAtUtc);
}
