using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class ExportAndSystemProxyCoverageTests
{
    [TestMethod]
    public async Task ExportCommands_CoverEmptyCancelAndSelectedPaths()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-export-cov-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var picker = new ScriptedInspectorPathPicker();
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                pathPicker: picker);

            // Empty selection / empty store cancel paths.
            await ExecuteAsync(vm.ExportArchiveCommand);
            StringAssert.Contains(vm.StatusText, "No sessions");

            await ExecuteAsync(vm.ExportSelectedHarCommand);
            StringAssert.Contains(vm.StatusText, "Select a session");

            await ExecuteAsync(vm.ExportSelectedArchiveCommand);
            StringAssert.Contains(vm.StatusText, "Select a session");

            await ExecuteAsync(vm.ExportHarCommand);
            StringAssert.Contains(vm.StatusText, "No sessions");

            // Seed one session and exercise selected export cancel + success.
            var snap = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                Host = "example.com",
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            vm.SetSelectedSessions([snap]);

            picker.SavePath = null;
            await ExecuteAsync(vm.ExportHarCommand);
            StringAssert.Contains(vm.StatusText, "cancelled");

            picker.SavePath = null;
            await ExecuteAsync(vm.ExportSelectedHarCommand);
            StringAssert.Contains(vm.StatusText, "cancelled");

            var har = Path.Combine(Path.GetTempPath(), $"twp-sel-{Guid.NewGuid():N}.har");
            try
            {
                picker.SavePath = har;
                await ExecuteAsync(vm.ExportSelectedHarCommand);
                StringAssert.Contains(vm.StatusText, "Exported");
                Assert.IsTrue(File.Exists(har));
            }
            finally
            {
                if (File.Exists(har))
                {
                    File.Delete(har);
                }
            }

            var zip = Path.Combine(Path.GetTempPath(), $"twp-sel-{Guid.NewGuid():N}.zip");
            try
            {
                picker.SavePath = zip;
                await ExecuteAsync(vm.ExportSelectedArchiveCommand);
                StringAssert.Contains(vm.StatusText, "Exported");
                Assert.IsTrue(File.Exists(zip));
            }
            finally
            {
                if (File.Exists(zip))
                {
                    File.Delete(zip);
                }
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task StartStopCapture_AndSystemProxyWithoutCapture_AreCovered()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-start-cov-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);
            vm.BindPort = GetFreePort();
            vm.BindAddress = "127.0.0.1";

            vm.SystemProxy = true;
            Assert.IsFalse(vm.SystemProxy);
            StringAssert.Contains(vm.StatusText, "Start interception");

            await ExecuteAsync(vm.StartCaptureCommand);
            Assert.IsTrue(interception.IsRunning, vm.StatusText);

            await ExecuteAsync(vm.StopCaptureCommand);
            Assert.IsFalse(interception.IsRunning, vm.StatusText);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await Task.Delay(150);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
