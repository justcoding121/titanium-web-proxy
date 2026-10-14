using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionRelationTests
{
    [TestMethod]
    public void SameUrl_AndConnection_AndReferer()
    {
        var a = Row(1, "https://ex/a", connection: 7, referer: "https://ex/page");
        var b = Row(2, "https://ex/a", connection: 7, referer: "https://ex/page");
        var c = Row(3, "https://ex/c", connection: 8, referer: "https://ex/other");
        var rows = new List<SessionSnapshot> { a, b, c };

        var url = SessionRelations.SameUrl(rows, a);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, url.Ids);
        Assert.IsFalse(url.Capped);

        var connection = SessionRelations.SameConnection(rows, a);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, connection.Ids);
        Assert.AreEqual(0, SessionRelations.SameConnection(rows, Row(9, "https://ex/a", connection: 0)).Ids.Length);

        var referer = SessionRelations.SameReferer(rows, a);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, referer.Ids);
        Assert.AreEqual(0, SessionRelations.SameReferer(rows, c).Ids.Length);
        Assert.AreEqual(0, SessionRelations.SameUrl(rows, Row(4, "")).Ids.Length);
    }

    [TestMethod]
    public void Redirect_FollowsLocation_AndStopsAtTwentyHops()
    {
        var rows = new List<SessionSnapshot>();
        for (var i = 1; i <= 22; i++)
        {
            rows.Add(new SessionSnapshot
            {
                Id = i,
                Url = "https://ex/" + i.ToString(),
                ResponseHeadersText = i < 22 ? "Location: https://ex/" + (i + 1).ToString() + "\r\n" : null,
            });
        }

        var chain = SessionRelations.RedirectChain(rows, rows[0]);
        Assert.AreEqual(21, chain.Ids.Length);
        Assert.AreEqual(1, chain.Ids[0]);
        Assert.AreEqual(21, chain.Ids[^1]);

        var relative = new List<SessionSnapshot>
        {
            new() { Id = 1, Url = "https://ex/start", ResponseHeadersText = "Location: /next\r\n" },
            new() { Id = 2, Url = "https://ex/next" },
        };
        var followed = SessionRelations.RedirectChain(relative, relative[0]);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, followed.Ids);

        var back = SessionRelations.RedirectChain(relative, relative[1]);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, back.Ids);
    }

    [TestMethod]
    public void Redirect_IgnoresSessionsOutsideTheNewestWindow()
    {
        var rows = new List<SessionSnapshot>(5001);
        for (var i = 1; i <= 5001; i++)
        {
            rows.Add(new SessionSnapshot { Id = i, Url = "https://ex/" + i.ToString() });
        }

        rows[^1].ResponseHeadersText = "Location: https://ex/1\r\n";
        var missed = SessionRelations.RedirectChain(rows, rows[^1]);
        Assert.AreEqual(0, missed.Ids.Length);

        rows[^1].ResponseHeadersText = "Location: https://ex/5000\r\n";
        var hit = SessionRelations.RedirectChain(rows, rows[^1]);
        CollectionAssert.AreEqual(new long[] { 5000, 5001 }, hit.Ids);
    }

    [TestMethod]
    public void Cap_StopsAtFiveHundred()
    {
        var rows = new List<SessionSnapshot>(501);
        for (var i = 1; i <= 501; i++)
        {
            rows.Add(new SessionSnapshot { Id = i, Url = "https://ex/same", ConnectionId = 3 });
        }

        var url = SessionRelations.SameUrl(rows, rows[0]);
        Assert.IsTrue(url.Capped);
        Assert.AreEqual(SessionRelations.MaxIds, url.Ids.Length);
        Assert.AreEqual(1, url.Ids[0]);
    }

    [TestMethod]
    public void IdsToken_MatchesAParsedSet_AndClearFiltersRemovesIt()
    {
        var keep = new SessionSnapshot { Id = 2, Url = "https://ex/a" };
        var drop = new SessionSnapshot { Id = 9, Url = "https://ex/a" };
        Assert.IsTrue(SessionSearch.Matches(keep, "ids:2,8"));
        Assert.IsFalse(SessionSearch.Matches(drop, "ids:2,8"));
        Assert.IsFalse(SessionSearch.Matches(keep, "ids:abc"));
        Assert.IsFalse(SessionSearch.Matches(keep, "-ids:2,8"));
        Assert.IsTrue(SessionSearch.Matches(drop, "-ids:2"));

        var (vm, _) = NewVm();
        vm.SeedSession(keep);
        vm.SeedSession(drop);
        vm.SelectedSession = keep;
        vm.FindRelatedCommand.Execute("url");
        StringAssert.Contains(vm.SearchQuery, "ids:");
        StringAssert.Contains(vm.RelatedChipText, "2");
        Assert.AreEqual(2, vm.Sessions.Count);

        vm.ClearFiltersCommand.Execute(null);
        Assert.AreEqual("", vm.SearchQuery);
        Assert.AreEqual("", vm.RelatedChipText);
        Assert.AreEqual(2, vm.Sessions.Count);
    }

    [TestMethod]
    public void PruneRemovedIds_DropsIdsThatAreGone()
    {
        Assert.AreEqual(
            "ids:1,3 method:GET",
            SessionSearch.PruneRemovedIds("ids:1,2,3 method:GET", new HashSet<long> { 2 }));
        Assert.AreEqual("method:GET", SessionSearch.PruneRemovedIds("ids:2 method:GET", new HashSet<long> { 2 }));
        Assert.AreEqual("method:GET", SessionSearch.PruneRemovedIds("ids:1,2 method:GET", null));
        Assert.AreEqual("-ids:8", SessionSearch.PruneRemovedIds("-ids:2,8", new HashSet<long> { 2 }));
        var same = "host:ex";
        Assert.AreSame(same, SessionSearch.PruneRemovedIds(same, new HashSet<long> { 1 }));
    }

    [TestMethod]
    public void DeleteAndRetention_SyncFilterDiffAndStatistics()
    {
        var (vm, registry, _) = NewTrackedVm();
        var a = Row(1, "https://ex/a");
        var b = Row(2, "https://ex/b");
        var c = Row(3, "https://ex/c");
        vm.SeedSession(a);
        vm.SeedSession(b);
        vm.SeedSession(c);

        vm.SetSelectedSessions([a, b]);
        vm.DiffSessionsCommand.Execute(null);
        Assert.IsFalse(string.IsNullOrEmpty(vm.SessionDiffText));

        vm.SetSelectedSessions([c]);
        vm.RemoveSelectedSessionsCommand.Execute(null);
        Assert.IsFalse(string.IsNullOrEmpty(vm.SessionDiffText));
        Assert.IsNull(registry.TryGet(3));

        vm.SearchQuery = "ids:1,2 method:GET";
        vm.SetSelectedSessions([a]);
        vm.RemoveSelectedSessionsCommand.Execute(null);
        Assert.AreEqual("", vm.SessionDiffText);
        Assert.AreEqual("ids:2 method:GET", vm.SearchQuery);
        StringAssert.Contains(vm.RelatedChipText, "1");
        Assert.IsNull(registry.TryGet(1));

        vm.ClearSessionsCommand.Execute(null);
        Assert.AreEqual("method:GET", vm.SearchQuery);
        Assert.AreEqual("", vm.RelatedChipText);
        Assert.AreEqual(0, vm.Sessions.Count);
        Assert.AreEqual(0, registry.Store.Count);

        var (limited, limitedRegistry, _) = NewTrackedVm(maxSessions: 3);
        var first = Row(1, "https://ex/1");
        var second = Row(2, "https://ex/2");
        var third = Row(3, "https://ex/3");
        limited.SeedSession(first);
        limited.SeedSession(second);
        limited.SeedSession(third);
        limited.SelectedInspectTabIndex = (int)InspectTab.Statistics;
        limited.SetSelectedSessions([first, second, third]);
        limited.SelectedSession = third;
        limited.SearchQuery = "ids:1,2,3";
        StringAssert.Contains(limited.StatisticsSummary, "Sessions: 3");

        limited.SeedSession(Row(4, "https://ex/4"));
        Assert.IsNull(limitedRegistry.TryGet(1));
        Assert.IsNotNull(limitedRegistry.TryGet(4));
        Assert.AreEqual(3, limitedRegistry.Store.Count);
        StringAssert.Contains(limited.StatisticsSummary, "Sessions: 2");
        Assert.AreEqual("ids:2,3", limited.SearchQuery);
        Assert.AreEqual(2, limited.Sessions.Count);
        Assert.IsFalse(limited.Sessions.Any(s => s.Id is 1 or 4));
    }

    [TestMethod]
    public void EmptyRelation_DoesNotChangeTheQuery()
    {
        var (vm, _) = NewVm();
        var only = new SessionSnapshot { Id = 1, Url = "https://ex/only", ConnectionId = 0 };
        vm.SeedSession(only);
        vm.SelectedSession = only;
        vm.SearchQuery = "host:ex";
        vm.FindRelatedCommand.Execute("url");
        Assert.AreEqual("host:ex", vm.SearchQuery);
        vm.FindRelatedCommand.Execute("connection");
        Assert.AreEqual("host:ex", vm.SearchQuery);
    }

    [TestMethod]
    public async Task Import_DropsConnectionId()
    {
        var snap = new SessionSnapshot
        {
            Id = 5,
            Url = "https://ex/a",
            Method = "GET",
            ConnectionId = 42,
            StatusCode = 200,
        };
        var path = Path.Combine(Path.GetTempPath(), "ti-rel-" + Guid.NewGuid().ToString("N") + ".twparchive");
        await SessionArchive.ExportNativeArchiveAsync([snap], path);
        var imported = await SessionArchive.ImportNativeArchiveAsync(path);
        Assert.AreEqual(0, imported[0].ConnectionId);

        using var har = new MemoryStream();
        SessionArchive.WriteHarDocument(har, snap);
        har.Position = 0;
        using var doc = JsonDocument.Parse(har);
        var fromHar = SessionArchive.ParseHarDocument(doc.RootElement, 1);
        Assert.AreEqual(0, fromHar[0].ConnectionId);
        Assert.IsFalse(EncodingUtf8(har).Contains("ConnectionId", StringComparison.Ordinal));
    }

    private static string EncodingUtf8(MemoryStream har)
    {
        har.Position = 0;
        return System.Text.Encoding.UTF8.GetString(har.ToArray());
    }

    private static SessionSnapshot Row(long id, string url, long connection = 0, string? referer = null) =>
        new()
        {
            Id = id,
            Url = url,
            ConnectionId = connection,
            RequestHeadersText = referer is null ? null : "Referer: " + referer + "\r\n",
        };

    private static (MainWindowViewModel Vm, ScriptedInspectorPathPicker Picker) NewVm()
    {
        var (vm, _, picker) = NewTrackedVm();
        return (vm, picker);
    }

    private static (MainWindowViewModel Vm, SessionRegistry Registry, ScriptedInspectorPathPicker Picker) NewTrackedVm(
        int maxSessions = 10_000)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-rel-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var picker = new ScriptedInspectorPathPicker();
        var registry = new SessionRegistry(new SessionStoreOptions
        {
            SpillBodiesToDisk = false,
            MaxSessionsInMemory = maxSessions,
        });
        var vm = new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            new ScriptedInspectorDialogs(),
            picker));
        return (vm, registry, picker);
    }
}
