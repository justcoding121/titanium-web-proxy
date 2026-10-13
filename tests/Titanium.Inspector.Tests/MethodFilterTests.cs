using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;

namespace Titanium.Inspector.Tests;

[TestClass]
public class MethodFilterTests
{
    [TestMethod]
    public void CommaList_ShowsEither_AndExclusionHidesOnlyThose()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, Method = "GET", Url = "http://a/" },
            new SessionSnapshot { Id = 2, Method = "POST", Url = "http://a/" },
            new SessionSnapshot { Id = 3, Method = "PROPFIND", Url = "http://a/" },
            new SessionSnapshot { Id = 4, Method = "CONNECT", Url = "a:443", IsTunnel = true },
        };

        CollectionAssert.AreEquivalent(new long[] { 1, 2 }, Ids(sessions, "method:GET,POST"));
        CollectionAssert.AreEquivalent(new long[] { 2, 3 }, Ids(sessions, "-method:GET,CONNECT"));
        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, Ids(sessions, "hide:tunnel"));
        Assert.IsTrue(SessionSearch.Matches(sessions[0], "method:get"));
        Assert.IsFalse(SessionSearch.Matches(sessions[3], "-method:connect"));
        Assert.IsTrue(MethodFilter.IsShown("GET", "method:GET,POST"));
        Assert.IsFalse(MethodFilter.IsShown("GET", "method:GET method:POST"));
        Assert.IsFalse(MethodFilter.IsShown("CONNECT", "hide:tunnel"));
    }

    [TestMethod]
    public void Flyout_UncheckWritesOneToken_AndCheckAgainClearsIt()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "host:a";
        Row(vm, "GET").IsChecked = false;

        Assert.AreEqual("host:a -method:GET", vm.SearchQuery);
        Assert.IsFalse(Row(vm, "GET").IsChecked);
        Assert.IsTrue(Row(vm, "POST").IsChecked);
        Assert.AreEqual("1", vm.MethodFilterBadge);
        Assert.AreEqual("Hidden: GET", vm.MethodFilterTip);
        Assert.IsFalse(vm.HasHiddenChips);

        Row(vm, "GET").IsChecked = true;
        Assert.AreEqual("host:a", vm.SearchQuery);
        Assert.AreEqual("", vm.MethodFilterBadge);
        StringAssert.Contains(vm.MethodFilterTip, "Uncheck a method");
        Assert.IsFalse(vm.MethodFilterActive);
    }

    [TestMethod]
    public void OnlyPost_HidesTheOtherListedVerbs_AndLeavesUnseenMethods()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "host:a";
        Row(vm, "POST").OnlyCommand.Execute(null);

        Assert.AreEqual("host:a -method:GET,PUT,PATCH,DELETE,HEAD,OPTIONS,CONNECT", vm.SearchQuery);
        Assert.IsTrue(Row(vm, "POST").IsChecked);
        Assert.IsFalse(vm.ConnectMethodRow!.IsChecked);
        Assert.IsTrue(SessionSearch.Matches(
            new SessionSnapshot { Method = "PROPFIND", Url = "http://a/" },
            vm.SearchQuery));
        Assert.IsFalse(SessionSearch.Matches(
            new SessionSnapshot { Method = "GET", Url = "http://a/" },
            vm.SearchQuery));
    }

    [TestMethod]
    public void SelectAll_ClearsHideList_HideTunnel_AndPositiveMethod()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "host:a hide:tunnel method:GET";
        Assert.IsTrue(vm.MethodFilterShowsTypedNote);
        Assert.IsFalse(vm.ConnectMethodRow!.IsChecked);

        vm.SelectAllMethodsCommand.Execute(null);

        Assert.AreEqual("host:a", vm.SearchQuery);
        Assert.IsTrue(vm.ConnectMethodRow.IsChecked);
        Assert.IsTrue(Row(vm, "GET").IsChecked);
        Assert.IsFalse(vm.MethodFilterShowsTypedNote);
        Assert.AreEqual("", vm.MethodFilterBadge);
    }

    [TestMethod]
    public void HideTunnel_UnchecksConnect_AndCheckingTheRowRestoresIt()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "hide:tunnel";

        Assert.IsFalse(vm.ConnectMethodRow!.IsChecked);
        Assert.IsTrue(Row(vm, "GET").IsChecked);
        Assert.AreEqual("1", vm.MethodFilterBadge);
        Assert.AreEqual("Hidden: CONNECT", vm.MethodFilterTip);
        Assert.IsTrue(vm.MethodFilterActive);
        Assert.IsTrue(vm.MethodFilterShowsTypedNote);
        Assert.IsFalse(vm.HasHiddenChips);

        vm.ConnectMethodRow.IsChecked = true;
        Assert.AreEqual("", vm.SearchQuery);
        Assert.IsTrue(vm.ConnectMethodRow.IsChecked);
        Assert.AreEqual("", vm.MethodFilterBadge);
    }

    [TestMethod]
    public void TwoAndNinePlusHidden_BadgeAndTip()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "-method:GET,CONNECT";
        Assert.AreEqual("2", vm.MethodFilterBadge);
        Assert.AreEqual("Hidden: GET, CONNECT", vm.MethodFilterTip);

        Row(vm, "GET").IsChecked = true;
        Assert.AreEqual("-method:CONNECT", vm.SearchQuery);
        Assert.IsTrue(Row(vm, "GET").IsChecked);

        vm.SearchQuery = "-method:GET,POST,PUT,PATCH,DELETE,HEAD,OPTIONS,CONNECT,PROPFIND,TRACE";
        Assert.AreEqual("9+", vm.MethodFilterBadge);
        StringAssert.StartsWith(vm.MethodFilterTip, "Hidden: ");
    }

    [TestMethod]
    public void TypedMethodToken_IsReplacedOnFirstEdit()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "host:a method:GET";
        Assert.IsTrue(vm.MethodFilterShowsTypedNote);
        Assert.IsTrue(Row(vm, "GET").IsChecked);
        Assert.IsFalse(Row(vm, "POST").IsChecked);
        Assert.AreEqual("7", vm.MethodFilterBadge);

        Row(vm, "POST").IsChecked = true;

        StringAssert.Contains(vm.SearchQuery, "host:a");
        StringAssert.Contains(vm.SearchQuery, "-method:PUT,PATCH,DELETE,HEAD,OPTIONS,CONNECT");
        Assert.IsFalse(vm.SearchQuery.Contains("method:GET", StringComparison.Ordinal));
        Assert.IsFalse(vm.MethodFilterShowsTypedNote);
        Assert.IsTrue(SessionSearch.Matches(
            new SessionSnapshot { Method = "PROPFIND", Url = "http://a/" },
            vm.SearchQuery));
    }

    [TestMethod]
    public void HiddenMethod_StaysListedUntilRestored_SeenMethod_LeavesOnClear()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "-method:PROPFIND";
        var hidden = Row(vm, "PROPFIND");
        Assert.IsFalse(hidden.IsChecked);

        hidden.IsChecked = true;
        Assert.AreEqual("", vm.SearchQuery);
        Assert.IsNull(vm.MethodFilterRows.FirstOrDefault(r => r.Name == "PROPFIND"));

        vm.SeedSession(new SessionSnapshot { Id = 9, Method = "PROPFIND", Url = "http://a/", Host = "a" });
        Assert.IsTrue(Row(vm, "PROPFIND").IsChecked);

        vm.ClearSessionsCommand.Execute(null);
        Assert.IsNull(vm.MethodFilterRows.FirstOrDefault(r => r.Name == "PROPFIND"));
    }

    [TestMethod]
    public void StatusBadge_CountsTermsWhileSet()
    {
        var vm = NewViewModel();
        Assert.AreEqual("", vm.StatusFilterBadge);
        StringAssert.Contains(vm.StatusFilterTip, "status:");
        Assert.IsFalse(vm.StatusFilterActive);
        vm.StatusClass2xx = true;
        vm.StatusFreeText = "304";
        Assert.AreEqual("2", vm.StatusFilterBadge);
        Assert.AreEqual("Showing: 2xx,304", vm.StatusFilterTip);
        Assert.IsTrue(vm.StatusFilterActive);
    }

    [TestMethod]
    public void Match_AllocatesNothingAcross100kRows()
    {
        const string token = "GET,POST,CONNECT";
        MethodFilter.Matches("GET", token);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hits = 0;
        for (var i = 0; i < 100_000; i++)
        {
            if (MethodFilter.Matches(i % 2 == 0 ? "GET" : "PROPFIND", token))
            {
                hits++;
            }
        }

        var used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(50_000, hits);
        Assert.AreEqual(0L, used);
    }

    [TestMethod]
    public void SearchQuery_CollapsesPastedLineBreaksToSpaces()
    {
        Assert.AreEqual("one two three", SearchQueryTextBox.CollapseLineBreaks("one\r\ntwo\n\nthree"));
        Assert.AreEqual("already one line", SearchQueryTextBox.CollapseLineBreaks("already one line"));
        Assert.AreEqual("lead", SearchQueryTextBox.CollapseLineBreaks("\nlead"));
    }

    private static MethodFilterRow Row(MainWindowViewModel vm, string name) =>
        vm.MethodFilterRows.First(r => r.Name == name);

    private static List<long> Ids(IEnumerable<SessionSnapshot> sessions, string query) =>
        SessionSearch.Filter(sessions, query).Select(s => s.Id).ToList();

    private static MainWindowViewModel NewViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-method-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var registry = new SessionRegistry();
        return new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            new ScriptedInspectorDialogs(),
            new ScriptedInspectorPathPicker()));
    }
}
