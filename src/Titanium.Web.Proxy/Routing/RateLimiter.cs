using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
///     Counters behind the <c>rateLimit</c> policy. A key is the policy name plus a client address, a
///     route id, or a 16-byte hash of a header or cookie value. A policy never holds more than
///     <c>MaxKeys</c> counters: when full, expired ones are dropped, and any new key then shares one
///     overflow counter so a flood of distinct values cannot grow memory without bound.
/// </summary>
internal sealed class RateLimiter
{
    private const int DefaultMaxKeys = 100_000;
    private const double Epsilon = 1e-9;
    private const string OverflowSuffix = "|*overflow";

    private readonly ConcurrentDictionary<string, RateState> States = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, KeyCounter> Counters = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<RoutePolicyConfig, RateSpec> Specs = new();
    private int _writes;

    internal int Count => States.Count;

    internal void Reset()
    {
        States.Clear();
        Counters.Clear();
        _writes = 0;
    }

    internal readonly struct Verdict
    {
        public bool Allowed { get; init; }
        public int Limit { get; init; }
        public int Remaining { get; init; }
        public int RetryAfterSeconds { get; init; }
        public int ResetSeconds { get; init; }
    }

    internal Verdict Check(
        RoutePolicyConfig policy,
        string routeId,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies,
        DateTimeOffset now)
    {
        var spec = Specs.GetValue(policy, static p => RateSpec.From(p));
        var key = BuildKey(policy.Name, spec, routeId, request, client, trustedProxies);
        var state = Acquire(spec, policy.Name, key, now);
        Verdict verdict;
        lock (state)
        {
            verdict = spec.TokenBucket ? TakeToken(state, spec, now) : CountInWindow(state, spec, now);
        }

        // The sweep takes other states' locks. Run it after this one is released so two writers
        // that sweep at the same time cannot wait on each other.
        Sweep(now);
        return verdict;
    }

    private static Verdict CountInWindow(RateState state, RateSpec spec, DateTimeOffset now)
    {
        if (state.Start == default || now - state.Start >= spec.Window)
        {
            state.Start = now;
            state.Count = 0;
            state.Span = spec.Window;
        }

        state.Count++;
        state.Seen = now;
        var windowEnd = state.Start + spec.Window;
        state.Expires = windowEnd;
        var untilReset = WholeSeconds((windowEnd - now).TotalSeconds);
        return new Verdict
        {
            Allowed = state.Count <= spec.Limit,
            Limit = spec.Limit,
            Remaining = Math.Max(0, spec.Limit - state.Count),
            RetryAfterSeconds = untilReset,
            ResetSeconds = untilReset,
        };
    }

    private static Verdict TakeToken(RateState state, RateSpec spec, DateTimeOffset now)
    {
        if (!state.Initialized)
        {
            state.Initialized = true;
            state.Tokens = spec.Burst;
            state.Last = now;
        }
        else
        {
            var elapsed = (now - state.Last).TotalSeconds;
            if (elapsed > 0)
            {
                state.Tokens = Math.Min(spec.Burst, state.Tokens + elapsed * spec.RatePerSecond);
                state.Last = now;
            }
        }

        // Refill is floating point, so a token that is a rounding error short still counts.
        var allowed = state.Tokens >= 1 - Epsilon;
        if (allowed)
        {
            state.Tokens = Math.Max(0, state.Tokens - 1);
        }

        state.Seen = now;
        state.Span = spec.Horizon;
        var untilFull = (spec.Burst - state.Tokens) / spec.RatePerSecond;
        state.Expires = state.Last + TimeSpan.FromSeconds(untilFull);
        return new Verdict
        {
            Allowed = allowed,
            Limit = spec.Limit,
            Remaining = (int)Math.Floor(state.Tokens + Epsilon),
            RetryAfterSeconds = allowed ? 0 : WholeSeconds((1 - state.Tokens) / spec.RatePerSecond),
            ResetSeconds = WholeSeconds(untilFull),
        };
    }

    private static int WholeSeconds(double seconds) => (int)Math.Max(1, Math.Ceiling(seconds - Epsilon));

    private RateState Acquire(RateSpec spec, string policyName, string key, DateTimeOffset now)
    {
        if (States.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var counter = Counters.GetOrAdd(policyName, static _ => new KeyCounter());
        if (Volatile.Read(ref counter.Count) >= spec.MaxKeys)
        {
            EvictExpired(policyName, counter, now);
            if (Volatile.Read(ref counter.Count) >= spec.MaxKeys)
            {
                return States.GetOrAdd(
                    policyName + OverflowSuffix,
                    static (_, name) => new RateState { Policy = name, IsOverflow = true },
                    policyName);
            }
        }

        var created = new RateState { Policy = policyName };
        var actual = States.GetOrAdd(key, created);
        if (ReferenceEquals(actual, created))
        {
            Interlocked.Increment(ref counter.Count);
        }

        return actual;
    }

    /// <summary>Scans at most once a second per policy, so a flood of new keys cannot force a scan per request.</summary>
    private void EvictExpired(string policyName, KeyCounter counter, DateTimeOffset now)
    {
        var last = Interlocked.Read(ref counter.LastEvictTicks);
        if (last != 0 && now.UtcTicks - last < TimeSpan.TicksPerSecond)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref counter.LastEvictTicks, now.UtcTicks, last) != last)
        {
            return;
        }

        foreach (var pair in States)
        {
            var state = pair.Value;
            if (state.IsOverflow || !string.Equals(state.Policy, policyName, StringComparison.Ordinal))
            {
                continue;
            }

            bool expired;
            lock (state)
            {
                expired = now >= state.Expires;
            }

            if (expired)
            {
                Remove(pair);
            }
        }
    }

    private void Remove(KeyValuePair<string, RateState> pair)
    {
        if (States.TryRemove(pair) && !pair.Value.IsOverflow && Counters.TryGetValue(pair.Value.Policy, out var counter))
        {
            Interlocked.Decrement(ref counter.Count);
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        if ((Interlocked.Increment(ref _writes) & 31) != 0)
        {
            return;
        }

        foreach (var pair in States)
        {
            var state = pair.Value;
            if (state.Span <= TimeSpan.Zero)
            {
                continue;
            }

            bool idle;
            lock (state)
            {
                idle = now - state.Seen > state.Span + state.Span;
            }

            if (idle)
            {
                Remove(pair);
            }
        }
    }

    private static string BuildKey(
        string policyName,
        RateSpec spec,
        string routeId,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies)
    {
        switch (spec.Source)
        {
            case KeySource.ForwardedClient:
                return policyName + "|" + (ResolveForwardedClient(request, client, trustedProxies)?.ToString() ?? "");
            case KeySource.Route:
                return policyName + "|r:" + routeId;
            case KeySource.Header:
            case KeySource.Cookie:
                var value = spec.Source == KeySource.Header
                    ? FirstHeader(request, spec.Name!)
                    : CookieValue(request, spec.Name!);

                // A request without the value is counted by address, so leaving the header out never escapes the limit.
                return string.IsNullOrEmpty(value)
                    ? policyName + "|" + (client?.ToString() ?? "")
                    : policyName + "|h:" + HashValue(value);
            default:
                return policyName + "|" + (client?.ToString() ?? "");
        }
    }

    /// <summary>16 bytes of SHA-256 as hex. Bounds key size and keeps raw header or cookie values out of memory dumps and logs.</summary>
    internal static string HashValue(string value)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        var count = Encoding.UTF8.GetByteCount(value);
        if (count <= 512)
        {
            Span<byte> buffer = stackalloc byte[512];
            var written = Encoding.UTF8.GetBytes(value, buffer);
            SHA256.HashData(buffer[..written], hash);
        }
        else
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        }

        return Convert.ToHexString(hash[..16]);
    }

    private static string? FirstHeader(Request request, string name)
    {
        var headers = request.Headers.GetHeaders(name);
        return headers is { Count: > 0 } ? headers[0].Value : null;
    }

    private static string? CookieValue(Request request, string name)
    {
        var lines = request.Headers.GetHeaders("Cookie");
        if (lines is not { Count: > 0 })
        {
            return null;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var rest = lines[i].Value.AsSpan();
            while (!rest.IsEmpty)
            {
                var semicolon = rest.IndexOf(';');
                var part = (semicolon < 0 ? rest : rest[..semicolon]).Trim();
                rest = semicolon < 0 ? default : rest[(semicolon + 1)..];
                var equals = part.IndexOf('=');
                if (equals > 0 && part[..equals].Trim().SequenceEqual(name))
                {
                    var value = part[(equals + 1)..].Trim();
                    return value.IsEmpty ? null : value.ToString();
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     The rightmost X-Forwarded-For entry that is not a trusted proxy, but only when the peer itself is
    ///     trusted. A peer that is not trusted could have written the whole chain, so its address is used.
    ///     A chain that is empty, entirely trusted, or ends in something that is not an address also falls back to the peer.
    /// </summary>
    internal static IPAddress? ResolveForwardedClient(Request request, IPAddress? peer, IReadOnlyList<string>? trustedProxies)
    {
        if (peer is null)
        {
            return null;
        }

        if (!ForwardedHeadersApplier.IsTrusted(peer, trustedProxies))
        {
            return Normalize(peer);
        }

        var lines = request.Headers.GetHeaders("X-Forwarded-For");
        if (lines is not { Count: > 0 })
        {
            return Normalize(peer);
        }

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var rest = lines[i].Value.AsSpan();
            while (!rest.IsEmpty)
            {
                var comma = rest.LastIndexOf(',');
                var entry = (comma < 0 ? rest : rest[(comma + 1)..]).Trim();
                rest = comma < 0 ? default : rest[..comma];
                if (entry.IsEmpty)
                {
                    continue;
                }

                if (!TryParseAddress(entry, out var address))
                {
                    return Normalize(peer);
                }

                if (!ForwardedHeadersApplier.IsTrusted(address, trustedProxies))
                {
                    return Normalize(address);
                }
            }
        }

        return Normalize(peer);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseAddress(ReadOnlySpan<char> entry, out IPAddress address)
    {
        var candidate = entry;
        if (entry[0] == '[')
        {
            var close = entry.IndexOf(']');
            if (close > 1)
            {
                candidate = entry[1..close];
            }
        }
        else
        {
            var colon = entry.IndexOf(':');
            if (colon > 0 && colon == entry.LastIndexOf(':'))
            {
                candidate = entry[..colon];
            }
        }

        return IPAddress.TryParse(candidate, out address!);
    }

    private enum KeySource
    {
        Client,
        ForwardedClient,
        Route,
        Header,
        Cookie,
    }

    private sealed class RateSpec
    {
        public KeySource Source { get; private init; }
        public string? Name { get; private init; }
        public bool TokenBucket { get; private init; }
        public int Limit { get; private init; }
        public int Burst { get; private init; }
        public int MaxKeys { get; private init; }
        public TimeSpan Window { get; private init; }
        public double RatePerSecond { get; private init; }

        /// <summary>How long an idle token bucket takes to refill completely. A bucket removed sooner would come back fuller than it should.</summary>
        public TimeSpan Horizon { get; private init; }

        public static RateSpec From(RoutePolicyConfig policy)
        {
            var limit = Math.Max(policy.Limit ?? 1, 1);
            var windowSeconds = Math.Max(policy.WindowSeconds ?? 1, 1);
            var burst = Math.Max(policy.Burst ?? limit, 1);
            var rate = (double)limit / windowSeconds;
            var (source, name) = ParseKey(policy.Key);
            return new RateSpec
            {
                Source = source,
                Name = name,
                TokenBucket = string.Equals(policy.Algorithm, "tokenBucket", StringComparison.OrdinalIgnoreCase),
                Limit = limit,
                Burst = burst,
                MaxKeys = Math.Clamp(policy.MaxKeys ?? DefaultMaxKeys, 1, 10_000_000),
                Window = TimeSpan.FromSeconds(windowSeconds),
                RatePerSecond = rate,
                Horizon = TimeSpan.FromSeconds(Math.Max(windowSeconds, burst / rate)),
            };
        }

        private static (KeySource Source, string? Name) ParseKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Equals("client", StringComparison.OrdinalIgnoreCase))
            {
                return (KeySource.Client, null);
            }

            if (key.Equals("forwardedClient", StringComparison.OrdinalIgnoreCase))
            {
                return (KeySource.ForwardedClient, null);
            }

            if (key.Equals("route", StringComparison.OrdinalIgnoreCase))
            {
                return (KeySource.Route, null);
            }

            if (key.StartsWith("header:", StringComparison.OrdinalIgnoreCase) && key.Length > 7)
            {
                return (KeySource.Header, key[7..].Trim());
            }

            if (key.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase) && key.Length > 7)
            {
                return (KeySource.Cookie, key[7..].Trim());
            }

            return (KeySource.Client, null);
        }
    }

    private sealed class KeyCounter
    {
        public int Count;
        public long LastEvictTicks;
    }

    private sealed class RateState
    {
        public string Policy = string.Empty;
        public bool IsOverflow;

        /// <summary>When this counter would look the same as a new one. Past this time it is safe to drop.</summary>
        public DateTimeOffset Expires;

        public DateTimeOffset Seen;
        public TimeSpan Span;

        // Fixed window.
        public DateTimeOffset Start;
        public int Count;

        // Token bucket.
        public bool Initialized;
        public double Tokens;
        public DateTimeOffset Last;
    }
}
