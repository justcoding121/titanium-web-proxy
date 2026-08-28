using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Web.Proxy.Caching;

/// <summary>In-process GET/HEAD response cache. Zero cost when unused (not registered in middleware).</summary>
public sealed class MemoryHttpResponseCache : IHttpResponseCache
{
    private readonly ConcurrentDictionary<string, CachedHttpResponse> _entries =
        new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public bool TryGet(string cacheKey, out CachedHttpResponse? response)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);

        if (!_entries.TryGetValue(cacheKey, out var entry))
        {
            response = null;
            return false;
        }

        if (entry.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(cacheKey, out _);
            response = null;
            return false;
        }

        response = entry;
        return true;
    }

    public void Set(string cacheKey, CachedHttpResponse response, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        ArgumentNullException.ThrowIfNull(response);

        var expires = response.ExpiresUtc > DateTimeOffset.MinValue
            ? response.ExpiresUtc
            : DateTimeOffset.UtcNow + (ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : ttl);

        _entries[cacheKey] = new CachedHttpResponse
        {
            StatusCode = response.StatusCode,
            Body = response.Body,
            Headers = response.Headers,
            ExpiresUtc = expires,
        };
    }

    public int Purge(string? pathPrefix = null)
    {
        if (string.IsNullOrEmpty(pathPrefix))
        {
            var n = _entries.Count;
            _entries.Clear();
            return n;
        }

        var removed = 0;
        foreach (var key in _entries.Keys.ToArray())
        {
            if (key.Contains(pathPrefix, StringComparison.OrdinalIgnoreCase) &&
                _entries.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
