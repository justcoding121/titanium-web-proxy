using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

[TestClass]
public class ManagerUiTests
{
    private const string ValidYaml = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    private const string BadYaml = """
        listeners:
          - port: not-a-number
        """;

    [TestMethod]
    public void YamlDraft_AddAndRemoveRoute_RoundTrips()
    {
        var withRoute = ManagerUiYamlDraft.UpsertRoute(ValidYaml, "api", "/api", "backend");
        var summary = ManagerUiYamlDraft.Summarize(withRoute);
        Assert.AreEqual(1, summary.Routes.Count);
        Assert.AreEqual("api", summary.Routes[0].Id);
        Assert.AreEqual("/api", summary.Routes[0].Path);
        Assert.AreEqual("backend", summary.Routes[0].ClusterId);

        var withCluster = ManagerUiYamlDraft.UpsertCluster(withRoute, "backend");
        Assert.AreEqual(1, ManagerUiYamlDraft.Summarize(withCluster).Clusters.Count);

        var removed = ManagerUiYamlDraft.RemoveRoute(withCluster, "api");
        Assert.AreEqual(0, ManagerUiYamlDraft.Summarize(removed).Routes.Count);
    }

    [TestMethod]
    public async Task Ui_Login_SetsHttpOnlySessionCookie()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();

        var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = host.AdminToken,
        }));
        Assert.AreEqual(HttpStatusCode.SeeOther, login.StatusCode);
        Assert.AreEqual("/", login.Headers.Location?.OriginalString);

        Assert.IsTrue(login.Headers.TryGetValues("Set-Cookie", out var cookies));
        var setCookie = cookies.Single(c => c.StartsWith(ManagerUiHost.SessionCookie + "=", StringComparison.Ordinal));
        StringAssert.Contains(setCookie, "HttpOnly");
        StringAssert.Contains(setCookie, "SameSite=Strict");

        var fleets = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, fleets.StatusCode);
        var html = await fleets.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "data-testid=\"manager-ui\"");
        StringAssert.Contains(html, "data-testid=\"fleets\"");
        StringAssert.Contains(html, "data-csrf=");
    }

    [TestMethod]
    public async Task Ui_Login_WrongToken_ShowsError()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();
        var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "not-the-token",
        }));
        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "data-testid=\"login-error\"");
    }

    [TestMethod]
    public async Task Ui_Anonymous_RedirectsToLogin()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(host.BaseUrl),
        };
        var page = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.SeeOther, page.StatusCode);
        Assert.AreEqual("/login", page.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task Ui_Publish_RequiresCsrf()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();
        await SignInAsync(client, host.AdminToken);
        var csrf = await ReadCsrfAsync(client);

        using var fleetDoc = await ReadJsonAsync(await PostJsonAsync(client, "/ui/fleets", new { name = "csrf-fleet" }, csrf));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var noCsrf = await client.PostAsync(
            $"/v1/fleets/{fleetId}/revisions",
            new StringContent(JsonSerializer.Serialize(new { yaml = ValidYaml }), Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Forbidden, noCsrf.StatusCode);

        var withCsrf = await PostJsonAsync(client, $"/v1/fleets/{fleetId}/revisions", new { yaml = ValidYaml }, csrf);
        Assert.AreEqual(HttpStatusCode.Created, withCsrf.StatusCode);
        using var revDoc = await ReadJsonAsync(withCsrf);
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();

        var publishNoCsrf = await client.PostAsync(
            $"/v1/fleets/{fleetId}/revisions/{revId}/publish",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Forbidden, publishNoCsrf.StatusCode);

        var publish = await PostJsonAsync(client, $"/v1/fleets/{fleetId}/revisions/{revId}/publish", new { }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, publish.StatusCode);
        using var rolloutDoc = await ReadJsonAsync(publish);
        Assert.IsTrue(rolloutDoc.RootElement.TryGetProperty("id", out _));
    }

    [TestMethod]
    public async Task Ui_EditorValidate_ReturnsErrorsForBadYaml()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();
        await SignInAsync(client, host.AdminToken);
        var csrf = await ReadCsrfAsync(client);

        using var fleetDoc = await ReadJsonAsync(await PostJsonAsync(client, "/ui/fleets", new { name = "validate" }, csrf));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var editor = await client.GetAsync($"/fleets/{fleetId}/editor");
        Assert.AreEqual(HttpStatusCode.OK, editor.StatusCode);
        var html = await editor.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "data-testid=\"draft-yaml\"");
        StringAssert.Contains(html, "data-testid=\"btn-validate\"");
        StringAssert.Contains(html, "data-testid=\"btn-publish\"");
        StringAssert.Contains(html, "data-testid=\"route-form\"");

        var bad = await PostJsonAsync(client, $"/v1/fleets/{fleetId}/validate", new { yaml = BadYaml }, csrf);
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
        using var badDoc = await ReadJsonAsync(bad);
        Assert.AreEqual(false, badDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.IsTrue(badDoc.RootElement.GetProperty("errors").GetArrayLength() > 0);

        var good = await PostJsonAsync(client, $"/v1/fleets/{fleetId}/validate", new { yaml = ValidYaml }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, good.StatusCode);
    }

    [TestMethod]
    public async Task Ui_PublishFromSession_CreatesRollout_AndAudit()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();
        await SignInAsync(client, host.AdminToken);
        var csrf = await ReadCsrfAsync(client);

        using var fleetDoc = await ReadJsonAsync(await PostJsonAsync(client, "/ui/fleets", new { name = "ship" }, csrf));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var patched = await PostJsonAsync(client, "/ui/draft/patch", new
        {
            yaml = ValidYaml,
            op = "upsert-route",
            id = "web",
            path = "/",
            clusterId = "backend",
        }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode);
        using var patchDoc = await ReadJsonAsync(patched);
        var draft = patchDoc.RootElement.GetProperty("yaml").GetString()!;
        draft = ManagerUiYamlDraft.UpsertCluster(draft, "backend");

        using var revDoc = await ReadJsonAsync(
            await PostJsonAsync(client, $"/v1/fleets/{fleetId}/revisions", new { yaml = draft }, csrf));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();

        using var rolloutDoc = await ReadJsonAsync(
            await PostJsonAsync(client, $"/v1/fleets/{fleetId}/revisions/{revId}/publish", new { }, csrf));
        var rolloutId = rolloutDoc.RootElement.GetProperty("id").GetInt64();

        var detail = await client.GetAsync($"/fleets/{fleetId}/rollouts/{rolloutId}");
        Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
        var detailHtml = await detail.Content.ReadAsStringAsync();
        StringAssert.Contains(detailHtml, "data-testid=\"btn-rollback-rollout\"");
        StringAssert.Contains(detailHtml, "data-testid=\"btn-pause-rollout\"");

        var audit = await client.GetAsync("/audit");
        var auditHtml = await audit.Content.ReadAsStringAsync();
        StringAssert.Contains(auditHtml, "revision.publish");
    }

    [TestMethod]
    public async Task Ui_Assets_ServeUnderCsp()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        var js = await client.GetAsync("/assets/manager.js");
        var css = await client.GetAsync("/assets/manager.css");
        Assert.AreEqual(HttpStatusCode.OK, js.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, css.StatusCode);
        StringAssert.Contains(await js.Content.ReadAsStringAsync(), "X-Titanium-Csrf");
    }

    private static async Task SignInAsync(HttpClient client, string token)
    {
        var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
        }));
        Assert.AreEqual(HttpStatusCode.SeeOther, login.StatusCode);
    }

    private static async Task<string> ReadCsrfAsync(HttpClient client)
    {
        var page = await client.GetAsync("/");
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "data-csrf=\"([^\"]+)\"");
        Assert.IsTrue(match.Success, "csrf missing from page");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        object body,
        string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ManagerUiHost.CsrfHeader, csrf);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }
}
