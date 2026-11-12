using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;
using Titanium.Manager.Auth;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     Manager leftovers: YamlDraft pathPrefix / cluster-only upsert, TokenHasher malformed hashes,
///     YamlDiff deletion-heavy edits, and viewer UI draft forbid.
/// </summary>
[TestClass]
public class ManagerYamlDraftTokenAndUiCoverageBehaviorTests
{
    [TestMethod]
    public void TokenHasher_Matches_RejectsMalformedAndWrongLengthHashes()
    {
        // Bug class: Convert.FromHexString / FixedTimeEquals throw and take down login / API auth.
        Assert.IsFalse(TokenHasher.Matches("secret", "not-hex"));
        Assert.IsFalse(TokenHasher.Matches("secret", "zz"));
        Assert.IsFalse(TokenHasher.Matches("secret", "abcd"));
        Assert.IsFalse(TokenHasher.Matches("secret", "DEADBEEF"));
        var good = TokenHasher.Hash("secret");
        Assert.IsTrue(TokenHasher.Matches("secret", good));
        Assert.IsTrue(TokenHasher.Matches("secret", good.ToLowerInvariant()));
        Assert.IsFalse(TokenHasher.Matches("secret", good[..^2]));
    }

    [TestMethod]
    public void YamlDiff_DeletionHeavyAndIdenticalInputs()
    {
        var identical = YamlDiff.Unified("a\nb\n", "a\nb\n", "from", "to");
        StringAssert.Contains(identical, " a\n");
        StringAssert.Contains(identical, " b\n");
        Assert.IsFalse(identical.Split('\n').Any(static l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal)));

        var deleted = YamlDiff.Unified("a\nb\nc\nd\n", "a\n", "old", "new");
        StringAssert.Contains(deleted, "-b\n");
        StringAssert.Contains(deleted, "-c\n");
        StringAssert.Contains(deleted, "-d\n");
        StringAssert.Contains(deleted, " a\n");
    }

    [TestMethod]
    public void YamlDraft_Summarize_ReadsPathPrefix_AndUpsertKeepsClusterOnlyUpdate()
    {
        var yaml = """
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
        var summary = ManagerUiYamlDraft.Summarize(yaml);
        Assert.AreEqual(1, summary.Routes.Count);
        Assert.AreEqual("/v1", summary.Routes[0].Path);
        Assert.AreEqual("backend", summary.Routes[0].ClusterId);

        var clusterOnly = ManagerUiYamlDraft.UpsertRoute(yaml, "api", path: null, clusterId: "backend-2");
        var after = ManagerUiYamlDraft.Summarize(clusterOnly);
        Assert.AreEqual(1, after.Routes.Count);
        Assert.AreEqual("/v1", after.Routes[0].Path);
        Assert.AreEqual("backend-2", after.Routes[0].ClusterId);
    }

    [TestMethod]
    public async Task Ui_ViewerSession_CannotPatchDraftOrCreateFleet()
    {
        await using var host = await ManagerTestHost.StartAsync();
        var id = host.Sessions.Create(AdminRole.Viewer, "ui:viewer", out var session);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(host.BaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie",
            $"{ManagerUiHost.SessionCookie}={id}");
        client.DefaultRequestHeaders.TryAddWithoutValidation(ManagerUiHost.CsrfHeader, session.CsrfToken);

        using var patch = new StringContent(
            """{"op":"upsert-route","id":"api","path":"/v1","clusterId":"c","yaml":"listeners:\n  - port: 8000\n    type: explicit\n"}""",
            Encoding.UTF8,
            "application/json");
        var patchResponse = await client.PostAsync("/ui/draft/patch", patch);
        Assert.AreEqual(HttpStatusCode.Forbidden, patchResponse.StatusCode);

        using var fleet = new StringContent("""{"name":"nope"}""", Encoding.UTF8, "application/json");
        var fleetResponse = await client.PostAsync("/ui/fleets", fleet);
        Assert.AreEqual(HttpStatusCode.Forbidden, fleetResponse.StatusCode);
    }
}
