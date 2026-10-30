using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// The RFC 9111 subset the shared response cache implements, as pure functions of headers and a clock.
/// Nothing here touches a socket or a store, so the rules are table-testable.
/// </summary>
/// <remarks>
/// Implemented: s-maxage, max-age, Expires, Age, opt-in heuristic freshness (Last-Modified only, 10 percent,
/// capped at 24 hours), Vary secondary keys (at most 8 named headers), stale-while-revalidate, stale-if-error,
/// validator-based revalidation with 304 header merge, request no-cache / max-age / min-fresh / only-if-cached.
/// Never stored (also see <see cref="TryPlanStore"/>): Set-Cookie, private, no-store, no-cache, Vary: *, partial
/// content. Range requests bypass the cache.
/// </remarks>
internal static class HttpCachePolicy
{
    /// <summary>Most request headers a response may name in <c>Vary</c> (Accept-Encoding is always keyed and not counted).</summary>
    internal const int MaxVaryHeaders = 8;

    internal const long MaxFreshnessSeconds = 365L * 24 * 3600;
    internal const long HeuristicCapSeconds = 24L * 3600;
    internal const long MaxStaleExtensionSeconds = 7L * 24 * 3600;

    /// <summary>Separator inside a cache key. Control characters never appear in a request target or a header value we key on.</summary>
    internal const char KeySeparator = '\u0001';

    /// <summary>
    /// Longest normalized Accept-Encoding or Vary header value that may take part in a key. A longer one makes the
    /// request bypass the cache, so a client cannot mint arbitrarily large keys.
    /// </summary>
    internal const int MaxKeyPartChars = 256;

    private static readonly string[] HttpDateFormats =
    [
        "r",
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM  d HH:mm:ss yyyy",
    ];

    /// <summary>Status codes a shared cache may store (RFC 9110 section 15.1 heuristically cacheable set).</summary>
    internal static bool IsCacheableStatus(int status) =>
        status is 200 or 203 or 204 or 300 or 301 or 308 or 404 or 405 or 410 or 414 or 501;

    // ---------------------------------------------------------------- Cache-Control

    internal struct Directives
    {
        public bool NoStore;
        public bool NoCache;
        public bool Private;
        public bool Public;
        public bool MustRevalidate;
        public bool ProxyRevalidate;
        public bool OnlyIfCached;
        public long MaxAge;
        public long SMaxAge;
        public long StaleWhileRevalidate;
        public long StaleIfError;
        public long MinFresh;

        /// <summary>A max-age or s-maxage that is not a delta-seconds. Treated as "already stale" (RFC 9111 section 4.2.1).</summary>
        public bool MalformedFreshness;

        public static Directives Create() => new()
        {
            MaxAge = -1,
            SMaxAge = -1,
            StaleWhileRevalidate = -1,
            StaleIfError = -1,
            MinFresh = -1,
        };
    }

    internal static Directives ParseCacheControl(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var d = Directives.Create();
        foreach (var h in headers.Where(h => h.Key.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase)))
        {
            ParseCacheControlValue(h.Value, ref d);
        }

        return d;
    }

    internal static Directives ParseCacheControl(IReadOnlyList<string>? values)
    {
        var d = Directives.Create();
        if (values is null)
        {
            return d;
        }

        foreach (var v in values)
        {
            ParseCacheControlValue(v, ref d);
        }

        return d;
    }

    private static void ParseCacheControlValue(string value, ref Directives d) // NOSONAR S3776 -- Directive tokenizer keeps RFC 9111 parsing in one place.
    {
        var span = value.AsSpan();
        while (!span.IsEmpty)
        {
            var end = NextDirectiveEnd(span);
            var part = span[..end].Trim();
            span = end < span.Length ? span[(end + 1)..] : default;
            if (part.IsEmpty)
            {
                continue;
            }

            var eq = part.IndexOf('=');
            var name = (eq < 0 ? part : part[..eq]).Trim();
            var arg = eq < 0 ? default : part[(eq + 1)..].Trim();
            if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
            {
                arg = arg[1..^1];
            }

            if (name.Equals("no-store", StringComparison.OrdinalIgnoreCase))
            {
                d.NoStore = true;
            }
            else if (name.Equals("no-cache", StringComparison.OrdinalIgnoreCase))
            {
                d.NoCache = true;
            }
            else if (name.Equals("private", StringComparison.OrdinalIgnoreCase))
            {
                d.Private = true;
            }
            else if (name.Equals("public", StringComparison.OrdinalIgnoreCase))
            {
                d.Public = true;
            }
            else if (name.Equals("must-revalidate", StringComparison.OrdinalIgnoreCase))
            {
                d.MustRevalidate = true;
            }
            else if (name.Equals("proxy-revalidate", StringComparison.OrdinalIgnoreCase))
            {
                d.ProxyRevalidate = true;
            }
            else if (name.Equals("only-if-cached", StringComparison.OrdinalIgnoreCase))
            {
                d.OnlyIfCached = true;
            }
            else if (name.Equals("max-age", StringComparison.OrdinalIgnoreCase))
            {
                SetDelta(arg, ref d.MaxAge, ref d.MalformedFreshness);
            }
            else if (name.Equals("s-maxage", StringComparison.OrdinalIgnoreCase))
            {
                SetDelta(arg, ref d.SMaxAge, ref d.MalformedFreshness);
            }
            else if (name.Equals("stale-while-revalidate", StringComparison.OrdinalIgnoreCase))
            {
                var ignored = false;
                SetDelta(arg, ref d.StaleWhileRevalidate, ref ignored);
            }
            else if (name.Equals("stale-if-error", StringComparison.OrdinalIgnoreCase))
            {
                var ignored = false;
                SetDelta(arg, ref d.StaleIfError, ref ignored);
            }
            else if (name.Equals("min-fresh", StringComparison.OrdinalIgnoreCase))
            {
                var ignored = false;
                SetDelta(arg, ref d.MinFresh, ref ignored);
            }
        }
    }

    /// <summary>Index of the next top-level comma, skipping commas inside quoted strings.</summary>
    private static int NextDirectiveEnd(ReadOnlySpan<char> span)
    {
        var quoted = false;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                return i;
            }
        }

        return span.Length;
    }

    private static void SetDelta(ReadOnlySpan<char> arg, ref long target, ref bool malformed)
    {
        if (!TryParseDeltaSeconds(arg, out var seconds))
        {
            malformed = true;
            return;
        }

        // First occurrence wins when a directive repeats (the conservative reading is the smaller, but a
        // duplicate is itself a sign of a broken origin; keep the first so the result is deterministic).
        if (target < 0)
        {
            target = seconds;
        }
    }

    internal static bool TryParseDeltaSeconds(ReadOnlySpan<char> text, out long seconds)
    {
        seconds = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        // RFC 9111 section 1.2.2: a value that overflows 31 bits is treated as 2^31.
        if (text.Length > 10 || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
            || seconds > int.MaxValue)
        {
            seconds = 2147483648L;
        }

        return true;
    }

    // ---------------------------------------------------------------- headers

    internal static string? First(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            if (headers[i].Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return headers[i].Value;
            }
        }

        return null;
    }

    internal static bool Has(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        First(headers, name) is not null;

    internal static DateTimeOffset? ParseHttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(value.Trim(), HttpDateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    internal static string FormatHttpDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- storing

    /// <summary>What the cache decided to keep about one origin response.</summary>
    internal sealed class StorePlan
    {
        public required int StatusCode { get; init; }
        public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        public required DateTimeOffset StoredUtc { get; init; }
        public required long InitialAgeSeconds { get; init; }
        public required long FreshnessLifetimeSeconds { get; init; }
        public required DateTimeOffset ExpiresUtc { get; init; }
        public required DateTimeOffset RetainUntilUtc { get; init; }
        public required int StaleWhileRevalidateSeconds { get; init; }
        public required int StaleIfErrorSeconds { get; init; }
        public required bool MustRevalidate { get; init; }
        public required bool EntryHasValidator { get; init; }

        /// <summary>Named Vary headers (lower-case, Accept-Encoding excluded because it is always keyed).</summary>
        public required IReadOnlyList<string> VaryNames { get; init; }

        public required bool HasDateHeader { get; init; }
    }

    /// <summary>
    /// Decides whether a shared cache may keep an origin response and, if so, for how long.
    /// Never stores: Set-Cookie, Cache-Control private / no-store / no-cache, Vary: *, a Vary naming more than
    /// <see cref="MaxVaryHeaders"/> headers, partial content, and a response with no usable freshness.
    /// </summary>
    internal static bool TryPlanStore( // NOSONAR S3776 -- Store planning combines RFC 9111 refusal rules and freshness math.
        int statusCode,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        DateTimeOffset now,
        ResponseCacheOptions options,
        out StorePlan? plan,
        out string refusal)
    {
        plan = null;
        refusal = string.Empty;
        if (!IsCacheableStatus(statusCode))
        {
            refusal = "status";
            return false;
        }

        if (headers.Any(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
                              h.Key.Equals("Set-Cookie2", StringComparison.OrdinalIgnoreCase)))
        {
            refusal = "set-cookie";
            return false;
        }

        if (headers.Any(h => h.Key.Equals("Content-Range", StringComparison.OrdinalIgnoreCase)))
        {
            refusal = "partial";
            return false;
        }

        var cc = ParseCacheControl(headers);
        if (cc.NoStore || cc.Private || cc.NoCache)
        {
            refusal = "cache-control";
            return false;
        }

        if (!TryParseVary(headers, out var varyNames, out refusal))
        {
            return false;
        }

        var date = ParseHttpDate(First(headers, "Date"));
        var hasDate = date is not null;
        var dateValue = date ?? now;

        var lastModified = ParseHttpDate(First(headers, "Last-Modified"));
        var hasValidator = lastModified is not null || !string.IsNullOrWhiteSpace(First(headers, "ETag"));

        var explicitFreshness = true;
        long lifetime;
        if (cc.SMaxAge >= 0)
        {
            lifetime = cc.SMaxAge;
        }
        else if (cc.MaxAge >= 0)
        {
            lifetime = cc.MaxAge;
        }
        else if (cc.MalformedFreshness)
        {
            lifetime = 0;
        }
        else if (Has(headers, "Expires"))
        {
            // An invalid Expires (including "0" and "-1") means already expired.
            var expires = ParseHttpDate(First(headers, "Expires"));
            lifetime = expires is { } e ? (long)Math.Max(0, (e - dateValue).TotalSeconds) : 0;
        }
        else
        {
            explicitFreshness = false;
            lifetime = 0;
            if (options.HeuristicFreshness && lastModified is { } lm && dateValue > lm)
            {
                lifetime = Math.Min(HeuristicCapSeconds, (long)((dateValue - lm).TotalSeconds * 0.1));
            }
            else if (options.DefaultTtl is { } ttl && ttl > TimeSpan.Zero)
            {
                lifetime = (long)ttl.TotalSeconds;
            }
        }

        lifetime = Math.Min(lifetime, MaxFreshnessSeconds);

        var mustRevalidate = cc.MustRevalidate || cc.ProxyRevalidate || cc.SMaxAge >= 0;
        var swr = mustRevalidate ? 0 : (int)Math.Clamp(Math.Max(0, cc.StaleWhileRevalidate), 0, MaxStaleExtensionSeconds);
        var sie = mustRevalidate ? 0 : (int)Math.Clamp(Math.Max(0, cc.StaleIfError), 0, MaxStaleExtensionSeconds);

        long ageValue = 0;
        if (First(headers, "Age") is { } ageText && TryParseDeltaSeconds(ageText.AsSpan().Trim(), out var parsedAge))
        {
            ageValue = parsedAge;
        }

        var apparentAge = (long)Math.Max(0, (now - dateValue).TotalSeconds);
        var initialAge = Math.Max(apparentAge, ageValue);

        // Without explicit freshness, heuristic freshness or a configured default there is nothing to serve from.
        if (!explicitFreshness && lifetime == 0)
        {
            refusal = "no-freshness";
            return false;
        }

        var remaining = lifetime - initialAge;
        if (remaining <= 0 && !hasValidator && swr == 0 && sie == 0)
        {
            refusal = "stale-on-arrival";
            return false;
        }

        var expiresUtc = now.AddSeconds(Math.Max(0, remaining));
        long extension = Math.Max(swr, sie);
        if (hasValidator)
        {
            extension = Math.Max(extension, (long)options.StaleRetention.TotalSeconds);
        }

        extension = Math.Min(extension, MaxStaleExtensionSeconds);

        plan = new StorePlan
        {
            StatusCode = statusCode,
            Headers = headers,
            StoredUtc = now,
            InitialAgeSeconds = initialAge,
            FreshnessLifetimeSeconds = lifetime,
            ExpiresUtc = expiresUtc,
            RetainUntilUtc = expiresUtc.AddSeconds(extension),
            StaleWhileRevalidateSeconds = swr,
            StaleIfErrorSeconds = sie,
            MustRevalidate = mustRevalidate,
            EntryHasValidator = hasValidator,
            VaryNames = varyNames,
            HasDateHeader = hasDate,
        };
        return true;
    }

    private static bool TryParseVary( // NOSONAR S3776 -- Vary parsing validates tokens and enforces header limits.
        IReadOnlyList<KeyValuePair<string, string>> headers,
        out List<string> names,
        out string refusal)
    {
        names = [];
        refusal = string.Empty;
        foreach (var h in headers.Where(h => h.Key.Equals("Vary", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var raw in h.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw == "*")
                {
                    refusal = "vary-star";
                    return false;
                }

                if (!IsToken(raw))
                {
                    refusal = "vary-invalid";
                    return false;
                }

                var name = raw.ToLowerInvariant();
                if (name == "accept-encoding" || names.Contains(name))
                {
                    continue;
                }

                if (names.Count >= MaxVaryHeaders)
                {
                    refusal = "vary-too-many";
                    return false;
                }

                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return true;
    }

    private static bool IsToken(string value)
    {
        foreach (var c in value)
        {
            var ok = c is > ' ' and < (char)0x7f && "()<>@,;:\\\"/[]?={} \t".IndexOf(c) < 0;
            if (!ok)
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static readonly HashSet<string> NeverStoredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Proxy-Authenticate", "Proxy-Authorization", "Set-Cookie", "Set-Cookie2", "Age", "Content-Length",
    };

    /// <summary>Headers worth keeping, with framing normalized: no hop-by-hop, no Age, one Content-Length for the body.</summary>
    internal static List<KeyValuePair<string, string>> BuildStoredHeaders( // NOSONAR S3776 -- Stored header normalization combines hop-by-hop stripping and framing.
        IReadOnlyList<KeyValuePair<string, string>> headers,
        int statusCode,
        int bodyLength,
        DateTimeOffset now,
        bool hasDate)
    {
        HashSet<string>? listed = null;
        foreach (var h in headers.Where(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var part in h.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .Where(part => !part.Equals("close", StringComparison.OrdinalIgnoreCase) &&
                                        !part.Equals("keep-alive", StringComparison.OrdinalIgnoreCase)))
            {
                (listed ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(part);
            }
        }

        var kept = new List<KeyValuePair<string, string>>(headers.Count + 2);
        foreach (var h in headers.Where(h => !NeverStoredHeaders.Contains(h.Key) &&
                                             (listed is null || !listed.Contains(h.Key))))
        {
            kept.Add(h);
        }

        if (!hasDate)
        {
            kept.Add(new KeyValuePair<string, string>("Date", FormatHttpDate(now)));
        }

        if (statusCode != 204)
        {
            kept.Add(new KeyValuePair<string, string>("Content-Length",
                bodyLength.ToString(CultureInfo.InvariantCulture)));
        }

        return kept;
    }

    internal static CachedHttpResponse BuildEntry(StorePlan plan, byte[] body) => new()
    {
        StatusCode = plan.StatusCode,
        Body = body,
        Headers = BuildStoredHeaders(plan.Headers, plan.StatusCode, body.Length, plan.StoredUtc, plan.HasDateHeader),
        ExpiresUtc = plan.ExpiresUtc,
        StoredUtc = plan.StoredUtc,
        InitialAgeSeconds = plan.InitialAgeSeconds,
        RetainUntilUtc = plan.RetainUntilUtc,
        StaleWhileRevalidateSeconds = plan.StaleWhileRevalidateSeconds,
        StaleIfErrorSeconds = plan.StaleIfErrorSeconds,
        MustRevalidate = plan.MustRevalidate,
    };

    // ---------------------------------------------------------------- evaluating a stored entry

    internal readonly record struct EntryAge(long AgeSeconds, long RemainingFreshSeconds, bool Fresh, long StalenessSeconds);

    internal static EntryAge Evaluate(CachedHttpResponse entry, DateTimeOffset now)
    {
        var resident = entry.StoredUtc == default ? 0 : (long)Math.Max(0, (now - entry.StoredUtc).TotalSeconds);
        var age = entry.InitialAgeSeconds + resident;
        var remaining = (long)Math.Floor((entry.ExpiresUtc - now).TotalSeconds);
        if (entry.ExpiresUtc > now && remaining == 0)
        {
            remaining = 1;
        }

        var fresh = entry.ExpiresUtc > now;
        return new EntryAge(age, fresh ? remaining : 0, fresh, fresh ? 0 : Math.Max(0, -remaining));
    }

    /// <summary>The parts of a client request that restrict what a cache may answer with.</summary>
    internal readonly struct RequestDirectives
    {
        public bool NoStore { get; init; }
        public bool NoCache { get; init; }
        public bool OnlyIfCached { get; init; }
        public long MaxAge { get; init; }
        public long MinFresh { get; init; }

        /// <summary>No request restrictions (max-age and min-fresh absent are -1, not 0).</summary>
        public static RequestDirectives None => new() { MaxAge = -1, MinFresh = -1 };

        public static RequestDirectives From(Func<string, IReadOnlyList<string>?> header)
        {
            var d = ParseCacheControl(header("Cache-Control"));
            var noCache = d.NoCache;
            if (!noCache && header("Cache-Control") is not { Count: > 0 } && header("Pragma") is { } pragma)
            {
                noCache = pragma.Any(p => p.Contains("no-cache", StringComparison.OrdinalIgnoreCase));
            }

            return new RequestDirectives
            {
                NoStore = d.NoStore,
                NoCache = noCache,
                OnlyIfCached = d.OnlyIfCached,
                MaxAge = d.MaxAge,
                MinFresh = d.MinFresh,
            };
        }
    }

    internal enum CacheDecision
    {
        /// <summary>Serve the stored response.</summary>
        Hit,

        /// <summary>Stale but inside stale-while-revalidate: serve it, one request revalidates.</summary>
        ServeStale,

        /// <summary>Ask the origin with a conditional request.</summary>
        Revalidate,

        /// <summary>Fetch from the origin unconditionally.</summary>
        Miss,
    }

    internal static CacheDecision Decide(CachedHttpResponse entry, EntryAge age, RequestDirectives request)
    {
        var clientForces = request.NoCache || (request.MaxAge >= 0 && (request.MaxAge == 0 || age.AgeSeconds > request.MaxAge));
        var minFreshViolated = request.MinFresh >= 0 && age.RemainingFreshSeconds < request.MinFresh;
        if (!clientForces && !minFreshViolated && age.Fresh)
        {
            return CacheDecision.Hit;
        }

        if (!clientForces && !minFreshViolated && !age.Fresh && !entry.MustRevalidate
            && entry.StaleWhileRevalidateSeconds > 0 && age.StalenessSeconds <= entry.StaleWhileRevalidateSeconds)
        {
            return CacheDecision.ServeStale;
        }

        return HasValidator(entry) ? CacheDecision.Revalidate : CacheDecision.Miss;
    }

    /// <summary>True when an error from the origin may be answered with this stale entry (RFC 5861).</summary>
    internal static bool MayServeStaleOnError(CachedHttpResponse entry, EntryAge age, RequestDirectives request) =>
        !entry.MustRevalidate && !request.NoCache && request.MaxAge < 0
        && entry.StaleIfErrorSeconds > 0 && !age.Fresh && age.StalenessSeconds <= entry.StaleIfErrorSeconds;

    internal static bool HasValidator(CachedHttpResponse entry) =>
        ETagOf(entry) is not null || LastModifiedOf(entry) is not null;

    internal static string? ETagOf(CachedHttpResponse entry) =>
        First(entry.Headers, "ETag") is { Length: > 0 } etag ? etag.Trim() : null;

    internal static string? LastModifiedOf(CachedHttpResponse entry)
    {
        var text = First(entry.Headers, "Last-Modified");
        return ParseHttpDate(text) is null ? null : text!.Trim();
    }

    // ---------------------------------------------------------------- 304 merge

    private static readonly HashSet<string> NotUpdatedBy304 = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Encoding", "Content-Length", "Content-Range", "Content-Type",
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Proxy-Authenticate", "Proxy-Authorization", "Set-Cookie", "Set-Cookie2",
    };

    /// <summary>
    /// RFC 9111 section 3.2: fold the headers of a 304 into the stored response. Content-Encoding, Content-Length and
    /// framing headers are kept from the stored response; everything else the 304 sends replaces the stored field.
    /// Returns the merged header list (stored Age is not kept: the 304's own Age, if any, restarts the age).
    /// </summary>
    internal static List<KeyValuePair<string, string>> MergeNotModified(
        CachedHttpResponse stored,
        IReadOnlyList<KeyValuePair<string, string>> notModified)
    {
        var replaced = notModified.Where(h => !NotUpdatedBy304.Contains(h.Key))
            .Select(h => h.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new List<KeyValuePair<string, string>>(stored.Headers.Count + notModified.Count);
        foreach (var h in stored.Headers.Where(h => !replaced.Contains(h.Key)))
        {
            merged.Add(h);
        }

        foreach (var h in notModified.Where(h => !NotUpdatedBy304.Contains(h.Key)))
        {
            merged.Add(h);
        }

        return merged;
    }

    // ---------------------------------------------------------------- keys

    /// <summary>
    /// Accept-Encoding reduced to the sorted set of codings the client accepts (q=0 removed), so spelling and
    /// weights do not split the cache but a different acceptable set never aliases.
    /// </summary>
    internal static string NormalizeAcceptEncoding(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var tokens = new List<string>(4);
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var semi = part.IndexOf(';');
            var coding = (semi < 0 ? part : part[..semi]).Trim().ToLowerInvariant();
            if (coding.Length == 0)
            {
                continue;
            }

            if (semi >= 0)
            {
                var q = part[(semi + 1)..].Replace(" ", string.Empty, StringComparison.Ordinal);
                if (q.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(q[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                    && weight <= 0)
                {
                    continue;
                }
            }

            if (!tokens.Contains(coding))
            {
                tokens.Add(coding);
            }
        }

        tokens.Sort(StringComparer.Ordinal);
        return string.Join(',', tokens);
    }

    private static readonly HashSet<string> CaseInsensitiveVaryValues = new(StringComparer.Ordinal)
    {
        "accept", "accept-language", "accept-charset", "content-type", "content-language",
    };

    /// <summary>Header value normalized for a secondary key: fields joined, whitespace collapsed, token headers lower-cased.</summary>
    internal static string NormalizeVaryValue(string lowerName, IReadOnlyList<string>? values) // NOSONAR S3776 -- Vary normalization handles token casing and whitespace collapse.
    {
        if (values is null || values.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var inSpace = false;
            foreach (var c in values[i].Trim())
            {
                if (c is ' ' or '\t')
                {
                    if (!inSpace)
                    {
                        sb.Append(' ');
                    }

                    inSpace = true;
                }
                else
                {
                    sb.Append(c);
                    inSpace = false;
                }
            }
        }

        var text = sb.ToString();
        return CaseInsensitiveVaryValues.Contains(lowerName) ? text.ToLowerInvariant() : text;
    }

    /// <summary>True when the text can sit inside a cache key without being able to forge a separator.</summary>
    internal static bool IsKeySafe(string? text)
    {
        if (text is null)
        {
            return true;
        }

        foreach (var c in text)
        {
            if (c < ' ' || c == (char)0x7f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Secondary key: the normalized Accept-Encoding plus each named Vary header. Returns null when a value cannot be
    /// keyed safely (the request then bypasses the cache).
    /// </summary>
    internal static string? BuildSecondaryKey(
        string? acceptEncoding,
        IReadOnlyList<string>? varyNames,
        Func<string, IReadOnlyList<string>?>? requestHeader)
    {
        if (!IsKeySafe(acceptEncoding))
        {
            return null;
        }

        var encodings = NormalizeAcceptEncoding(acceptEncoding);
        if (encodings.Length > MaxKeyPartChars)
        {
            return null;
        }

        var sb = new StringBuilder(24);
        sb.Append("ae=").Append(encodings);
        if (varyNames is { Count: > 0 })
        {
            foreach (var name in varyNames)
            {
                var values = requestHeader?.Invoke(name);
                var normalized = NormalizeVaryValue(name, values);
                if (!IsKeySafe(normalized) || normalized.Length > MaxKeyPartChars)
                {
                    return null;
                }

                sb.Append(KeySeparator).Append(name).Append('=').Append(normalized);
            }
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- client conditionals

    internal enum ClientConditional
    {
        /// <summary>No usable validator on the request: answer in full.</summary>
        None,

        /// <summary>The client's copy is current: answer 304.</summary>
        NotModified,
    }

    internal static ClientConditional EvaluateClientConditional(
        CachedHttpResponse entry,
        Func<string, IReadOnlyList<string>?> requestHeader)
    {
        if (requestHeader("If-None-Match") is { Count: > 0 } inm)
        {
            var etag = ETagOf(entry);
            if (etag is null)
            {
                return ClientConditional.None;
            }

            if (inm.SelectMany(line => line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Any(candidate => candidate == "*" || WeakEquals(candidate, etag)))
            {
                return ClientConditional.NotModified;
            }

            return ClientConditional.None;
        }

        if (requestHeader("If-Modified-Since") is { Count: > 0 } ims
            && ParseHttpDate(ims[0]) is { } since
            && ParseHttpDate(LastModifiedOf(entry)) is { } modified
            && modified <= since)
        {
            return ClientConditional.NotModified;
        }

        return ClientConditional.None;
    }

    private static bool WeakEquals(string a, string b)
    {
        static string Strip(string s) => s.StartsWith("W/", StringComparison.Ordinal) ? s[2..] : s;
        return Strip(a.Trim()) == Strip(b.Trim());
    }
}
