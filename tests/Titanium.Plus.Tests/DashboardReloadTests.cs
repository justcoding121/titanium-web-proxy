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
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class DashboardReloadTests
{
    private const string Secret = "operator-secret-value";
    private const string ViewerSecret = "viewer-secret-value";
    private const string Remote = "10.4.5.6";

    [TestMethod]
    public async Task Reload_WithCsrf_SucceedsAndAudits()
    {
        var calls = 0;
        await using var rig = await Rig.CreateAsync(requestReload: _ =>
        {
            calls++;
            return Task.FromResult(new PlusReloadResult { Ok = true, ConfigHash = "abc123" });
        });

        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);
        var response = await rig.SendAsync("POST", "/reload/-", cookie: cookie, csrf: csrf);
        Assert.AreEqual(200, response.Status);
        StringAssert.Contains(response.Text, "abc123");
        Assert.AreEqual(1, calls);
        var entry = rig.Audit().First(e => e.Action == AuditActions.Reload);
        Assert.AreEqual("ok", entry.Result);
        Assert.AreEqual("dashboard:break-glass:operator", entry.Actor);
    }

    [TestMethod]
    public async Task Reload_Viewer_IsForbidden()
    {
        await using var rig = await Rig.CreateAsync(
            viewerSecret: ViewerSecret,
            requestReload: _ => Task.FromResult(new PlusReloadResult { Ok = true }));
        var cookie = await rig.SignInAsync(ViewerSecret);
        var csrf = await rig.CsrfAsync(cookie);
        var response = await rig.SendAsync("POST", "/reload/-", cookie: cookie, csrf: csrf);
        Assert.AreEqual(403, response.Status);
        Assert.AreEqual("denied", rig.Audit().First(e => e.Action == AuditActions.Reload).Result);
    }

    [TestMethod]
    public async Task Reload_MissingCsrf_IsForbidden()
    {
        await using var rig = await Rig.CreateAsync(
            requestReload: _ => Task.FromResult(new PlusReloadResult { Ok = true }));
        var cookie = await rig.SignInAsync(Secret);
        var response = await rig.SendAsync("POST", "/reload/-", cookie: cookie);
        Assert.AreEqual(403, response.Status);
        StringAssert.Contains(response.Text, "csrf");
    }

    [TestMethod]
    public async Task ConfigPage_ShowsDriftBanner_WhenManagedAndDrifted()
    {
        await using var rig = await Rig.CreateAsync(hostStatus: new FakeStatus(managed: true, drifted: true));
        var cookie = await rig.SignInAsync(Secret);
        var page = await rig.SendAsync("GET", "/config", cookie: cookie);
        StringAssert.Contains(page.Text, "data-testid=\"config-drift\"");
        Assert.IsFalse(page.Text.Contains("data-testid=\"btn-reload\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConfigPage_ShowsReloadButton_WhenOperatorAndCallbackSet()
    {
        await using var rig = await Rig.CreateAsync(
            hostStatus: new FakeStatus(managed: true, drifted: false),
            requestReload: _ => Task.FromResult(new PlusReloadResult { Ok = true }));
        var cookie = await rig.SignInAsync(Secret);
        var page = await rig.SendAsync("GET", "/config", cookie: cookie);
        StringAssert.Contains(page.Text, "data-testid=\"btn-reload\"");
        StringAssert.Contains(page.Text, "data-testid=\"config-managed\"");
        Assert.IsFalse(page.Text.Contains("config-drift", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedReload_KeepsServing_AndShowsError()
    {
        await using var rig = await Rig.CreateAsync(requestReload: _ =>
            Task.FromResult(new PlusReloadResult { Ok = false, Error = "validation failed" }));
        var cookie = await rig.SignInAsync(Secret);
        var csrf = await rig.CsrfAsync(cookie);
        var response = await rig.SendAsync("POST", "/reload/-", cookie: cookie, csrf: csrf);
        Assert.AreEqual(500, response.Status);
        StringAssert.Contains(response.Text, "validation failed");
        Assert.AreEqual(200, (await rig.SendAsync("GET", "/", cookie: cookie)).Status);
        Assert.AreEqual("error", rig.Audit().First(e => e.Action == AuditActions.Reload).Result);
    }

    private sealed class FakeStatus(bool managed, bool drifted) : IPlusHostStatus
    {
        public PlusConfigStatus GetConfigStatus() => new()
        {
            SchemaVersion = "1",
            ConfigHash = "hash",
            Managed = managed,
            Drifted = drifted,
        };

        public IReadOnlyList<PlusCertificateInfo> GetCertificates() => [];
    }

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(ControlPlaneServer control, DashboardHost host)
        {
            Control = control;
            Host = host;
        }

        public ControlPlaneServer Control { get; }
        public DashboardHost Host { get; }

        public static async Task<Rig> CreateAsync(
            string? viewerSecret = null,
            IPlusHostStatus? hostStatus = null,
            Func<CancellationToken, Task<PlusReloadResult>>? requestReload = null)
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
                    ViewerSecret = viewerSecret,
                    HostStatus = hostStatus,
                    RequestReload = requestReload,
                });
            return new Rig(control, host);
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
                    RemoteAddress = IPAddress.Parse(Remote),
                },
                CancellationToken.None);
            return new Response(response.Status, response.Headers, Encoding.UTF8.GetString(response.Body));
        }

        public async Task<string> SignInAsync(string secret)
        {
            var login = await SendAsync("POST", "/login", body: "secret=" + Uri.EscapeDataString(secret));
            Assert.AreEqual(303, login.Status);
            return login.Header("Set-Cookie")!.Split(';')[0];
        }

        public async Task<string> CsrfAsync(string cookie)
        {
            var page = await SendAsync("GET", "/", cookie: cookie);
            return Regex.Match(page.Text, "data-csrf=\"([^\"]+)\"").Groups[1].Value;
        }

        public ValueTask DisposeAsync()
        {
            Host.Dispose();
            Control.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Response(int Status, IReadOnlyList<KeyValuePair<string, string>> Headers, string Text)
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
