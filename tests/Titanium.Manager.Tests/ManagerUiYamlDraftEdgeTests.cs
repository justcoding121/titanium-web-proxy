using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     YamlDraft edge coverage beyond the basic round-trip: empty input, upsert idempotency, remove-cluster,
///     and parse failures.
/// </summary>
[TestClass]
public class ManagerUiYamlDraftEdgeTests
{
    [TestMethod]
    public void Summarize_EmptyYaml_UsesEmptyDraftShape()
    {
        var summary = ManagerUiYamlDraft.Summarize("   ");
        Assert.AreEqual(0, summary.Routes.Count);
        Assert.AreEqual(0, summary.Clusters.Count);
    }

    [TestMethod]
    public void UpsertRoute_UpdatesExisting_WithoutDuplicating()
    {
        var first = ManagerUiYamlDraft.UpsertRoute(ManagerUiYamlDraft.EmptyDraft, "api", "/v1", "backend");
        var second = ManagerUiYamlDraft.UpsertRoute(first, "api", "/v2", "backend-2");
        var summary = ManagerUiYamlDraft.Summarize(second);
        Assert.AreEqual(1, summary.Routes.Count);
        Assert.AreEqual("/v2", summary.Routes[0].Path);
        Assert.AreEqual("backend-2", summary.Routes[0].ClusterId);
    }

    [TestMethod]
    public void UpsertCluster_IsIdempotent_AndRemoveCluster_DropsIt()
    {
        var with = ManagerUiYamlDraft.UpsertCluster(ManagerUiYamlDraft.EmptyDraft, "backend", "10.0.0.1", 9090);
        var again = ManagerUiYamlDraft.UpsertCluster(with, "backend", "10.0.0.2", 8080);
        Assert.AreEqual(1, ManagerUiYamlDraft.Summarize(again).Clusters.Count);
        Assert.AreEqual(1, ManagerUiYamlDraft.Summarize(again).Clusters[0].DestinationCount);

        var removed = ManagerUiYamlDraft.RemoveCluster(again, "backend");
        Assert.AreEqual(0, ManagerUiYamlDraft.Summarize(removed).Clusters.Count);
    }

    [TestMethod]
    public void Summarize_RejectsMalformedYaml()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ManagerUiYamlDraft.Summarize("listeners: ["));
        StringAssert.Contains(ex.Message, "YAML parse error");
    }

    [TestMethod]
    public void UpsertRoute_RejectsBlankId()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            ManagerUiYamlDraft.UpsertRoute(ManagerUiYamlDraft.EmptyDraft, "  ", "/", "c"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ManagerUiYamlDraft.RemoveRoute(ManagerUiYamlDraft.EmptyDraft, ""));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ManagerUiYamlDraft.UpsertCluster(ManagerUiYamlDraft.EmptyDraft, "\t"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ManagerUiYamlDraft.RemoveCluster(ManagerUiYamlDraft.EmptyDraft, " "));
    }
}
