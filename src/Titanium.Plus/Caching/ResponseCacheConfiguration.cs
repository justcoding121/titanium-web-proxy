using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Caching;

/// <summary>
/// Reads <c>cache.*</c> from the Plus options dictionary and installs the in-memory response cache.
/// The host calls <see cref="ITitaniumPlusModule.Apply"/> only when Plus is enabled, so this is the
/// <c>plus.enabled</c> half of the old CLI guard.
/// </summary>
internal static class ResponseCacheConfiguration
{
    internal readonly record struct ParsedResponseCacheOptions(long MaxBytes, ResponseCacheOptions Entry);

    /// <summary>
    /// Creates the in-memory cache even when <c>cache.enable</c> is false (control-plane purge) and inserts the
    /// middleware only when caching is on. The insert index is the list length at this moment, so anything the host
    /// registered before Plus stays ahead, and later Plus modules (access control, WAF, shared state) are added after.
    /// </summary>
    internal static MemoryHttpResponseCache Install(PlusActivationContext context)
    {
        var options = context.Options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parsed = Parse(options, message => PlusLog.Warn(context, message));
        var cache = new MemoryHttpResponseCache(parsed.MaxBytes, parsed.Entry.MaxEntryBytes);
        context.ResponseCache = cache;
        if (!IsTruthy(options, "cache.enable"))
        {
            return cache;
        }

        if (context.Middleware is not { } middleware)
        {
            PlusLog.Warn(context, "Plus cache: Middleware list is null — response cache not registered.");
            return cache;
        }

        // The cache middleware keeps the response side (revalidation, body tee, store) on the same hooks the
        // reverse proxy already calls, so no BeforeResponse/AfterResponse subscription is added: those would force
        // every request off H1 terminate-lite and a hit would no longer skip the origin.
        middleware.Insert(middleware.Count, new HttpResponseCacheMiddleware(cache, options: parsed.Entry));
        return cache;
    }

    /// <summary>
    /// Reads the <c>cache.*</c> keys of <c>plus.options</c>. A value that is not a valid number (or is out of range)
    /// is reported and replaced by its default, so a typo never disables or unbounds the cache.
    /// </summary>
    internal static ParsedResponseCacheOptions Parse( // NOSONAR S3776 -- Option parsing keeps defaults and warnings colocated per key.
        IReadOnlyDictionary<string, string> options,
        Action<string> warn)
    {
        long maxBytes = MemoryHttpResponseCache.DefaultMaxBytes;
        var entry = new ResponseCacheOptions();

        long ReadLong(string key, long fallback, long min, long max)
        {
            if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (long.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
            {
                return value;
            }

            warn($"warning: plus.options '{key}' must be an integer from {min} to {max}; using {fallback}.");
            return fallback;
        }

        bool ReadFlag(string key, bool fallback)
        {
            if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" ||
                raw.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0" ||
                raw.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            warn($"warning: plus.options '{key}' must be true or false; using {fallback.ToString().ToLowerInvariant()}.");
            return fallback;
        }

        maxBytes = ReadLong("cache.maxBytes", maxBytes, 1024, long.MaxValue / 4);
        var maxEntryBytes = (int)ReadLong("cache.maxEntryBytes", MemoryHttpResponseCache.DefaultMaxEntryBytes, 1,
            int.MaxValue / 2);
        if (maxEntryBytes > maxBytes)
        {
            warn($"warning: plus.options 'cache.maxEntryBytes' exceeds 'cache.maxBytes'; using {maxBytes}.");
            maxEntryBytes = (int)Math.Min(maxBytes, int.MaxValue / 2);
        }

        var defaultTtlSeconds = ReadLong("cache.defaultTtlSeconds", 0, 0, 365L * 24 * 3600);
        entry = new ResponseCacheOptions
        {
            MaxEntryBytes = maxEntryBytes,
            DefaultTtl = defaultTtlSeconds > 0 ? TimeSpan.FromSeconds(defaultTtlSeconds) : null,
            HeuristicFreshness = ReadFlag("cache.heuristicFreshness", false),
            ExposeHeaders = ReadFlag("cache.exposeHeaders", false),
            Collapse = ReadFlag("cache.collapse", true),
            CollapseMaxWaiters = (int)ReadLong("cache.collapse.maxWaiters", entry.CollapseMaxWaiters, 0, 100_000),
            CollapseTimeout = TimeSpan.FromMilliseconds(
                ReadLong("cache.collapse.timeoutMs", (long)entry.CollapseTimeout.TotalMilliseconds, 1, 600_000)),
            StaleRetention = TimeSpan.FromSeconds(
                ReadLong("cache.staleRetentionSeconds", (long)entry.StaleRetention.TotalSeconds, 0, 7L * 24 * 3600)),
        };
        return new ParsedResponseCacheOptions(maxBytes, entry);
    }

    private static bool IsTruthy(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
