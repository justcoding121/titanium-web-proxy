using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

/// <summary>
///     <c>titanium run</c> with an explicit loopback listener, Plus, and a geo allow-list.
///     Loopback is not in the test database, so an allow-list answers 403 and does not call the origin.
/// </summary>
[TestClass]
public class CliGeoE2ETests
{
    private const string DatabaseUrl =
        "https://github.com/maxmind/MaxMind-DB/raw/main/test-data/GeoLite2-Country-Test.mmdb";

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(90_000)]
    public async Task Run_GeoAllowList_DeniesAnUnknownLoopbackClient()
    {
        var temp = Path.Combine(Path.GetTempPath(), "twp-e2e-geo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using var origin = new EchoOrigin();
        CliProcessHarness? harness = null;
        try
        {
            var database = Path.Combine(temp, "country.mmdb");
            if (!await TryDownloadAsync(database))
            {
                Assert.Inconclusive("The GeoIP test database could not be downloaded.");
                return;
            }

            var listen = CliProcessHarness.GetFreePort();
            var control = CliProcessHarness.GetFreePort();
            var cfg = Path.Combine(temp, "geo.yaml");
            var dbYaml = database.Replace("\\", "/");
            await File.WriteAllTextAsync(cfg, $"""
                schemaVersion: "7.1"
                listeners:
                  - host: "127.0.0.1"
                    port: {listen}
                    decryptSsl: false
                server:
                  geoIpDatabasePath: "{dbYaml}"
                plus:
                  enabled: true
                  controlPlane:
                    host: "127.0.0.1"
                    port: {control}
                    sharedSecret: "geo-e2e-secret"
                policies:
                  - name: geo
                    kind: geo
                    allow: ZZ
                routes:
                  - id: app
                    clusterId: web
                    match:
                      path: /
                      pathKind: Prefix
                    policies: [geo]
                clusters:
                  - id: web
                    destinations:
                      - id: origin
                        address: 127.0.0.1
                        port: {origin.Port}
                """);

            harness = new CliProcessHarness();
            harness.EnsurePlusDllBesideCli(copy: true);
            await harness.StartRunAsync(cfg, new Dictionary<string, string?>
            {
                ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
            });

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var response = await http.GetAsync($"http://127.0.0.1:{listen}/");
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            harness?.Dispose();
            origin.Dispose();
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    private static async Task<bool> TryDownloadAsync(string path)
    {
        var cached = Path.Combine(Path.GetTempPath(), "twp-geolite2-country-test.mmdb");
        if (File.Exists(cached) && new FileInfo(cached).Length > 1000)
        {
            File.Copy(cached, path, overwrite: true);
            return true;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            await using var stream = await client.GetStreamAsync(DatabaseUrl);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file);
            File.Copy(path, cached, overwrite: true);
            return new FileInfo(path).Length > 1000;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }
}
