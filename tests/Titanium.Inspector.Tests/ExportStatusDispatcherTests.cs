using Microsoft.VisualStudio.TestTools.UnitTesting;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>Bootstrap for a headless dispatcher so export/import status can be covered off the UI thread.</summary>
public static class InspectorDispatcherCoverageBootstrap
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Titanium.Inspector.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

[TestClass]
public class ExportStatusDispatcherTests
{
    [TestMethod]
    public async Task ExportAndImport_PresentStatusOnTheLiveDispatcher()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-disp-" + Guid.NewGuid().ToString("N") + ".json");
        var harPath = Path.Combine(Path.GetTempPath(), "twp-disp-" + Guid.NewGuid().ToString("N") + ".har");
        Environment.SetEnvironmentVariable("TITANIUM_INSPECTOR_SKIP_AUTO_MAINWINDOW", "1");
        using var session = HeadlessUnitTestSession.StartNew(typeof(InspectorDispatcherCoverageBootstrap));
        try
        {
            await session.Dispatch(async () =>
            {
                var settings = new SettingsService(settingsPath);
                var picker = new ScriptedInspectorPathPicker { SavePath = harPath };
                var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
                var vm = new MainWindowViewModel(
                    new SessionStreamBuffer(registry),
                    registry,
                    new UpdateService(settings),
                    settings,
                    new InterceptionService(new RecordingSystemProxyController()),
                    dialogs: null,
                    pathPicker: picker);
                vm.SeedSession(new SessionSnapshot
                {
                    Id = 1,
                    Method = "GET",
                    Url = "https://example.com/dispatcher-export",
                    StatusCode = 200,
                });

                // Not the dispatcher thread: Flush returns before touching the tree.
                await Task.Run(vm.FlushDeferredInspectorUi);

                vm.ExportHarCommand.Execute(null);
                var exportDeadline = DateTime.UtcNow.AddSeconds(8);
                while (vm.StatusText.Contains("Exporting", StringComparison.Ordinal)
                       && DateTime.UtcNow < exportDeadline)
                {
                    await Task.Delay(30);
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.IsTrue(File.Exists(harPath), vm.StatusText);
                Assert.IsTrue(vm.StatusText.Contains("Exported", StringComparison.Ordinal), vm.StatusText);

                picker.OpenPath = harPath;
                vm.ImportHarCommand.Execute(null);
                var importDeadline = DateTime.UtcNow.AddSeconds(8);
                while (!vm.StatusText.Contains("Appended", StringComparison.Ordinal)
                       && DateTime.UtcNow < importDeadline)
                {
                    await Task.Delay(30);
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.IsTrue(vm.StatusText.Contains("Appended", StringComparison.Ordinal), vm.StatusText);
                Assert.IsTrue(vm.Sessions.Count >= 2, "Import should append the exported row");
            }, CancellationToken.None);
        }
        finally
        {
            try { File.Delete(settingsPath); } catch { /* temp */ }
            try { File.Delete(harPath); } catch { /* temp */ }
        }
    }
}
