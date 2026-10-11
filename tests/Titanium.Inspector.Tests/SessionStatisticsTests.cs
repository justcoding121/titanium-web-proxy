using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionStatisticsTests
{
    [TestMethod]
    public void Phases_KeepOrder_AndOmitMissingValues()
    {
        var model = SessionStatistics.Build(
        [
            new SessionSnapshot
            {
                TimingRecorded = true,
                DurationMs = 180,
                BlockedMs = 10,
                ConnectMs = 30,
                TlsMs = 0,
                SendMs = 5,
                WaitMs = 100,
                ReceiveMs = 25,
                TimingAttempts = 2,
            },
        ]);

        Assert.AreEqual(SessionStatistics.Kind.Phases, model.Kind);
        CollectionAssert.AreEqual(
            new[]
            {
                SessionStatistics.Phase.Blocked,
                SessionStatistics.Phase.Connect,
                SessionStatistics.Phase.Tls,
                SessionStatistics.Phase.Send,
                SessionStatistics.Phase.Wait,
                SessionStatistics.Phase.Receive,
            },
            model.Phases.Select(phase => phase.Phase).ToArray());
        Assert.AreEqual(SessionStatistics.Phase.Wait, model.Slowest);
        Assert.AreEqual(2, model.Attempts);
        Assert.AreEqual(SessionStatistics.ConnectionNote.None, model.Note);
    }

    [TestMethod]
    public void TunnelAndEstimated_DrawNoPhases()
    {
        var tunnel = SessionStatistics.Build(
        [
            new SessionSnapshot
            {
                IsTunnel = true,
                DurationMs = 40,
                TimingRecorded = true,
                BlockedMs = 0,
                ReceiveMs = 0,
            },
        ]);
        Assert.AreEqual(SessionStatistics.Kind.NotRecorded, tunnel.Kind);
        Assert.AreEqual(0, tunnel.Phases.Count);

        var estimated = SessionStatistics.Build(
        [
            new SessionSnapshot
            {
                TimingEstimated = true,
                DurationMs = 80,
                TtfbMs = 48,
                ReceiveMs = 28,
            },
        ]);
        Assert.AreEqual(SessionStatistics.Kind.Estimated, estimated.Kind);
        Assert.AreEqual(0, estimated.Phases.Count);
    }

    [TestMethod]
    public void MissingSetup_SaysReusedOrPrefetched()
    {
        var reused = SessionStatistics.Build(
        [
            new SessionSnapshot
            {
                TimingRecorded = true,
                DurationMs = 20,
                BlockedMs = 5,
                WaitMs = 10,
                ReceiveMs = 5,
                ConnectionReused = true,
            },
        ]);
        Assert.AreEqual(SessionStatistics.ConnectionNote.Reused, reused.Note);

        var prefetched = SessionStatistics.Build(
        [
            new SessionSnapshot
            {
                TimingRecorded = true,
                DurationMs = 20,
                BlockedMs = 15,
                ReceiveMs = 5,
            },
        ]);
        Assert.AreEqual(SessionStatistics.ConnectionNote.Prefetched, prefetched.Note);
    }

    [TestMethod]
    public void MultiSelect_UsesTheFirstTwoThousand()
    {
        var sessions = new List<SessionSnapshot>(2001);
        for (var i = 0; i < 2000; i++)
        {
            sessions.Add(new SessionSnapshot
            {
                StatusCode = 200,
                BodySize = 10,
                DurationMs = i,
            });
        }

        sessions.Add(new SessionSnapshot { StatusCode = 500, BodySize = 9999, DurationMs = 9999 });
        var model = SessionStatistics.Build(sessions);
        Assert.AreEqual(SessionStatistics.Kind.Multi, model.Kind);
        Assert.AreEqual(2001, model.SessionCount);
        Assert.AreEqual(2000, model.ShownCount);
        Assert.IsTrue(model.Truncated);
        Assert.AreEqual(20000, model.TotalBytes);
        Assert.AreEqual(1999, model.SlowestDurationMs);
        Assert.AreEqual(1, model.StatusCounts.Count);
        Assert.AreEqual(2, model.StatusCounts[0].Class);
        Assert.AreEqual(2000, model.StatusCounts[0].Count);
    }

    [TestMethod]
    public void InspectTabs_MatchTheEnum_AndStatisticsStaysLazy()
    {
        var axaml = File.ReadAllText(FindRepoFile(Path.Combine("src", "Titanium.Inspector", "Views", "MainWindow.axaml")));
        var ids = new[]
        {
            "TabRequest",
            "TabResponse",
            "TabDiff",
            "TabFrames",
            "TabSse",
            "TabProtobuf",
            "TabStatistics",
        };
        var previous = -1;
        foreach (var id in ids)
        {
            var index = axaml.IndexOf("AutomationProperties.AutomationId=\"" + id + "\"", StringComparison.Ordinal);
            Assert.IsTrue(index > previous, id);
            previous = index;
        }

        Assert.AreEqual(ids.Length, MainWindowViewModel.InspectTabCount);
        Assert.AreEqual(ids.Length - 1, (int)InspectTab.Statistics);

        LanguageService.Apply("en");
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-stats-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));
            vm.SelectedSession = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                StatusCode = 200,
                DurationMs = 180,
                TimingRecorded = true,
                BlockedMs = 10,
                WaitMs = 100,
                ReceiveMs = 70,
                TimingAttempts = 2,
            };
            Assert.AreEqual("", vm.StatisticsSummary);

            vm.SelectedDetailTabIndex = (int)InspectTab.Statistics;
            Assert.AreEqual((int)InspectTab.Statistics, vm.SelectedInspectTabIndex);
            StringAssert.Contains(vm.StatisticsSummary, "180 ms");
            StringAssert.Contains(vm.StatisticsSummary, "Most of the time was Waiting for server.");
            StringAssert.Contains(vm.StatisticsSummary, "Retried 2 times");
            Assert.AreEqual(3, vm.StatisticsPhases.Count);
            Assert.AreEqual("Time to first byte", vm.StatisticsPhases[1].ToolTip);
            StringAssert.Contains(vm.StatisticsCopyText, "Waiting for server");
            Assert.IsTrue(vm.ShowStatisticsBar);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
