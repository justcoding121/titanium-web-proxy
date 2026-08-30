using System.Net;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.E2E.Tests.UiPlusDashboard;

[TestClass]
public class PlusDashboardPlaywrightTests : PageTest
{
    // Must be const — Playwright creates the browser context before [TestInitialize].
    private const string Secret = "pw-dashboard-secret";

    private ClusterManager? _manager;
    private ControlPlaneServer? _control;
    private DashboardHost? _dashboard;
    private string _prefix = "";

    [TestInitialize]
    public async Task StartDashboardAsync()
    {
        Environment.SetEnvironmentVariable("TITANIUM_PLUS_ALLOW_DEV_SECRET", "1");
        _manager = new ClusterManager();
        await _manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 },
                ],
            },
        ]);

        var port = GetFreePort();
        _control = new ControlPlaneServer(_manager, "127.0.0.1", port, Secret);
        _control.Start();
        var ops = new DrainOperations(_manager);
        var metrics = new PrometheusMetricsExporter(_manager, null);
        _dashboard = new DashboardHost(_control, ops, metrics, _manager);
        _dashboard.Start();
        Assert.IsNotNull(_dashboard.Prefix);
        _prefix = _dashboard.Prefix!;

        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
        {
            [ControlPlaneServer.SharedSecretHeader] = Secret,
        });
    }

    [TestCleanup]
    public void StopDashboard()
    {
        _dashboard?.Dispose();
        _control?.Dispose();
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
    public async Task Shell_RendersDestinationAndActions()
    {
        await Page.GotoAsync(_prefix);
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("dest-row-d1")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("btn-drain-d1")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("btn-healthy-d1")).ToBeVisibleAsync();
        await Expect(Page.Locator("h1")).ToContainTextAsync("Titanium Plus");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task ClickDrain_ThenHealthy_UpdatesState()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync(Secret);

        await Page.GotoAsync(_prefix);
        await Page.GetByTestId("btn-drain-d1").ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Draining");
        Assert.AreEqual(DestinationState.Draining, _manager!.GetDestinationState("d1"));

        await Page.GetByTestId("btn-healthy-d1").ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Healthy");
        Assert.AreEqual(DestinationState.Healthy, _manager.GetDestinationState("d1"));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task MetricsAndSnapshot_LinksWork()
    {
        await Page.GotoAsync(_prefix);
        await Page.GetByTestId("link-metrics").ClickAsync();
        var metricsBody = await Page.InnerTextAsync("body");
        StringAssert.Contains(metricsBody, "titanium_destination_state");

        await Page.GotoAsync(_prefix);
        await Page.GetByTestId("link-snapshot").ClickAsync();
        var snap = await Page.InnerTextAsync("body");
        StringAssert.Contains(snap, "d1");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Unauthorized_WithoutSecret_Returns401()
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync(_prefix);
        Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task EmptyTable_ShowsNoDestinationsMessage()
    {
        _dashboard?.Dispose();
        _control?.Dispose();

        _manager = new ClusterManager();
        await _manager.ApplyAsync([]);
        var port = GetFreePort();
        _control = new ControlPlaneServer(_manager, "127.0.0.1", port, Secret);
        _control.Start();
        var ops = new DrainOperations(_manager);
        var metrics = new PrometheusMetricsExporter(_manager, null);
        _dashboard = new DashboardHost(_control, ops, metrics, _manager);
        _dashboard.Start();
        _prefix = _dashboard.Prefix!;

        await Page.GotoAsync(_prefix);
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
        await Expect(Page.Locator("body")).ToContainTextAsync("No destinations in snapshot.");
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
