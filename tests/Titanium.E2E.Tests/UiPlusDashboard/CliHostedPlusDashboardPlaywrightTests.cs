using System.Net;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Plus.ControlPlane;

namespace Titanium.E2E.Tests.UiPlusDashboard;

/// <summary>
/// Playwright against a real <c>titanium run</c> Plus dashboard (not in-process DashboardHost).
/// </summary>
[TestClass]
public class CliHostedPlusDashboardPlaywrightTests : PageTest
{
    private const string Secret = "pw-cli-dashboard-secret";
    private const string ViewerSecret = "pw-cli-dashboard-viewer-secret";

    private string _tempDir = null!;
    private CliProcessHarness? _harness;
    private EchoOrigin? _origin;
    private string _prefix = "";
    private int _dashboardPort;

    [TestInitialize]
    public async Task StartCliDashboardAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-pw-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        _dashboardPort = CliProcessHarness.GetFreePort();
        var cfg = WriteCliHostedDashboardConfig(
            _tempDir,
            listen,
            _origin.Port,
            control,
            Secret,
            ViewerSecret,
            _dashboardPort);

        _harness = new CliProcessHarness();
        _harness.EnsurePlusDllBesideCli(copy: true);
        await _harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });

        _prefix = $"http://127.0.0.1:{_dashboardPort}/";
        using var probe = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _prefix + "api/snapshot");
                req.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, Secret);
                var resp = await probe.SendAsync(req);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    break;
                }
            }
            catch
            {
                // wait
            }

            await Task.Delay(200);
        }
    }

    [TestCleanup]
    public void StopCliDashboard()
    {
        _harness?.Dispose();
        _origin?.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_LoginAndDrainHealthy()
    {
        await LoginAsync(Secret);
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
        await Expect(Page.Locator("h1")).ToContainTextAsync("Titanium Plus");
        await Expect(Page.GetByTestId("ov-rps")).ToBeVisibleAsync();

        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("dest-row-d1")).ToBeVisibleAsync();

        await Page.GetByTestId("btn-drain-d1").ClickAsync();
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Draining");

        await Page.GetByTestId("btn-healthy-d1").ClickAsync();
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Healthy");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_WrongSecret_IsRefused()
    {
        await LoginAsync("not-the-secret");
        await Expect(Page.GetByTestId("login-error")).ToBeVisibleAsync();
        Assert.AreEqual(0, (await Context.CookiesAsync()).Count(c => c.Name == "titanium_plus_session"));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_ConfigAndCertificateViews_Render()
    {
        await LoginAsync(Secret);

        await Page.GetByTestId("nav-config").ClickAsync();
        await Expect(Page.GetByTestId("cfg-schema-version")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("cfg-schema-version")).Not.ToHaveTextAsync("");

        await Page.GetByTestId("nav-certs").ClickAsync();
        await Expect(Page.GetByTestId("cert-table")).ToBeAttachedAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_SharedSecretHeader_WorksWithoutLogin()
    {
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
        {
            [ControlPlaneServer.SharedSecretHeader] = Secret,
        });

        await Page.GotoAsync(_prefix);
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_ViewerSecret_ShowsViewerRole()
    {
        await LoginAsync(ViewerSecret);
        await Expect(Page.GetByTestId("whoami")).ToContainTextAsync("viewer");
        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("btn-drain-d1")).ToBeDisabledAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_MetricsLink()
    {
        await LoginAsync(Secret);
        await Page.GetByTestId("link-metrics").ClickAsync();
        var metricsBody = await Page.InnerTextAsync("body");
        StringAssert.Contains(metricsBody, "titanium_destination_state");
    }

    private async Task LoginAsync(string secret)
    {
        await Page.GotoAsync(_prefix);
        await Expect(Page.GetByTestId("login-form")).ToBeVisibleAsync();
        await Page.GetByTestId("login-secret").FillAsync(secret);
        await Page.GetByTestId("login-submit").ClickAsync();
        if (secret is Secret or ViewerSecret)
        {
            await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
        }
    }

    private static string WriteCliHostedDashboardConfig(
        string dir,
        int listen,
        int origin,
        int control,
        string secret,
        string viewerSecret,
        int dashboardPort)
    {
        var path = Path.Combine(dir, $"cli-dash-{listen}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": {{listen}}, "decryptSsl": false }
              ],
              "routes": [
                {
                  "id": "r1",
                  "clusterId": "c1",
                  "order": 1,
                  "match": { "path": "/", "pathKind": "Prefix" }
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "RoundRobin",
                  "destinations": [
                    { "id": "d1", "address": "127.0.0.1", "port": {{origin}}, "useDestinationHost": true }
                  ]
                }
              ],
              "plus": {
                "enabled": true,
                "controlPlane": {
                  "host": "127.0.0.1",
                  "port": {{control}},
                  "sharedSecret": "{{secret}}",
                  "dashboardPort": {{dashboardPort}},
                  "dashboardViewerSecret": "{{viewerSecret}}"
                }
              }
            }
            """);
        return path;
    }
}
