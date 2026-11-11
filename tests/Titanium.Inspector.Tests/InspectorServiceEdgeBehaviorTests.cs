using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Pure Inspector service edges that remain thin without spinning Avalonia: status-filter matching caps,
///     opaque-reason search tokens, spreadsheet/codegen sanitizers, and run-log capacity rebuilds.
/// </summary>
[TestClass]
public class InspectorServiceEdgeBehaviorTests
{
    private static readonly string[] ExpectedRingMessages = ["a", "b", "c", "d", "e"];

    // Bug class: Matches ignores MaxChars truncation, treats "errors" differently from "error", or FreeText/HasClass
    // blow up on null/empty so the flyout and the grid disagree on the same token.
    [TestMethod]
    public void StatusFilter_MatchesCapsSynonymsAndEmptyHelpers()
    {
        Assert.IsFalse(StatusFilter.Matches(null, string.Empty));
        Assert.IsFalse(StatusFilter.Matches(200, ""));
        Assert.IsFalse(StatusFilter.HasClass(null, '2'));
        Assert.IsFalse(StatusFilter.HasClass("", '2'));
        Assert.AreEqual("", StatusFilter.FreeText(null));
        Assert.AreEqual("", StatusFilter.FreeText("2xx,3xx"));

        Assert.IsTrue(StatusFilter.Matches(503, "ERRORS"));
        Assert.IsTrue(StatusFilter.Matches(404, "error"));
        Assert.IsFalse(StatusFilter.Matches(200, "errors"));
        Assert.IsTrue(StatusFilter.Matches(101, "1xx"));
        Assert.IsFalse(StatusFilter.Matches(200, "1xx"));

        // Past MaxChars the matcher must still honor the leading terms (same as TermCount/FreeText).
        var leading = "404," + new string('x', StatusFilter.MaxChars);
        Assert.IsTrue(StatusFilter.Matches(404, leading));
        Assert.IsFalse(StatusFilter.Matches(500, leading));

        Assert.IsTrue(StatusFilter.TryCompose(
            true, true, true, true, "2XX 304  200-299", out var composed, out var err));
        StringAssert.Contains(composed, "2xx");
        StringAssert.Contains(composed, "3xx");
        StringAssert.Contains(composed, "4xx");
        StringAssert.Contains(composed, "5xx");
        StringAssert.Contains(composed, "304");
        StringAssert.Contains(composed, "200-299");
        Assert.IsNull(err);
    }

    // Bug class: is:opaque-reason:* aliases never match (or only match the enum name), so operators cannot filter
    // decrypt-off / learned / built-in tunnels from the search box.
    [TestMethod]
    public void SessionSearch_OpaqueReasonTokens_MatchAliases()
    {
        var sessions = new[]
        {
            new SessionSnapshot
            {
                Id = 1,
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.DecryptOff,
                Url = "https://a/",
            },
            new SessionSnapshot
            {
                Id = 2,
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.LearnedFailure,
                Url = "https://b/",
            },
            new SessionSnapshot
            {
                Id = 3,
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.BuiltInIdentity,
                Url = "https://c/",
            },
            new SessionSnapshot
            {
                Id = 4,
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.BuiltInPinning,
                Url = "https://d/",
            },
            new SessionSnapshot
            {
                Id = 5,
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.UserSkipList,
                Url = "https://e/",
            },
            new SessionSnapshot
            {
                Id = 6,
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.UserOnlyList,
                Url = "https://f/",
            },
            new SessionSnapshot
            {
                Id = 7,
                IsTunnel = false,
                OpaqueReason = OpaqueTunnelReason.DecryptOff,
                Url = "https://g/",
            },
        };

        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:decrypt-off").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:decryptoff").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:learned").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:auto").Count());
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "is:opaque-reason:builtin").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:identity").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:pinning").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:skip").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:opaque-reason:onlylist").Count());
        Assert.IsFalse(SessionSearch.Matches(sessions[6], "is:opaque-reason:decrypt-off"));
        Assert.IsTrue(SessionSearch.Matches(sessions[0], "is:opaque-reason:DecryptOff"));
        Assert.IsTrue(SessionSearch.Matches(sessions[0], "is:opaque"));
    }

    // Bug class: formula-looking cells leak into spreadsheets, CanGenerate accepts CONNECT tunnels, or PowerShell
    // drops User-Agent onto -Headers instead of -UserAgent.
    [TestMethod]
    public void SessionRequestCodegen_SanitizersCanGenerateAndPowerShellUserAgent()
    {
        Assert.AreEqual("'=1+1", SessionRequestCodegen.SpreadsheetCell("=1+1"));
        Assert.AreEqual("'+cmd", SessionRequestCodegen.SpreadsheetCell("+cmd"));
        Assert.AreEqual("'-1", SessionRequestCodegen.SpreadsheetCell("-1"));
        Assert.AreEqual("'@x", SessionRequestCodegen.SpreadsheetCell("@x"));
        Assert.AreEqual("safe path  ", SessionRequestCodegen.SpreadsheetCell("safe\tpath\r\n"));
        Assert.AreEqual("", SessionRequestCodegen.SpreadsheetCell(null));
        Assert.AreEqual("''", SessionRequestCodegen.ShellSingleQuote(""));
        StringAssert.Contains(SessionRequestCodegen.ShellSingleQuote("a'b"), "'\\''");

        Assert.IsFalse(SessionRequestCodegen.CanGenerate(null));
        Assert.IsFalse(SessionRequestCodegen.CanGenerate(new SessionSnapshot { Method = "CONNECT", Url = "h:443", IsTunnel = true }));
        Assert.IsTrue(SessionRequestCodegen.CanGenerate(new SessionSnapshot { Method = "GET", Url = "https://example/" }));

        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/x",
            RequestHeadersText = "User-Agent: ua-test\r\nX-Trace: 1\r\nContent-Type: text/plain\r\n",
            RequestBodyText = "hi",
        };
        var ps = SessionRequestCodegen.ToPowerShell(snap);
        StringAssert.Contains(ps, "-UserAgent 'ua-test'");
        StringAssert.Contains(ps, "-ContentType 'text/plain'");
        StringAssert.Contains(ps, "-Headers @{");
        StringAssert.Contains(ps, "'X-Trace' = '1'");
        Assert.IsFalse(ps.Contains("-Headers @{ 'User-Agent'", StringComparison.OrdinalIgnoreCase));

        var table = SessionRequestCodegen.ToTable(
            [new SessionSnapshot { Id = 1, Method = "GET", StatusCode = 200, Url = "=evil", Comment = "+boom" }],
            out var written,
            out var truncated);
        Assert.AreEqual(1, written);
        Assert.IsFalse(truncated);
        StringAssert.Contains(table, "'=evil");
        StringAssert.Contains(table, "'+boom");
    }

    // Bug class: SetCapacity no-ops when shrinking past the ring, or drops the newest lines instead of the oldest.
    // SetCapacity clamps to [100, 50_000]; the constructor does not, so rebuild tests stay in that range.
    [TestMethod]
    public async Task RunLogRing_SetCapacity_KeepsNewestAndBumpsVersion()
    {
        using var ring = new RunLogRing(200);
        var logger = ring.CreateLogger("cap");
        foreach (var word in ExpectedRingMessages)
        {
            logger.Log(LogLevel.Error, 0, word, null, static (state, _) => state);
        }

        var before = await WaitFor(ring, 5);
        CollectionAssert.AreEqual(ExpectedRingMessages, before.Select(e => e.Message).ToArray());
        var version = ring.Version;

        ring.SetCapacity(200);
        Assert.AreEqual(version, ring.Version, "same capacity must not rebuild");

        ring.SetCapacity(100);
        Assert.IsTrue(ring.Version > version);
        Assert.AreEqual(100, ring.Capacity);
        CollectionAssert.AreEqual(ExpectedRingMessages, ring.Snapshot().Select(e => e.Message).ToArray());

        // Fill past the new capacity, then shrink again so the newest lines are kept.
        for (var i = 0; i < 120; i++)
        {
            logger.Log(LogLevel.Error, 0, "n" + i, null, static (state, _) => state);
        }

        // Wait until the async writer has drained all 120 enqueues (count alone hits 100 early).
        var afterFill = await WaitForNewest(ring, "n119");
        Assert.AreEqual(100, afterFill.Count);
        ring.SetCapacity(100); // no-op
        ring.SetCapacity(101);
        Assert.AreEqual(101, ring.Capacity);
        Assert.AreEqual(100, ring.Snapshot().Count);
        Assert.AreEqual(afterFill[^1].Message, ring.Snapshot()[^1].Message);
    }

    // Bug class: TryBuild records TooSmall / Status / Head under the wrong counter, so progressive metrics look
    // healthy while bodies still go out as single buffers.
    [TestMethod]
    public void ProgressiveDelivery_FallbackCount_TracksTryBuildSkips()
    {
        var beforeTooSmall = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.TooSmall);
        var beforeStatus = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Status);
        var beforeHead = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Head);

        var tiny = new byte[512];
        Assert.IsNull(ProgressiveDelivery.TryBuild(
            new Response { StatusCode = 200, HttpVersion = new Version(1, 1) },
            new Request { Method = "GET" },
            tiny,
            tiny.Length,
            transcoded: false,
            edited: false));

        Assert.IsNull(ProgressiveDelivery.TryBuild(
            new Response { StatusCode = 204, HttpVersion = new Version(1, 1) },
            new Request { Method = "GET" },
            tiny,
            tiny.Length,
            transcoded: false,
            edited: false));

        var headBody = new byte[ProgressiveDelivery.MinWireBytes];
        Assert.IsNull(ProgressiveDelivery.TryBuild(
            new Response { StatusCode = 200, HttpVersion = new Version(1, 1) },
            new Request { Method = "HEAD" },
            headBody,
            headBody.Length,
            transcoded: false,
            edited: false));

        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.TooSmall) > beforeTooSmall);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Status) > beforeStatus);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Head) > beforeHead);
        Assert.AreEqual(0, ProgressiveDelivery.FallbackCount((ProgressiveSkipReason)999));
    }

    private static async Task<IReadOnlyList<RunLogEntry>> WaitFor(RunLogRing ring, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var snap = ring.Snapshot();
            if (snap.Count >= count)
            {
                return snap;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"expected {count} log lines, got {ring.Snapshot().Count}");
        return Array.Empty<RunLogEntry>();
    }

    private static async Task<IReadOnlyList<RunLogEntry>> WaitForNewest(RunLogRing ring, string newest)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var snap = ring.Snapshot();
            if (snap.Count > 0 && snap[^1].Message == newest)
            {
                return snap;
            }

            await Task.Delay(20);
        }

        var last = ring.Snapshot();
        Assert.Fail($"expected newest '{newest}', got '{(last.Count == 0 ? "<empty>" : last[^1].Message)}'");
        return Array.Empty<RunLogEntry>();
    }
}
