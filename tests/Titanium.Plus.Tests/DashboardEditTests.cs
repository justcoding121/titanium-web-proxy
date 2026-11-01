using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

/// <summary>The operator forms that edit one route or cluster through <see cref="IConfigEditor" />.</summary>
[TestClass]
public partial class DashboardEditTests
{
    [GeneratedRegex("data-csrf=\"([^\"]+)\"")]
    private static partial Regex CsrfTokenPattern();

    private const string Secret = "operator-secret-value";
    private const string ViewerSecret = "viewer-secret-value";
    private const string RouteJson = """{"id":"r2","clusterId":"c1","match":{"hosts":["b.test"]}}""";

    [TestMethod]
    public async Task RoutesPage_ShowsEditLinksAndOverlayBanner_ToAnOperatorWhenEditingIsOn()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        rig.Editor.Status = new OverlayStatus { HasOverlay = true, EditCount = 2 };
        var cookie = await rig.SignInAsync(Secret);

        var page = await rig.SendAsync("GET", "/routes", cookie: cookie);

        StringAssert.Contains(page.Text, "data-testid=\"edit-route-r1\"");
        StringAssert.Contains(page.Text, "data-testid=\"edit-cluster-c1\"");
        StringAssert.Contains(page.Text, "data-testid=\"add-route\"");
        StringAssert.Contains(page.Text, "data-testid=\"overlay-active\"");
        StringAssert.Contains(page.Text, "data-testid=\"btn-clear-overlay\"");
    }

    [TestMethod]
    public async Task RoutesPage_HasNoEditPanel_WhenEditingIsOff_OrForAViewer()
    {
        await using (var off = await Rig.CreateAsync(editing: false))
        {
            var cookie = await off.SignInAsync(Secret);
            var page = await off.SendAsync("GET", "/routes", cookie: cookie);
            Assert.IsFalse(page.Text.Contains("edit-panel", StringComparison.Ordinal));
            Assert.AreEqual(404, (await off.SendAsync("GET", "/edit/route/r1", cookie: cookie)).Status);
        }

        await using var on = await Rig.CreateAsync(editing: true, viewerSecret: ViewerSecret);
        var viewer = await on.SignInAsync(ViewerSecret);
        Assert.IsFalse((await on.SendAsync("GET", "/routes", cookie: viewer)).Text.Contains("edit-panel", StringComparison.Ordinal));
        Assert.AreEqual(403, (await on.SendAsync("GET", "/edit/route/r1", cookie: viewer)).Status);
    }

    [TestMethod]
    public async Task EditPage_PrefillsTheCurrentItemAsJson_AndEncodesIt()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        var cookie = await rig.SignInAsync(Secret);

        var route = await rig.SendAsync("GET", "/edit/route/r1", cookie: cookie);
        Assert.AreEqual(200, route.Status);
        StringAssert.Contains(route.Text, "data-testid=\"edit-form\"");
        StringAssert.Contains(route.Text, "&quot;clusterId&quot;: &quot;c1&quot;");
        StringAssert.Contains(route.Text, "data-testid=\"edit-delete\"");
        Assert.IsFalse(route.Text.Contains("onsubmit", StringComparison.Ordinal), "CSP forbids inline handlers");

        var cluster = await rig.SendAsync("GET", "/edit/cluster/c1", cookie: cookie);
        Assert.AreEqual(200, cluster.Status);
        StringAssert.Contains(cluster.Text, "&quot;address&quot;: &quot;127.0.0.1&quot;");

        var add = await rig.SendAsync("GET", "/edit/route", cookie: cookie);
        Assert.AreEqual(200, add.Status);
        Assert.IsFalse(add.Text.Contains("data-testid=\"edit-delete\"", StringComparison.Ordinal), "no delete for a new item");

        Assert.AreEqual(404, (await rig.SendAsync("GET", "/edit/route/nope", cookie: cookie)).Status);
        Assert.AreEqual(404, (await rig.SendAsync("GET", "/edit/widget/x", cookie: cookie)).Status);
    }

    [TestMethod]
    public async Task Save_AppliesTheEdit_AndAudits()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        var response = await rig.SendAsync("POST", "/edit/route/save", cookie: cookie, csrf: csrf, body: RouteJson);

        Assert.AreEqual(200, response.Status, response.Text);
        var edit = rig.Editor.Applied.Single();
        Assert.AreEqual(ConfigEditKind.UpsertRoute, edit.Kind);
        Assert.AreEqual("r2", edit.Route!.Id);
        Assert.AreEqual("dashboard:break-glass:operator", rig.Editor.Actors.Single());
        var entry = rig.Audit().First(e => e.Action == AuditActions.ConfigEdit);
        Assert.AreEqual("ok", entry.Result);
        Assert.AreEqual("route/save", entry.Target);
    }

    [TestMethod]
    public async Task Save_OfAnExistingItem_RequiresTheSameId_AndAnAddCannotOverwrite()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        var renamed = await rig.SendAsync("POST", "/edit/route/save/r1", cookie: cookie, csrf: csrf, body: RouteJson);
        Assert.AreEqual(400, renamed.Status);
        StringAssert.Contains(renamed.Text, "id cannot change");

        var overwrite = await rig.SendAsync(
            "POST", "/edit/route/save", cookie: cookie, csrf: csrf,
            body: """{"id":"r1","clusterId":"c1","match":{"hosts":["x.test"]}}""");
        Assert.AreEqual(409, overwrite.Status);
        Assert.AreEqual(0, rig.Editor.Applied.Count);

        var update = await rig.SendAsync(
            "POST", "/edit/route/save/r1", cookie: cookie, csrf: csrf,
            body: """{"id":"r1","clusterId":"c1","match":{"hosts":["x.test"]}}""");
        Assert.AreEqual(200, update.Status, update.Text);
    }

    [TestMethod]
    public async Task Validate_ReportsValidatorErrors_WithoutChangingAnything()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        rig.Editor.ValidationErrors = ["routes[1].clusterId 'zzz' is not a cluster"];
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        var response = await rig.SendAsync("POST", "/edit/route/validate", cookie: cookie, csrf: csrf, body: RouteJson);

        Assert.AreEqual(422, response.Status);
        StringAssert.Contains(response.Text, "is not a cluster");
        Assert.AreEqual(0, rig.Editor.Applied.Count);
        Assert.IsFalse(rig.Audit().Any(e => e.Action == AuditActions.ConfigEdit), "a dry run is not an edit");
        CollectionAssert.AreEquivalent(new[] { "r1", "r2" }, rig.Editor.LastValidatedRouteIds!.ToArray());
    }

    [TestMethod]
    public async Task Save_RejectedByTheEditor_Returns422_AndAuditsRejected()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        rig.Editor.Reject = ["listener port clash"];
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        var response = await rig.SendAsync("POST", "/edit/route/save", cookie: cookie, csrf: csrf, body: RouteJson);

        Assert.AreEqual(422, response.Status);
        StringAssert.Contains(response.Text, "listener port clash");
        Assert.AreEqual("rejected", rig.Audit().First(e => e.Action == AuditActions.ConfigEdit).Result);
    }

    [TestMethod]
    public async Task Delete_RemovesTheItem_AndAnUnknownIdIs404()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        Assert.AreEqual(404, (await rig.SendAsync("POST", "/edit/cluster/delete/nope", cookie: cookie, csrf: csrf)).Status);
        var response = await rig.SendAsync("POST", "/edit/cluster/delete/c1", cookie: cookie, csrf: csrf);

        Assert.AreEqual(200, response.Status, response.Text);
        var edit = rig.Editor.Applied.Single();
        Assert.AreEqual(ConfigEditKind.RemoveCluster, edit.Kind);
        Assert.AreEqual("c1", edit.Id);
    }

    [TestMethod]
    public async Task BadJson_And_OversizedBody_AreRefused()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        var bad = await rig.SendAsync("POST", "/edit/route/save", cookie: cookie, csrf: csrf, body: "{not json");
        Assert.AreEqual(400, bad.Status);
        StringAssert.Contains(bad.Text, "invalid JSON");

        var noId = await rig.SendAsync("POST", "/edit/route/save", cookie: cookie, csrf: csrf, body: """{"clusterId":"c1"}""");
        Assert.AreEqual(400, noId.Status);

        var huge = await rig.SendAsync("POST", "/edit/route/save", cookie: cookie, csrf: csrf,
            body: new string(' ', DashboardEditing.MaxBodyBytes + 1));
        Assert.AreEqual(413, huge.Status);
        Assert.AreEqual(0, rig.Editor.Applied.Count);
    }

    [TestMethod]
    public async Task Edits_NeedCsrf_AndTheOperatorRole()
    {
        await using var rig = await Rig.CreateAsync(editing: true, viewerSecret: ViewerSecret);
        var operatorCookie = await rig.SignInAsync(Secret);
        var noCsrf = await rig.SendAsync("POST", "/edit/route/save", cookie: operatorCookie, body: RouteJson);
        Assert.AreEqual(403, noCsrf.Status);

        var viewerCookie = await rig.SignInAsync(ViewerSecret);
        var viewerCsrf = await rig.CsrfAsync(viewerCookie);
        var viewer = await rig.SendAsync("POST", "/edit/route/save", cookie: viewerCookie, csrf: viewerCsrf, body: RouteJson);
        Assert.AreEqual(403, viewer.Status);
        StringAssert.Contains(viewer.Text, "operator role required");
        Assert.AreEqual(0, rig.Editor.Applied.Count);
        Assert.IsTrue(rig.Audit().Count(e => e.Action == AuditActions.ConfigEdit && e.Result == "denied") >= 2);
    }

    [TestMethod]
    public async Task ClearOverlay_CallsTheEditor_AndAudits()
    {
        await using var rig = await Rig.CreateAsync(editing: true);
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        var response = await rig.SendAsync("POST", "/clear-overlay/-", cookie: cookie, csrf: csrf);

        Assert.AreEqual(200, response.Status, response.Text);
        Assert.AreEqual(1, rig.Editor.Cleared);
        Assert.AreEqual("ok", rig.Audit().First(e => e.Action == AuditActions.OverlayClear).Result);
    }

    [TestMethod]
    public async Task EditEndpoints_AreNotFound_WhenEditingIsOff()
    {
        await using var rig = await Rig.CreateAsync(editing: false);
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);

        Assert.AreEqual(404, (await rig.SendAsync("POST", "/edit/route/save", cookie: cookie, csrf: csrf, body: RouteJson)).Status);
        Assert.AreEqual(404, (await rig.SendAsync("POST", "/clear-overlay/-", cookie: cookie, csrf: csrf)).Status);
        Assert.AreEqual(0, rig.Editor.Applied.Count);
    }

    private sealed class FakeEditor : IConfigEditor
    {
        public List<ConfigEdit> Applied { get; } = [];

        public List<string> Actors { get; } = [];

        public int Cleared { get; private set; }

        public IReadOnlyList<string> ValidationErrors { get; set; } = [];

        public IReadOnlyList<string>? Reject { get; set; }

        public OverlayStatus Status { get; set; } = new();

        public IReadOnlyList<string>? LastValidatedRouteIds { get; private set; }

        public IReadOnlyList<string> ValidateCandidate(IReadOnlyList<RouteConfig>? routes, IReadOnlyList<ClusterConfig>? clusters)
        {
            LastValidatedRouteIds = routes?.Select(r => r.Id).ToList();
            return ValidationErrors;
        }

        public Task<ConfigEditResult> ApplyEditAsync(ConfigEdit edit, string actor, CancellationToken cancellationToken = default)
        {
            if (Reject is not null)
            {
                return Task.FromResult(ConfigEditResult.Rejected(Reject));
            }

            Applied.Add(edit);
            Actors.Add(actor);
            return Task.FromResult(ConfigEditResult.Ok("digest"));
        }

        public Task<ConfigEditResult> ClearOverlayAsync(string actor, CancellationToken cancellationToken = default)
        {
            Cleared++;
            return Task.FromResult(ConfigEditResult.Ok());
        }

        public OverlayStatus GetOverlayStatus() => Status;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(ControlPlaneServer control, DashboardHost host, FakeEditor editor)
        {
            Control = control;
            Host = host;
            Editor = editor;
        }

        public ControlPlaneServer Control { get; }

        public DashboardHost Host { get; }

        public FakeEditor Editor { get; }

        public static async Task<Rig> CreateAsync(bool editing, string? viewerSecret = null)
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
            var routes = new List<RouteConfig>
            {
                new() { Id = "r1", ClusterId = "c1", Match = new RouteMatch { Hosts = ["a.test"] } },
            };
            var editor = new FakeEditor();
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
                    ConfigEditor = editor,
                    DashboardEditing = editing,
                });
            return new Rig(control, host, editor);
        }

        public IReadOnlyList<AuditEntry> Audit() => Control.Security.Audit.Recent();

        public async Task<Response> SendAsync(
            string method, string path, string? cookie = null, string? csrf = null, string? body = null)
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

            var response = await Host.HandleAsync(
                new ControlHttpRequest
                {
                    Method = method,
                    Path = path,
                    Headers = headers,
                    Body = body is null ? [] : Encoding.UTF8.GetBytes(body),
                    RemoteAddress = IPAddress.Parse("10.4.5.6"),
                },
                CancellationToken.None);
            return new Response(response.Status, Encoding.UTF8.GetString(response.Body), response.Headers);
        }

        public async Task<string> SignInAsync(string secret)
        {
            var login = await SendAsync("POST", "/login", body: "secret=" + Uri.EscapeDataString(secret));
            Assert.AreEqual(303, login.Status);
            return login.Headers.First(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Value.Split(';')[0];
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

    private sealed record Response(int Status, string Text, IReadOnlyList<KeyValuePair<string, string>> Headers);
}
