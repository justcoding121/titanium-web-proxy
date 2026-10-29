using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;
using Titanium.Manager.Validation;

namespace Titanium.Manager.Tests;

[TestClass]
public class ManagerStoreAndApiTests
{
    private const string ValidYaml = """
        listeners:
          - port: 8000
            type: explicit
        """;

    private const string ValidYamlV2 = """
        listeners:
          - port: 8001
            type: explicit
        """;

    private const string InlineSecretYaml = """
        plus:
          enabled: true
          controlPlane:
            host: 127.0.0.1
            sharedSecret: literal-not-allowed
            allowInsecureNonLoopback: true
        """;

    private const string BadYaml = """
        listeners:
          - port: not-a-number
        """;

    [TestMethod]
    public void Validator_RejectsBadYamlAndInlineSecrets()
    {
        Assert.AreEqual(0, ConfigRevisionValidator.ValidateYaml(ValidYaml).Count);

        var bad = ConfigRevisionValidator.ValidateYaml(BadYaml);
        Assert.IsTrue(bad.Count > 0, string.Join("; ", bad));

        var secrets = ConfigRevisionValidator.ValidateYaml(InlineSecretYaml);
        Assert.IsTrue(secrets.Any(e => e.Contains("Inline secret rejected", StringComparison.Ordinal)),
            string.Join("; ", secrets));
    }

    [TestMethod]
    public void Store_CreateFleetAndRevision_RejectsBadYamlAtApi()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("alpha");
        var rev = store.CreateRevision(fleet.Id, ValidYaml, "tester");
        Assert.AreEqual(ManagerStore.ContentHash(ValidYaml), rev.ContentHash);
        Assert.AreEqual(1, store.ListRevisions(fleet.Id).Count);
    }

    [TestMethod]
    public async Task Api_AdminTokenRequired_AndCreatesFleetRevisionPublish()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var anon = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anon.GetAsync("/v1/fleets")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await anon.GetAsync("/v1/healthz")).StatusCode);

        using var admin = host.CreateAdminClient();
        var createFleet = await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "prod" }));
        Assert.AreEqual(HttpStatusCode.Created, createFleet.StatusCode);
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(createFleet);
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var badRev = await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(InlineSecretYaml));
        Assert.AreEqual(HttpStatusCode.BadRequest, badRev.StatusCode);

        var rev = await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml));
        Assert.AreEqual(HttpStatusCode.Created, rev.StatusCode);
        using var revDoc = await ManagerTestHost.ReadJsonAsync(rev);
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();
        var hash = revDoc.RootElement.GetProperty("contentHash").GetString();

        var publish = await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{revId}/publish", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, publish.StatusCode);

        var tokenResp = await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId, name = "node-a" }));
        Assert.AreEqual(HttpStatusCode.Created, tokenResp.StatusCode);
        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(tokenResp);
        var agentToken = tokenDoc.RootElement.GetProperty("token").GetString()!;
        var tokenId = tokenDoc.RootElement.GetProperty("id").GetInt64();

        using var agent = host.CreateAgentClient(agentToken);
        var cfg = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have=&wait=0");
        Assert.AreEqual(HttpStatusCode.OK, cfg.StatusCode);
        using var cfgDoc = await ManagerTestHost.ReadJsonAsync(cfg);
        Assert.AreEqual(hash, cfgDoc.RootElement.GetProperty("hash").GetString());
        Assert.AreEqual(ValidYaml.Replace("\r\n", "\n").TrimEnd(),
            cfgDoc.RootElement.GetProperty("yaml").GetString()!.Replace("\r\n", "\n").TrimEnd());

        var same = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have={hash}&wait=0");
        Assert.AreEqual(HttpStatusCode.NoContent, same.StatusCode);

        var ack = await agent.PostAsync("/v1/agent/ack", ManagerTestHost.Json(new
        {
            fleet = fleetId,
            node_id = "node-1",
            config_hash = hash,
            ok = true,
            health = new { cpu = 0.1 },
        }));
        Assert.AreEqual(HttpStatusCode.OK, ack.StatusCode);

        var agents = await admin.GetAsync($"/v1/fleets/{fleetId}/agents");
        using var agentsDoc = await ManagerTestHost.ReadJsonAsync(agents);
        Assert.AreEqual(1, agentsDoc.RootElement.GetArrayLength());
        Assert.AreEqual("ok", agentsDoc.RootElement[0].GetProperty("status").GetString());
        Assert.AreEqual("node-1", agentsDoc.RootElement[0].GetProperty("nodeId").GetString());

        var revoke = await admin.PostAsync($"/v1/agent-tokens/{tokenId}/revoke", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have=&wait=0")).StatusCode);

        var audit = await admin.GetAsync("/v1/audit");
        using var auditDoc = await ManagerTestHost.ReadJsonAsync(audit);
        var actions = auditDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToHashSet();
        Assert.IsTrue(actions.Contains("revision.publish"));
        Assert.IsTrue(actions.Contains("agent-token.create"));
    }

    [TestMethod]
    public async Task Api_LongPoll_WakesOnPublish()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "poll" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        using var revDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();
        await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{revId}/publish", new StringContent(""));
        var hash1 = revDoc.RootElement.GetProperty("contentHash").GetString()!;

        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId, name = "poller" })));
        var agentToken = tokenDoc.RootElement.GetProperty("token").GetString()!;
        using var agent = host.CreateAgentClient(agentToken);

        var waitTask = agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have={hash1}&wait=30");
        await Task.Delay(200);

        using var rev2Doc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYamlV2)));
        var rev2Id = rev2Doc.RootElement.GetProperty("id").GetInt64();
        var hash2 = rev2Doc.RootElement.GetProperty("contentHash").GetString();
        await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{rev2Id}/publish", new StringContent(""));

        using var waited = await waitTask;
        Assert.AreEqual(HttpStatusCode.OK, waited.StatusCode);
        using var body = await ManagerTestHost.ReadJsonAsync(waited);
        Assert.AreEqual(hash2, body.RootElement.GetProperty("hash").GetString());
    }

    [TestMethod]
    public async Task Api_DiffBetweenRevisions()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "diff" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        using var r1 = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        using var r2 = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYamlV2)));
        var from = r1.RootElement.GetProperty("id").GetInt64();
        var to = r2.RootElement.GetProperty("id").GetInt64();

        var textDiff = await admin.GetAsync($"/v1/fleets/{fleetId}/diff?from={from}&to={to}");
        Assert.AreEqual(HttpStatusCode.OK, textDiff.StatusCode);
        var text = await textDiff.Content.ReadAsStringAsync();
        StringAssert.Contains(text, "8000");
        StringAssert.Contains(text, "8001");

        var jsonDiff = await admin.GetAsync($"/v1/fleets/{fleetId}/diff?from={from}&to={to}&format=json");
        using var diffDoc = await ManagerTestHost.ReadJsonAsync(jsonDiff);
        Assert.IsTrue(diffDoc.RootElement.GetProperty("unified").GetString()!.Contains("8001"));
    }

    [TestMethod]
    public async Task Api_ValidateWithoutStoring()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "val" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var ok = await admin.PostAsync($"/v1/fleets/{fleetId}/validate", ManagerTestHost.Yaml(ValidYaml));
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        var bad = await admin.PostAsync($"/v1/fleets/{fleetId}/validate", ManagerTestHost.Yaml(InlineSecretYaml));
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.AreEqual(0, host.Store.ListRevisions(fleetId).Count);
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-store-{Guid.NewGuid():N}.db");
}
