using System;
using System.Collections.Concurrent;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Circuit breaker for clients that keep aborting the MITM TLS handshake for the same host
///     (certificate pinning / a client that does not trust the proxy root, e.g. an IDE agent or a
///     mobile SDK). After <see cref="Threshold" /> consecutive aborted handshakes inside
///     <see cref="Window" /> with no successful handshake in between, the host is reported so the caller
///     can stop decrypting it (the same opaque-tunnel fallback origin-side learning uses).
///     <para>
///         Success path cost: one lock-free <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty" />
///         check per completed handshake while no host is accumulating failures.
///     </para>
/// </summary>
internal sealed class ClientHandshakeRejectTracker
{
    private readonly ConcurrentDictionary<string, Entry> map = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxTrustedHosts = 4096;

    private readonly ConcurrentDictionary<string, long> recentSuccess = new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan window = TimeSpan.FromMinutes(2);
    private TimeSpan trustGrace = TimeSpan.FromMinutes(30);
    private int maxEntries = 256;

    /// <summary>
    ///     How long a completed handshake keeps aborted handshakes for the same host from counting toward
    ///     the threshold.
    /// </summary>
    internal TimeSpan TrustGrace
    {
        get => trustGrace;
        set => trustGrace = value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    /// <summary>Consecutive aborted handshakes required to trip. 0 or less disables tracking.</summary>
    internal int Threshold { get; set; } = 5;

    /// <summary>Failures older than this no longer count toward the threshold.</summary>
    internal TimeSpan Window
    {
        get => window;
        set => window = value <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : value;
    }

    internal int MaxEntries
    {
        get => maxEntries;
        set => maxEntries = value < 1 ? 1 : value;
    }

    internal int Count => map.Count;

    /// <summary>Current consecutive-abort count for a host (0 when none). Diagnostics and tests.</summary>
    internal int Strikes(string host)
    {
        if (!map.TryGetValue(DecryptBypassCache.Normalize(host), out var entry))
            return 0;
        lock (entry)
            return entry.Count;
    }

    /// <summary>
    ///     Records one aborted handshake. Returns true exactly when the threshold is reached
    ///     (the entry is cleared so the caller trips once per learn).
    /// </summary>
    internal bool RecordFailure(string? host, DateTime utcNow)
    {
        var limit = Threshold;
        if (limit <= 0 || string.IsNullOrWhiteSpace(host))
            return false;

        var key = DecryptBypassCache.Normalize(host);

        // A pinning client fails from its very first attempt. A host this client already completed a
        // handshake for is trusted: later aborts are the browser giving up (tab closed, navigation, a
        // cancelled preconnect race), and learning them turns a healthy CDN into an opaque tunnel.
        if (recentSuccess.TryGetValue(key, out var trustedTicks) && utcNow.Ticks - trustedTicks < trustGrace.Ticks)
            return false;

        if (map.Count >= maxEntries && !map.ContainsKey(key))
            Trim(utcNow);

        var entry = map.GetOrAdd(key, static _ => new Entry());
        bool tripped;
        lock (entry)
        {
            if (entry.Count == 0 || utcNow - entry.FirstUtc > window)
            {
                entry.Count = 1;
                entry.FirstUtc = utcNow;
            }
            else
            {
                entry.Count++;
            }

            tripped = entry.Count >= limit;
            if (tripped)
                entry.Count = 0;
        }

        if (tripped)
            map.TryRemove(key, out _);
        return tripped;
    }

    /// <summary>
    ///     A completed handshake proves the client trusts the proxy certificate: reset the host and remember
    ///     the proof for <see cref="TrustGrace" />.
    /// </summary>
    internal void RecordSuccess(string? host, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(host) || Threshold <= 0)
            return;

        var key = DecryptBypassCache.Normalize(host);
        if (!map.IsEmpty)
            map.TryRemove(key, out _);

        if (recentSuccess.Count >= MaxTrustedHosts && !recentSuccess.ContainsKey(key))
            TrimTrusted(utcNow ?? DateTime.UtcNow);

        recentSuccess[key] = (utcNow ?? DateTime.UtcNow).Ticks;
    }

    internal void Clear()
    {
        map.Clear();
        recentSuccess.Clear();
    }

    private void TrimTrusted(DateTime utcNow)
    {
        var cutoff = (utcNow - trustGrace).Ticks;
        foreach (var kv in recentSuccess)
            if (kv.Value < cutoff)
                recentSuccess.TryRemove(kv.Key, out _);

        if (recentSuccess.Count >= MaxTrustedHosts)
            recentSuccess.Clear();
    }

    private void Trim(DateTime utcNow)
    {
        foreach (var kv in map)
            if (utcNow - kv.Value.FirstUtc > window)
                map.TryRemove(kv.Key, out _);

        // Still full of live entries: drop everything rather than grow without bound. A client that
        // really is pinning will re-accumulate quickly; a hostile client cannot grow memory.
        if (map.Count >= maxEntries)
            map.Clear();
    }

    private sealed class Entry
    {
        internal int Count;
        internal DateTime FirstUtc;
    }
}
