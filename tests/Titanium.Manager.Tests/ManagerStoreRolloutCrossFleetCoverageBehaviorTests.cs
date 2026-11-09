using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;

namespace Titanium.Manager.Tests;

/// <summary>
///     Remaining ManagerStore KeyNotFound / blank-token / cross-fleet rollout edges still listed
///     uncovered on the Sonar PR measure for ManagerStore.cs.
/// </summary>
[TestClass]
public class ManagerStoreRolloutCrossFleetCoverageBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-cross-{Guid.NewGuid():N}.db");

    private const string Yaml = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    private const string Yaml2 = """
        listeners:
          - port: 8000
            type: explicit
        routes:
          - id: api
            match:
              pathPrefix: /v1
            clusterId: backend
        clusters:
          - id: backend
            destinations:
              - address: 10.0.0.1
                port: 80
        """;

    [TestMethod]
    public void BlankTokens_MissingFleet_AndCrossFleetRollout_ThrowOrNull()
    {
        using var store = new ManagerStore(TempDb());
        Assert.IsNull(store.FindAdminByToken(""));
        Assert.IsNull(store.FindAgentByToken(""));
        Assert.IsNull(store.FindAdminByToken(string.Empty));
        Assert.IsNull(store.FindAgentByToken(string.Empty));

        Assert.ThrowsExactly<KeyNotFoundException>(() => store.CreateRevision(42, Yaml, "a"));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.EnsureAgent(42, "n"));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.CreateAgentToken(42, "t"));

        var a = store.CreateFleet("a");
        var b = store.CreateFleet("b");
        var rev = store.CreateRevision(a.Id, Yaml, "author");
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.Publish(b.Id, rev.Id));

        store.Publish(a.Id, rev.Id);
        var next = store.CreateRevision(a.Id, Yaml2, "author", parentId: rev.Id);
        var rollout = store.Publish(
            a.Id,
            next.Id,
            [new PublishWaveSpec(new Dictionary<string, string> { ["wave"] = "1" }, 0, 0)]);
        Assert.AreEqual("running", rollout.State, ignoreCase: true);

        store.PauseRollout(a.Id, rollout.Id, "hold");
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.ResumeRollout(b.Id, rollout.Id));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.RollbackRollout(b.Id, rollout.Id));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.ResumeRollout(a.Id, 9_999));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.RollbackRollout(a.Id, 9_999));

        store.ResumeRollout(a.Id, rollout.Id);
        var rolled = store.RollbackRollout(a.Id, rollout.Id);
        Assert.AreEqual("rolled_back", rolled.State, ignoreCase: true);
        Assert.AreEqual(rolled.Id, store.RollbackRollout(a.Id, rollout.Id).Id, "idempotent rollback");
    }
}
