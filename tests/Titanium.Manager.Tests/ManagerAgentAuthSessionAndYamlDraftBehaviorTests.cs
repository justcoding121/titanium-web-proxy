using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;
using Titanium.Manager.Auth;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     Manager leftovers: agent config/ack auth edges, session store ctor clamps, UI session CSRF on admin API,
///     and YamlDraft parse/upsert arms that stay thin on Sonar.
/// </summary>
[TestClass]
public class ManagerAgentAuthSessionAndYamlDraftBehaviorTests
{
    [TestMethod]
    public async Task AgentConfig_InvalidFleetQuery_AndMissingAuth_ReturnClientErrors()
    {
        await using var host = await ManagerTestHost.StartAsync();
        var fleet = host.Store.CreateFleet("agents");
        var (token, plaintext) = host.Store.CreateAgentToken(fleet.Id, "edge");

        using var agent = host.CreateAgentClient(plaintext);
        var badFleet = await agent.GetAsync("/v1/agent/config?fleet=not-a-number&node_id=n1&wait=0");
        Assert.AreEqual(HttpStatusCode.BadRequest, badFleet.StatusCode);
        Assert.AreEqual("fleet required", await badFleet.Content.ReadAsStringAsync());

        var otherFleet = host.Store.CreateFleet("other");
        var scoped = await agent.GetAsync($"/v1/agent/config?fleet={otherFleet.Id}&node_id=n1&wait=0");
        Assert.AreEqual(HttpStatusCode.Forbidden, scoped.StatusCode);

        using var anon = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var unauthorized = await anon.GetAsync($"/v1/agent/config?fleet={fleet.Id}&node_id=n1&wait=0");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var missing = await agent.GetAsync("/v1/agent/nope");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(token.Id, host.Store.FindAgentByToken(plaintext)!.Id);
    }

    [TestMethod]
    public async Task AgentAck_InvalidBody_MissingNode_AndFleetMismatch()
    {
        await using var host = await ManagerTestHost.StartAsync();
        var fleet = host.Store.CreateFleet("ack");
        var (_, plaintext) = host.Store.CreateAgentToken(fleet.Id, "edge");
        using var agent = host.CreateAgentClient(plaintext);

        using var badJson = new StringContent("{", Encoding.UTF8, "application/json");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await agent.PostAsync("/v1/agent/ack", badJson)).StatusCode);

        using var noNode = ManagerTestHost.Json(new { fleet = fleet.Id, ok = true, config_hash = "abc" });
        var missingNode = await agent.PostAsync("/v1/agent/ack", noNode);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingNode.StatusCode);
        StringAssert.Contains(await missingNode.Content.ReadAsStringAsync(), "node_id");

        var other = host.Store.CreateFleet("other-ack");
        using var wrongFleet = ManagerTestHost.Json(new
        {
            fleet = other.Id,
            node_id = "n1",
            ok = true,
            config_hash = "abc",
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, (await agent.PostAsync("/v1/agent/ack", wrongFleet)).StatusCode);
    }

    [TestMethod]
    public async Task SessionAdmin_PostWithoutCsrf_IsForbidden()
    {
        // Bug class: cookie session without CSRF must not mutate fleets.
        await using var host = await ManagerTestHost.StartAsync();
        var id = host.Sessions.Create(AdminRole.Admin, "ui:admin", out var session);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(host.BaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie",
            $"{ManagerUiHost.SessionCookie}={id}");

        using var body = ManagerTestHost.Json(new { name = "no-csrf" });
        var response = await client.PostAsync("/v1/fleets", body);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

        client.DefaultRequestHeaders.TryAddWithoutValidation(ManagerUiHost.CsrfHeader, session.CsrfToken);
        var ok = await client.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "with-csrf" }));
        Assert.AreEqual(HttpStatusCode.Created, ok.StatusCode);
    }

    [TestMethod]
    public void SessionStore_ZeroIdleAndMax_ClampToDefaults()
    {
        var store = new ManagerSessionStore(
            idleTimeout: TimeSpan.Zero,
            maxSessions: 0);
        Assert.AreEqual(ManagerSessionStore.DefaultIdleTimeout, store.IdleTimeout);

        var id = store.Create(AdminRole.Viewer, " ui:viewer ", out var session);
        Assert.AreEqual("ui:viewer", session.Actor);
        Assert.AreEqual(1, store.Count);
        Assert.IsTrue(store.Remove(id));
        Assert.IsFalse(store.Remove(new string('x', 200)));
    }

    [TestMethod]
    public void YamlDraft_EmptyParse_StringKeyedMaps_AndPathPrefixRead()
    {
        var empty = ManagerUiYamlDraft.Summarize("   ");
        Assert.AreEqual(0, empty.Routes.Count);
        Assert.AreEqual(0, empty.Clusters.Count);

        var created = ManagerUiYamlDraft.UpsertRoute(ManagerUiYamlDraft.EmptyDraft, "api", "/v1", "backend");
        var again = ManagerUiYamlDraft.UpsertRoute(created, "api", "/v2", null);
        var summary = ManagerUiYamlDraft.Summarize(again);
        Assert.AreEqual(1, summary.Routes.Count);
        Assert.AreEqual("/v2", summary.Routes[0].Path);
        Assert.AreEqual("backend", summary.Routes[0].ClusterId);

        var withPrefix = """
            routes:
              - id: r1
                match:
                  pathPrefix: /legacy
                clusterId: c1
            clusters:
              - id: c1
                destinations:
                  - address: 127.0.0.1
                    port: 80
            """;
        Assert.AreEqual("/legacy", ManagerUiYamlDraft.Summarize(withPrefix).Routes[0].Path);

        var idempotent = ManagerUiYamlDraft.UpsertCluster(withPrefix, "c1", "10.0.0.9", 9);
        Assert.AreEqual(1, ManagerUiYamlDraft.Summarize(idempotent).Clusters.Count);

        var removed = ManagerUiYamlDraft.RemoveRoute(again, "api");
        Assert.AreEqual(0, ManagerUiYamlDraft.Summarize(removed).Routes.Count);
    }
}
