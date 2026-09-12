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
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            _origin.Port,
            control,
            Secret,
            new Dictionary<string, string>(),
            useRoutes: true,
            dashboardPort: _dashboardPort);

        _harness = new CliProcessHarness();
        _harness.EnsurePlusDllBesideCli(copy: true);
        await _harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });

        _prefix = $"http://127.0.0.1:{_dashboardPort}/";
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _prefix);
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

        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
        {
            [ControlPlaneServer.SharedSecretHeader] = Secret,
        });
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

    public override BrowserNewContextOptions ContextOptions() =>
        new()
        {
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                [ControlPlaneServer.SharedSecretHeader] = Secret,
            },
        };

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_ShellAndDrainHealthy()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync(Secret);

        await Page.GotoAsync(_prefix);
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
        await Expect(Page.Locator("h1")).ToContainTextAsync("Titanium Plus");
        await Expect(Page.GetByTestId("dest-row-d1")).ToBeVisibleAsync();

        await Page.GetByTestId("btn-drain-d1").ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Draining");

        await Page.GetByTestId("btn-healthy-d1").ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Healthy");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    [TestCategory("E2E")]
    public async Task CliHosted_MetricsLink()
    {
        await Page.GotoAsync(_prefix);
        await Page.GetByTestId("link-metrics").ClickAsync();
        var metricsBody = await Page.InnerTextAsync("body");
        StringAssert.Contains(metricsBody, "titanium_destination_state");
    }
}
