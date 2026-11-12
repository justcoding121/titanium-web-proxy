using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Auth;
using Titanium.Manager.Store;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     ManagerStore KeyNotFound / blank-token leftovers plus ManagerUiViews empty and populated
///     page branches still thin on the PR Sonar measure.
/// </summary>
[TestClass]
public class ManagerStoreNotFoundAndUiViewsCoverageBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-nf-ui-{Guid.NewGuid():N}.db");

    private const string YamlV1 = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    private const string YamlV2 = """
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
    public void Store_BlankTokens_AndMissingFleetRevisionRolloutAgent_ThrowOrNull()
    {
        using var store = new ManagerStore(TempDb());
        Assert.IsNull(store.FindAdminByToken(""));
        Assert.IsNull(store.FindAdminByToken(string.Empty));
        Assert.IsNull(store.FindAgentByToken(""));
        Assert.IsNull(store.FindAgentByToken(string.Empty));

        Assert.ThrowsExactly<KeyNotFoundException>(() => store.CreateRevision(999_001, YamlV1, "a"));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.EnsureAgent(999_002, "n1"));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.CreateAgentToken(999_003, "tok"));

        var fleetA = store.CreateFleet("a");
        var fleetB = store.CreateFleet("b");
        var rev = store.CreateRevision(fleetA.Id, YamlV1, "author");
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.Publish(fleetB.Id, rev.Id));

        store.Publish(fleetA.Id, rev.Id);
        var next = store.CreateRevision(fleetA.Id, YamlV2, "author", parentId: rev.Id);
        var rollout = store.Publish(
            fleetA.Id,
            next.Id,
            [new PublishWaveSpec(new Dictionary<string, string> { ["wave"] = "1" }, 0, 0)]);
        store.PauseRollout(fleetA.Id, rollout.Id, "hold");
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.ResumeRollout(fleetB.Id, rollout.Id));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.RollbackRollout(fleetB.Id, rollout.Id));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.ResumeRollout(fleetA.Id, 999_004));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.RollbackRollout(fleetA.Id, 999_005));
    }

    [TestMethod]
    public void UiViews_EmptyAndPopulated_RenderFleetRevisionRolloutAuditPages()
    {
        var now = DateTimeOffset.Parse("2026-03-10T12:00:00Z");
        var emptyFleets = ManagerUiViews.Fleets([], canEdit: false);
        StringAssert.Contains(emptyFleets, "No fleets yet.");
        Assert.IsFalse(emptyFleets.Contains("create-fleet", StringComparison.Ordinal));

        var fleets = ManagerUiViews.Fleets(
            [new FleetRow(7, "prod<script>", "{}", now)],
            canEdit: true);
        StringAssert.Contains(fleets, "create-fleet-form");
        StringAssert.Contains(fleets, "prod&lt;script&gt;");
        StringAssert.Contains(fleets, "fleet-link-7");

        var published = new PublishedConfig(3, "abc123", YamlV1);
        var detail = ManagerUiViews.FleetDetail(
            new FleetRow(7, "prod", "{}", now),
            published,
            [
                new AgentRow(1, 7, "node-a", "{}", now, "abc123", null, "ready", 3),
                new AgentRow(2, 7, "node-b\"x", "{}", null, "old", "{}", "ready", null),
            ]);
        StringAssert.Contains(detail, "published-revision");
        StringAssert.Contains(detail, ">3<");
        StringAssert.Contains(detail, "class=\"drift\"");
        StringAssert.Contains(detail, "node-b&quot;x");
        StringAssert.Contains(detail, "(drift)");

        var emptyDetail = ManagerUiViews.FleetDetail(
            new FleetRow(8, "empty", "{}", now),
            published: null,
            []);
        StringAssert.Contains(emptyDetail, "none");
        StringAssert.Contains(emptyDetail, "No agents reported yet.");

        var revisions = ManagerUiViews.Revisions(
            7,
            [
                new RevisionRow(1, 7, "h1", YamlV1, "alice", null, now),
                new RevisionRow(2, 7, "h2", YamlV2, "bob&", 1, now),
            ]);
        StringAssert.Contains(revisions, "diff-link-2");
        StringAssert.Contains(revisions, "bob&amp;");
        StringAssert.Contains(ManagerUiViews.Revisions(7, []), "No revisions yet.");

        var diff = ManagerUiViews.Diff(7, 1, 2, "-a\n+b<script>");
        StringAssert.Contains(diff, "Diff rev 1");
        StringAssert.Contains(diff, "+b&lt;script&gt;");

        var summary = ManagerUiYamlDraft.Summarize(YamlV2);
        var editor = ManagerUiViews.Editor(7, YamlV2, summary, canEdit: true);
        StringAssert.Contains(editor, "btn-publish");
        StringAssert.Contains(editor, "route-item-api");
        StringAssert.Contains(editor, "cluster-item-backend");
        var readonlyEditor = ManagerUiViews.Editor(7, YamlV1, ManagerUiYamlDraft.Summarize(YamlV1), canEdit: false);
        StringAssert.Contains(readonlyEditor, "readonly");
        Assert.IsFalse(readonlyEditor.Contains("btn-publish", StringComparison.Ordinal));

        var rollouts = ManagerUiViews.Rollouts(
            7,
            [new RolloutRow(9, 7, 2, 1, "paused", now, now, "hold&")]);
        StringAssert.Contains(rollouts, "rollout-link-9");
        StringAssert.Contains(rollouts, "hold&amp;");
        StringAssert.Contains(ManagerUiViews.Rollouts(7, []), "No rollouts yet.");

        var rolloutDetail = ManagerUiViews.RolloutDetail(
            7,
            new RolloutRow(9, 7, 2, 1, "running", now, now, null),
            [
                new RolloutStepRow(1, 9, 0, """{"wave":"1"}""", 1, 30, "baking", now, null),
            ],
            canEdit: true);
        StringAssert.Contains(rolloutDetail, "btn-pause-rollout");
        StringAssert.Contains(rolloutDetail, "baking");
        var emptySteps = ManagerUiViews.RolloutDetail(
            7,
            new RolloutRow(9, 7, 2, null, "succeeded", now, now, null),
            [],
            canEdit: false);
        StringAssert.Contains(emptySteps, "No steps.");
        Assert.IsFalse(emptySteps.Contains("rollout-actions", StringComparison.Ordinal));

        var audit = ManagerUiViews.Audit(
            [new AuditRow(1, now, "alice", "publish", "fleet/7", "ok", "detail<script>")]);
        StringAssert.Contains(audit, "detail&lt;script&gt;");
        StringAssert.Contains(ManagerUiViews.Audit([]), "No audit events yet.");
        Assert.AreEqual("viewer", ManagerUiViews.RoleName(AdminRole.Viewer));
    }
}
