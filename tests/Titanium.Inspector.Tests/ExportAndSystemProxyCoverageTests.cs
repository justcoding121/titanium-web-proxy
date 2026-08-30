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

    [TestMethod]
    public async Task CaCommands_Filters_Capturing_AndShutdown_CoverMoreBranches()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-cov-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var registry = new SessionRegistry();
            var dialogs = new ScriptedInspectorDialogs
            {
                RemoveRootCaResult = false,
                ElevateRootCaResult = false,
                DeviceCaSetupResult = false,
            };
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            await ExecuteAsync(vm.InstallCaCommand);
            StringAssert.Contains(vm.StatusText, "Start interception");

            await ExecuteAsync(vm.UntrustCaCommand);
            StringAssert.Contains(vm.StatusText, "Start interception");

            await ExecuteAsync(vm.ExportCaCommand);
            StringAssert.Contains(vm.StatusText, "start interception");

            await ExecuteAsync(vm.LoadIntoComposerCommand);
            StringAssert.Contains(vm.StatusText, "Select a session");

            await ExecuteAsync(vm.CopyUrlCommand);
            StringAssert.Contains(vm.StatusText, "Select a session");

            vm.HideTunnelsFilter = true;
            Assert.IsTrue(vm.HideTunnelsFilter);
            vm.HideTunnelsFilter = true; // no-op branch
            vm.ErrorsOnlyFilter = true;
            Assert.IsTrue(vm.ErrorsOnlyFilter);
            vm.ErrorsOnlyFilter = true;
            await ExecuteAsync(vm.ClearFiltersCommand);
            Assert.IsFalse(vm.HideTunnelsFilter);
            Assert.IsFalse(vm.ErrorsOnlyFilter);

            await ExecuteAsync(vm.OpenToolsComposerCommand);
            await ExecuteAsync(vm.OpenToolsBreakpointsCommand);
            await ExecuteAsync(vm.OpenToolsAutoResponderCommand);
            await ExecuteAsync(vm.OpenToolsScriptsCommand);

            vm.BindPort = GetFreePort();
            vm.BindAddress = "127.0.0.1";
            await ExecuteAsync(vm.StartCaptureCommand);
            Assert.IsTrue(interception.IsRunning, vm.StatusText);

            await ExecuteAsync(vm.UntrustCaCommand);
            StringAssert.Contains(vm.StatusText, "cancelled");

            await ExecuteAsync(vm.ToggleCapturingCommand);
            Assert.IsFalse(vm.Capturing);
            await ExecuteAsync(vm.ToggleCapturingCommand);
            Assert.IsTrue(vm.Capturing);

            vm.DecryptHttps = false;
            Assert.IsFalse(vm.DecryptHttps);

            var snap = new SessionSnapshot
            {
                Id = 42,
                Method = "GET",
                Url = "https://example.com/x",
                Host = "example.com",
                ProcessName = "chrome",
                ProcessId = 99,
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            await ExecuteAsync(vm.LoadIntoComposerCommand);
            StringAssert.Contains(vm.StatusText, "Composer");
            await ExecuteAsync(vm.CopyUrlCommand);
            StringAssert.Contains(vm.StatusText, "Copied");

            await ExecuteAsync(vm.FilterByHostCommand);
            Assert.AreEqual("host:example.com", vm.SearchQuery);
            StringAssert.Contains(vm.StatusText, "host:example.com");
            await ExecuteAsync(vm.FilterByProcessCommand);
            Assert.AreEqual("host:example.com process:chrome", vm.SearchQuery);
            StringAssert.Contains(vm.StatusText, "process:chrome");
            vm.SearchQuery = "";

            await ExecuteAsync(vm.ClearSessionsCommand);
            StringAssert.Contains(vm.StatusText, "cleared");

            var keep = new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a/", Host = "a" };
            var drop = new SessionSnapshot { Id = 2, Method = "POST", Url = "https://b/", Host = "b" };
            vm.SeedSession(keep);
            vm.SeedSession(drop);
            vm.SetSelectedSessions([drop]);
            vm.SelectedSession = drop;
            await ExecuteAsync(vm.RemoveSelectedSessionsCommand);
            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreEqual(1, vm.Sessions[0].Id);
            Assert.IsNull(vm.SelectedSession);
            StringAssert.Contains(vm.StatusText, "Removed");

            vm.BeginBackgroundShutdown();
            vm.EnsureShutdown();
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
    public async Task TryAutoStart_WithLaunchPrefs_CoversSystemProxySuccessPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-autostart-cov-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = true;
            settings.Current.AutoSystemProxyOnStart = true;
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

            // Clobber prefs after construction to hit RestoreLaunchPreferencesIfClobbered.
            vm.AutoStartCapture = false;
            vm.AutoSystemProxyOnStart = false;

            await vm.TryAutoStartAsync();
            Assert.IsTrue(interception.IsRunning, vm.StatusText);
            Assert.IsTrue(vm.AutoStartCapture);
            Assert.IsTrue(vm.AutoSystemProxyOnStart);
            Assert.IsTrue(vm.SystemProxy, vm.StatusText);

            vm.EnsureShutdown();
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
