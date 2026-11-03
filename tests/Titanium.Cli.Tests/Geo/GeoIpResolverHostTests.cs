using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Geo;

namespace Titanium.Cli.Tests.Geo;

[TestClass]
public class GeoIpResolverHostTests
{
    private const string TestDatabaseUrl =
        "https://github.com/maxmind/MaxMind-DB/raw/main/test-data/GeoLite2-Country-Test.mmdb";

    [TestMethod]
    public async Task Apply_LoadsTheDatabase_AndResolvesTheMappedCountry()
    {
        var path = await EnsureOfficialDatabaseAsync();
        using var host = new GeoIpResolverHost();
        host.Apply(path, startup: true);
        Assert.IsNotNull(host.Resolver);
        Assert.IsTrue(host.Resolver.TryGetCountry(IPAddress.Parse("2.125.160.216"), out var code));
        Assert.AreEqual("GB", code);
        Assert.IsFalse(host.Resolver.TryGetCountry(IPAddress.Loopback, out _));

        host.Apply(null, startup: true);
        Assert.IsNull(host.Resolver);
    }

    private static async Task<string> EnsureOfficialDatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-geolite2-country-test.mmdb");
        if (!File.Exists(path) || new FileInfo(path).Length < 1000)
        {
            try
            {
                using var client = new HttpClient();
                await using var stream = await client.GetStreamAsync(TestDatabaseUrl);
                await using var file = File.Create(path);
                await stream.CopyToAsync(file);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive("The official GeoIP test database could not be downloaded: " + ex.Message);
            }
        }

        return path;
    }

    [TestMethod]
    public void Apply_MissingDatabase_FailsStartup()
    {
        using var host = new GeoIpResolverHost();
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            host.Apply(Path.Combine(Path.GetTempPath(), "twp-missing-" + Guid.NewGuid().ToString("N") + ".mmdb"), startup: true));
    }

    [TestMethod]
    public void Apply_MissingDatabase_OnReload_KeepsPreviousAndWarns()
    {
        using var host = new GeoIpResolverHost();
        var warnings = new List<string>();
        host.Apply(
            Path.Combine(Path.GetTempPath(), "twp-missing-" + Guid.NewGuid().ToString("N") + ".mmdb"),
            startup: false,
            warn: warnings.Add);
        Assert.IsNull(host.Resolver);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "not found");
    }

    [TestMethod]
    public async Task Apply_SamePathAndTimestamp_IsANoOp()
    {
        var path = await EnsureOfficialDatabaseAsync();
        using var host = new GeoIpResolverHost();
        host.Apply(path, startup: true);
        var first = host.Resolver;
        host.Apply(path, startup: true);
        Assert.AreSame(first, host.Resolver);
    }

    [TestMethod]
    public void Apply_UnreadablePath_OnReload_WarnsWithoutThrowing()
    {
        using var host = new GeoIpResolverHost();
        var warnings = new List<string>();
        // A directory path makes GetLastWriteTimeUtc succeed on some OSes and Create fail;
        // a null device path is not portable. Use a missing file for the reload warn path above
        // and an empty file that is not a valid MMDB for the corrupt-reload path.
        var empty = Path.Combine(Path.GetTempPath(), "twp-empty-" + Guid.NewGuid().ToString("N") + ".mmdb");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        try
        {
            host.Apply(empty, startup: false, warn: warnings.Add);
            Assert.IsNull(host.Resolver);
            Assert.IsTrue(warnings.Count >= 1, string.Join("; ", warnings));
        }
        finally
        {
            try { File.Delete(empty); } catch { /* ignore */ }
        }
    }
}
