using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus;
using Titanium.Plus.Audit;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

/// <summary>Dashboard v2 handlers: login, sessions, roles, CSRF, headers, views, and the data behind them.</summary>
[TestClass]
public partial class DashboardV2Tests
{
    private const string Secret = "operator-secret-value";
    private const string ViewerSecret = "viewer-secret-value";
    private const string Remote = "10.1.2.3";

    // ---- login and sessions ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Login_WithSharedSecret_SetsAHardenedCookie_AndOpensThePage()
    {
        await using var rig = await Rig.CreateAsync();

        var login = await rig.SendAsync("POST", "/login", body: "secret=" + Uri.EscapeDataString(Secret));

        Assert.AreEqual(303, login.Status);
        Assert.AreEqual("/", login.Header("Location"));
        var cookie = login.Header("Set-Cookie")!;
        StringAssert.StartsWith(cookie, DashboardHost.SessionCookie + "=");
        StringAssert.Contains(cookie, "HttpOnly");
        StringAssert.Contains(cookie, "SameSite=Strict");
        StringAssert.Contains(cookie, "Max-Age=1800");
        StringAssert.Contains(cookie, "Path=/");
        Assert.IsFalse(cookie.Contains("Secure", StringComparison.Ordinal), "plain HTTP listener");
        // 256 bits as unpadded base64url is 43 characters.
        Assert.AreEqual(43, Rig.SessionId(login).Length);
        Assert.IsFalse(Rig.SessionId(login).Contains(Secret, StringComparison.Ordinal));

        var page = await rig.SendAsync("GET", "/", cookie: Rig.CookieOf(login));
        Assert.AreEqual(200, page.Status);
        StringAssert.Contains(page.Text, "data-role=\"operator\"");
        StringAssert.Contains(page.Text, "Titanium Plus");
        Assert.AreEqual(1, rig.Host.Sessions.Count);
        Assert.AreEqual("login", rig.Audit()[0].Action);
        Assert.AreEqual("dashboard:break-glass:operator", rig.Audit()[0].Actor);
    }

    [TestMethod]
    public async Task Login_WrongSecret_IsRefused_Audited_AndLocksOutAfterFiveFailures()
    {
        await using var rig = await Rig.CreateAsync();

        for (var i = 0; i < 5; i++)
        {
            var wrong = await rig.SendAsync("POST", "/login", body: "secret=nope");
            Assert.AreEqual(401, wrong.Status);
            StringAssert.Contains(wrong.Text, "data-testid=\"login-error\"");
            Assert.IsNull(wrong.Header("Set-Cookie"));
        }

        Assert.AreEqual(0, rig.Host.Sessions.Count);
        Assert.AreEqual("login-failed", rig.Audit()[0].Action);
        Assert.AreEqual("denied", rig.Audit()[0].Result);

        var locked = await rig.SendAsync("POST", "/login", body: "secret=" + Uri.EscapeDataString(Secret));
        Assert.AreEqual(429, locked.Status, "the right secret is refused while the remote is locked out");
        Assert.IsNotNull(locked.Header("Retry-After"));
    }

    [TestMethod]
    public async Task Login_EmptyAndOversizedBodies_AreRefused()
    {
        await using var rig = await Rig.CreateAsync();

        Assert.AreEqual(401, (await rig.SendAsync("POST", "/login", body: "")).Status);
        Assert.AreEqual(401, (await rig.SendAsync("POST", "/login", body: "other=1")).Status);
        Assert.AreEqual(413, (await rig.SendAsync("POST", "/login", body: "secret=" + new string('a', 5000))).Status);
    }

    [TestMethod]
    public async Task LoginPage_ShowsTheForm_AndRedirectsASignedInBrowser()
    {
        await using var rig = await Rig.CreateAsync();

        var form = await rig.SendAsync("GET", "/login");
        Assert.AreEqual(200, form.Status);
        StringAssert.Contains(form.Text, "data-testid=\"login-form\"");
        StringAssert.Contains(form.Text, "type=\"password\"");
        Assert.AreEqual(405, (await rig.SendAsync("PUT", "/login")).Status);

        var cookie = await rig.SignInAsync(Secret);
        var again = await rig.SendAsync("GET", "/login", cookie: cookie);
        Assert.AreEqual(303, again.Status);
        Assert.AreEqual("/", again.Header("Location"));
    }

    [TestMethod]
    public async Task Anonymous_PagesRedirectToLogin_AndApiCallsAreRefused()
    {
        await using var rig = await Rig.CreateAsync();

        var page = await rig.SendAsync("GET", "/routes");
        Assert.AreEqual(303, page.Status);
        Assert.AreEqual("/login", page.Header("Location"));

        Assert.AreEqual(401, (await rig.SendAsync("GET", "/api/overview")).Status);
        Assert.AreEqual(401, (await rig.SendAsync("GET", "/metrics")).Status);
        Assert.AreEqual(401, (await rig.SendAsync("POST", "/drain/d1")).Status);
        Assert.AreEqual(DestinationState.Healthy, rig.Manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task ExpiredSessionApiCalls_AreRefusedWithoutCountingAsFailures()
    {
        await using var rig = await Rig.CreateAsync();
        var cookie = await rig.SignInAsync(Secret);
        rig.Clock.Advance(TimeSpan.FromMinutes(31));

        // Several open tabs polling after the session lapsed must not lock their own user out.
        for (var i = 0; i < 8; i++)
        {
            Assert.AreEqual(401, (await rig.SendAsync("GET", "/api/overview", cookie: cookie, poll: true)).Status);
        }

        var fresh = await rig.SignInAsync(Secret);
        Assert.AreEqual(200, (await rig.SendAsync("GET", "/api/overview", cookie: fresh)).Status);
    }

    [TestMethod]
    public async Task Session_ExpiresAfterThirtyMinutesIdle_OnAManualClock()
    {
        await using var rig = await Rig.CreateAsync();
        var cookie = await rig.SignInAsync(Secret);

        rig.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.AreEqual(200, (await rig.SendAsync("GET", "/config", cookie: cookie)).Status, "29 minutes idle is still in");

        // The page above moved the deadline, so 29 more minutes is fine too.
        rig.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.AreEqual(200, (await rig.SendAsync("GET", "/config", cookie: cookie)).Status);

        rig.Clock.Advance(TimeSpan.FromMinutes(30));
        var expired = await rig.SendAsync("GET", "/config", cookie: cookie);
        Assert.AreEqual(303, expired.Status);
        Assert.AreEqual("/login", expired.Header("Location"));
        Assert.AreEqual(0, rig.Host.Sessions.Count);
    }

    [TestMethod]
    public async Task BackgroundPolling_DoesNotKeepAnIdleSessionAlive()
    {
        await using var rig = await Rig.CreateAsync();
        var cookie = await rig.SignInAsync(Secret);

        for (var i = 0; i < 20; i++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(1));
            var poll = await rig.SendAsync("GET", "/api/overview", cookie: cookie, poll: true);
            Assert.AreEqual(200, poll.Status, $"minute {i + 1}");
        }

        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.AreEqual(401, (await rig.SendAsync("GET", "/api/overview", cookie: cookie, poll: true)).Status);
    }

    [TestMethod]
    public async Task Logout_EndsTheSession_AndClearsTheCookie()
    {
        await using var rig = await Rig.CreateAsync();
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        var noToken = await rig.SendAsync("POST", "/logout", cookie: cookie);
        Assert.AreEqual(403, noToken.Status);
        Assert.AreEqual(1, rig.Host.Sessions.Count);

        var logout = await rig.SendAsync("POST", "/logout", cookie: cookie, csrf: csrf);
        Assert.AreEqual(200, logout.Status);
        StringAssert.Contains(logout.Header("Set-Cookie")!, "Max-Age=0");
        Assert.AreEqual(0, rig.Host.Sessions.Count);
        Assert.AreEqual(303, (await rig.SendAsync("GET", "/", cookie: cookie)).Status);
        Assert.AreEqual("logout", rig.Audit()[0].Action);
    }

    [TestMethod]
    public void SessionStore_CapsSessions_EvictingTheLeastRecentlyUsed()
    {
        var clock = new ManualTime();
        var store = new DashboardSessionStore(clock);
        var first = store.Create(DashboardRole.Viewer, "a", DashboardAuthSource.Local, out _);
        clock.Advance(TimeSpan.FromSeconds(1));
        var ids = new List<string>();
        for (var i = 0; i < DashboardSessionStore.DefaultMaxSessions - 1; i++)
        {
            ids.Add(store.Create(DashboardRole.Viewer, "b", DashboardAuthSource.Local, out _));
        }

        Assert.AreEqual(64, store.Count);
        Assert.IsNotNull(store.Find(first, touch: false));

        // Touching the first session makes the second-oldest the eviction candidate.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(store.Find(first, touch: true));
        var extra = store.Create(DashboardRole.Operator, "c", DashboardAuthSource.Local, out _);

        Assert.AreEqual(64, store.Count);
        Assert.IsNotNull(store.Find(first, touch: false));
        Assert.IsNull(store.Find(ids[0], touch: false));
        Assert.IsNotNull(store.Find(extra, touch: false));
    }

    [TestMethod]
    public void SessionStore_RejectsUnknownMalformedAndOverlongIds_AndDropsExpiredOnes()
    {
        var clock = new ManualTime();
        var store = new DashboardSessionStore(clock, TimeSpan.FromMinutes(5));
        var id = store.Create(DashboardRole.Viewer, "viewer", DashboardAuthSource.Local, out var session);

        Assert.IsNull(store.Find(null, true));
        Assert.IsNull(store.Find("", true));
        Assert.IsNull(store.Find(new string('a', 500), true));
        Assert.IsNull(store.Find("not-a-session", true));
        Assert.IsFalse(store.Remove(null));
        Assert.IsFalse(store.Remove(new string('a', 500)));
        Assert.IsTrue(DashboardSessionStore.CsrfMatches(session, session.CsrfToken));
        Assert.IsFalse(DashboardSessionStore.CsrfMatches(session, "wrong"));
        Assert.IsFalse(DashboardSessionStore.CsrfMatches(session, null));
        Assert.AreNotEqual(id, session.CsrfToken);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.AreEqual(0, store.Count);
        Assert.IsNull(store.Find(id, true));
    }

    // ---- CSRF and roles -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Mutation_WithoutOrWithWrongCsrfToken_IsRejected_AndNothingChanges()
    {
        await using var rig = await Rig.CreateAsync();
        var cookie = await rig.SignInAsync(Secret);

        var missing = await rig.SendAsync("POST", "/drain/d1", cookie: cookie);
        var wrong = await rig.SendAsync("POST", "/drain/d1", cookie: cookie, csrf: "x" + await rig.CsrfAsync(cookie));
        var fromAnotherSession = await rig.SendAsync(
            "POST", "/drain/d1", cookie: cookie, csrf: await rig.CsrfAsync(await rig.SignInAsync(Secret)));

        Assert.AreEqual(403, missing.Status);
        Assert.AreEqual(403, wrong.Status);
        Assert.AreEqual(403, fromAnotherSession.Status);
        Assert.AreEqual(DestinationState.Healthy, rig.Manager.GetDestinationState("d1"));
        var denied = rig.Audit().First(e => e.Action == "drain");
        Assert.AreEqual("denied", denied.Result);
    }

    [TestMethod]
    public async Task Mutation_WithTheSessionCsrfToken_DrainsAndRestores()
    {
        await using var rig = await Rig.CreateAsync();
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        Assert.AreEqual(200, (await rig.SendAsync("POST", "/drain/d1", cookie: cookie, csrf: csrf)).Status);
        Assert.AreEqual(DestinationState.Draining, rig.Manager.GetDestinationState("d1"));
        Assert.AreEqual(200, (await rig.SendAsync("POST", "/healthy/d1", cookie: cookie, csrf: csrf)).Status);
        Assert.AreEqual(DestinationState.Healthy, rig.Manager.GetDestinationState("d1"));
        Assert.AreEqual(400, (await rig.SendAsync("POST", "/drain/", cookie: cookie, csrf: csrf)).Status);
        Assert.AreEqual(404, (await rig.SendAsync("POST", "/frobnicate", cookie: cookie, csrf: csrf)).Status);
        Assert.AreEqual(405, (await rig.SendAsync("DELETE", "/drain/d1", cookie: cookie, csrf: csrf)).Status);

        var ok = rig.Audit().Where(e => e.Result == "ok" && e.Action is "drain" or "healthy").ToList();
        Assert.AreEqual(2, ok.Count);
        Assert.AreEqual("dashboard:break-glass:operator", ok[0].Actor);
    }

    [TestMethod]
    public async Task HeaderSecret_KeepsWorkingForScripts_WithoutACsrfToken()
    {
        await using var rig = await Rig.CreateAsync();

        var drain = await rig.SendAsync("POST", "/drain/d1", secret: Secret);
        Assert.AreEqual(200, drain.Status);
        Assert.AreEqual(DestinationState.Draining, rig.Manager.GetDestinationState("d1"));
        Assert.AreEqual("dashboard:shared-secret", rig.Audit()[0].Actor);

        var page = await rig.SendAsync("GET", "/", secret: Secret);
        Assert.AreEqual(200, page.Status);
        Assert.AreEqual("login", rig.Audit()[0].Action);

        var wrong = await rig.SendAsync("GET", "/api/overview", secret: "bad");
        Assert.AreEqual(401, wrong.Status);
        Assert.AreEqual("login-failed", rig.Audit()[0].Action);
    }

    [TestMethod]
    public async Task Viewer_CanReadEveryView_ButCannotDrainOrRestore()
    {
        await using var rig = await Rig.CreateAsync(viewerSecret: ViewerSecret);
        var cookie = await rig.SignInAsync(ViewerSecret);
        var csrf = await rig.CsrfAsync(cookie);

        foreach (var path in new[] { "/", "/routes", "/health", "/config", "/certificates", "/audit", "/api/overview", "/api/routes",
                     "/api/health", "/api/config", "/api/certs", "/api/audit", "/api/snapshot", "/metrics" })
        {
            Assert.AreEqual(200, (await rig.SendAsync("GET", path, cookie: cookie)).Status, path);
        }

        var routes = await rig.SendAsync("GET", "/routes", cookie: cookie);
        StringAssert.Contains(routes.Text, "data-role=\"viewer\"");
        Assert.IsTrue(ViewerDrainDisabledPattern().IsMatch(routes.Text), "buttons are disabled for a viewer");

        var drain = await rig.SendAsync("POST", "/drain/d1", cookie: cookie, csrf: csrf);
        var healthy = await rig.SendAsync("POST", "/healthy/d1", cookie: cookie, csrf: csrf);
        Assert.AreEqual(403, drain.Status);
        Assert.AreEqual(403, healthy.Status);
        Assert.AreEqual(DestinationState.Healthy, rig.Manager.GetDestinationState("d1"));
        Assert.AreEqual("denied", rig.Audit()[0].Result);
        Assert.AreEqual("dashboard:break-glass:viewer", rig.Audit()[0].Actor);

        // The viewer secret through the script header is read-only as well.
        Assert.AreEqual(200, (await rig.SendAsync("GET", "/api/overview", secret: ViewerSecret)).Status);
        Assert.AreEqual(403, (await rig.SendAsync("POST", "/drain/d1", secret: ViewerSecret)).Status);
        Assert.AreEqual(DestinationState.Healthy, rig.Manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task ViewerSecret_IsOffUnlessConfigured()
    {
        await using var rig = await Rig.CreateAsync();

        var attempt = await rig.SendAsync("POST", "/login", body: "secret=" + ViewerSecret);
        Assert.AreEqual(401, attempt.Status);
    }

    [TestMethod]
    public void ValidateViewerSecret_RejectsTheDefaultAndTheSharedSecret()
    {
        TitaniumPlusModule.ValidateViewerSecret(null, "shared");
        TitaniumPlusModule.ValidateViewerSecret("", "shared");
        TitaniumPlusModule.ValidateViewerSecret("viewer", "shared");
        Assert.ThrowsExactly<InvalidOperationException>(() => TitaniumPlusModule.ValidateViewerSecret("shared", "shared"));
        Assert.ThrowsExactly<InvalidOperationException>(() => TitaniumPlusModule.ValidateViewerSecret("ChangeMe", "shared"));
    }

    // ---- headers, assets, and markup safety -----------------------------------------------------------------------------

    [TestMethod]
    public async Task EveryResponse_CarriesTheSecurityHeaders()
    {
        await using var rig = await Rig.CreateAsync(viewerSecret: ViewerSecret);
        var cookie = await rig.SignInAsync(Secret);
        var responses = new List<Response>
        {
            await rig.SendAsync("GET", "/", cookie: cookie),
            await rig.SendAsync("GET", "/api/overview", cookie: cookie),
            await rig.SendAsync("GET", "/login"),
            await rig.SendAsync("POST", "/login", body: "secret=bad"),
            await rig.SendAsync("GET", "/assets/dashboard.js"),
            await rig.SendAsync("GET", "/assets/dashboard.css"),
            await rig.SendAsync("GET", "/routes"),
            await rig.SendAsync("GET", "/api/overview"),
            await rig.SendAsync("GET", "/nope", cookie: cookie),
            await rig.SendAsync("PUT", "/", cookie: cookie),
        };

        foreach (var response in responses)
        {
            var csp = response.Header("Content-Security-Policy");
            Assert.IsNotNull(csp, $"status {response.Status}");
            StringAssert.Contains(csp, "default-src 'self'");
            Assert.IsFalse(csp.Contains("unsafe-inline", StringComparison.Ordinal));
            Assert.AreEqual("nosniff", response.Header("X-Content-Type-Options"));
            Assert.AreEqual("no-store", response.Header("Cache-Control"));
            Assert.AreEqual("no-referrer", response.Header("Referrer-Policy"));
            Assert.AreEqual("DENY", response.Header("X-Frame-Options"));
        }
    }

    [TestMethod]
    public async Task Pages_UseNoInlineScriptStyleOrEventHandlers()
    {
        await using var rig = await Rig.CreateAsync();
        var cookie = await rig.SignInAsync(Secret);

        foreach (var path in new[] { "/", "/routes", "/health", "/config", "/certificates", "/audit", "/login" })
        {
            var html = (await rig.SendAsync("GET", path, cookie: path == "/login" ? null : cookie)).Text;
            Assert.IsFalse(InlineScriptPattern().IsMatch(html), $"inline script on {path}");
            Assert.IsFalse(InlineStyleElementPattern().IsMatch(html), $"style element on {path}");
            Assert.IsFalse(InlineHandlerOrStylePattern().IsMatch(html), $"inline handler or style on {path}");
        }

        var js = await rig.SendAsync("GET", "/assets/dashboard.js");
        Assert.AreEqual(200, js.Status);
        StringAssert.Contains(js.Header("Content-Type")!, "javascript");
        StringAssert.Contains(js.Text, "/api/");
        StringAssert.Contains(js.Text, "2000");
        Assert.IsFalse(js.Text.Contains("innerHTML", StringComparison.Ordinal), "values are written as text");
        Assert.IsFalse(js.Text.Contains("prompt(", StringComparison.Ordinal), "the prompt() flow is gone");
        Assert.IsFalse(js.Text.Contains("eval(", StringComparison.Ordinal));
        Assert.AreEqual(200, (await rig.SendAsync("GET", "/assets/dashboard.css")).Status);
    }

    [TestMethod]
    public async Task DestinationIdWithMarkup_IsHtmlEncodedEverywhere()
    {
        const string evil = "<img src=x onerror=alert(1)>";
        const string breakout = "a\" onmouseover=\"alert(2)";
        await using var rig = await Rig.CreateAsync(destinationIds: [evil, breakout]);
        rig.Control.Security.Audit.Record("dashboard:operator", Remote, "drain", evil, "ok");
        var cookie = await rig.SignInAsync(Secret);

        foreach (var path in new[] { "/routes", "/audit" })
        {
            var html = (await rig.SendAsync("GET", path, cookie: cookie)).Text;
            Assert.IsFalse(html.Contains("<img", StringComparison.Ordinal), $"raw tag on {path}");
            Assert.IsFalse(html.Contains("\" onmouseover=\"", StringComparison.Ordinal), $"attribute breakout on {path}");
        }

        var routes = (await rig.SendAsync("GET", "/routes", cookie: cookie)).Text;
        StringAssert.Contains(routes, WebUtility.HtmlEncode(evil));
        StringAssert.Contains(routes, WebUtility.HtmlEncode(breakout));
        StringAssert.Contains((await rig.SendAsync("GET", "/audit", cookie: cookie)).Text, WebUtility.HtmlEncode(evil));

        // The id still reaches the operation, percent-encoded, exactly as typed.
        var csrf = await rig.CsrfAsync(cookie);
        Assert.AreEqual(200, (await rig.SendAsync("POST", "/drain/" + Uri.EscapeDataString(evil), cookie: cookie, csrf: csrf)).Status);
        Assert.AreEqual(DestinationState.Draining, rig.Manager.GetDestinationState(evil));
    }

    [TestMethod]
    public async Task TwoThousandDestinations_RenderInUnderHalfASecond()
    {
        var ids = Enumerable.Range(0, 2000).Select(i => $"dest-{i:D4}").ToArray();
        await using var rig = await Rig.CreateAsync(destinationIds: ids);
        var cookie = await rig.SignInAsync(Secret);
        _ = await rig.SendAsync("GET", "/routes", cookie: cookie);

        var watch = Stopwatch.StartNew();
        var page = await rig.SendAsync("GET", "/routes", cookie: cookie);
        watch.Stop();

        Assert.AreEqual(200, page.Status);
        Assert.AreEqual(2000, DestRowTestIdPattern().Count(page.Text));
        Assert.IsTrue(watch.ElapsedMilliseconds < 500, $"rendering 2000 destinations took {watch.ElapsedMilliseconds} ms");

        watch.Restart();
        var api = await rig.SendAsync("GET", "/api/routes", cookie: cookie, poll: true);
        watch.Stop();
        Assert.AreEqual(2000, JsonDocument.Parse(api.Text).RootElement.GetProperty("destinations").GetArrayLength());
        Assert.IsTrue(watch.ElapsedMilliseconds < 500, $"serving 2000 destinations took {watch.ElapsedMilliseconds} ms");
    }

    // ---- views ----------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Routes_ShowConfigReadOnly_WithSecretsRedacted()
    {
        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "api",
                ClusterId = "c1",
                Order = 1,
                Match = new RouteMatch
                {
                    Host = "api.example.com",
                    Path = "/v1",
                    Headers = new Dictionary<string, string> { ["X-Api-Key"] = "header-secret-123" },
                    Query = new Dictionary<string, string> { ["token"] = "query-secret-456" },
                },
                Transforms =
                [
                    new TransformConfig
                    {
                        Kind = "requestHeader",
                        Parameters = new Dictionary<string, string> { ["name"] = "Authorization", ["value"] = "Bearer transform-secret-789" },
                    },
                ],
            },
            new()
            {
                Id = "moved",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/old" },
                Redirect = new RedirectConfig { Status = 301, Host = "new.example.com" },
            },
            new()
            {
                Id = "static",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/ok" },
                DirectResponse = new DirectResponseConfig
                {
                    Status = 200,
                    Body = "direct-body-secret",
                    Headers = new Dictionary<string, string> { ["X-Token"] = "direct-header-secret" },
                },
            },
        };
        var clusters = new[]
        {
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80, Health = "http://u:p@h/?token=health-secret" }],
                HttpClient = new ClusterHttpClientConfig
                {
                    ServerName = "backend.internal",
                    ClientCertificatePath = "/etc/secret/client.pfx",
                    ClientCertificatePasswordEnv = "CLIENT_CERT_PASSWORD",
                    CaBundlePath = "/etc/secret/ca.pem",
                },
                HealthCheck = new HealthCheckConfig { Active = new ActiveHealthConfig { Enabled = true } },
            },
        };
        await using var rig = await Rig.CreateAsync(routes: routes, clusters: clusters);
        var cookie = await rig.SignInAsync(Secret);

        var html = (await rig.SendAsync("GET", "/routes", cookie: cookie)).Text;
        var json = (await rig.SendAsync("GET", "/api/routes", cookie: cookie)).Text;
        foreach (var text in new[] { html, json })
        {
            foreach (var leaked in new[]
                     {
                         "header-secret-123", "query-secret-456", "transform-secret-789", "direct-body-secret",
                         "direct-header-secret", "health-secret", "/etc/secret", "CLIENT_CERT_PASSWORD",
                     })
            {
                Assert.IsFalse(text.Contains(leaked, StringComparison.Ordinal), leaked);
            }
        }

        StringAssert.Contains(html, "host api.example.com");
        StringAssert.Contains(html, "headers X-Api-Key");
        StringAssert.Contains(html, "query token");
        StringAssert.Contains(html, "requestHeader(name=Authorization, value=***)");
        StringAssert.Contains(html, "redirect 301");
        StringAssert.Contains(html, "direct 200");
        StringAssert.Contains(html, "sni backend.internal");
        StringAssert.Contains(html, "client certificate");
        StringAssert.Contains(html, "CA bundle");

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual(3, doc.GetProperty("routes").GetArrayLength());
        Assert.IsTrue(doc.GetProperty("clusters")[0].GetProperty("activeHealth").GetBoolean());
    }

    [TestMethod]
    public async Task Routes_ShowPerRouteRequestsErrorsAndLatency()
    {
        var meterName = "Titanium.Test." + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        var requests = meter.CreateCounter<long>("twp.proxy.requests");
        var duration = meter.CreateHistogram<double>("twp.proxy.request.duration");
        using var collector = new DashboardMetrics(meterName);
        var routes = new List<RouteConfig>
        {
            new() { Id = "api", ClusterId = "c1", Match = new RouteMatch { Path = "/api" } },
            new() { Id = "idle", ClusterId = "c1", Match = new RouteMatch { Path = "/idle" } },
        };
        await using var rig = await Rig.CreateAsync(routes: routes, metrics: collector);
        for (var i = 0; i < 4; i++)
        {
            requests.Add(1, Tag("route", "api"), Tag("status_class", i == 0 ? "5xx" : "2xx"));
            duration.Record(0.004, Tag("route", "api"), Tag("status_class", "2xx"));
        }

        var json = JsonDocument.Parse((await rig.SendAsync("GET", "/api/routes", secret: Secret)).Text).RootElement;
        var api = json.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "api");
        var idle = json.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "idle");
        Assert.AreEqual(4, api.GetProperty("requests").GetInt64());
        Assert.AreEqual(1, api.GetProperty("errors").GetInt64());
        Assert.IsTrue(api.GetProperty("p95Seconds").GetDouble() is > 0.0025 and <= 0.005);
        Assert.AreEqual(0, idle.GetProperty("requests").GetInt64());
        Assert.AreEqual(JsonValueKind.Null, idle.GetProperty("p50Seconds").ValueKind);
    }

    [TestMethod]
    public async Task Overview_ShowsRateErrorRatioPercentilesAndActiveGauges()
    {
        var meterName = "Titanium.Test." + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        var requests = meter.CreateCounter<long>("twp.proxy.requests");
        var duration = meter.CreateHistogram<double>("twp.proxy.request.duration");
        var connections = meter.CreateUpDownCounter<long>("twp.connections.active");
        var active = meter.CreateUpDownCounter<long>("twp.proxy.requests.active");
        var clock = new ManualTime();
        using var collector = new DashboardMetrics(meterName, clock);
        await using var rig = await Rig.CreateAsync(metrics: collector, clock: clock);

        connections.Add(5);
        connections.Add(-2);
        active.Add(4);
        for (var i = 0; i < 10; i++)
        {
            requests.Add(1, Tag("route", "r"), Tag("status_class", i < 2 ? "5xx" : "2xx"));
            duration.Record(i < 9 ? 0.004 : 0.4, Tag("route", "r"), Tag("status_class", "2xx"));
        }

        var overview = JsonDocument.Parse((await rig.SendAsync("GET", "/api/overview", secret: Secret)).Text).RootElement;
        Assert.AreEqual(3, overview.GetProperty("activeConnections").GetInt64());
        Assert.AreEqual(4, overview.GetProperty("activeRequests").GetInt64());
        Assert.AreEqual(10, overview.GetProperty("requestsPerSecond").GetDouble(), 0.001);
        Assert.AreEqual(0.2, overview.GetProperty("errorRatio").GetDouble(), 0.001);
        Assert.AreEqual(10, overview.GetProperty("totalRequests").GetInt64());
        var latency = overview.GetProperty("latency");
        Assert.IsTrue(latency.GetProperty("p50Seconds").GetDouble() is > 0.0025 and < 0.005);
        Assert.IsTrue(latency.GetProperty("p99Seconds").GetDouble() is > 0.25 and <= 0.5);
        Assert.IsTrue(overview.GetProperty("observing").GetBoolean());

        var html = (await rig.SendAsync("GET", "/", secret: Secret)).Text;
        StringAssert.Contains(html, "data-testid=\"ov-error-ratio\">20 %");
        StringAssert.Contains(html, "data-testid=\"ov-active-connections\">3<");
        StringAssert.Contains(html, "data-testid=\"ov-rps\">10 /s");

        // A minute later the window is empty; the totals stay.
        clock.Advance(TimeSpan.FromSeconds(DashboardMetrics.WindowSeconds + 1));
        var later = JsonDocument.Parse((await rig.SendAsync("GET", "/api/overview", secret: Secret)).Text).RootElement;
        Assert.AreEqual(JsonValueKind.Null, later.GetProperty("errorRatio").ValueKind);
        Assert.AreEqual(0, later.GetProperty("requestsPerSecond").GetDouble());
        Assert.AreEqual(10, later.GetProperty("totalRequests").GetInt64());
    }

    [TestMethod]
    public void Percentile_InterpolatesInsideTheBucket_AndHandlesEmptyAndOverflow()
    {
        var counts = new long[DashboardMetrics.BucketBounds.Count + 1];
        Assert.IsNull(DashboardMetrics.Percentile(counts, 0.5));

        counts[2] = 90;
        counts[8] = 10;
        Assert.AreEqual(0.00389, DashboardMetrics.Percentile(counts, 0.50)!.Value, 0.0001);
        Assert.AreEqual(0.375, DashboardMetrics.Percentile(counts, 0.95)!.Value, 0.0001);
        Assert.AreEqual(0.475, DashboardMetrics.Percentile(counts, 0.99)!.Value, 0.0001);

        var slow = new long[DashboardMetrics.BucketBounds.Count + 1];
        slow[^1] = 3;
        Assert.AreEqual(30, DashboardMetrics.Percentile(slow, 0.99)!.Value);
    }

    [TestMethod]
    public void Metrics_CapRouteCardinality_AndIgnoreOtherMeters()
    {
        var meterName = "Titanium.Test." + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var other = new Meter("Some.Other.Meter");
        var requests = meter.CreateCounter<long>("twp.proxy.requests");
        var foreign = other.CreateCounter<long>("twp.proxy.requests");
        using var collector = new DashboardMetrics(meterName);

        for (var i = 0; i < DashboardMetrics.MaxRoutes + 10; i++)
        {
            requests.Add(1, Tag("route", "route-" + i), Tag("status_class", "2xx"));
        }

        foreign.Add(1000);
        requests.Add(1);

        var routes = collector.GetRoutes();
        Assert.AreEqual(DashboardMetrics.MaxRoutes + 1, routes.Count, "the cap plus the overflow bucket");
        Assert.IsTrue(routes.Any(r => r.Route == DashboardMetrics.OverflowRoute));
        Assert.AreEqual(DashboardMetrics.MaxRoutes + 11, collector.GetOverview().TotalRequests);
    }

    [TestMethod]
    public async Task Health_ListsRecentEjectionsAndProbeFailures_InARingOf200()
    {
        var manager = new ClusterManager();
        using var log = new HealthEventLog(manager);
        await using var rig = await Rig.CreateAsync(health: log, manager: manager);

        for (var i = 0; i < HealthEventLog.Capacity + 25; i++)
        {
            manager.ReportHealthEvent(new DestinationHealthEvent
            {
                Time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddSeconds(i),
                DestinationId = "d" + i,
                Kind = i % 2 == 0 ? "ejection" : "probe-failure",
                Source = i % 2 == 0 ? "circuit-breaker" : "active-probe",
                Detail = "n=" + i,
            });
        }

        Assert.AreEqual(HealthEventLog.Capacity, log.Count);
        var events = JsonDocument.Parse((await rig.SendAsync("GET", "/api/health", secret: Secret)).Text).RootElement;
        Assert.AreEqual(200, events.GetArrayLength());
        Assert.AreEqual("d224", events[0].GetProperty("destination").GetString(), "newest first");
        Assert.AreEqual("d25", events[199].GetProperty("destination").GetString(), "the oldest 25 were overwritten");
        StringAssert.Contains((await rig.SendAsync("GET", "/health", secret: Secret)).Text, "probe-failure");
        Assert.AreEqual(0, log.Recent(0).Count);
    }

    [TestMethod]
    public async Task Health_EmptyRing_SaysSo()
    {
        await using var rig = await Rig.CreateAsync();

        StringAssert.Contains((await rig.SendAsync("GET", "/health", secret: Secret)).Text, "No ejections or probe failures recorded.");
        Assert.AreEqual("[]", (await rig.SendAsync("GET", "/api/health", secret: Secret)).Text);
    }

    [TestMethod]
    public void ClusterManagerEvents_ReportPassiveEjectionAndRecovery()
    {
        var manager = new ClusterManager();
        using var log = new HealthEventLog(manager);
        var tracker = new DestinationHealthTracker();
        var passive = new PassiveHealthConfig { ConsecutiveFailures = 1, HalfOpenSuccesses = 1 };

        tracker.ReportStatus("d1", 503, manager, passive, destinationCount: 1);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("d1"));

        var events = log.Recent();
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("ejection", events[0].Kind);
        Assert.AreEqual("passive", events[0].Source);
        Assert.AreEqual("d1", events[0].DestinationId);

        tracker.ReportStatus("d1", 200, manager, passive, destinationCount: 1);
        Assert.AreEqual("recovery", log.Recent()[0].Kind);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public void HealthEventLog_TrimsLongFields_AndDetachesOnDispose()
    {
        var manager = new ClusterManager();
        var log = new HealthEventLog(manager);
        manager.ReportHealthEvent(new DestinationHealthEvent
        {
            Time = DateTimeOffset.UnixEpoch,
            DestinationId = new string('d', 1000),
            Kind = "ejection",
            Source = "passive",
            Detail = new string('x', 1000),
        });
        Assert.AreEqual(256, log.Recent()[0].DestinationId.Length);
        Assert.AreEqual(256, log.Recent()[0].Detail!.Length);

        log.Dispose();
        manager.ReportHealthEvent(new DestinationHealthEvent
        {
            Time = DateTimeOffset.UnixEpoch, DestinationId = "late", Kind = "ejection", Source = "passive",
        });
        Assert.AreEqual(1, log.Count);
        Assert.ThrowsExactly<ArgumentNullException>(() => log.Add(null!));
    }

    [TestMethod]
    public void ClusterManager_SurvivesAThrowingHealthSubscriber()
    {
        var manager = new ClusterManager();
        manager.HealthEvent += _ => throw new InvalidOperationException("subscriber bug");

        manager.ReportHealthEvent(new DestinationHealthEvent
        {
            Time = DateTimeOffset.UnixEpoch, DestinationId = "d1", Kind = "ejection", Source = "passive",
        });
        Assert.ThrowsExactly<ArgumentNullException>(() => manager.ReportHealthEvent(null!));
    }

    [TestMethod]
    public async Task Config_ShowsSchemaVersionHashAndReloadOutcome()
    {
        var status = new FakeHostStatus
        {
            Config = new PlusConfigStatus
            {
                SchemaVersion = "7.1",
                ConfigHash = "abc123",
                LoadedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                LastReloadUtc = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero),
                LastReloadResult = "failed",
                LastReloadError = "route <x> names an unknown cluster",
            },
        };
        await using var rig = await Rig.CreateAsync(hostStatus: status);

        var html = (await rig.SendAsync("GET", "/config", secret: Secret)).Text;
        StringAssert.Contains(html, "data-testid=\"cfg-schema-version\">7.1<");
        StringAssert.Contains(html, "data-testid=\"cfg-config-hash\">abc123<");
        StringAssert.Contains(html, "failed");
        StringAssert.Contains(html, "route &lt;x&gt; names an unknown cluster");

        var json = JsonDocument.Parse((await rig.SendAsync("GET", "/api/config", secret: Secret)).Text).RootElement;
        Assert.IsTrue(json.GetProperty("available").GetBoolean());
        Assert.AreEqual("failed", json.GetProperty("lastResult").GetString());
    }

    [TestMethod]
    public async Task Config_WithoutAHostStatus_SaysItIsUnavailable()
    {
        await using var rig = await Rig.CreateAsync();

        StringAssert.Contains((await rig.SendAsync("GET", "/config", secret: Secret)).Text, "config-unavailable");
        var json = JsonDocument.Parse((await rig.SendAsync("GET", "/api/config", secret: Secret)).Text).RootElement;
        Assert.IsFalse(json.GetProperty("available").GetBoolean());
    }

    [TestMethod]
    public async Task Certificates_ShowSubjectIssuerAndDaysLeft_ForListenerAndUpstream()
    {
        var clock = new ManualTime();
        var now = clock.GetUtcNow();
        var status = new FakeHostStatus
        {
            Certificates =
            [
                new PlusCertificateInfo { Name = "server", Role = "listener", Subject = "CN=proxy", Issuer = "CN=Corp CA", NotAfterUtc = now.AddDays(90).AddHours(1) },
                new PlusCertificateInfo { Name = "client", Role = "upstream", Subject = "CN=client", Issuer = "CN=Corp CA", NotAfterUtc = now.AddDays(10).AddHours(1) },
                new PlusCertificateInfo { Name = "old", Role = "upstream", Subject = "CN=old", Issuer = "CN=Corp CA", NotAfterUtc = now.AddDays(-3) },
                new PlusCertificateInfo { Name = "broken", Role = "listener", Error = "could not be read (CryptographicException)" },
            ],
        };
        await using var rig = await Rig.CreateAsync(hostStatus: status, clock: clock);

        var certs = JsonDocument.Parse((await rig.SendAsync("GET", "/api/certs", secret: Secret)).Text).RootElement;
        Assert.AreEqual(4, certs.GetArrayLength());
        Assert.AreEqual(90, certs[0].GetProperty("daysLeft").GetInt32());
        Assert.AreEqual("ok", certs[0].GetProperty("status").GetString());
        Assert.AreEqual("expiring", certs[1].GetProperty("status").GetString());
        Assert.AreEqual("expired", certs[2].GetProperty("status").GetString());
        Assert.IsTrue(certs[2].GetProperty("daysLeft").GetInt32() < 0);
        Assert.AreEqual("unknown", certs[3].GetProperty("status").GetString());

        var html = (await rig.SendAsync("GET", "/certificates", secret: Secret)).Text;
        StringAssert.Contains(html, "CN=Corp CA");
        StringAssert.Contains(html, "upstream");
        StringAssert.Contains(html, "could not be read");
    }

    [TestMethod]
    public async Task Certificates_WithNothingToReport_SaysSo()
    {
        await using var rig = await Rig.CreateAsync();

        StringAssert.Contains((await rig.SendAsync("GET", "/certificates", secret: Secret)).Text, "No certificates to report.");
    }

    [TestMethod]
    public async Task Audit_ShowsRecentEvents_AndTheApiKeepsItsShape()
    {
        await using var rig = await Rig.CreateAsync();
        _ = await rig.SendAsync("POST", "/drain/d1", secret: Secret);

        StringAssert.Contains((await rig.SendAsync("GET", "/audit", secret: Secret)).Text, "dashboard:shared-secret");
        var rows = JsonDocument.Parse((await rig.SendAsync("GET", "/api/audit", secret: Secret)).Text).RootElement;
        Assert.AreEqual("drain", rows[rows.GetArrayLength() - 1].GetProperty("action").GetString());
        foreach (var field in new[] { "time", "actor", "remote", "action", "target", "result" })
        {
            Assert.IsTrue(rows[0].TryGetProperty(field, out _), field);
        }
    }

    [TestMethod]
    public async Task UnknownPathsAndApiNames_AreNotFound()
    {
        await using var rig = await Rig.CreateAsync();

        Assert.AreEqual(404, (await rig.SendAsync("GET", "/nope", secret: Secret)).Status);
        Assert.AreEqual(404, (await rig.SendAsync("GET", "/api/nope", secret: Secret)).Status);
        Assert.AreEqual(405, (await rig.SendAsync("PUT", "/", secret: Secret)).Status);
    }

    [TestMethod]
    public async Task OverARealSocket_LoginSessionAndDrain_WorkEndToEnd()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Host.Start();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var http = new HttpClient(handler);

        var login = await http.PostAsync(rig.Host.Prefix + "login", new FormUrlEncodedContent([new("secret", Secret)]));
        Assert.AreEqual(HttpStatusCode.SeeOther, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.IsTrue(login.Headers.Contains("Content-Security-Policy"));

        using var pageReq = new HttpRequestMessage(HttpMethod.Get, rig.Host.Prefix + "routes");
        pageReq.Headers.Add("Cookie", cookie);
        var page = await http.SendAsync(pageReq);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var csrf = CsrfTokenPattern().Match(await page.Content.ReadAsStringAsync()).Groups[1].Value;
        Assert.IsTrue(csrf.Length > 0);

        using var drain = new HttpRequestMessage(HttpMethod.Post, rig.Host.Prefix + "drain/d1");
        drain.Headers.Add("Cookie", cookie);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(drain)).StatusCode);
        using var drainWithToken = new HttpRequestMessage(HttpMethod.Post, rig.Host.Prefix + "drain/d1");
        drainWithToken.Headers.Add("Cookie", cookie);
        drainWithToken.Headers.Add(DashboardHost.CsrfHeader, csrf);
        Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(drainWithToken)).StatusCode);
        Assert.AreEqual(DestinationState.Draining, rig.Manager.GetDestinationState("d1"));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static KeyValuePair<string, object?> Tag(string key, object value) => new(key, value);

    private sealed class FakeHostStatus : IPlusHostStatus
    {
        public PlusConfigStatus Config { get; init; } = new();

        public IReadOnlyList<PlusCertificateInfo> Certificates { get; init; } = [];

        public PlusConfigStatus GetConfigStatus() => Config;

        public IReadOnlyList<PlusCertificateInfo> GetCertificates() => Certificates;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Response(int Status, string ContentType, IReadOnlyList<KeyValuePair<string, string>> Headers, string Text)
    {
        public string? Header(string name)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                return ContentType;
            }

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

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(ClusterManager manager, ControlPlaneServer control, DashboardHost host, ManualTime clock)
        {
            Manager = manager;
            Control = control;
            Host = host;
            Clock = clock;
        }

        public ClusterManager Manager { get; }

        public ControlPlaneServer Control { get; }

        public DashboardHost Host { get; }

        public ManualTime Clock { get; }

        public static async Task<Rig> CreateAsync(
            string? viewerSecret = null,
            string[]? destinationIds = null,
            IList<RouteConfig>? routes = null,
            ClusterConfig[]? clusters = null,
            DashboardMetrics? metrics = null,
            HealthEventLog? health = null,
            IPlusHostStatus? hostStatus = null,
            ManualTime? clock = null,
            ClusterManager? manager = null)
        {
            manager ??= new ClusterManager();
            clock ??= new ManualTime();
            await manager.ApplyAsync(clusters ??
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Destinations = (destinationIds ?? ["d1"])
                        .Select(id => new DestinationConfig { Id = id, Address = "127.0.0.1", Port = 80 })
                        .ToList(),
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
                    ViewerSecret = viewerSecret,
                    Routes = routes,
                    Metrics = metrics,
                    HealthEvents = health,
                    HostStatus = hostStatus,
                    TimeProvider = clock,
                });
            return new Rig(manager, control, host, clock);
        }

        public static string CookieOf(Response login) => login.Header("Set-Cookie")!.Split(';')[0];

        public static string SessionId(Response login) => CookieOf(login)[(DashboardHost.SessionCookie.Length + 1)..];

        public IReadOnlyList<AuditEntry> Audit() => Control.Security.Audit.Recent();

        public async Task<Response> SendAsync(
            string method,
            string path,
            string? cookie = null,
            string? secret = null,
            string? csrf = null,
            string? body = null,
            bool poll = false)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (cookie is not null)
            {
                headers["Cookie"] = cookie;
            }

            if (secret is not null)
            {
                headers[ControlPlaneServer.SharedSecretHeader] = secret;
            }

            if (csrf is not null)
            {
                headers[DashboardHost.CsrfHeader] = csrf;
            }

            if (poll)
            {
                headers[DashboardHost.PollHeader] = "1";
            }

            var request = new ControlHttpRequest
            {
                Method = method,
                Path = path,
                Headers = headers,
                Body = body is null ? [] : Encoding.UTF8.GetBytes(body),
                RemoteAddress = IPAddress.Parse(Remote),
            };
            var response = await Host.HandleAsync(request, CancellationToken.None);
            return new Response(response.Status, response.ContentType, response.Headers, Encoding.UTF8.GetString(response.Body));
        }

        /// <summary>Signs in through the login form and returns the cookie header value.</summary>
        public async Task<string> SignInAsync(string secret)
        {
            var login = await SendAsync("POST", "/login", body: "secret=" + Uri.EscapeDataString(secret));
            Assert.AreEqual(303, login.Status);
            return CookieOf(login);
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

    [GeneratedRegex("data-testid=\"btn-drain-d1\" disabled")]
    private static partial Regex ViewerDrainDisabledPattern();

    [GeneratedRegex("<script(?![^>]*\\bsrc=)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptPattern();

    [GeneratedRegex("<style", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyleElementPattern();

    [GeneratedRegex("\\s(on[a-z]+|style)=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandlerOrStylePattern();

    [GeneratedRegex("data-testid=\"dest-row-")]
    private static partial Regex DestRowTestIdPattern();

    [GeneratedRegex("data-csrf=\"([^\"]+)\"")]
    private static partial Regex CsrfTokenPattern();
}
