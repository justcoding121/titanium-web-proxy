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
///               <b>HTTPS/SVCB DNS RR</b>: callers may <see cref="Set" /> a result after resolving
///               DNS and parsing SVCB records, including an optional <c>TargetName</c> for QUIC routing.
///             </description>
///           </item>
///         </list>
///     </para>
/// </summary>
internal sealed class Http3OriginCapabilityCache
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long Auto mode refuses HTTP/3 after a QUIC connect fails before any request bytes
    ///     were sent (UDP blackhole, handshake timeout). Advertisements (<c>Alt-Svc</c>, HTTPS/SVCB)
    ///     cannot revive the origin during this window; a later successful QUIC exchange can.
    ///     Matches the initial broken-alternative-service window browsers use when they stay on HTTP/2.
    /// </summary>
    internal static readonly TimeSpan BrokenTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    /// <summary>
    ///     Returns <see langword="true" /> and fills <paramref name="altPort" /> and
    ///     <paramref name="targetName" /> when there is a still-valid cached HTTP/3 capability for
    ///     <paramref name="hostAndPort" />.
    ///     <paramref name="altPort" /> is <see cref="int.MinValue" /> when the entry was stored
    ///     without an alternative port (i.e. the caller should use the same port as the current
    ///     HTTPS connection).
    ///     <paramref name="targetName" /> is <see langword="null" /> when no SVCB TargetName was
    ///     recorded (use the origin host for QUIC connect as well).
    /// </summary>
    internal bool TryGet(string hostAndPort, out int altPort, out string? targetName)
    {
        if (_cache.TryGetValue(hostAndPort, out var entry)
            && !entry.Broken
            && entry.ExpiresAtUtc > DateTime.UtcNow)
        {
            altPort = entry.AltPort;
            targetName = entry.TargetName;
            return true;
        }

        altPort = int.MinValue;
        targetName = null;
        return false;
    }

    /// <summary>
    ///     Convenience overload that discards <c>targetName</c> when the caller only needs the port.
    /// </summary>
    internal bool TryGet(string hostAndPort, out int altPort) => TryGet(hostAndPort, out altPort, out _);

    /// <summary>
    ///     Records a freshly discovered HTTP/3 capability for <paramref name="hostAndPort" />.
    /// </summary>
    /// <param name="hostAndPort">The origin key, e.g. <c>"example.com:443"</c>.</param>
    /// <param name="altPort">
    ///     An alternative port advertised by the origin (<c>h3=":8443"</c>), or <see cref="int.MinValue" />
    ///     when the same port applies.
    /// </param>
    /// <param name="ttl">How long the entry should remain valid. Defaults to <see cref="DefaultTtl" />.</param>
    /// <param name="targetName">
    ///     Optional SVCB TargetName for the QUIC connect host. <see langword="null" /> means use the
    ///     origin host for the QUIC connection (Alt-Svc path; no separate target).
    /// </param>
    /// <param name="overrideBroken">
    ///     When <see langword="true"/>, a live broken entry is replaced. Only a QUIC exchange that
    ///     actually completed may pass this. <c>Alt-Svc</c> and SVCB advertisements must leave it
    ///     <see langword="false"/> so a UDP blackhole cannot be re-armed by the TCP fallback response.
    /// </param>
    /// <returns>
    ///     <see langword="false"/> when a live broken entry rejected the write.
    /// </returns>
    internal bool Set(string hostAndPort, int altPort = int.MinValue, TimeSpan? ttl = null, string? targetName = null,
        bool overrideBroken = false)
    {
        var candidate = new Entry(altPort, targetName, DateTime.UtcNow.Add(ttl ?? DefaultTtl), Broken: false);
        while (true)
        {
            if (_cache.TryGetValue(hostAndPort, out var existing))
            {
                if (existing.Broken && existing.ExpiresAtUtc > DateTime.UtcNow && !overrideBroken)
                    return false;

                if (_cache.TryUpdate(hostAndPort, candidate, existing))
                    return true;

                continue;
            }

            if (_cache.TryAdd(hostAndPort, candidate))
                return true;
        }
    }

    /// <summary>
    ///     Records that QUIC to <paramref name="hostAndPort"/> failed before the request was sent.
    ///     <see cref="TryGet"/> misses until <see cref="BrokenTtl"/> elapses, and <see cref="Set"/>
    ///     from an advertisement does not clear it.
    /// </summary>
    internal void MarkBroken(string hostAndPort, TimeSpan? ttl = null)
    {
        var until = DateTime.UtcNow.Add(ttl ?? BrokenTtl);
        var broken = new Entry(int.MinValue, null, until, Broken: true);
        while (true)
        {
            if (_cache.TryGetValue(hostAndPort, out var existing))
            {
                // Keep the longer window so a second failure cannot shorten the backoff.
                if (existing.Broken && existing.ExpiresAtUtc >= until)
                    return;

                if (_cache.TryUpdate(hostAndPort, broken, existing))
                    return;

                continue;
            }

            if (_cache.TryAdd(hostAndPort, broken))
                return;
        }
    }

    /// <summary>True when Auto mode must not attempt HTTP/3 to this origin yet.</summary>
    internal bool IsBroken(string hostAndPort) =>
        _cache.TryGetValue(hostAndPort, out var entry) && entry.Broken && entry.ExpiresAtUtc > DateTime.UtcNow;

    /// <summary>Removes an entry so the next request re-probes. Also clears a broken mark.</summary>
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

    private readonly record struct Entry(int AltPort, string? TargetName, DateTime ExpiresAtUtc, bool Broken = false);
}
