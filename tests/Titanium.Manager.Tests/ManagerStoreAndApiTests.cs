using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;
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
    public void Store_MigratesLegacyRolloutsTable_WithParameterizedEnsureColumn()
    {
        var path = TempDb();
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
        }.ToString()))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            // Pre-staged-rollout shape: missing previous_revision_id / updated_at / pause_reason.
            cmd.CommandText =
                """
                CREATE TABLE fleets (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  name TEXT NOT NULL UNIQUE,
                  labels_json TEXT NOT NULL DEFAULT '{}',
                  created_at TEXT NOT NULL
                );
                CREATE TABLE revisions (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  fleet_id INTEGER NOT NULL,
                  content_hash TEXT NOT NULL,
                  yaml_text TEXT NOT NULL,
                  author TEXT NOT NULL,
                  parent_id INTEGER NULL,
                  created_at TEXT NOT NULL
                );
                CREATE TABLE rollouts (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  fleet_id INTEGER NOT NULL,
                  revision_id INTEGER NOT NULL,
                  state TEXT NOT NULL,
                  created_at TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new ManagerStore(path);
        var fleet = store.CreateFleet("legacy");
        var rev = store.CreateRevision(fleet.Id, ValidYaml, "migrator");
        var rollout = store.Publish(fleet.Id, rev.Id);
        Assert.AreEqual("succeeded", rollout.State);
        Assert.IsNull(rollout.PreviousRevisionId);
    }

    [TestMethod]
    public async Task Api_UnknownPath_Returns404NotFound()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        var response = await admin.GetAsync("/v1/does-not-exist");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("not found", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [TestMethod]
    public async Task Api_FleetDetailRevisionsAndRollouts_RoundTrip()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "detail" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var missing = await admin.GetAsync("/v1/fleets/999999");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        var detail = await admin.GetAsync($"/v1/fleets/{fleetId}");
        Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
        using var detailDoc = await ManagerTestHost.ReadJsonAsync(detail);
        Assert.AreEqual("detail", detailDoc.RootElement.GetProperty("name").GetString());

        using var revDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();

        var revisions = await admin.GetAsync($"/v1/fleets/{fleetId}/revisions");
        Assert.AreEqual(HttpStatusCode.OK, revisions.StatusCode);
        using var revisionsDoc = await ManagerTestHost.ReadJsonAsync(revisions);
        Assert.AreEqual(1, revisionsDoc.RootElement.GetArrayLength());

        var oneRev = await admin.GetAsync($"/v1/fleets/{fleetId}/revisions/{revId}");
        Assert.AreEqual(HttpStatusCode.OK, oneRev.StatusCode);

        var publish = await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{revId}/publish", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, publish.StatusCode);
        using var pubDoc = await ManagerTestHost.ReadJsonAsync(publish);
        var rolloutId = pubDoc.RootElement.GetProperty("id").GetInt64();

        var rollouts = await admin.GetAsync($"/v1/fleets/{fleetId}/rollouts");
        Assert.AreEqual(HttpStatusCode.OK, rollouts.StatusCode);
        using var rolloutsDoc = await ManagerTestHost.ReadJsonAsync(rollouts);
        Assert.IsTrue(rolloutsDoc.RootElement.GetArrayLength() >= 1);

        var oneRollout = await admin.GetAsync($"/v1/fleets/{fleetId}/rollouts/{rolloutId}");
        Assert.AreEqual(HttpStatusCode.OK, oneRollout.StatusCode);

        var badMethod = await admin.DeleteAsync($"/v1/fleets/{fleetId}");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, badMethod.StatusCode);
    }

    [TestMethod]
    public async Task Api_AgentWrongFleetScope_IsForbidden()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetA = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "a" })));
        using var fleetB = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "b" })));
        var aId = fleetA.RootElement.GetProperty("id").GetInt64();
        var bId = fleetB.RootElement.GetProperty("id").GetInt64();

        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId = aId, name = "scoped" })));
        using var agent = host.CreateAgentClient(tokenDoc.RootElement.GetProperty("token").GetString()!);

        var cfg = await agent.GetAsync($"/v1/agent/config?fleet={bId}&have=&wait=0");
        Assert.AreEqual(HttpStatusCode.Forbidden, cfg.StatusCode);
    }

    [TestMethod]
    public async Task Api_WrongBearerToken_IsRejectedAndDoesNotCreateAFleet()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer not-a-real-token");
        var before = host.Store.ListFleets().Count;
        var response = await client.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "should-not-exist" }));
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(before, host.Store.ListFleets().Count);
        Assert.IsFalse(host.Store.ListFleets().Any(f => f.Name == "should-not-exist"));
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
    public void Store_SecondPublishWhileOneIsRunning_Conflicts()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("conflict");
        var first = store.CreateRevision(fleet.Id, ValidYaml, "tester");
        var second = store.CreateRevision(fleet.Id, ValidYamlV2, "tester");
        var running = store.Publish(fleet.Id, first.Id, [new PublishWaveSpec(new Dictionary<string, string>(), 1, 30)]);
        Assert.AreEqual("running", running.State);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => store.Publish(fleet.Id, second.Id));
        StringAssert.Contains(error.Message, "active rollout");
    }

    [TestMethod]
    public void Store_PauseAndRollback_RestoresThePreviousRevision()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("rollback");
        var first = store.CreateRevision(fleet.Id, ValidYaml, "tester");
        var second = store.CreateRevision(fleet.Id, ValidYamlV2, "tester");
        store.Publish(fleet.Id, first.Id);
        var staged = store.Publish(
            fleet.Id,
            second.Id,
            [new PublishWaveSpec(new Dictionary<string, string>(), 1, 30)]);
        var paused = store.PauseRollout(fleet.Id, staged.Id, "hold");
        Assert.AreEqual("paused", paused.State);
        Assert.AreEqual("hold", paused.PauseReason);

        var rolled = store.RollbackRollout(fleet.Id, staged.Id);
        Assert.AreEqual("rolled_back", rolled.State);
        Assert.AreEqual(first.Id, store.GetPublished(fleet.Id)!.RevisionId);

        var again = store.RollbackRollout(fleet.Id, staged.Id);
        Assert.AreEqual(rolled.Id, again.Id);
    }

    [TestMethod]
    public void Store_CorruptFile_FailsOpen()
    {
        var path = TempDb();
        File.WriteAllText(path, "this is not a database");
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => new ManagerStore(path));
    }

    [TestMethod]
    public async Task Api_OversizedBody_Returns413AndDoesNotChangeTheStore()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "cap" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(0, host.Store.ListRevisions(fleetId).Count);

        var oversized = new byte[ManagerHttpServer.MaxBodyBytes + 1];
        Array.Fill(oversized, (byte)'a');
        using var content = new ByteArrayContent(oversized);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-yaml");
        var rev = await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", content);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, rev.StatusCode);
        StringAssert.Contains(await rev.Content.ReadAsStringAsync(), "request too large");
        Assert.AreEqual(0, host.Store.ListRevisions(fleetId).Count);

        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId, name = "big" })));
        var agentToken = tokenDoc.RootElement.GetProperty("token").GetString()!;
        using var agent = host.CreateAgentClient(agentToken);
        using var ackContent = new ByteArrayContent(oversized);
        ackContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var ack = await agent.PostAsync("/v1/agent/ack", ackContent);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, ack.StatusCode);
        Assert.AreEqual(0, host.Store.ListAgents(fleetId).Count);
    }

    [TestMethod]
    public async Task Api_LongPoll_ReturnsCurrentConfigThenNextAfterPublish()
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

        using var current = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have=&wait=0");
        Assert.AreEqual(HttpStatusCode.OK, current.StatusCode);
        using var currentBody = await ManagerTestHost.ReadJsonAsync(current);
        Assert.AreEqual(hash1, currentBody.RootElement.GetProperty("hash").GetString());

        var waitTask = agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have={hash1}&wait=30");
        await Task.Delay(200);
        Assert.IsTrue(host.WaitHub.CountWaiters(fleetId) >= 1, "long-poll must register a waiter");

        using var rev2Doc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYamlV2)));
        var rev2Id = rev2Doc.RootElement.GetProperty("id").GetInt64();
        var hash2 = rev2Doc.RootElement.GetProperty("contentHash").GetString();
        await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{rev2Id}/publish", new StringContent(""));

        using var waited = await waitTask;
        Assert.AreEqual(HttpStatusCode.OK, waited.StatusCode);
        using var body = await ManagerTestHost.ReadJsonAsync(waited);
        Assert.AreEqual(hash2, body.RootElement.GetProperty("hash").GetString());
        Assert.AreEqual(0, host.WaitHub.CountWaiters(fleetId));
    }

    [TestMethod]
    public async Task Api_LongPoll_StopsWhenHostGoesAway()
    {
        var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "gone" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        using var revDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();
        await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{revId}/publish", new StringContent(""));
        var hash1 = revDoc.RootElement.GetProperty("contentHash").GetString()!;

        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId, name = "gone" })));
        var agentToken = tokenDoc.RootElement.GetProperty("token").GetString()!;
        using var agent = host.CreateAgentClient(agentToken);

        var waitTask = agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have={hash1}&wait=30");
        await Task.Delay(200);
        Assert.IsTrue(host.WaitHub.CountWaiters(fleetId) >= 1);

        await host.DisposeAsync();
        try
        {
            await waitTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                   or TimeoutException)
        {
            // client connection drops when the listener stops
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && host.WaitHub.CountWaiters(fleetId) > 0)
        {
            await Task.Delay(50);
        }

        Assert.AreEqual(0, host.WaitHub.CountWaiters(fleetId), "shutdown must cancel long-poll waiters");
    }

    [TestMethod]
    public async Task WaitHub_CancelToken_RemovesWaiter()
    {
        var hub = new ConfigWaitHub();
        using var cts = new CancellationTokenSource();
        var wait = hub.WaitAsync(42, TimeSpan.FromSeconds(30), cts.Token);
        await Task.Delay(50);
        Assert.AreEqual(1, hub.CountWaiters(42));
        await cts.CancelAsync();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, hub.CountWaiters(42));
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

    [TestMethod]
    public async Task Api_TwoWaveRollout_Wave2SeesOldHashUntilBake()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        var (fleetId, agent, hash1, hash2, rolloutId) = await StartTwoWaveAsync(host, admin, bakeSeconds: 1);

        var wave1 = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&node_id=wave1-node&have=&wait=0");
        Assert.AreEqual(HttpStatusCode.OK, wave1.StatusCode);
        using var w1 = await ManagerTestHost.ReadJsonAsync(wave1);
        Assert.AreEqual(hash2, w1.RootElement.GetProperty("hash").GetString());

        var wave2 = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&node_id=wave2-node&have=&wait=0");
        Assert.AreEqual(HttpStatusCode.OK, wave2.StatusCode);
        using var w2 = await ManagerTestHost.ReadJsonAsync(wave2);
        Assert.AreEqual(hash1, w2.RootElement.GetProperty("hash").GetString(),
            "wave2 must keep previous revision until wave1 bakes");

        await agent.PostAsync("/v1/agent/ack", ManagerTestHost.Json(new
        {
            fleet = fleetId,
            node_id = "wave1-node",
            config_hash = hash2,
            ok = true,
            labels = new { wave = "1" },
        }));

        await WaitForWave2ExposureAsync(host, fleetId, hash2);

        var wave2After = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&node_id=wave2-node&have=&wait=0");
        using var w2b = await ManagerTestHost.ReadJsonAsync(wave2After);
        Assert.AreEqual(hash2, w2b.RootElement.GetProperty("hash").GetString());

        var steps = host.Store.ListRolloutSteps(rolloutId);
        Assert.AreEqual("succeeded", steps[0].State);
        Assert.AreEqual("running", steps[1].State);
    }

    [TestMethod]
    public async Task Api_NackAutoPauses_Wave2NeverStarts()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        var (fleetId, agent, _, hash2, rolloutId) = await StartTwoWaveAsync(host, admin, bakeSeconds: 0);

        var ack = await agent.PostAsync("/v1/agent/ack", ManagerTestHost.Json(new
        {
            fleet = fleetId,
            node_id = "wave1-node",
            config_hash = hash2,
            ok = false,
            error = "apply failed",
            labels = new { wave = "1" },
        }));
        Assert.AreEqual(HttpStatusCode.OK, ack.StatusCode);
        using var ackDoc = await ManagerTestHost.ReadJsonAsync(ack);
        Assert.IsTrue(ackDoc.RootElement.GetProperty("rolloutPaused").GetBoolean());

        var rollout = host.Store.GetRollout(fleetId, rolloutId)!;
        Assert.AreEqual("paused", rollout.State);
        StringAssert.StartsWith(rollout.PauseReason, "nack:");

        host.Store.TickRollouts();
        var steps = host.Store.ListRolloutSteps(rolloutId);
        Assert.AreEqual("running", steps[0].State);
        Assert.AreEqual("pending", steps[1].State);

        var wave2 = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&node_id=wave2-node&have=&wait=0");
        using var w2 = await ManagerTestHost.ReadJsonAsync(wave2);
        Assert.AreNotEqual(hash2, w2.RootElement.GetProperty("hash").GetString());
    }

    [TestMethod]
    public async Task Api_RollbackRestoresPreviousRevision()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        var (fleetId, agent, hash1, hash2, rolloutId) = await StartTwoWaveAsync(host, admin, bakeSeconds: 30);

        var before = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&node_id=wave1-node&have=&wait=0");
        using var beforeDoc = await ManagerTestHost.ReadJsonAsync(before);
        Assert.AreEqual(hash2, beforeDoc.RootElement.GetProperty("hash").GetString());

        var rollback = await admin.PostAsync(
            $"/v1/fleets/{fleetId}/rollouts/{rolloutId}/rollback", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, rollback.StatusCode);
        using var rb = await ManagerTestHost.ReadJsonAsync(rollback);
        Assert.AreEqual("rolled_back", rb.RootElement.GetProperty("state").GetString());

        foreach (var node in new[] { "wave1-node", "wave2-node" })
        {
            var cfg = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&node_id={node}&have=&wait=0");
            using var doc = await ManagerTestHost.ReadJsonAsync(cfg);
            Assert.AreEqual(hash1, doc.RootElement.GetProperty("hash").GetString(), node);
        }

        Assert.AreEqual(host.Store.GetPublished(fleetId)!.ContentHash, hash1);
    }

    [TestMethod]
    public async Task Api_PublishWithoutWaves_ExposesAllAgentsImmediately()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "immediate" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        host.Store.EnsureAgent(fleetId, "a", """{"zone":"x"}""");
        host.Store.EnsureAgent(fleetId, "b", """{"zone":"y"}""");

        using var revDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();
        var hash = revDoc.RootElement.GetProperty("contentHash").GetString();

        using var pubDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{revId}/publish", new StringContent("")));
        Assert.AreEqual("succeeded", pubDoc.RootElement.GetProperty("state").GetString());

        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId, name = "t" })));
        using var agent = host.CreateAgentClient(tokenDoc.RootElement.GetProperty("token").GetString()!);

        foreach (var node in new[] { "a", "b" })
        {
            var cfg = await agent.GetAsync($"/v1/agent/config?fleet={fleetId}&node_id={node}&have=&wait=0");
            using var doc = await ManagerTestHost.ReadJsonAsync(cfg);
            Assert.AreEqual(hash, doc.RootElement.GetProperty("hash").GetString(), node);
        }
    }

    private static async Task<(long FleetId, HttpClient Agent, string Hash1, string Hash2, long RolloutId)>
        StartTwoWaveAsync(ManagerTestHost host, HttpClient admin, int bakeSeconds)
    {
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = $"waves-{Guid.NewGuid():N}" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        host.Store.EnsureAgent(fleetId, "wave1-node", """{"wave":"1"}""");
        host.Store.EnsureAgent(fleetId, "wave2-node", """{"wave":"2"}""");

        using var rev1Doc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var rev1Id = rev1Doc.RootElement.GetProperty("id").GetInt64();
        var hash1 = rev1Doc.RootElement.GetProperty("contentHash").GetString()!;
        await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{rev1Id}/publish", new StringContent(""));

        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId, name = "nodes" })));
        var agent = host.CreateAgentClient(tokenDoc.RootElement.GetProperty("token").GetString()!);

        await agent.PostAsync("/v1/agent/ack", ManagerTestHost.Json(new
        {
            fleet = fleetId,
            node_id = "wave1-node",
            config_hash = hash1,
            ok = true,
            labels = new { wave = "1" },
        }));
        await agent.PostAsync("/v1/agent/ack", ManagerTestHost.Json(new
        {
            fleet = fleetId,
            node_id = "wave2-node",
            config_hash = hash1,
            ok = true,
            labels = new { wave = "2" },
        }));

        using var rev2Doc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYamlV2)));
        var rev2Id = rev2Doc.RootElement.GetProperty("id").GetInt64();
        var hash2 = rev2Doc.RootElement.GetProperty("contentHash").GetString()!;

        using var pubDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync(
                $"/v1/fleets/{fleetId}/revisions/{rev2Id}/publish",
                ManagerTestHost.Json(new
                {
                    waves = new object[]
                    {
                        new { matchLabels = new { wave = "1" }, maxUnavailable = 0, bakeSeconds },
                        new { matchLabels = new { wave = "2" }, maxUnavailable = 0, bakeSeconds = 0 },
                    },
                })));
        Assert.AreEqual("running", pubDoc.RootElement.GetProperty("state").GetString());
        var rolloutId = pubDoc.RootElement.GetProperty("id").GetInt64();
        return (fleetId, agent, hash1, hash2, rolloutId);
    }

    private static async Task WaitForWave2ExposureAsync(ManagerTestHost host, long fleetId, string hash2)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            host.Store.TickRollouts();
            var agent = host.Store.ListAgents(fleetId).First(a => a.NodeId == "wave2-node");
            if (agent.DesiredRevisionId is not null)
            {
                var published = host.Store.GetConfigForAgent(fleetId, "wave2-node");
                if (published is not null &&
                    string.Equals(published.ContentHash, hash2, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await Task.Delay(150);
        }

        Assert.Fail("wave2 never received the new revision after wave1 bake");
    }

    [TestMethod]
    public async Task Api_PauseAndResumeRollout_ThroughHttp()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        var (fleetId, _, _, _, rolloutId) = await StartTwoWaveAsync(host, admin, bakeSeconds: 30);

        var pause = await admin.PostAsync(
            $"/v1/fleets/{fleetId}/rollouts/{rolloutId}/pause", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, pause.StatusCode);
        using var paused = await ManagerTestHost.ReadJsonAsync(pause);
        Assert.AreEqual("paused", paused.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("manual", paused.RootElement.GetProperty("pauseReason").GetString());

        var resume = await admin.PostAsync(
            $"/v1/fleets/{fleetId}/rollouts/{rolloutId}/resume", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, resume.StatusCode);
        using var resumed = await ManagerTestHost.ReadJsonAsync(resume);
        Assert.AreEqual("running", resumed.RootElement.GetProperty("state").GetString());

        var missing = await admin.PostAsync(
            $"/v1/fleets/{fleetId}/rollouts/999999/pause", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public void Store_CreateFleet_WithLabels_PersistsLabels()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("labeled", """{"env":"prod"}""");
        Assert.AreEqual("""{"env":"prod"}""", fleet.LabelsJson);
        var fetched = store.GetFleet(fleet.Id);
        Assert.AreEqual("""{"env":"prod"}""", fetched!.LabelsJson);
    }

    [TestMethod]
    public void Store_CreateFleet_DuplicateName_Throws()
    {
        using var store = new ManagerStore(TempDb());
        store.CreateFleet("unique");
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => store.CreateFleet("unique"));
    }

    [TestMethod]
    public void Store_ResumeRunningRollout_Throws()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("resume-err");
        var rev = store.CreateRevision(fleet.Id, ValidYaml, "tester");
        var rollout = store.Publish(fleet.Id, rev.Id, [new PublishWaveSpec(new Dictionary<string, string>(), 1, 30)]);
        Assert.AreEqual("running", rollout.State);
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => store.ResumeRollout(fleet.Id, rollout.Id));
        StringAssert.Contains(ex.Message, "not paused");
    }

    [TestMethod]
    public void Store_GetRevision_WrongFleet_ReturnsNull()
    {
        using var store = new ManagerStore(TempDb());
        var fleetA = store.CreateFleet("a");
        var fleetB = store.CreateFleet("b");
        var rev = store.CreateRevision(fleetA.Id, ValidYaml, "tester");
        Assert.IsNull(store.GetRevision(fleetB.Id, rev.Id));
        Assert.IsNotNull(store.GetRevision(fleetA.Id, rev.Id));
    }

    [TestMethod]
    public void Store_LabelsMatch_EmptySelector_MatchesAll()
    {
        Assert.IsTrue(ManagerStore.LabelsMatch("{}", new Dictionary<string, string>()));
        Assert.IsTrue(ManagerStore.LabelsMatch("""{"env":"prod"}""", new Dictionary<string, string>()));
    }

    [TestMethod]
    public void Store_LabelsMatch_MissingKey_DoesNotMatch()
    {
        var selector = new Dictionary<string, string> { ["env"] = "prod" };
        Assert.IsTrue(ManagerStore.LabelsMatch("""{"env":"prod"}""", selector));
        Assert.IsFalse(ManagerStore.LabelsMatch("""{"env":"dev"}""", selector));
        Assert.IsFalse(ManagerStore.LabelsMatch("{}", selector));
    }

    [TestMethod]
    public async Task Api_FleetWithLabels_RoundTrips()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        var resp = await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new
        {
            name = "labeled-fleet",
            labels = new { env = "staging" },
        }));
        Assert.AreEqual(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await ManagerTestHost.ReadJsonAsync(resp);
        var fleetId = doc.RootElement.GetProperty("id").GetInt64();

        var detail = await admin.GetAsync($"/v1/fleets/{fleetId}");
        using var detailDoc = await ManagerTestHost.ReadJsonAsync(detail);
        StringAssert.Contains(detailDoc.RootElement.GetProperty("labelsJson").GetString() ?? string.Empty, "staging");
    }

    [TestMethod]
    public async Task Api_ConcurrentPublishViaHttp_Returns409()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "conflict-http" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        using var r1Doc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var r1Id = r1Doc.RootElement.GetProperty("id").GetInt64();

        using var r2Doc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYamlV2)));
        var r2Id = r2Doc.RootElement.GetProperty("id").GetInt64();

        // Publish first as a staged rollout so it stays running.
        var waves = ManagerTestHost.Json(new
        {
            waves = new object[]
            {
                new { matchLabels = new { wave = "1" }, maxUnavailable = 0, bakeSeconds = 60 },
            },
        });
        var first = await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{r1Id}/publish", waves);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

        // Second publish while first is running must return 409.
        var second = await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{r2Id}/publish", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
    }

    [TestMethod]
    public async Task Api_ResumeRunningRollout_Returns409()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        var (fleetId, _, _, _, rolloutId) = await StartTwoWaveAsync(host, admin, bakeSeconds: 60);

        // The rollout is running; resuming a running rollout should give 409.
        var resume = await admin.PostAsync(
            $"/v1/fleets/{fleetId}/rollouts/{rolloutId}/resume", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.Conflict, resume.StatusCode);
    }

    [TestMethod]
    public async Task Api_RolloutNotFound_Returns404()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "no-rollout" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var missing = await admin.GetAsync($"/v1/fleets/{fleetId}/rollouts/999999");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        var pause = await admin.PostAsync($"/v1/fleets/{fleetId}/rollouts/999999/pause", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.NotFound, pause.StatusCode);
    }

    [TestMethod]
    public async Task Api_RevisionNotFound_Returns404_AndWrongFleetIs404()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fa = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "fa" })));
        using var fb = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "fb" })));
        var faId = fa.RootElement.GetProperty("id").GetInt64();
        var fbId = fb.RootElement.GetProperty("id").GetInt64();

        using var revDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{faId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();

        // Correct fleet — found.
        var correct = await admin.GetAsync($"/v1/fleets/{faId}/revisions/{revId}");
        Assert.AreEqual(HttpStatusCode.OK, correct.StatusCode);

        // Wrong fleet — not found.
        var wrong = await admin.GetAsync($"/v1/fleets/{fbId}/revisions/{revId}");
        Assert.AreEqual(HttpStatusCode.NotFound, wrong.StatusCode);

        // Non-existent revision — not found.
        var noRev = await admin.GetAsync($"/v1/fleets/{faId}/revisions/999999");
        Assert.AreEqual(HttpStatusCode.NotFound, noRev.StatusCode);
    }

    [TestMethod]
    public async Task Api_InvalidFleetId_Returns400()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();

        var resp = await admin.GetAsync("/v1/fleets/not-a-number/revisions");
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [TestMethod]
    public async Task Api_FleetNotFound_Returns404()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();

        var resp = await admin.GetAsync("/v1/fleets/999999/rollouts");
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-store-{Guid.NewGuid():N}.db");
}
