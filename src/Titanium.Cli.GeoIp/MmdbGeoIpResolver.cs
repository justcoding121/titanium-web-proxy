using System.Collections.Concurrent;
using System.Net;
using MaxMind.Db;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Cli.GeoIp;

/// <summary>MaxMind DB (MMDB) country lookup for <see cref="IGeoIpResolver"/>.</summary>
public sealed class MmdbGeoIpResolver : IGeoIpResolver, IDisposable
{
    private const int CacheCapacity = 16_384;

    private readonly Reader _reader;
    private readonly ConcurrentDictionary<IPAddress, string?> _cache = new();
    private int _disposed;

    public MmdbGeoIpResolver(string path)
    {
        _reader = new Reader(path, FileAccessMode.MemoryMapped);
    }

    public bool TryGetCountry(IPAddress address, out string countryIsoCode)
    {
        countryIsoCode = string.Empty;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (_cache.TryGetValue(address, out var cached))
        {
            if (cached is null)
            {
                return false;
            }

            countryIsoCode = cached;
            return true;
        }

        string? resolved = null;
        try
        {
            var data = _reader.Find<Dictionary<string, object>>(address);
            resolved = ReadCountryCode(data);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        Remember(address, resolved);
        if (resolved is null)
        {
            return false;
        }

        countryIsoCode = resolved;
        return true;
    }

    private void Remember(IPAddress address, string? country)
    {
        if (_cache.Count >= CacheCapacity)
        {
            _cache.Clear();
        }

        _cache[address] = country;
    }

    private static string? ReadCountryCode(Dictionary<string, object>? data)
    {
        if (data is null)
        {
            return null;
        }

        if (TryNestedIso(data, "country", out var code) || TryNestedIso(data, "registered_country", out code))
        {
            return code;
        }

        return null;
    }

    private static bool TryNestedIso(Dictionary<string, object> data, string key, out string code)
    {
        code = string.Empty;
        if (!data.TryGetValue(key, out var raw) || raw is not Dictionary<string, object> map)
        {
            return false;
        }

        if (!map.TryGetValue("iso_code", out var iso) || iso is not string text || text.Length != 2)
        {
            return false;
        }

        code = text.ToUpperInvariant();
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cache.Clear();
        _reader.Dispose();
    }
}
