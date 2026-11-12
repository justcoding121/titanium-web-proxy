using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     Fat ManagerStore GetConfigForAgent / UpsertAgentAck paths and YamlDraft preserve edges.
/// </summary>
[TestClass]
public class ManagerStoreAgentConfigBehaviorTests
{
    private const string YamlV1 = """
        listeners:
          - port: 8000
            type: explicit
        """;

    private const string YamlV2 = """
        listeners:
          - port: 8001
            type: explicit
        """;

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-agent-cfg-{Guid.NewGuid():N}.db");

    [TestMethod]
    public void GetConfigForAgent_UsesDesired_PreviousDuringStaged_AndPublishedFallback()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("cfg");
        var first = store.CreateRevision(fleet.Id, YamlV1, "a");
        store.Publish(fleet.Id, first.Id);
        store.EnsureAgent(fleet.Id, "late", """{"wave":"2"}""");
        store.EnsureAgent(fleet.Id, "early", """{"wave":"1"}""");

        var second = store.CreateRevision(fleet.Id, YamlV2, "a");
        store.Publish(
            fleet.Id,
            second.Id,
            [
                new PublishWaveSpec(new Dictionary<string, string> { ["wave"] = "1" }, 0, 30),
                new PublishWaveSpec(new Dictionary<string, string> { ["wave"] = "2" }, 0, 0),
            ]);

        Assert.AreEqual(second.ContentHash, store.GetConfigForAgent(fleet.Id, "early")!.ContentHash);
        Assert.AreEqual(first.ContentHash, store.GetConfigForAgent(fleet.Id, "late")!.ContentHash,
            "wave-2 agent keeps previous while wave-1 is still baking");
        Assert.AreEqual(second.ContentHash, store.GetConfigForAgent(fleet.Id, null)!.ContentHash);
        Assert.AreEqual(second.ContentHash, store.GetConfigForAgent(fleet.Id, "unknown-node")!.ContentHash);
    }

    [TestMethod]
    public void UpsertAgentAck_NackAndHealthRegression_PauseRunningRollout()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("ack");
        var first = store.CreateRevision(fleet.Id, YamlV1, "a");
        store.Publish(fleet.Id, first.Id);
        var second = store.CreateRevision(fleet.Id, YamlV2, "a");
        var rollout = store.Publish(
            fleet.Id,
            second.Id,
            [new PublishWaveSpec(new Dictionary<string, string>(), 1, 60)]);

        var (nackAgent, nackPaused) = store.UpsertAgentAck(fleet.Id, "n1", second.ContentHash, ok: false, error: "boom", healthJson: null);
        Assert.AreEqual("error", nackAgent.Status);
        Assert.IsTrue(nackPaused);
        StringAssert.StartsWith(store.GetRollout(fleet.Id, rollout.Id)!.PauseReason, "nack:");

        store.ResumeRollout(fleet.Id, rollout.Id);
        store.UpsertAgentAck(fleet.Id, "n2", second.ContentHash, ok: true, error: null, healthJson: """{"status":"healthy"}""");
        var (_, healthPaused) = store.UpsertAgentAck(
            fleet.Id,
            "n2",
            second.ContentHash,
            ok: true,
            error: null,
            healthJson: """{"status":"unhealthy"}""");
        Assert.IsTrue(healthPaused);
        StringAssert.StartsWith(store.GetRollout(fleet.Id, rollout.Id)!.PauseReason, "health:");
    }

    [TestMethod]
    public void YamlDraft_UpsertRoute_PreservesListeners_AndRemoveMissingIsNoOp()
    {
        var withRoute = ManagerUiYamlDraft.UpsertRoute(ManagerUiYamlDraft.EmptyDraft, "api", "/v1", "backend");
        StringAssert.Contains(withRoute, "port: 8000");
        StringAssert.Contains(withRoute, "type: explicit");
        Assert.AreEqual(1, ManagerUiYamlDraft.Summarize(withRoute).Routes.Count);

        var removedMissing = ManagerUiYamlDraft.RemoveRoute(withRoute, "nope");
        Assert.AreEqual(1, ManagerUiYamlDraft.Summarize(removedMissing).Routes.Count);

        var removed = ManagerUiYamlDraft.RemoveRoute(withRoute, "api");
        Assert.AreEqual(0, ManagerUiYamlDraft.Summarize(removed).Routes.Count);
        StringAssert.Contains(removed, "listeners:");
    }
}
