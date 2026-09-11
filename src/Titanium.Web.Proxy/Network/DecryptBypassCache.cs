using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Process-lifetime cache of hosts that failed origin TLS under MITM. Approximate LRU:
///     last-access is updated on bypass hit and on record only — not on every CONNECT miss.
/// </summary>
internal sealed class DecryptBypassCache
{
    private readonly ConcurrentDictionary<string, Entry> cache =
        new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan ttl = TimeSpan.FromMinutes(30);
    private int maxEntries = 256;
    private int strikeThreshold = 2;

    internal TimeSpan Ttl
    {
        get => ttl;
        set => ttl = value <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : value;
    }

    internal int MaxEntries
    {
        get => maxEntries;
        set => maxEntries = value < 1 ? 1 : value;
    }

    internal int StrikeThreshold
    {
        get => strikeThreshold;
        set => strikeThreshold = value < 1 ? 1 : value;
    }

    internal int Count => cache.Count;

    /// <summary>
    ///     Returns true when decrypt should be skipped. Updates last-access only on a bypass hit.
    /// </summary>
    internal bool ShouldBypass(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var key = Normalize(host);
        if (!cache.TryGetValue(key, out var entry))
            return false;

        var now = DateTime.UtcNow;
        if (entry.ExpiresAtUtc <= now || !entry.BypassActive)
            return false;

        entry.LastAccessUtc = now;
        return true;
    }

    /// <summary>
    ///     Records an origin TLS handshake failure. When strikes reach the threshold, bypass becomes active.
    ///     Returns true when bypass is (now) active.
    /// </summary>
    internal bool RecordFailure(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var key = Normalize(host);
        var now = DateTime.UtcNow;
        var entry = cache.AddOrUpdate(key,
            _ => new Entry
            {
                Strikes = 1,
                LearnedAtUtc = now,
                ExpiresAtUtc = now.Add(Ttl),
                LastAccessUtc = now,
                BypassActive = 1 >= StrikeThreshold
            },
            (_, existing) =>
            {
                if (existing.ExpiresAtUtc <= now)
                {
                    existing.Strikes = 1;
                    existing.LearnedAtUtc = now;
                    existing.BypassActive = 1 >= StrikeThreshold;
                }
                else
                {
                    existing.Strikes++;
                    if (existing.Strikes >= StrikeThreshold)
                        existing.BypassActive = true;
                }

                existing.ExpiresAtUtc = now.Add(Ttl);
                existing.LastAccessUtc = now;
                return existing;
            });

        EvictIfOverCapacity();
        return entry.BypassActive;
    }

    /// <summary>
    ///     Forces bypass for a host (same-CONNECT opaque fallback after an awaited probe failure).
    /// </summary>
    internal void MarkBypassed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;

        var key = Normalize(host);
        var now = DateTime.UtcNow;
        cache.AddOrUpdate(key,
            _ => new Entry
            {
                Strikes = Math.Max(StrikeThreshold, 1),
                LearnedAtUtc = now,
                ExpiresAtUtc = now.Add(Ttl),
                LastAccessUtc = now,
                BypassActive = true
            },
            (_, existing) =>
            {
                existing.Strikes = Math.Max(existing.Strikes, StrikeThreshold);
                existing.BypassActive = true;
                existing.ExpiresAtUtc = now.Add(Ttl);
                existing.LastAccessUtc = now;
                if (existing.LearnedAtUtc == default)
                    existing.LearnedAtUtc = now;
                return existing;
            });

        EvictIfOverCapacity();
    }

    internal bool Remove(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        return cache.TryRemove(Normalize(host), out _);
    }

    internal void Clear() => cache.Clear();

    internal IReadOnlyList<DecryptFailureBypassEntry> Snapshot()
    {
        var now = DateTime.UtcNow;
        return cache
            .Where(kv => kv.Value.ExpiresAtUtc > now)
            .OrderByDescending(kv => kv.Value.LastAccessUtc)
            .Select(kv => new DecryptFailureBypassEntry(
                kv.Key,
                kv.Value.Strikes,
                kv.Value.LearnedAtUtc,
                kv.Value.ExpiresAtUtc,
                kv.Value.BypassActive))
            .ToList();
    }

    internal void TrimExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in cache.Keys)
            if (cache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc <= now)
                cache.TryRemove(key, out _);
    }

    private void EvictIfOverCapacity()
    {
        var over = cache.Count - MaxEntries;
        if (over <= 0)
            return;

        TrimExpired();
        over = cache.Count - MaxEntries;
        if (over <= 0)
            return;

        var victims = cache
            .OrderBy(kv => kv.Value.LastAccessUtc)
            .Take(over)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in victims)
            cache.TryRemove(key, out _);
    }

    internal static string Normalize(string host)
    {
        host = host.Trim();
        // Strip brackets / port from host:port or [ipv6]:port
        if (host.StartsWith('[') && host.Contains(']'))
        {
            var end = host.IndexOf(']');
            host = host.Substring(1, end - 1);
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host.AsSpan(colon + 1), out _))
                host = host.Substring(0, colon);
        }

        return host.Trim().ToLowerInvariant();
    }

    private sealed class Entry
    {
        internal int Strikes;
        internal DateTime LearnedAtUtc;
        internal DateTime ExpiresAtUtc;
        internal DateTime LastAccessUtc;
        internal bool BypassActive;
    }
}
