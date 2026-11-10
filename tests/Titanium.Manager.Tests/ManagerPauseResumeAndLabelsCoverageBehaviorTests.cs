using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;

namespace Titanium.Manager.Tests;

/// <summary>
///     Manager leftovers toward 90%: Pause/Resume wave lifecycle, Resume when not paused,
///     LabelsMatch on malformed agent JSON, and ContentHash stability.
/// </summary>
[TestClass]
public class ManagerPauseResumeAndLabelsCoverageBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-pause-{Guid.NewGuid():N}.db");

    private const string Yaml = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    [TestMethod]
    public void PauseResume_WaveRollout_AndResumeWhenRunningThrows()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("edge");
        Assert.IsNull(store.GetPublished(fleet.Id));

        var first = store.CreateRevision(fleet.Id, Yaml, "a");
        store.Publish(fleet.Id, first.Id);
        var second = store.CreateRevision(fleet.Id, Yaml + "\n#2\n", "a");
        var rollout = store.Publish(
            fleet.Id,
            second.Id,
            [new PublishWaveSpec(new Dictionary<string, string> { ["tier"] = "edge" }, 0, 30)]);
        Assert.AreEqual("running", rollout.State, ignoreCase: true);

        Assert.ThrowsExactly<InvalidOperationException>(() => store.ResumeRollout(fleet.Id, rollout.Id));

        var paused = store.PauseRollout(fleet.Id, rollout.Id, "hold");
        Assert.AreEqual("paused", paused.State, ignoreCase: true);
        StringAssert.Contains(paused.PauseReason ?? "", "hold");

        var resumed = store.ResumeRollout(fleet.Id, rollout.Id);
        Assert.AreEqual("running", resumed.State, ignoreCase: true);

        Assert.IsNotNull(store.GetPublished(fleet.Id));
        Assert.AreEqual(second.Id, store.GetPublished(fleet.Id)!.RevisionId);
    }

    [TestMethod]
    public void LabelsMatch_AndContentHash_CoverMalformedAgentJson()
    {
        Assert.IsTrue(ManagerStore.LabelsMatch("not-json", new Dictionary<string, string>()));
        Assert.IsFalse(ManagerStore.LabelsMatch(
            "not-json",
            new Dictionary<string, string> { ["env"] = "prod" }));
        // Non-object selector JSON parses as empty → matches any agent labels.
        Assert.IsTrue(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", """["x"]"""));
        Assert.IsTrue(ManagerStore.LabelsMatchJson("""{"env":"prod","tier":"a"}""", """{"env":"prod"}"""));
        Assert.IsFalse(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", """{"env":"staging"}"""));

        var hash = ManagerStore.ContentHash(Yaml);
        Assert.AreEqual(64, hash.Length);
        Assert.AreEqual(hash, ManagerStore.ContentHash(Yaml));
        Assert.AreNotEqual(hash, ManagerStore.ContentHash(Yaml + "\n"));
    }
}
