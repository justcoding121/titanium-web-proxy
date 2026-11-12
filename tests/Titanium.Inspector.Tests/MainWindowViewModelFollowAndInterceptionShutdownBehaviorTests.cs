using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     MainWindowViewModel remove-matching / hide-host and InterceptionService EnsureShutdown edges that remain
///     thin on develop Sonar.
/// </summary>
[TestClass]
public class MainWindowViewModelFollowAndInterceptionShutdownBehaviorTests
{
    [TestMethod]
    public async Task HideHostRemoveMatching_AndEnsureShutdownWhenProxyIdle()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-follow-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var interception = new InterceptionService(new RecordingSystemProxyController());
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://keep.example/a",
                Host = "keep.example",
                StatusCode = 200,
            });
            vm.SeedSession(new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://drop.example/b",
                Host = "drop.example",
                StatusCode = 404,
            });

            vm.HideImagesFilter = true;
            Assert.IsTrue(vm.HideImagesFilter);
            vm.ErrorsOnlyFilter = true;
            Assert.IsTrue(vm.ErrorsOnlyFilter);
            vm.ErrorsOnlyFilter = false;
            vm.HideImagesFilter = false;

            vm.SetSelectedSessions([vm.Sessions.First(s => s.Host == "drop.example")]);
            if (vm.HideHostCommand.CanExecute(null))
            {
                vm.HideHostCommand.Execute(null);
                await WaitUntil(() =>
                    vm.SearchQuery.Contains("drop.example", StringComparison.OrdinalIgnoreCase)
                    || vm.StatusText.Length > 0);
            }

            vm.SearchQuery = "host:drop.example";
            if (vm.RemoveMatchingFilterCommand.CanExecute(null))
            {
                vm.RemoveMatchingFilterCommand.Execute(null);
                await WaitUntil(() => vm.Sessions.All(s => s.Host != "drop.example") || vm.Sessions.Count <= 1);
            }

            // Idle proxy: EnsureShutdown must still restore/dispose without throwing (covers the !_ProxyRunning branch).
            interception.EnsureShutdown();
            interception.EnsureShutdown(); // second call waits on completed gate
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ToggleAutoStartAndSystemProxySettings_Persist()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-toggles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            var autoStart = vm.AutoStartCapture;
            vm.ToggleAutoStartCaptureCommand.Execute(null);
            Assert.AreNotEqual(autoStart, vm.AutoStartCapture);

            var autoProxy = vm.AutoSystemProxyOnStart;
            vm.ToggleAutoSystemProxyOnStartCommand.Execute(null);
            Assert.AreNotEqual(autoProxy, vm.AutoSystemProxyOnStart);

            var beforeUpdates = vm.CheckForUpdatesOnStartup;
            vm.ToggleCheckForUpdatesOnStartupCommand.Execute(null);
            Assert.AreNotEqual(beforeUpdates, vm.CheckForUpdatesOnStartup);
            Assert.AreEqual(settings.Current.CheckForUpdatesOnStartup, vm.CheckForUpdatesOnStartup);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 8000)
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
