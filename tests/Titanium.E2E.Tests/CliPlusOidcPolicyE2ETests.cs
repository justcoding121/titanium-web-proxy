using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.TestKit;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliPlusOidcPolicyE2ETests
{
    private const string MatrixHost = "matrix.test";
    private const string Callback = "/_titanium/oidc/callback";
    private static readonly string CookieKey = Convert.ToHexString(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-oidc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(120_000)]
    public async Task Run_OidcPolicy_RedirectsBrowserToIdP()
    {
        await using var idp = await FakeIdp.StartAsync(new OidcClock());
        idp.RegisteredRedirectUri = $"http://{MatrixHost}{Callback}";

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = WriteOidcRoutes(_tempDir, listen, origin.Port, idp.Issuer);

        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["OIDC_CLIENT_SECRET"] = FakeIdp.ClientSecret,
            ["OIDC_COOKIE_KEY"] = CookieKey,
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });

        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{listen}/app");
            req.Headers.TryAddWithoutValidation("Host", MatrixHost);
            req.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var resp = await http.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.Found, resp.StatusCode);
            var location = resp.Headers.Location?.OriginalString;
            Assert.IsNotNull(location);
            StringAssert.StartsWith(location, idp.Issuer);
            StringAssert.Contains(location, "/authorize");
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static string WriteOidcRoutes(string dir, int listen, int originPort, string issuer)
    {
        var path = Path.Combine(dir, $"oidc-{listen}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": {{listen}}, "decryptSsl": false, "enableHttp2": false }
              ],
              "routes": [
                {
                  "id": "r1",
                  "clusterId": "c1",
                  "order": 1,
                  "policies": [ "login" ],
                  "match": { "path": "/", "pathKind": "Prefix" }
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "RoundRobin",
                  "destinations": [
                    { "id": "d1", "address": "127.0.0.1", "port": {{originPort}}, "useDestinationHost": true }
                  ]
                }
              ],
              "policies": [
                {
                  "name": "login",
                  "kind": "oidc",
                  "issuer": "{{issuer}}",
                  "clientId": "{{FakeIdp.ClientId}}",
                  "clientSecretEnv": "OIDC_CLIENT_SECRET",
                  "cookieSecretEnv": "OIDC_COOKIE_KEY",
                  "redirectPath": "{{Callback}}",
                  "passClaims": {
                    "email": "X-User-Email",
                    "sub": "X-User-Id"
                  }
                }
              ],
              "plus": {
                "enabled": true,
                "controlPlane": {
                  "host": "127.0.0.1",
                  "port": {{CliProcessHarness.GetFreePort()}},
                  "sharedSecret": "oidc-e2e-secret"
                }
              }
            }
            """);
        return path;
    }
}
