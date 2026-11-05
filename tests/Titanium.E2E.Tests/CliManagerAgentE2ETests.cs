using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Plus.Manager;

namespace Titanium.E2E.Tests;

[TestClass]
[DoNotParallelize]
public class CliManagerAgentE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-mgr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(180_000)]
    public async Task Run_ManagerPublish_AgentAcksDesiredRevision()
    {
        await using var manager = await ManagerE2EHost.StartAsync();
        using var admin = manager.CreateAdminClient();

        using var fleetDoc = await ManagerE2EHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerE2EHost.Json(new { name = "e2e-fleet" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();

        // Published YAML must reload cleanly in place of the agent node's boot config.
        var publishedYaml = $$"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {{listen}}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {{origin.Port}}
            plus:
              enabled: true
              controlPlane:
                host: "127.0.0.1"
                port: {{control}}
                sharedSecretEnv: TITANIUM_E2E_MGR_CP_SECRET
              options:
                manager.url: "{{manager.BaseUrl.TrimEnd('/')}}"
                manager.tokenFile: "{{Path.Combine(_tempDir, "agent.token").Replace("\\", "/")}}"
                manager.fleet: "{{fleetId}}"
                manager.mode: "managed"
                manager.stateDir: "{{Path.Combine(_tempDir, "state").Replace("\\", "/")}}"
                manager.nodeId: "e2e-cli-node"
                manager.applyDebounceMs: "50"
            """;

        using var revDoc = await ManagerE2EHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerE2EHost.Yaml(publishedYaml)));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();
        var hash = revDoc.RootElement.GetProperty("contentHash").GetString()!;

        Assert.AreEqual(HttpStatusCode.OK, (await admin.PostAsync(
            $"/v1/fleets/{fleetId}/revisions/{revId}/publish", new StringContent(""))).StatusCode);

        using var tokenDoc = await ManagerE2EHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerE2EHost.Json(new { fleetId, name = "e2e-node" })));
        var agentToken = tokenDoc.RootElement.GetProperty("token").GetString()!;

        var tokenFile = Path.Combine(_tempDir, "agent.token");
        await File.WriteAllTextAsync(tokenFile, agentToken);
        var stateDir = Path.Combine(_tempDir, "state");
        Directory.CreateDirectory(stateDir);

        var cfg = WriteManagerAgentConfig(
            _tempDir, listen, origin.Port, control, manager.BaseUrl, fleetId, tokenFile, stateDir);

        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
            ["TITANIUM_E2E_MGR_CP_SECRET"] = "mgr-agent-e2e",
        });

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            string? lastAgents = null;
            while (DateTime.UtcNow < deadline)
            {
                using var agents = await admin.GetAsync($"/v1/fleets/{fleetId}/agents");
                if (agents.StatusCode == HttpStatusCode.OK)
                {
                    lastAgents = await agents.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(lastAgents) ? "[]" : lastAgents);
                    if (doc.RootElement.GetArrayLength() >= 1)
                    {
                        var agent = doc.RootElement[0];
                        var status = agent.GetProperty("status").GetString();
                        var configHash = agent.GetProperty("configHash").GetString();
                        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(configHash, hash, StringComparison.Ordinal))
                        {
                            Assert.IsTrue(ManagerAgent.TryReadLastKnownGood(stateDir, out var lkg, out var lkgHash));
                            // LKG hash is the CLI reload hash (AppliedHash), which may differ from the manager content hash.
                            Assert.IsFalse(string.IsNullOrWhiteSpace(lkgHash));
                            StringAssert.Contains(lkg, "schemaVersion");
                            Assert.AreEqual(HttpStatusCode.OK,
                                (await new HttpClient { Timeout = TimeSpan.FromSeconds(10) }
                                    .GetAsync($"http://127.0.0.1:{listen}/after-apply")).StatusCode);
                            return;
                        }

                        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                        {
                            Assert.Fail(
                                "Agent nack'd apply. agents=" + lastAgents +
                                "\nstdout=" + harness.StdOut + "\nstderr=" + harness.StdErr);
                        }
                    }
                }

                await Task.Delay(500);
            }

            Assert.Fail(
                "Timed out waiting for manager agent ack / desired revision." +
                "\nagents=" + (lastAgents ?? "(none)") +
                "\nstdout=" + harness.StdOut + "\nstderr=" + harness.StdErr);
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static string WriteManagerAgentConfig(
        string dir,
        int listen,
        int origin,
        int control,
        string managerUrl,
        long fleetId,
        string tokenFile,
        string stateDir)
    {
        // Use .yaml so CliManagedConfigApplier can replace the file with manager-pushed YAML
        // without ConfigLoader treating YAML content as JSON by extension.
        var path = Path.Combine(dir, $"mgr-{listen}.yaml");
        var token = tokenFile.Replace("\\", "/");
        var state = stateDir.Replace("\\", "/");
        File.WriteAllText(path, $$"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {{listen}}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {{origin}}
            plus:
              enabled: true
              controlPlane:
                host: "127.0.0.1"
                port: {{control}}
                sharedSecret: "mgr-agent-e2e"
              options:
                manager.url: "{{managerUrl.TrimEnd('/')}}"
                manager.tokenFile: "{{token}}"
                manager.fleet: "{{fleetId}}"
                manager.mode: "managed"
                manager.stateDir: "{{state}}"
                manager.nodeId: "e2e-cli-node"
                manager.applyDebounceMs: "50"
            """);
        return path;
    }
}
