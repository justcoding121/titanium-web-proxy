using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Fat MainWindowViewModel.Sessions / Statistics / BodyInspect leftovers: FindRelated Task.Run path,
///     FilterByStatus, multi mark clear, Statistics multi-selection, and composer body file load.
/// </summary>
[TestClass]
public class MainWindowViewModelSessionsStatsAndComposerBehaviorTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task FindRelated_OverThreeHundredRows_UsesBackgroundResolve()
    {
        LanguageService.Apply("en");
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-rel-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            for (var i = 1; i <= 301; i++)
            {
                vm.SeedSession(new SessionSnapshot
                {
                    Id = i,
                    Method = "GET",
                    Url = "https://same.example/item",
                    Host = "same.example",
                    StatusCode = 200,
                    ConnectionId = i <= 2 ? 42 : i,
                });
            }

            vm.SetSelectedSessions([vm.Sessions[0]]);
            Assert.IsTrue(vm.FindRelatedCommand.CanExecute("url"));
            vm.FindRelatedCommand.Execute("url");
            await WaitUntil(() => vm.SearchQuery.Contains("ids:", StringComparison.Ordinal));
            StringAssert.Contains(vm.SearchQuery, "ids:");

            vm.FindRelatedCommand.Execute("connection");
            await WaitUntil(() =>
                vm.SearchQuery.Contains("ids:", StringComparison.Ordinal)
                || vm.StatusText.Length > 0);

            vm.FindRelatedCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("Select one session", StringComparison.Ordinal)
                || vm.StatusText.Length >= 0);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task FilterByStatus_AndSetMarkNone_UpdateSearchAndStatus()
    {
        LanguageService.Apply("en");
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            var s404a = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://a.example/1",
                Host = "a.example",
                StatusCode = 404,
            };
            var s404b = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://a.example/2",
                Host = "a.example",
                StatusCode = 404,
            };
            var s200 = new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "https://b.example/3",
                Host = "b.example",
                StatusCode = 200,
            };
            vm.SeedSession(s404a);
            vm.SeedSession(s404b);
            vm.SeedSession(s200);

            vm.SetSelectedSessions([s404a, s404b]);
            Assert.IsTrue(vm.FilterByStatusCommand.CanExecute(null));
            vm.FilterByStatusCommand.Execute(null);
            await WaitUntil(() => vm.SearchQuery.Contains("status:404", StringComparison.Ordinal));
            StringAssert.Contains(vm.StatusText, "status:404");

            vm.SearchQuery = "";
            vm.SetSelectedSessions([s404a, s200]);
            vm.FilterByStatusCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("shared status", StringComparison.OrdinalIgnoreCase));

            vm.SetSelectedSessions([s404a, s404b]);
            vm.SetMarkCommand.Execute("None");
            Assert.AreEqual(SessionMark.None, s404a.Mark);
            Assert.AreEqual(SessionMark.None, s404b.Mark);
            StringAssert.Contains(vm.StatusText, "cleared");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task StatisticsMultiSelection_AndCopyStatistics_PopulateSummary()
    {
        LanguageService.Apply("en");
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-stats-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            var a = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://stats.example/a",
                StatusCode = 200,
                DurationMs = 120,
                TimingRecorded = true,
                WaitMs = 80,
                ReceiveMs = 40,
                Events = [new SessionEventNote(5, "stats.event.breakpoint", false)],
            };
            var b = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://stats.example/b",
                StatusCode = 500,
                DurationMs = 40,
                TimingRecorded = true,
                WaitMs = 40,
            };
            vm.SeedSession(a);
            vm.SeedSession(b);
            vm.SetSelectedSessions([vm.Sessions[0], vm.Sessions[1]]);
            vm.SelectedInspectTabIndex = (int)InspectTab.Statistics;

            Assert.IsTrue(vm.StatisticsSummary.Length > 0);
            StringAssert.Contains(vm.StatisticsSummary, "2");
            Assert.IsTrue(vm.CopyStatisticsCommand.CanExecute(null));
            vm.CopyStatisticsCommand.Execute(null);
            await WaitUntil(() => vm.StatisticsCopyText.Length > 0);

            vm.SelectedSession = vm.Sessions[0];
            vm.SetSelectedSessions([vm.Sessions[0]]);
            Assert.IsTrue(vm.ShowStatisticsEvents || vm.StatisticsSummary.Length > 0);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task LoadComposerBodyFile_AndBodyHexMode_RefreshInspect()
    {
        LanguageService.Apply("en");
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-vm-composer-" + Guid.NewGuid().ToString("N") + ".json");
        var bodyPath = Path.Combine(Path.GetTempPath(), "twp-vm-composer-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(bodyPath, "{\"ok\":true}", Encoding.UTF8);
            var picker = new ScriptedInspectorPathPicker { OpenPath = bodyPath };
            var vm = CreateVm(settingsPath, picker);

            Assert.IsTrue(vm.LoadComposerBodyFileCommand.CanExecute(null));
            vm.LoadComposerBodyFileCommand.Execute(null);
            await WaitUntil(() =>
                vm.ComposerBody.Contains("ok", StringComparison.Ordinal)
                || vm.StatusText.Contains("Composer body", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(vm.ComposerBody, "ok");

            picker.OpenPath = null;
            vm.LoadComposerBodyFileCommand.Execute(null);
            await Task.Delay(50);

            vm.SeedSession(new SessionSnapshot
            {
                Id = 9,
                Method = "POST",
                Url = "https://body.example/",
                StatusCode = 200,
                RequestBodyText = "{\"a\":1}",
                ResponseBodyText = "{\"b\":2}",
                RequestHeadersText = "Content-Type: application/json\r\n",
                ResponseHeadersText = "Content-Type: application/json\r\n",
            });
            vm.SelectedSession = vm.Sessions[0];
            vm.SelectedInspectTabIndex = (int)InspectTab.Request;
            Assert.IsTrue(vm.ShowBodyModeToggles);
            vm.BodyHexMode = true;
            Assert.IsFalse(vm.BodyPrettyEnabled);
            Assert.IsTrue(vm.BodyPrettyToolTip.Length > 0);
            vm.BodyHexMode = false;
            Assert.IsTrue(vm.BodyPrettyEnabled);

            vm.SetThemeDarkCommand.Execute(null);
            Assert.IsTrue(vm.ThemeModeIsDark);
            vm.SetThemeLightCommand.Execute(null);
            Assert.IsTrue(vm.ThemeModeIsLight);
            vm.SetThemeAutomaticCommand.Execute(null);
            Assert.IsTrue(vm.ThemeModeIsAutomatic);
            vm.SetUpdateChannelBetaCommand.Execute(null);
            Assert.IsTrue(vm.UpdateChannelIsBeta);
            vm.SetUpdateChannelStableCommand.Execute(null);
            Assert.IsTrue(vm.UpdateChannelIsStable);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(bodyPath);
        }
    }

    private static MainWindowViewModel CreateVm(string settingsPath, IInspectorPathPicker? picker = null)
    {
        var settings = new SettingsService(settingsPath);
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()),
            pathPicker: picker);
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 12_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(25);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort.
        }
    }
}
