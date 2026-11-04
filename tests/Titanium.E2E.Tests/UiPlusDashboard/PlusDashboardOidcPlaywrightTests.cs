using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Plus.Security;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.E2E.Tests.UiPlusDashboard;

[TestClass]
public class PlusDashboardOidcPlaywrightTests : PageTest
{
    private const string Secret = "pw-oidc-dashboard-secret";
    private static readonly string CookieKey = Convert.ToHexString(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private OidcDashboardHost? _host;

    [TestCleanup]
    public void StopHost() => _host?.Dispose();

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task LoginPage_ShowsOidcLink_WhenOidcConfigured()
    {
        await using var idp = await FakeIdp.StartAsync(new OidcClock());
        idp.RegisteredRedirectUri = "http://dash.test/login/oidc/callback";
        _host = StartHost(oidc: new DashboardOidcOptions
        {
            Issuer = idp.Issuer,
            ClientId = FakeIdp.ClientId,
            ClientSecret = FakeIdp.ClientSecret,
            CookieSecret = CookieKey,
            RedirectUri = idp.RegisteredRedirectUri,
            AllowedEmails = ["operator@example.test"],
        });

        await Page.GotoAsync(_host.Prefix + "login");
        await Expect(Page.GetByTestId("login-oidc")).ToBeVisibleAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task LoginPage_ShowsLocalUserFields_WhenUsersFileEnabled()
    {
        var users = Path.Combine(Path.GetTempPath(), "twp-dash-users-" + Guid.NewGuid().ToString("N") + ".txt");
        using (var harness = new CliProcessHarness())
        {
            var (code, stdout, stderr) = await harness.RunOnceAsync(
                ["plus", "hash-password", "--user", "alice", "--role", "operator", "s3cret"]);
            Assert.AreEqual(0, code, stdout + stderr);
            await File.WriteAllTextAsync(users, stdout.Trim() + "\n");
        }
        try
        {
            _host = StartHost(usersFile: users, breakGlass: false);
            await Page.GotoAsync(_host.Prefix + "login");
            await Expect(Page.GetByTestId("login-user")).ToBeVisibleAsync();
        }
        finally
        {
            try { File.Delete(users); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task ConfigPage_ReloadButton_WorksWhenManagedAndCallbackSet()
    {
        var reloads = 0;
        _host = StartHost(
            hostStatus: new StaticHostStatus(managed: true, drifted: false),
            requestReload: _ =>
            {
                reloads++;
                return Task.FromResult(new PlusReloadResult { Ok = true, ConfigHash = "reload-hash" });
            });

        await LoginAsync(Secret);
        await Page.GetByTestId("nav-config").ClickAsync();
        await Expect(Page.GetByTestId("btn-reload")).ToBeVisibleAsync();
        await Page.GetByTestId("btn-reload").ClickAsync();
        await Expect(Page.Locator("body")).ToContainTextAsync("reload-hash");
        Assert.AreEqual(1, reloads);
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task ConfigPage_ShowsManagedBanner_WhenManaged()
    {
        _host = StartHost(hostStatus: new StaticHostStatus(managed: true, drifted: false));
        await LoginAsync(Secret);
        await Page.GetByTestId("nav-config").ClickAsync();
        await Expect(Page.GetByTestId("config-managed")).ToBeVisibleAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Plus-Dashboard")]
    public async Task ConfigPage_ShowsDriftBanner_WhenDrifted()
    {
        _host = StartHost(hostStatus: new StaticHostStatus(managed: true, drifted: true));
        await LoginAsync(Secret);
        await Page.GetByTestId("nav-config").ClickAsync();
        await Expect(Page.GetByTestId("config-drift")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("btn-reload")).ToHaveCountAsync(0);
    }

    private async Task LoginAsync(string secret)
    {
        await Page.GotoAsync(_host!.Prefix);
        await Expect(Page.GetByTestId("login-form")).ToBeVisibleAsync();
        await Page.GetByTestId("login-secret").FillAsync(secret);
        await Page.GetByTestId("login-submit").ClickAsync();
        await Expect(Page.GetByTestId("plus-dashboard")).ToBeVisibleAsync();
    }

    private static OidcDashboardHost StartHost(
        DashboardOidcOptions? oidc = null,
        string? usersFile = null,
        bool breakGlass = true,
        IPlusHostStatus? hostStatus = null,
        Func<CancellationToken, Task<PlusReloadResult>>? requestReload = null)
    {
        try
        {
            return OidcDashboardHost.Start(
                Secret,
                oidc,
                usersFile,
                breakGlass,
                hostStatus,
                requestReload);
        }
        catch (Exception ex) when (ex is HttpListenerException or SocketException)
        {
            Assert.Inconclusive("Dashboard host could not bind: " + ex.Message);
            throw;
        }
    }

    private sealed class OidcDashboardHost : IDisposable
    {
        private readonly ControlPlaneServer _control;
        private readonly DashboardHost _dashboard;

        private OidcDashboardHost(ControlPlaneServer control, DashboardHost dashboard, string prefix)
        {
            _control = control;
            _dashboard = dashboard;
            Prefix = prefix;
        }

        public string Prefix { get; }

        public static OidcDashboardHost Start(
            string secret,
            DashboardOidcOptions? oidc,
            string? usersFile,
            bool breakGlass,
            IPlusHostStatus? hostStatus,
            Func<CancellationToken, Task<PlusReloadResult>>? requestReload)
        {
            var manager = new ClusterManager();
            manager.ApplyAsync(
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
                },
            ]).AsTask().GetAwaiter().GetResult();

            var control = new ControlPlaneServer(manager, "127.0.0.1", FreePort(), secret);
            control.Start();
            var dashboard = new DashboardHost(
                control,
                new DrainOperations(manager),
                new PrometheusMetricsExporter(manager, null),
                manager,
                options: new DashboardOptions
                {
                    BreakGlass = breakGlass,
                    UsersFile = usersFile,
                    Oidc = oidc,
                    HostStatus = hostStatus,
                    RequestReload = requestReload,
                });
            dashboard.Start();
            return new OidcDashboardHost(control, dashboard, dashboard.Prefix!);
        }

        public void Dispose()
        {
            _dashboard.Dispose();
            _control.Dispose();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class StaticHostStatus(bool managed, bool drifted) : IPlusHostStatus
    {
        public PlusConfigStatus GetConfigStatus() => new()
        {
            SchemaVersion = "7.0",
            ConfigHash = "local-hash",
            Managed = managed,
            Drifted = drifted,
        };

        public IReadOnlyList<PlusCertificateInfo> GetCertificates() => [];
    }
}
