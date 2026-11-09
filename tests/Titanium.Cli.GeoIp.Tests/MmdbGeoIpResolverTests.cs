using System.Net;
using MaxMind.Db;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.GeoIp;
using Titanium.Cli.Tests.Geo;

namespace Titanium.Cli.GeoIp.Tests;

[TestClass]
public class MmdbGeoIpResolverTests
{
    private const string TestDatabaseUrl =
        "https://github.com/maxmind/MaxMind-DB/raw/main/test-data/GeoLite2-Country-Test.mmdb";

    [TestMethod]
    public void WriterOutput_EndsWithMetadataMarker()
    {
        using var stream = new MemoryStream();
        MmdbTestWriter.Write(stream, [(IPAddress.Parse("127.0.0.1"), 32, "XX")]);
        var bytes = stream.ToArray();
        CollectionAssert.AreEqual(
            new byte[] { 0xAB, 0xCD, 0xEF, (byte)'M', (byte)'a', (byte)'x', (byte)'M', (byte)'i', (byte)'n', (byte)'d', (byte)'.', (byte)'c', (byte)'o', (byte)'m' },
            bytes[^14..]);
    }

    [TestMethod]
    public async Task Resolver_ReadsOfficialTestDatabase()
    {
        var path = await EnsureTestDatabaseAsync();
        using var resolver = new MmdbGeoIpResolver(path);
        Assert.IsTrue(resolver.TryGetCountry(IPAddress.Parse("2.125.160.216"), out var code));
        Assert.AreEqual("GB", code);
        Assert.IsFalse(resolver.TryGetCountry(IPAddress.Parse("10.0.0.0"), out _));
    }

    [TestMethod]
    public async Task Cache_ClearsWhenFull()
    {
        var path = await EnsureTestDatabaseAsync();
        using var resolver = new MmdbGeoIpResolver(path);
        for (var i = 0; i < 20_000; i++)
        {
            _ = resolver.TryGetCountry(IPAddress.Parse($"10.0.{i / 256}.{i % 256}"), out _);
        }

        Assert.IsTrue(resolver.TryGetCountry(IPAddress.Parse("2.125.160.216"), out var code));
        Assert.AreEqual("GB", code);
    }

    [TestMethod]
    public void RegisteredCountry_WithoutCountry_ReturnsThatIsoCode()
    {
        var data = new Dictionary<string, object>
        {
            ["registered_country"] = new Dictionary<string, object> { ["iso_code"] = "de" },
        };
        Assert.AreEqual("DE", MmdbGeoIpResolver.ReadCountryCodeForTests(data));
    }

    [TestMethod]
    public void Country_TakesPrecedenceOverRegisteredCountry()
    {
        var data = new Dictionary<string, object>
        {
            ["country"] = new Dictionary<string, object> { ["iso_code"] = "fr" },
            ["registered_country"] = new Dictionary<string, object> { ["iso_code"] = "de" },
        };
        Assert.AreEqual("FR", MmdbGeoIpResolver.ReadCountryCodeForTests(data));
    }

    [TestMethod]
    public void IsoCode_ThatIsNotTwoLetters_IsAMiss()
    {
        var data = new Dictionary<string, object>
        {
            ["country"] = new Dictionary<string, object> { ["iso_code"] = "DEU" },
        };
        Assert.IsNull(MmdbGeoIpResolver.ReadCountryCodeForTests(data));
    }

    [TestMethod]
    public async Task TryGetCountry_AfterDispose_ReturnsFalseAndDoesNotThrow()
    {
        var path = await EnsureTestDatabaseAsync();
        var resolver = new MmdbGeoIpResolver(path);
        Assert.IsTrue(resolver.TryGetCountry(IPAddress.Parse("2.125.160.216"), out var before));
        Assert.AreEqual("GB", before);
        resolver.Dispose();
        Assert.IsFalse(resolver.TryGetCountry(IPAddress.Parse("2.125.160.216"), out var after));
        Assert.AreEqual(string.Empty, after);
        resolver.Dispose();
    }

    [TestMethod]
    public async Task CachedNull_ReturnsFalseWithoutHittingReader()
    {
        // Query a private IP (not in the DB) twice. The second call must hit the cached null entry.
        var path = await EnsureTestDatabaseAsync();
        using var resolver = new MmdbGeoIpResolver(path);

        var privateIp = System.Net.IPAddress.Parse("10.0.0.1");

        // First call: reader returns null (private IP not in test DB), cached as null.
        Assert.IsFalse(resolver.TryGetCountry(privateIp, out var first));
        Assert.AreEqual(string.Empty, first);

        // Second call: null is retrieved from cache; reader is not re-invoked.
        Assert.IsFalse(resolver.TryGetCountry(privateIp, out var second));
        Assert.AreEqual(string.Empty, second);
    }

    [TestMethod]
    public async Task CachedHit_ReturnsTrueFromCache()
    {
        var path = await EnsureTestDatabaseAsync();
        using var resolver = new MmdbGeoIpResolver(path);

        var ip = System.Net.IPAddress.Parse("2.125.160.216");

        // First call fills the cache.
        Assert.IsTrue(resolver.TryGetCountry(ip, out var first));
        Assert.AreEqual("GB", first);

        // Second call returns the cached code without hitting the reader.
        Assert.IsTrue(resolver.TryGetCountry(ip, out var second));
        Assert.AreEqual("GB", second);
    }

    [TestMethod]
    public void ReadCountryCode_NullData_ReturnsNull()
    {
        Assert.IsNull(MmdbGeoIpResolver.ReadCountryCodeForTests(null));
    }

    [TestMethod]
    public void ReadCountryCode_NoCountryOrRegisteredCountry_ReturnsNull()
    {
        var data = new Dictionary<string, object>
        {
            ["continent"] = new Dictionary<string, object> { ["code"] = "EU" },
        };
        Assert.IsNull(MmdbGeoIpResolver.ReadCountryCodeForTests(data));
    }

    [TestMethod]
    public void ReadCountryCode_NonDictionaryCountryValue_IsAMiss()
    {
        // Bug class: MMDB country node that is a string/int instead of a map is treated as a hit.
        var data = new Dictionary<string, object>
        {
            ["country"] = "US",
            ["registered_country"] = 42,
        };
        Assert.IsNull(MmdbGeoIpResolver.ReadCountryCodeForTests(data));
    }

    [TestMethod]
    public void ReadCountryCode_EmptyIsoCode_IsAMiss()
    {
        // Bug class: empty iso_code is accepted and returned as a country.
        var data = new Dictionary<string, object>
        {
            ["country"] = new Dictionary<string, object> { ["iso_code"] = "" },
        };
        Assert.IsNull(MmdbGeoIpResolver.ReadCountryCodeForTests(data));
    }

    [TestMethod]
    public void ReadCountryCode_NonStringIsoCode_IsAMiss()
    {
        // Bug class: numeric iso_code is ToString()'d into a fake country code.
        var data = new Dictionary<string, object>
        {
            ["country"] = new Dictionary<string, object> { ["iso_code"] = 44 },
        };
        Assert.IsNull(MmdbGeoIpResolver.ReadCountryCodeForTests(data));
    }

    private static async Task<string> EnsureTestDatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-geolite2-country-test.mmdb");
        if (!File.Exists(path) || new FileInfo(path).Length < 1000)
        {
            using var client = new HttpClient();
            await using var stream = await client.GetStreamAsync(TestDatabaseUrl);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file);
        }

        using var reader = new Reader(path);
        _ = reader.Metadata;
        return path;
    }
}
