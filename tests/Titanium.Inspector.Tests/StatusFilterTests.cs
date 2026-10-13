using Avalonia.Headless;
using Avalonia.Interactivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;

namespace Titanium.Inspector.Tests;

[TestClass]
public class StatusFilterTests
{
    [TestMethod]
    public void Lists_Ranges_AndInvalidPieces()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, StatusCode = 200, Url = "https://a/" },
            new SessionSnapshot { Id = 2, StatusCode = 304, Url = "https://a/" },
            new SessionSnapshot { Id = 3, StatusCode = 404, Url = "https://a/" },
            new SessionSnapshot { Id = 4, StatusCode = 500, Url = "https://a/" },
            new SessionSnapshot { Id = 5, StatusCode = null, Url = "https://a/" },
        };

        CollectionAssert.AreEquivalent(new long[] { 2, 3 }, Ids(sessions, "status:301,302,304,404"));
        CollectionAssert.AreEquivalent(new long[] { 3, 4 }, Ids(sessions, "status:400-499,5xx"));
        CollectionAssert.AreEquivalent(new long[] { 1, 2 }, Ids(sessions, "status:2xx,304"));
        CollectionAssert.AreEquivalent(new long[] { 4 }, Ids(sessions, "status:nope,500"));
        Assert.AreEqual(0, SessionSearch.Filter(sessions, "status:nope").Count());
        Assert.IsFalse(SessionSearch.Matches(sessions[4], "status:2xx,304"));
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "status:error").Count());
        CollectionAssert.AreEquivalent(new long[] { 1, 2, 4, 5 }, Ids(sessions, "-status:404"));
    }

    [TestMethod]
    public void Flyout_WritesOneToken_AndKeepsExclusion()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "host:a -status:404";
        vm.StatusClass2xx = true;
        vm.StatusFreeText = "304, 401, nope";

        Assert.AreEqual("host:a -status:404 status:2xx,304,401", vm.SearchQuery);
        Assert.IsTrue(vm.StatusClass2xx);
        Assert.IsFalse(vm.StatusClass4xx);
        Assert.AreEqual("304,401", vm.StatusFreeText);
        Assert.IsTrue(vm.HasStatusFilterError);

        vm.StatusClass2xx = false;
        vm.StatusFreeText = "";
        Assert.AreEqual("host:a -status:404", vm.SearchQuery);
        Assert.IsFalse(vm.HasStatusFilterError);
    }

    [TestMethod]
    public void Flyout_RefusesMoreThan32Terms()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "host:a";
        var many = string.Join(',', Enumerable.Range(200, 40));
        vm.StatusFreeText = many;
        Assert.AreEqual("host:a", vm.SearchQuery);
        Assert.IsTrue(vm.HasStatusFilterError);
    }

    [TestMethod]
    public void FilterByThisStatus_ReplacesOnlyTheStatusToken()
    {
        var vm = NewViewModel();
        vm.SearchQuery = "-status:200 hide:tunnel";
        vm.SelectedSession = new SessionSnapshot { Id = 1, StatusCode = 304, Url = "https://a/" };
        vm.FilterByStatusCommand.Execute(null);
        Assert.AreEqual("-status:200 hide:tunnel status:304", vm.SearchQuery);
    }

    [TestMethod]
    public void FiltersWindow_KeepsStatusTokensInTheRemainder()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DeferredUiCoverageBootstrap));
        session.Dispatch(() =>
        {
            var filters = new SearchFiltersWindow("status:2xx,304 -status:404 hide:tunnel");
            Assert.AreEqual("status:2xx,304 -status:404 hide:tunnel", filters.RemainderBoxForTests.Text);
            filters.ApplyButtonForTests.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.AreEqual("status:2xx,304 -status:404 hide:tunnel", filters.AppliedQuery);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void Match_AllocatesNothingAcross100kRows()
    {
        const string token = "2xx,304,400-499,5xx";
        StatusFilter.Matches(304, token);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hits = 0;
        for (var i = 0; i < 100_000; i++)
        {
            if (StatusFilter.Matches(i % 2 == 0 ? 304 : 101, token))
            {
                hits++;
            }
        }

        var used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(50_000, hits);
        Assert.AreEqual(0L, used);
    }

    private static List<long> Ids(IEnumerable<SessionSnapshot> sessions, string query) =>
        SessionSearch.Filter(sessions, query).Select(s => s.Id).ToList();

    private static MainWindowViewModel NewViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-status-" + Guid.NewGuid().ToString("N"));
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
