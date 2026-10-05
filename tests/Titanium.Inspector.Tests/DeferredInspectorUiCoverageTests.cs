using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>Covers the dispatcher-live export/import path that unit tests otherwise skip.</summary>
[TestClass]
public class DeferredInspectorUiCoverageTests
{
    private static readonly int[] DeferredUiOrder = [1, 2];
    [TestMethod]
    public async Task DeferredUi_PresentsOnTheNextLiveTurn_AndPostsWhileTheAppIsUp()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(DeferredUiCoverageBootstrap));
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-defer-ui-" + Guid.NewGuid().ToString("N") + ".json");
        var zip = Path.Combine(Path.GetTempPath(), "twp-defer-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            MainWindowViewModel vm = null!;
            ScriptedInspectorPathPicker picker = null!;
            await session.Dispatch(
                () => (vm, picker) = CreateVm(settingsPath),
                CancellationToken.None);

            // Between dispatches the application is down, so flush must not run the queue.
            var ranEarly = false;
            vm.QueueLiveUi(() => ranEarly = true);
            vm.FlushDeferredInspectorUi();
            Assert.IsFalse(ranEarly);

            var order = new List<int>();
            vm.QueueLiveUi(() => order.Add(1));
            vm.QueueLiveUi(() => order.Add(2));

            await session.Dispatch(() =>
            {
                // StatusText presents a stashed queue once the dispatcher is live.
                _ = vm.StatusText;
                CollectionAssert.AreEqual(DeferredUiOrder, order);

                var inline = 0;
                vm.QueueLiveUi(() => inline++);
                Assert.AreEqual(1, inline);
            }, CancellationToken.None);

            var posted = 0;
            await session.Dispatch(async () =>
            {
                await Task.Run(() => vm.QueueLiveUi(() => posted++));
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(1, posted);

                vm.SeedSession(new SessionSnapshot
                {
                    Id = 3,
                    Method = "GET",
                    Url = "https://example.com/deferred",
                    Host = "example.com",
                });
                picker.SavePath = zip;
                vm.ExportArchiveCommand.Execute(null);
                await WaitUntilAsync(() => vm.StatusText.Contains("Exported", StringComparison.Ordinal));
                Assert.IsTrue(File.Exists(zip), vm.StatusText);

                picker.OpenPath = zip;
                vm.ImportArchiveCommand.Execute(null);
                await WaitUntilAsync(() => vm.StatusText.Contains("Appended", StringComparison.Ordinal));
                Assert.IsTrue(vm.Sessions.Count >= 2, vm.StatusText);
            }, CancellationToken.None);
        }
        finally
        {
            session.Dispose();
            TryDelete(settingsPath);
            TryDelete(zip);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    private static (MainWindowViewModel ViewModel, ScriptedInspectorPathPicker Picker) CreateVm(string settingsPath)
    {
        var settings = new SettingsService(settingsPath);
        var registry = new SessionRegistry();
        var picker = new ScriptedInspectorPathPicker();
        var vm = new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            pathPicker: picker);
        return (vm, picker);
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
            // ignore
        }
    }
}

internal static class DeferredUiCoverageBootstrap
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
