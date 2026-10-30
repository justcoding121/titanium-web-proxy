using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus;
using Titanium.Plus.Audit;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

/// <summary>Dashboard OIDC SSO, local users, break-glass, throttle, and CSRF identity audits.</summary>
[TestClass]
public partial class DashboardAuthTests
{
    [GeneratedRegex("data-csrf=\"([^\"]+)\"")]
    private static partial Regex CsrfTokenPattern();
    private const string Secret = "operator-secret-value";
    private const string Remote = "10.9.8.7";
    private static readonly string CookieKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [TestInitialize]
    public void ResetOidcCounter() => DashboardOidc.ResetConstructedProviders();

    [TestMethod]
    public async Task DefaultConfig_DoesNotConstructOidcProvider()
    {
        await using var rig = await AuthRig.CreateAsync();
        Assert.IsFalse(rig.Host.HasOidc);
        Assert.AreEqual(0, DashboardOidc.ConstructedProviders);
        Assert.IsNull(TitaniumPlusModule.ParseDashboardOidc(new Dictionary<string, string>()));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Oidc_SignIn_MapsOperatorAndViewerGroups()
    {
        await using var idp = await FakeIdp.StartAsync(new OidcClock());
        idp.Groups = ["dash-ops"];
        idp.RegisteredRedirectUri = "http://dash.test/login/oidc/callback";

        await using var rig = await AuthRig.CreateAsync(oidc: new DashboardOidcOptions
        {
            Issuer = idp.Issuer,
            ClientId = FakeIdp.ClientId,
            ClientSecret = FakeIdp.ClientSecret,
            CookieSecret = CookieKey,
            RedirectUri = idp.RegisteredRedirectUri,
            OperatorGroups = ["dash-ops"],
            ViewerGroups = ["dash-view"],
        });

        var cookie = await CompleteOidcAsync(rig, idp);
        var page = await rig.SendAsync("GET", "/", cookie: cookie);
        Assert.AreEqual(200, page.Status);
        StringAssert.Contains(page.Text, "data-role=\"operator\"");
        Assert.AreEqual("dashboard:oidc:alice@example.test:operator", rig.Audit()[0].Actor);

        await using var idpViewer = await FakeIdp.StartAsync(new OidcClock());
        idpViewer.Groups = ["dash-view"];
        idpViewer.Email = "bob@example.test";
        idpViewer.RegisteredRedirectUri = "http://dash.test/login/oidc/callback";
        await using var viewerRig = await AuthRig.CreateAsync(oidc: new DashboardOidcOptions
        {
            Issuer = idpViewer.Issuer,
            ClientId = FakeIdp.ClientId,
            ClientSecret = FakeIdp.ClientSecret,
            CookieSecret = CookieKey,
            RedirectUri = idpViewer.RegisteredRedirectUri,
            OperatorGroups = ["dash-ops"],
            ViewerGroups = ["dash-view"],
        });
        var viewerCookie = await CompleteOidcAsync(viewerRig, idpViewer);
        var viewerPage = await viewerRig.SendAsync("GET", "/", cookie: viewerCookie);
        StringAssert.Contains(viewerPage.Text, "data-role=\"viewer\"");
        var drain = await viewerRig.SendAsync("POST", "/drain/d1", cookie: viewerCookie, csrf: await viewerRig.CsrfAsync(viewerCookie));
        Assert.AreEqual(403, drain.Status);
    }

    [TestMethod]
    public async Task LocalUser_SignIn_Works_AndThrottleLocksOut()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-dash-users-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "s3cret") + "\n");
        try
        {
            await using var rig = await AuthRig.CreateAsync(usersFile: path);
            var login = await rig.SendAsync("POST", "/login", body: "user=alice&password=" + Uri.EscapeDataString("s3cret"));
            Assert.AreEqual(303, login.Status);
            Assert.AreEqual("dashboard:local:alice:operator", rig.Audit()[0].Actor);

            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual(401, (await rig.SendAsync("POST", "/login", body: "user=alice&password=nope")).Status);
            }

            var locked = await rig.SendAsync("POST", "/login", body: "user=alice&password=" + Uri.EscapeDataString("s3cret"));
            Assert.AreEqual(429, locked.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task BreakGlass_IsAudited_AndDisabledRejectsSecret()
    {
        await using var on = await AuthRig.CreateAsync(breakGlass: true);
        var login = await on.SendAsync("POST", "/login", body: "secret=" + Uri.EscapeDataString(Secret));
        Assert.AreEqual(303, login.Status);
        Assert.AreEqual("dashboard:break-glass:operator", on.Audit()[0].Actor);

        await using var off = await AuthRig.CreateAsync(breakGlass: false);
        var refused = await off.SendAsync("POST", "/login", body: "secret=" + Uri.EscapeDataString(Secret));
        Assert.AreEqual(401, refused.Status);
        Assert.IsNull(refused.Header("Set-Cookie"));
        StringAssert.Contains(refused.Text, "Break-glass");
    }

    [TestMethod]
    public async Task LocalOperator_Drain_RequiresCsrf_AndRecordsActor()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-dash-users-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, LocalUsersStore.FormatLine("ops", DashboardRole.Operator, "pw") + "\n");
        try
        {
            await using var rig = await AuthRig.CreateAsync(usersFile: path, breakGlass: false);
            var login = await rig.SendAsync("POST", "/login", body: "user=ops&password=pw");
            var cookie = AuthRig.CookieOf(login);
            Assert.AreEqual(403, (await rig.SendAsync("POST", "/drain/d1", cookie: cookie)).Status);

            var csrf = await rig.CsrfAsync(cookie);
            var drain = await rig.SendAsync("POST", "/drain/d1", cookie: cookie, csrf: csrf);
            Assert.AreEqual(200, drain.Status);
            var entry = rig.Audit().First(e => e.Action == AuditActions.Drain);
            Assert.AreEqual("dashboard:local:ops:operator", entry.Actor);
            Assert.AreEqual("ok", entry.Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LoginPage_ShowsConfiguredMethodsOnly()
    {
        await using var secretOnly = await AuthRig.CreateAsync();
        var form = await secretOnly.SendAsync("GET", "/login");
        StringAssert.Contains(form.Text, "data-testid=\"login-secret\"");
        Assert.IsFalse(form.Text.Contains("login-oidc", StringComparison.Ordinal));
        Assert.IsFalse(form.Text.Contains("login-user", StringComparison.Ordinal));

        var path = Path.Combine(Path.GetTempPath(), "twp-dash-users-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, LocalUsersStore.FormatLine("a", DashboardRole.Viewer, "x") + "\n");
        try
        {
            await using var local = await AuthRig.CreateAsync(usersFile: path, breakGlass: false);
            var localForm = await local.SendAsync("GET", "/login");
            StringAssert.Contains(localForm.Text, "login-user");
            Assert.IsFalse(localForm.Text.Contains("login-secret", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> CompleteOidcAsync(AuthRig rig, FakeIdp idp)
    {
        var start = await rig.SendAsync("GET", "/login/oidc", host: "dash.test");
        Assert.AreEqual(303, start.Status);
        var authorize = start.Header("Location")!;
        StringAssert.StartsWith(authorize, idp.Issuer);

        var flowCookie = start.Headers.First(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Value.Split(';')[0];
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var authResponse = await http.GetAsync(authorize);
        Assert.AreEqual(HttpStatusCode.Redirect, authResponse.StatusCode);
        var callback = authResponse.Headers.Location!.OriginalString;
        var uri = new Uri(callback);
        Assert.AreEqual("/login/oidc/callback", uri.AbsolutePath);

        var cb = await rig.SendAsync("GET", uri.PathAndQuery, cookie: flowCookie, host: "dash.test");
        Assert.AreEqual(303, cb.Status, cb.Text);
        Assert.AreEqual("/", cb.Header("Location"));
        return AuthRig.CookieOf(cb);
    }

    private sealed class AuthRig : IAsyncDisposable
    {
        private AuthRig(ClusterManager manager, ControlPlaneServer control, DashboardHost host)
        {
            Manager = manager;
            Control = control;
            Host = host;
        }

        public ClusterManager Manager { get; }
        public ControlPlaneServer Control { get; }
        public DashboardHost Host { get; }

        public static async Task<AuthRig> CreateAsync(
            string? usersFile = null,
            bool breakGlass = true,
            DashboardOidcOptions? oidc = null)
        {
            var manager = new ClusterManager();
            await manager.ApplyAsync(
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
                },
            ]);
            var control = new ControlPlaneServer(manager, "127.0.0.1", 0, Secret);
            var host = new DashboardHost(
                control,
                new DrainOperations(manager),
                new PrometheusMetricsExporter(manager, null),
                manager,
                options: new DashboardOptions
                {
                    BreakGlass = breakGlass,
                    UsersFile = usersFile,
                    Oidc = oidc,
                });
            return new AuthRig(manager, control, host);
        }

        public static string CookieOf(Response login)
        {
            var set = login.Headers.LastOrDefault(h =>
                h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) &&
                h.Value.StartsWith(DashboardHost.SessionCookie + "=", StringComparison.Ordinal));
            Assert.IsFalse(string.IsNullOrEmpty(set.Value), "missing session cookie");
            return set.Value.Split(';')[0];
        }

        public IReadOnlyList<AuditEntry> Audit() => Control.Security.Audit.Recent();

        public async Task<Response> SendAsync(
            string method,
            string path,
            string? cookie = null,
            string? csrf = null,
            string? body = null,
            string? host = null)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (cookie is not null)
            {
                headers["Cookie"] = cookie;
            }

            if (csrf is not null)
            {
                headers[DashboardHost.CsrfHeader] = csrf;
            }

            if (host is not null)
            {
                headers["Host"] = host;
            }

            var pathOnly = path;
            var query = "";
            var q = path.IndexOf('?', StringComparison.Ordinal);
            if (q >= 0)
            {
                pathOnly = path[..q];
                query = path[(q + 1)..];
            }

            var request = new ControlHttpRequest
            {
                Method = method,
                Path = pathOnly,
                Query = query,
                Headers = headers,
                Body = body is null ? [] : Encoding.UTF8.GetBytes(body),
                RemoteAddress = IPAddress.Parse(Remote),
            };
            var response = await Host.HandleAsync(request, CancellationToken.None);
            return new Response(response.Status, response.ContentType, response.Headers, Encoding.UTF8.GetString(response.Body));
        }

        public async Task<string> CsrfAsync(string cookie)
        {
            var page = await SendAsync("GET", "/", cookie: cookie);
            return CsrfTokenPattern().Match(page.Text).Groups[1].Value;
        }

        public ValueTask DisposeAsync()
        {
            Host.Dispose();
            Control.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Response(
        int Status,
        string ContentType,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        string Text)
    {
        public string? Header(string name)
        {
            foreach (var (key, value) in Headers)
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
