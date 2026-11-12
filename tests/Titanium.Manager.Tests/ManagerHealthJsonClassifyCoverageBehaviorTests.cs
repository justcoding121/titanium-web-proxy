using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;

namespace Titanium.Manager.Tests;

/// <summary>
///     ManagerStore.ClassifyHealth leftovers toward a stable 90%: malformed JSON, missing status,
///     blank status string, and null JSON status do not pause a healthy→unhealthy regression.
/// </summary>
[TestClass]
public class ManagerHealthJsonClassifyCoverageBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-health-{Guid.NewGuid():N}.db");

    private const string Yaml = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    [TestMethod]
    public void UpsertAgentAck_MalformedAndBlankHealthJson_DoNotPauseAsRegression()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("edge");
        var first = store.CreateRevision(fleet.Id, Yaml, "a");
        store.Publish(fleet.Id, first.Id);
        var second = store.CreateRevision(fleet.Id, Yaml + "\n#2\n", "a");
        var rollout = store.Publish(
            fleet.Id,
            second.Id,
            [new PublishWaveSpec(new Dictionary<string, string>(), 1, 60)]);
        Assert.AreEqual("running", rollout.State, ignoreCase: true);

        store.UpsertAgentAck(
            fleet.Id,
            "n1",
            second.ContentHash,
            ok: true,
            error: null,
            healthJson: """{"status":"healthy"}""");

        // Malformed / blank / missing status classify as Unknown, so Unknown→unhealthy is not a regression.
        foreach (var health in new[] { "{not-json", """{}""", """{"status":""}""", """{"status":null}""" })
        {
            var (row, paused) = store.UpsertAgentAck(
                fleet.Id,
                "n1",
                second.ContentHash,
                ok: true,
                error: null,
                healthJson: health);
            Assert.IsFalse(paused, health);
            Assert.AreEqual("ok", row.Status);
            Assert.AreEqual(health, row.HealthJson);
        }

        Assert.AreEqual("running", store.GetRollout(fleet.Id, rollout.Id)!.State, ignoreCase: true);
    }
}
