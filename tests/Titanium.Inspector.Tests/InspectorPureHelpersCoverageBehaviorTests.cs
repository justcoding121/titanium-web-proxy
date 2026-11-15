using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe Inspector leftovers: ExclusionPreview wording, HostListFormat edges,
///     SessionRelations empty seeds, SessionDiff null bodies, display format grid/size edges,
///     and InterceptionService property defaults.
/// </summary>
[TestClass]
public class InspectorPureHelpersCoverageBehaviorTests
{
    private static readonly string[] ExpectedParsedHosts = ["a.example", "b.example"];

    [TestMethod]
    public void ExclusionPreview_AndHostListFormat_CoverRemainingBranches()
    {
        Assert.AreEqual("", ExclusionPreview.DescribeOpaqueReason((OpaqueTunnelReason)999));
        Assert.AreEqual(
            "Encrypted: Decrypt HTTPS is off",
            ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.DecryptOff));
        Assert.AreEqual(
            "Encrypted: not on decrypt-only allowlist",
            ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.UserOnlyList));

        var settings = new InspectorSettings
        {
            SystemProxyBypassHosts = [" ", "os.example"],
            DecryptSkipHosts = ["tunnel.example", ""],
        };
        StringAssert.Contains(ExclusionPreview.ExclusionSummary(settings, learnedCount: 2), "OS bypass");
        StringAssert.Contains(ExclusionPreview.ExclusionSummary(settings, learnedCount: 2), "Learned: 2");
        Assert.AreEqual("", ExclusionPreview.ExclusionSummary(new InspectorSettings(), learnedCount: 0));

        Assert.AreEqual("", HostListFormat.Join([]));
        Assert.AreEqual("", HostListFormat.Join(["", "  "]));
        CollectionAssert.AreEqual(
            ExpectedParsedHosts,
            HostListFormat.Parse("a.example\r\nb.example\n# skip\nA.example"));
    }

    [TestMethod]
    public void SessionRelations_EmptySeeds_AndSessionDiff_NullBodies()
    {
        var rows = new List<SessionSnapshot>
        {
            new() { Id = 1, Url = "https://a/", ConnectionId = 7, RequestHeadersText = "Referer: https://from/\n" },
            new() { Id = 2, Url = "https://a/", ConnectionId = 7, RequestHeadersText = "Referer: https://from/\n" },
        };

        Assert.AreEqual(0, SessionRelations.SameUrl(rows, new SessionSnapshot { Id = 9, Url = "   " }).Ids.Length);
        Assert.AreEqual(0, SessionRelations.SameConnection(rows, new SessionSnapshot { Id = 9, ConnectionId = 0 }).Ids.Length);
        Assert.AreEqual(0, SessionRelations.SameReferer(rows, new SessionSnapshot { Id = 9, RequestHeadersText = "" }).Ids.Length);

        var sameUrl = SessionRelations.SameUrl(rows, rows[0]);
        CollectionAssert.Contains(sameUrl.Ids, 2L);

        var a = new SessionSnapshot { Id = 1, RequestBodyBytes = null, ResponseBodyBytes = null, RequestHeadersText = "A: 1" };
        var b = new SessionSnapshot
        {
            Id = 2,
            RequestBodyBytes = null,
            ResponseBodyBytes = Encoding.UTF8.GetBytes("x"),
            RequestHeadersText = "A: 2",
        };
        var diff = SessionDiff.Compare(a, b);
        Assert.IsTrue(diff.HasDifferences);
        StringAssert.Contains(diff.Text, "Request headers");
        StringAssert.Contains(diff.Text, "Response body");
    }

    [TestMethod]
    public void InterceptionService_PropertyDefaults_AndBypassHelpers()
    {
        using var service = new InterceptionService();
        Assert.IsFalse(service.IsRunning);
        Assert.IsTrue(service.Capturing);
        Assert.IsFalse(service.DecryptHttps);
        Assert.IsTrue(service.EnableDecryptFailureBypass);
        Assert.IsTrue(service.PreferHttp2);
        Assert.IsTrue(service.PreferHttp3);
        Assert.IsTrue(service.ReuseServerConnections);
        Assert.IsFalse(service.DisableCaching);
        Assert.IsFalse(service.ForceUncompressed);
        Assert.IsFalse(service.SystemProxyEnabled);
        Assert.AreEqual(0, service.GetDecryptFailureBypassEntries().Count);
        Assert.IsFalse(service.RemoveDecryptFailureBypass("missing.example"));
        service.ClearDecryptFailureBypass();
        service.ApplyDecryptFailureBypassSetting();
        service.ApplyHttpProtocols();
        _ = InterceptionService.IsHttp3Supported;
        _ = InterceptionService.IsFirefoxProfilePresent;
        service.EnsureShutdown();
    }

    [TestMethod]
    public void SessionDisplayFormat_GridUrlSizeAndProtocolEdges()
    {
        Assert.AreEqual("", SessionDisplayFormat.FormatUrlForGrid(null, "h"));
        Assert.AreEqual("https://x/", SessionDisplayFormat.FormatUrlForGrid("https://x/", null));
        Assert.AreEqual("/api?x=1", SessionDisplayFormat.FormatUrlForGrid("https://host.example/api?x=1", "host.example"));
        Assert.AreEqual(":3000/api", SessionDisplayFormat.FormatUrlForGrid("http://host.example:3000/api", "host.example"));
        Assert.AreEqual("", SessionDisplayFormat.FormatUrlForGrid("host.example:443", "host.example"));
        Assert.AreEqual("https://other/x", SessionDisplayFormat.FormatUrlForGrid("https://other/x", "host.example"));

        using (CulturePin.Use("en-US"))
        {
            Assert.AreEqual("", SessionDisplayFormat.FormatByteSize(null));
            Assert.AreEqual("", SessionDisplayFormat.FormatByteSize(-1));
            Assert.AreEqual("12 B", SessionDisplayFormat.FormatByteSize(12));
            Assert.AreEqual("1.5 KB", SessionDisplayFormat.FormatByteSize(1536));
            Assert.AreEqual("2.0 MB", SessionDisplayFormat.FormatByteSize(2L * 1024 * 1024));
        }

        Assert.AreEqual("h2", SessionDisplayFormat.FormatHttpProtocol(new Version(2, 0)));
        Assert.AreEqual("h1.1", SessionDisplayFormat.FormatHttpProtocol(new Version(1, 1)));
        Assert.AreEqual("?", SessionDisplayFormat.FormatHttpProtocol(null));
        Assert.AreEqual("h2 → h3", SessionDisplayFormat.FormatClientServer(new Version(2, 0), new Version(3, 0)));
        Assert.AreEqual("h2", SessionDisplayFormat.FormatClientServer(new Version(2, 0), null));

        Assert.AreEqual(HttpStatusClass.Other, SessionDisplayFormat.GetStatusClass(999));
        Assert.AreEqual(HttpStatusClass.ServerError, SessionDisplayFormat.GetStatusClass(503));
        Assert.AreEqual(HttpStatusClass.Redirection, SessionDisplayFormat.GetStatusClass(302));
    }
}
