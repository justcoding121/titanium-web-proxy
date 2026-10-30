using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// In-process response cache bounded by bytes. Entries are charged for their body, headers and key; when the total
/// would exceed <see cref="MaxBytes"/> the least recently used entries are evicted first. Zero cost when unused
/// (not registered in middleware).
/// </summary>
/// <remarks>
/// The store is split into up to 16 independently locked shards, so concurrent lookups of different keys do not
/// contend, and eviction recency is exact within a shard (approximate across shards). Each shard owns an equal slice
/// of <see cref="MaxBytes"/>, and the shard count shrinks until every slice can hold one maximum-size entry.
/// Resident bodies are exact-size arrays shared by all readers (a hit copies nothing); the pooled staging buffers
/// used while a streamed body is accumulated are returned before the entry is stored.
/// </remarks>
public sealed class MemoryHttpResponseCache : IHttpResponseCache
{
    /// <summary>Default total budget: 64 MiB.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>Default largest single body: 1 MiB.</summary>
    public const int DefaultMaxEntryBytes = 1024 * 1024;

    private const int MaxShards = 16;

    /// <summary>Room reserved per shard for the headers and bookkeeping of a maximum-size entry.</summary>
    private const long PerEntryAllowance = 16 * 1024;

    private sealed class Node
    {
        public required string Key;
        public required CachedHttpResponse Value;
        public required long Bytes;
        public Node? Prev;
        public Node? Next;
    }

    private sealed class Shard
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, Node> Map = new(StringComparer.Ordinal);
        public Node? Head;
        public Node? Tail;
        public long Bytes;
        public long Budget;
    }

    private readonly Shard[] _shards;
    private readonly int _shardMask;
    private long _bytes;
    private int _count;
    private long _evictions;

    public MemoryHttpResponseCache(long maxBytes = DefaultMaxBytes, int maxEntryBytes = DefaultMaxEntryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntryBytes, 0);
        MaxBytes = maxBytes;
        MaxEntryBytes = maxEntryBytes;

        var shards = MaxShards;
        while (shards > 1 && maxBytes / shards < maxEntryBytes + PerEntryAllowance)
        {
            shards >>= 1;
        }

        _shards = new Shard[shards];
        _shardMask = shards - 1;
        for (var i = 0; i < shards; i++)
        {
            _shards[i] = new Shard { Budget = maxBytes / shards };
        }

        ProxyMetrics.RegisterResponseCache(this);
    }

    /// <summary>Total byte budget across all entries.</summary>
    public long MaxBytes { get; }

    /// <summary>Largest body that will be stored.</summary>
    public int MaxEntryBytes { get; }

    public int Count => Volatile.Read(ref _count);

    /// <summary>Bytes currently charged, headers and keys included.</summary>
    public long Bytes => Volatile.Read(ref _bytes);

    /// <summary>Entries removed to make room since creation.</summary>
    public long Evictions => Volatile.Read(ref _evictions);

    public bool TryGet(string cacheKey, out CachedHttpResponse? response)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);

        var shard = ShardFor(cacheKey);
        lock (shard.Gate)
        {
            if (!shard.Map.TryGetValue(cacheKey, out var node))
            {
                response = null;
                return false;
            }

            if (RetainedUntil(node.Value) <= DateTimeOffset.UtcNow)
            {
                Remove(shard, node);
                response = null;
                return false;
            }

            MoveToFront(shard, node);
            response = node.Value;
            return true;
        }
    }

    public void Set(string cacheKey, CachedHttpResponse response, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        ArgumentNullException.ThrowIfNull(response);

        if (response.Body.Length > MaxEntryBytes)
        {
            return;
        }

        if (response.ExpiresUtc <= DateTimeOffset.MinValue)
        {
            var effectiveTtl = ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : ttl;
            response = new CachedHttpResponse
            {
                StatusCode = response.StatusCode,
                Body = response.Body,
                Headers = response.Headers,
                ExpiresUtc = DateTimeOffset.UtcNow + effectiveTtl,
                StoredUtc = response.StoredUtc,
                InitialAgeSeconds = response.InitialAgeSeconds,
                RetainUntilUtc = response.RetainUntilUtc,
                StaleWhileRevalidateSeconds = response.StaleWhileRevalidateSeconds,
                StaleIfErrorSeconds = response.StaleIfErrorSeconds,
                MustRevalidate = response.MustRevalidate,
                VaryNames = response.VaryNames,
            };
        }

        var bytes = EstimateBytes(cacheKey, response);
        var shard = ShardFor(cacheKey);
        if (bytes > shard.Budget)
        {
            return;
        }

        lock (shard.Gate)
        {
            if (shard.Map.TryGetValue(cacheKey, out var existing))
            {
                Remove(shard, existing);
            }

            while (shard.Bytes + bytes > shard.Budget && shard.Tail is { } victim)
            {
                Remove(shard, victim);
                Interlocked.Increment(ref _evictions);
            }

            var node = new Node { Key = cacheKey, Value = response, Bytes = bytes };
            shard.Map[cacheKey] = node;
            PushFront(shard, node);
            shard.Bytes += bytes;
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// Removes entries. <c>null</c> or empty removes everything. Otherwise an entry goes when its key starts with
    /// <paramref name="pathPrefix"/>, or the key without its leading <c>METHOD:</c> (and then without
    /// <c>http://</c> or <c>https://</c>) does, compared case-insensitively. Variant markers go with their resource.
    /// </summary>
    public int Purge(string? pathPrefix = null)
    {
        var removed = 0;
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                if (string.IsNullOrEmpty(pathPrefix))
                {
                    removed += shard.Map.Count;
                    Interlocked.Add(ref _bytes, -shard.Bytes);
                    Interlocked.Add(ref _count, -shard.Map.Count);
                    shard.Map.Clear();
                    shard.Head = shard.Tail = null;
                    shard.Bytes = 0;
                    continue;
                }

                List<Node>? doomed = null;
                foreach (var node in shard.Map.Values.Where(node => KeyMatchesPrefix(node.Key, pathPrefix)))
                {
                    (doomed ??= []).Add(node);
                }

                if (doomed is null)
                {
                    continue;
                }

                foreach (var node in doomed)
                {
                    Remove(shard, node);
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>Removes every entry whose key starts with <paramref name="keyPrefix"/> exactly (ordinal).</summary>
    internal int PurgeExactPrefix(string keyPrefix)
    {
        var removed = 0;
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                List<Node>? doomed = null;
                foreach (var node in shard.Map.Values.Where(node => node.Key.StartsWith(keyPrefix, StringComparison.Ordinal)))
                {
                    (doomed ??= []).Add(node);
                }

                if (doomed is null)
                {
                    continue;
                }

                foreach (var node in doomed)
                {
                    Remove(shard, node);
                    removed++;
                }
            }
        }

        return removed;
    }

    internal static bool KeyMatchesPrefix(string key, string prefix)
    {
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colon = key.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var rest = key.AsSpan(colon + 1);
        if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (rest.StartsWith("https://", StringComparison.Ordinal))
        {
            rest = rest["https://".Length..];
        }
        else if (rest.StartsWith("http://", StringComparison.Ordinal))
        {
            rest = rest["http://".Length..];
        }
        else
        {
            return false;
        }

        return rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What an entry costs: body, headers (UTF-16 text plus per-field overhead), key, and bookkeeping.</summary>
    internal static long EstimateBytes(string key, CachedHttpResponse response)
    {
        long bytes = 128 + 24 + response.Body.Length + (2L * key.Length) + 22;
        foreach (var header in response.Headers)
        {
            bytes += (2L * (header.Key.Length + header.Value.Length)) + 56;
        }

        if (response.VaryNames is { } vary)
        {
            foreach (var name in vary)
            {
                bytes += (2L * name.Length) + 30;
            }
        }

        return bytes;
    }

    private static DateTimeOffset RetainedUntil(CachedHttpResponse entry) =>
        entry.RetainUntilUtc > entry.ExpiresUtc ? entry.RetainUntilUtc : entry.ExpiresUtc;

    private Shard ShardFor(string key) => _shards[(key.GetHashCode() & int.MaxValue) & _shardMask];

    private void Remove(Shard shard, Node node)
    {
        shard.Map.Remove(node.Key);
        Unlink(shard, node);
        shard.Bytes -= node.Bytes;
        Interlocked.Add(ref _bytes, -node.Bytes);
        Interlocked.Decrement(ref _count);
    }

    private static void PushFront(Shard shard, Node node)
    {
        node.Prev = null;
        node.Next = shard.Head;
        if (shard.Head is not null)
        {
            shard.Head.Prev = node;
        }

        shard.Head = node;
        shard.Tail ??= node;
    }

    private static void Unlink(Shard shard, Node node)
    {
        if (node.Prev is not null)
        {
            node.Prev.Next = node.Next;
        }
        else
        {
            shard.Head = node.Next;
        }

        if (node.Next is not null)
        {
            node.Next.Prev = node.Prev;
        }
        else
        {
            shard.Tail = node.Prev;
        }

        node.Prev = node.Next = null;
    }

    private static void MoveToFront(Shard shard, Node node)
    {
        if (ReferenceEquals(shard.Head, node))
        {
            return;
        }

        Unlink(shard, node);
        PushFront(shard, node);
    }
}
