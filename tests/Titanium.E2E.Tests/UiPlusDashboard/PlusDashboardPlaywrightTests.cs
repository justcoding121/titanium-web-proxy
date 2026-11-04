using System.Diagnostics;
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

/// <summary>
/// Playwright against an in-process <see cref="DashboardHost"/>. Tests sign in through the login page unless they
/// say otherwise; the shared-secret header path used by scripts is covered separately.
/// </summary>
[TestClass]
public class PlusDashboardPlaywrightTests : PageTest
{
    private const string Secret = "pw-dashboard-secret";
    private const string ViewerSecret = "pw-dashboard-viewer-secret";
    private const string HostileId = "<img src=x onerror=window.__xss=1>";

    private ClusterManager? _manager;
    private ControlPlaneServer? _control;
    private DashboardHost? _dashboard;
    private string _prefix = "";

    [TestInitialize]
    public async Task StartDashboardAsync()
    {
        Environment.SetEnvironmentVariable("TITANIUM_PLUS_ALLOW_DEV_SECRET", "1");
        await StartAsync([new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }]);
    }

    [TestCleanup]
    public void StopDashboard()
    {
        _dashboard?.Dispose();
        _control?.Dispose();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Login_WithTheSecret_ShowsTheOverview()
    {
        await LoginAsync(Secret);

        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
        await Expect(Page.Locator("h1")).ToContainTextAsync("Titanium Plus");
        await Expect(Page.GetByTestId("whoami")).ToContainTextAsync("operator");
        await Expect(Page.GetByTestId("ov-rps")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("ov-error-ratio")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("ov-p95")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("ov-active-connections")).ToBeVisibleAsync();

        // The session cookie is HttpOnly + SameSite=Strict: script cannot read it.
        Assert.IsFalse((await Page.EvaluateAsync<string>("() => document.cookie")).Contains("titanium_plus_session", StringComparison.Ordinal));
        var cookie = (await Context.CookiesAsync()).Single(c => c.Name == "titanium_plus_session");
        Assert.IsTrue(cookie.HttpOnly);
        Assert.AreEqual(SameSiteAttribute.Strict, cookie.SameSite);
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Login_WithAWrongSecret_ShowsAnErrorAndNoSession()
    {
        await LoginAsync("not-the-secret");

        await Expect(Page.GetByTestId("login-error")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("login-form")).ToBeVisibleAsync();
        Assert.AreEqual(0, (await Context.CookiesAsync()).Count(c => c.Name == "titanium_plus_session"));

        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("login-form")).ToBeVisibleAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Logout_EndsTheSession()
    {
        await LoginAsync(Secret);
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();

        await Page.GetByTestId("btn-logout").ClickAsync();
        await Expect(Page.GetByTestId("login-form")).ToBeVisibleAsync();

        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("login-form")).ToBeVisibleAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Routes_RendersDestinationAndActions()
    {
        await LoginAsync(Secret);
        await Page.GotoAsync(_prefix + "routes");

        await Expect(Page.GetByTestId("dest-row-d1")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("btn-drain-d1")).ToBeEnabledAsync();
        await Expect(Page.GetByTestId("btn-healthy-d1")).ToBeEnabledAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task ClickDrain_ThenHealthy_UpdatesState()
    {
        await LoginAsync(Secret);
        await Page.GotoAsync(_prefix + "routes");

        await Page.GetByTestId("btn-drain-d1").ClickAsync();
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Draining");
        Assert.AreEqual(DestinationState.Draining, _manager!.GetDestinationState("d1"));

        await Page.GetByTestId("btn-healthy-d1").ClickAsync();
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Healthy");
        Assert.AreEqual(DestinationState.Healthy, _manager.GetDestinationState("d1"));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Mutation_WithoutTheCsrfHeader_IsRejected()
    {
        await LoginAsync(Secret);
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();

        var status = await Page.EvaluateAsync<int>(
            "() => fetch('/drain/d1', { method: 'POST', credentials: 'same-origin' }).then(r => r.status)");

        Assert.AreEqual(403, status);
        Assert.AreEqual(DestinationState.Healthy, _manager!.GetDestinationState("d1"));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Viewer_SeesDisabledButtons_AndCannotDrain()
    {
        await LoginAsync(ViewerSecret);
        await Expect(Page.GetByTestId("whoami")).ToContainTextAsync("viewer");

        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("btn-drain-d1")).ToBeDisabledAsync();
        await Expect(Page.GetByTestId("btn-healthy-d1")).ToBeDisabledAsync();

        // Even with a valid CSRF token the server refuses the viewer.
        var status = await Page.EvaluateAsync<int>(
            """
            () => fetch('/drain/d1', {
              method: 'POST',
              credentials: 'same-origin',
              headers: { 'X-Titanium-Csrf': document.body.getAttribute('data-csrf') }
            }).then(r => r.status)
            """);

        Assert.AreEqual(403, status);
        Assert.AreEqual(DestinationState.Healthy, _manager!.GetDestinationState("d1"));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Pages_CarryAContentSecurityPolicy_AndRunNoInlineScript()
    {
        var violations = new List<string>();
        Page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(message.Text);
            }
        };

        var login = await Page.GotoAsync(_prefix + "login");
        Assert.IsNotNull(login);
        var csp = await login.HeaderValueAsync("Content-Security-Policy");
        StringAssert.Contains(csp, "default-src 'self'");
        Assert.AreEqual("nosniff", await login.HeaderValueAsync("X-Content-Type-Options"));
        StringAssert.Contains(await login.HeaderValueAsync("Cache-Control"), "no-store");
        Assert.AreEqual("no-referrer", await login.HeaderValueAsync("Referrer-Policy"));

        await LoginAsync(Secret);
        await Expect(Page.GetByTestId("ov-rps")).ToBeVisibleAsync();
        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("dest-row-d1")).ToBeVisibleAsync();

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Navigation_ReachesEveryView()
    {
        await LoginAsync(Secret);

        foreach (var (slug, marker) in new[]
                 {
                     ("routes", "routes-table"),
                     ("health", "health-table"),
                     ("config", "config-unavailable"), // in-process host reports no config status; the CLI-hosted test covers the facts
                     ("certs", "cert-table"),
                     ("audit", "audit-table"),
                 })
        {
            await Page.GetByTestId("nav-" + slug).ClickAsync();
            await Expect(Page.GetByTestId(marker)).ToBeAttachedAsync();
        }

        await Expect(Page.GetByTestId("audit-table")).ToContainTextAsync("login");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task HostileDestinationId_IsEncoded_AndNeverBecomesAnElement()
    {
        await StartAsync([new DestinationConfig { Id = HostileId, Address = "127.0.0.1", Port = 80 }]);
        await LoginAsync(Secret);
        await Page.GotoAsync(_prefix + "routes");

        await Expect(Page.GetByTestId("routes-table")).ToBeVisibleAsync();
        await Expect(Page.Locator("body")).ToContainTextAsync(HostileId);
        Assert.AreEqual(0, await Page.Locator("img").CountAsync());
        Assert.IsNull(await Page.EvaluateAsync<object?>("() => window.__xss ?? null"));

        // Same after the script has re-rendered the table from /api/routes.
        await Page.WaitForTimeoutAsync(2500);
        Assert.AreEqual(0, await Page.Locator("img").CountAsync());
        Assert.IsNull(await Page.EvaluateAsync<object?>("() => window.__xss ?? null"));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task TwoThousandDestinations_RenderQuickly()
    {
        var destinations = Enumerable.Range(0, 2000)
            .Select(i => new DestinationConfig { Id = $"bulk-{i}", Address = "127.0.0.1", Port = 80 })
            .ToList();
        await StartAsync(destinations);

        await LoginAsync(Secret);
        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("dest-row-bulk-1999")).ToBeAttachedAsync();
        Assert.AreEqual(2000, await Page.Locator("tr[data-testid^='dest-row-']").CountAsync());

        // Server-side render time, measured after the page above has warmed the path.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var warm = new HttpRequestMessage(HttpMethod.Get, _prefix + "routes");
        warm.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, Secret);
        (await http.SendAsync(warm)).Dispose();

        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _prefix + "routes");
            req.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, Secret);
            var sw = Stopwatch.StartNew();
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            StringAssert.Contains(body, "dest-row-bulk-1999");
            best = sw.Elapsed < best ? sw.Elapsed : best;
        }

        Assert.IsTrue(best < TimeSpan.FromMilliseconds(500), $"2000 destinations took {best.TotalMilliseconds:F0} ms.");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task SharedSecretHeader_StillWorksForScripts_WithoutLogin()
    {
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
        {
            [ControlPlaneServer.SharedSecretHeader] = Secret,
        });

        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("dest-row-d1")).ToBeVisibleAsync();

        // Header-authenticated mutations need no CSRF token.
        await Page.GetByTestId("btn-drain-d1").ClickAsync();
        await Expect(Page.GetByTestId("dest-state-d1")).ToContainTextAsync("Draining");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task MetricsAndSnapshot_LinksWork()
    {
        await LoginAsync(Secret);
        await Page.GetByTestId("link-metrics").ClickAsync();
        StringAssert.Contains(await Page.InnerTextAsync("body"), "titanium_destination_state");

        await Page.GotoAsync(_prefix);
        await Page.GetByTestId("link-snapshot").ClickAsync();
        StringAssert.Contains(await Page.InnerTextAsync("body"), "d1");
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task Unauthorized_WithoutSecret_IsRedirectedOrRefused()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        using var page = await http.GetAsync(_prefix);
        Assert.AreEqual(HttpStatusCode.SeeOther, page.StatusCode);
        Assert.AreEqual("/login", page.Headers.Location?.OriginalString);

        using var api = await http.GetAsync(_prefix + "api/snapshot");
        Assert.AreEqual(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task EmptyTable_ShowsNoDestinationsMessage()
    {
        await StartAsync([]);

        await LoginAsync(Secret);
        await Page.GotoAsync(_prefix + "routes");
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
        await Expect(Page.Locator("body")).ToContainTextAsync("No destinations in snapshot.");
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
        else
        {
            await Expect(Page.GetByTestId("login-error")).ToBeVisibleAsync();
        }
    }

    /// <summary>(Re)starts the control plane and dashboard on fresh ports with the given destinations in one cluster.</summary>
    private async Task StartAsync(IReadOnlyList<DestinationConfig> destinations)
    {
        _dashboard?.Dispose();
        _control?.Dispose();

        _manager = new ClusterManager();
        await _manager.ApplyAsync(
        [
            new ClusterConfig { Id = "c1", Destinations = destinations },
        ]);

        _control = new ControlPlaneServer(_manager, "127.0.0.1", GetFreePort(), Secret);
        _control.Start();
        var ops = new DrainOperations(_manager);
        var metrics = new PrometheusMetricsExporter(_manager, null);
        _dashboard = new DashboardHost(_control, ops, metrics, _manager, null, new DashboardOptions { ViewerSecret = ViewerSecret });
        _dashboard.Start();
        Assert.IsNotNull(_dashboard.Prefix);
        _prefix = _dashboard.Prefix!;
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
