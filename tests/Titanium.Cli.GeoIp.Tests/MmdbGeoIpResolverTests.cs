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
