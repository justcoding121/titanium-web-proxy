using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Auth;
using Titanium.Manager.Store;

namespace Titanium.Manager.Tests;

/// <summary>
///     Manager leftovers toward 90%: blank EnsureAgent labels, revoked admin lookup,
///     numeric health status classification, and GetPublished when nothing is live.
/// </summary>
[TestClass]
public class ManagerEnsureAgentHealthAndRevokedAdminBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-ens-{Guid.NewGuid():N}.db");

    private const string Yaml = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    [TestMethod]
    public void EnsureAgent_BlankLabelsBecomeEmptyObject_AndUpdatesOnConflict()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("edge");
        var first = store.EnsureAgent(fleet.Id, "n1", "   ");
        Assert.AreEqual("{}", first.LabelsJson);

        var second = store.EnsureAgent(fleet.Id, "n1", """{"zone":"a"}""");
        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual("""{"zone":"a"}""", second.LabelsJson);
        Assert.AreEqual(1, store.ListAgents(fleet.Id).Count);
    }

    [TestMethod]
    public void FindAdminByToken_ReturnsNullAfterRevoke()
    {
        // Bug class: revoked admin rows must not authenticate even when the hash still matches.
        var path = TempDb();
        using var store = new ManagerStore(path);
        var plain = "admin-" + Guid.NewGuid().ToString("N");
        store.EnsureBootstrapAdminToken(plain);
        Assert.IsNotNull(store.FindAdminByToken(plain));

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString()))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE admin_tokens SET revoked_at = $t WHERE token_hash = $h;";
            update.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$h", TokenHasher.Hash(plain));
            Assert.AreEqual(1, update.ExecuteNonQuery());
        }

        Assert.IsNull(store.FindAdminByToken(plain));
    }

    [TestMethod]
    public void UpsertAgentAck_NumericHealthStatus_AndGetPublishedWhenEmpty()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("health");
        Assert.IsNull(store.GetPublished(fleet.Id));

        var first = store.CreateRevision(fleet.Id, Yaml, "author");
        store.Publish(fleet.Id, first.Id);
        Assert.AreEqual(first.Id, store.GetPublished(fleet.Id)!.RevisionId);

        var second = store.CreateRevision(fleet.Id, Yaml + "\n#2\n", "author");
        var rollout = store.Publish(
            fleet.Id,
            second.Id,
            [new PublishWaveSpec(new Dictionary<string, string>(), 1, 60)]);

        // Numeric status classifies as Unknown (ToString path), so unknown→unhealthy is not a regression.
        store.UpsertAgentAck(
            fleet.Id,
            "n1",
            second.ContentHash,
            ok: true,
            error: null,
            healthJson: """{"status":1}""");
        var (unknownThenBad, paused) = store.UpsertAgentAck(
            fleet.Id,
            "n1",
            second.ContentHash,
            ok: true,
            error: null,
            healthJson: """{"status":"unhealthy"}""");
        Assert.IsFalse(paused, "unknown→unhealthy is not a healthy→unhealthy regression");
        Assert.AreEqual("ok", unknownThenBad.Status);
        Assert.AreEqual("""{"status":"unhealthy"}""", unknownThenBad.HealthJson);

        store.UpsertAgentAck(
            fleet.Id,
            "n2",
            second.ContentHash,
            ok: true,
            error: null,
            healthJson: """{"status":"healthy"}""");
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
}
