using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Pure Inspector service edges without Avalonia UI: HAR <c>_inspector</c> extensions, method-filter caps,
///     GraphQL query-document parsing, and theme/PAC helpers that must not take down the testhost.
/// </summary>
[TestClass]
public class InspectorServiceHarAndFilterBehaviorTests
{
    // Bug class: binary request bodies, upstream bytes, timing, and events are dropped from _inspector so a
    // re-imported HAR loses capture honesty and progressive timing bars.
    [TestMethod]
    public void SessionArchive_ToHarEntry_PreservesInspectorExtensionFields()
    {
        var snap = new SessionSnapshot
        {
            Id = 42,
            Method = "POST",
            Url = "https://api.example/gql?x=1",
            Host = "api.example",
            StatusCode = 200,
            Comment = "note",
            Protocol = "HTTP/2",
            ProcessId = 99,
            ProcessName = "client",
            IsGrpc = true,
            IsTranscoded = true,
            IsMultipart = true,
            IsServerSentEvents = true,
            OpaqueReason = OpaqueTunnelReason.LearnedFailure,
            IsTunnel = true,
            RequestBodyBytes = [1, 2, 3],
            ResponseBodyBytes = [9, 8],
            UpstreamRequestBodyBytes = [4, 5],
            UpstreamResponseBodyBytes = [6],
            TimingRecorded = true,
            BlockedMs = 1,
            DnsMs = 2,
            ConnectMs = 3,
            TlsMs = 4,
            SendMs = 5,
            WaitMs = 6,
            ReceiveMs = 7,
            DurationMs = 28,
            ClientMethod = "POST",
            ClientPathAndQuery = "/gql",
            UpstreamMethod = "POST",
            UpstreamPath = "/upstream",
            ProtobufDecodedText = "field: 1",
            Events =
            [
                new SessionEventNote(4, "probe", false),
                new SessionEventNote(9, "stats.event.tls", true),
            ],
        };

        var entry = SessionArchive.ToHarEntry(snap);
        Assert.AreEqual("note", entry["comment"]!.GetValue<string>());
        var inspector = entry["_inspector"]!.AsObject();
        Assert.AreEqual(42, inspector["id"]!.GetValue<long>());
        Assert.AreEqual("HTTP/2", inspector["protocol"]!.GetValue<string>());
        Assert.IsTrue(inspector["isGrpc"]!.GetValue<bool>());
        Assert.IsTrue(inspector["isTranscoded"]!.GetValue<bool>());
        Assert.IsTrue(inspector["isMultipart"]!.GetValue<bool>());
        Assert.IsTrue(inspector["isServerSentEvents"]!.GetValue<bool>());
        Assert.AreEqual(nameof(OpaqueTunnelReason.LearnedFailure), inspector["opaqueReason"]!.GetValue<string>());
        Assert.AreEqual(Convert.ToBase64String([1, 2, 3]), inspector["requestBodyBase64"]!.GetValue<string>());
        Assert.AreEqual(Convert.ToBase64String([9, 8]), inspector["responseBodyBase64"]!.GetValue<string>());
        Assert.AreEqual(Convert.ToBase64String([4, 5]), inspector["upstreamRequestBodyBase64"]!.GetValue<string>());
        Assert.AreEqual(Convert.ToBase64String([6]), inspector["upstreamResponseBodyBase64"]!.GetValue<string>());
        Assert.AreEqual("field: 1", inspector["protobufDecodedText"]!.GetValue<string>());
        Assert.IsTrue(inspector["timing"]!.AsObject()["recorded"]!.GetValue<bool>());
        Assert.AreEqual(1d, inspector["timing"]!.AsObject()["blocked"]!.GetValue<double>());
        Assert.AreEqual(2, inspector["events"]!.AsArray().Count);
        Assert.AreEqual("probe", inspector["events"]![0]!["key"]!.GetValue<string>());

        var doc = SessionArchive.BuildHarDocument([snap]);
        Assert.AreEqual(1, doc["log"]!["entries"]!.AsArray().Count);
    }

    // Bug class: Matches truncates past MaxChars incorrectly, IsShown ignores -method / hide:tunnel, or
    // CollectMentioned keeps invalid tokens so the flyout drifts from the grid filter.
    [TestMethod]
    public void MethodFilter_CapsIsShownAndCollectMentioned()
    {
        Assert.IsFalse(MethodFilter.Matches(null, "GET"));
        Assert.IsFalse(MethodFilter.Matches("GET", ""));
        Assert.IsTrue(MethodFilter.Matches("GET", "get,POST"));
        Assert.IsFalse(MethodFilter.Matches("PUT", "get,POST"));

        var longList = string.Join(',', Enumerable.Range(0, 200).Select(i => "METHOD" + i));
        Assert.IsTrue(longList.Length > MethodFilter.MaxChars, longList.Length.ToString());
        Assert.IsFalse(MethodFilter.Matches("METHOD199", longList), "terms past MaxChars must not match");

        Assert.IsTrue(MethodFilter.IsShown("GET", null));
        Assert.IsTrue(MethodFilter.IsShown("GET", "host:a"));
        Assert.IsFalse(MethodFilter.IsShown("GET", "method:POST"));
        Assert.IsTrue(MethodFilter.IsShown("POST", "method:POST"));
        Assert.IsFalse(MethodFilter.IsShown("POST", "-method:POST"));
        Assert.IsFalse(MethodFilter.IsShown("CONNECT", "hide:tunnel"));
        Assert.IsFalse(MethodFilter.IsShown("CONNECT", "hide:connect"));
        Assert.IsTrue(MethodFilter.IsShown("GET", "hide:tunnel"));

        Assert.IsTrue(MethodFilter.HasPositiveMethodToken("method:GET host:a"));
        Assert.IsFalse(MethodFilter.HasPositiveMethodToken("-method:GET"));
        Assert.IsTrue(MethodFilter.HasHideTunnel("hide:tunnel"));
        Assert.IsTrue(MethodFilter.IsCommon("get"));
        Assert.IsFalse(MethodFilter.IsCommon("PROPFIND"));

        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        MethodFilter.CollectMentioned("method:get -method:ProPFind method:!!!", mentioned);
        CollectionAssert.Contains(mentioned.ToList(), "GET");
        CollectionAssert.Contains(mentioned.ToList(), "PROPFIND");
        Assert.IsFalse(mentioned.Contains("!!!"));
        Assert.IsTrue(MethodFilter.TryCanonical("patch", out var canonical));
        Assert.AreEqual("PATCH", canonical);
        Assert.IsFalse(MethodFilter.TryCanonical("too-long-" + new string('x', MethodFilter.MaxMethodLength), out _));
    }

    // Bug class: operationName missing falls back incorrectly, or mutation/subscription names are ignored so
    // auto-responder GraphQL filters never fire for those verbs.
    [TestMethod]
    public void GraphQlOperationMatcher_QueryDocumentFallbackAndEdges()
    {
        Assert.IsFalse(GraphQlOperationMatcher.TryGetOperationName(null, out _));
        Assert.IsFalse(GraphQlOperationMatcher.TryGetOperationName("{", out _));
        Assert.IsFalse(GraphQlOperationMatcher.TryGetOperationName("[]", out _));
        Assert.IsFalse(GraphQlOperationMatcher.TryGetOperationName("{\"operationName\":null}", out _));
        Assert.IsFalse(GraphQlOperationMatcher.TryGetOperationName("{\"operationName\":1}", out _));

        Assert.IsTrue(GraphQlOperationMatcher.TryGetOperationName(
            """{"query":"mutation CreateUser { create { id } }"}""",
            out var mutation));
        Assert.AreEqual("CreateUser", mutation);

        Assert.IsTrue(GraphQlOperationMatcher.TryGetOperationName(
            """{"query":"subscription OnEvent { ping }"}""",
            out var sub));
        Assert.AreEqual("OnEvent", sub);

        Assert.IsFalse(GraphQlOperationMatcher.TryGetOperationName(
            """{"query":"query { anon }"}""",
            out _));

        Assert.IsTrue(GraphQlOperationMatcher.MatchesOperation("{}", null));
        Assert.IsTrue(GraphQlOperationMatcher.MatchesOperation("{}", "  "));
        Assert.IsFalse(GraphQlOperationMatcher.MatchesOperation("{\"query\":\"query Q { a }\"}", "Other"));
        Assert.IsTrue(GraphQlOperationMatcher.MatchesOperation(
            """{"operationName":"Q"}""", "Q"));
    }

    // Bug class: PrefersDark / GTK / KDE helpers throw or disagree with env vs ini precedence, crashing theme init.
    [TestMethod]
    public void OsThemePreference_PrefersDark_HonorsEnvIniAndKde()
    {
        Assert.IsTrue(OsThemePreference.PrefersDark("GNOME", "Adwaita:dark", null, null, null));
        Assert.IsFalse(OsThemePreference.PrefersDark("GNOME", "Adwaita:light", null, null, null));
        Assert.IsTrue(OsThemePreference.PrefersDark("GNOME", "SomethingDark", null, null, null));
        Assert.IsNull(OsThemePreference.GtkThemeEnvPrefersDark(null));
        Assert.IsNull(OsThemePreference.GtkThemeEnvPrefersDark("Adwaita"));

        Assert.IsTrue(OsThemePreference.PrefersDark(
            "GNOME",
            null,
            "[Settings]\ngtk-application-prefer-dark-theme=1\n",
            null,
            null));
        Assert.IsFalse(OsThemePreference.PrefersDark(
            "GNOME",
            null,
            "[Settings]\ngtk-application-prefer-dark-theme=0\n",
            null,
            null));

        Assert.IsTrue(OsThemePreference.PrefersDark(
            "KDE",
            null,
            null,
            null,
            "[General]\nColorScheme=BreezeDark\n"));
        Assert.IsFalse(OsThemePreference.PrefersDark(
            "KDE",
            null,
            "[Settings]\ngtk-application-prefer-dark-theme=1\n",
            null,
            "[General]\nColorScheme=BreezeLight\n"),
            "KDE desktop prefers kdeglobals over GTK");

        Assert.AreEqual(Avalonia.Styling.ThemeVariant.Default, OsThemePreference.AutomaticVariant());
        _ = SystemProxyPacHelper.HasActivePacScript();
    }

    // Bug class: is:transcoded / ids: cache / FormatSince disagree with the grid so filtered counts lie.
    [TestMethod]
    public void SessionSearch_TranscodedIdsAndSinceFormatting()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, IsTranscoded = true, Url = "https://a/" },
            new SessionSnapshot { Id = 2, IsTranscoded = false, Url = "https://b/" },
            new SessionSnapshot { Id = 3, Url = "https://c/" },
        };
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "is:transcoded").Count());
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "ids:1,3,nope").Count());
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "ids:1,3").Count(), "ids cache must reuse the same set");

        var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var sameDay = SessionSearch.FormatSince(now.AddHours(-2), now);
        Assert.AreEqual(now.AddHours(-2).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture), sameDay);
        var priorDay = SessionSearch.FormatSince(now.AddDays(-2), now);
        Assert.AreEqual(
            now.AddDays(-2).ToLocalTime().ToString("MMM d, HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            priorDay);

        var text = SessionSearch.BuildSessionCountText(
            visibleCount: 1,
            totalCount: 3,
            searchQuery: "is:transcoded",
            retentionEvictedTotal: 2,
            oldestStartedUtc: now.AddHours(-1),
            nowUtc: now);
        StringAssert.Contains(text, "1 of");
        StringAssert.Contains(text, "most recent");
        StringAssert.Contains(text, "match filter");
        StringAssert.Contains(text, "since");
    }
}
