using Avalonia.Headless;
using Avalonia.Interactivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;

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
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
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
    public async Task ExportHarCommands_ReportFailureWhenPathCannotBeWritten()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-export-fail-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            var picker = new ScriptedInspectorPathPicker();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                pathPicker: picker);

            var snap = new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                Url = "https://example.com/",
                Host = "example.com",
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            vm.SetSelectedSessions([snap]);

            var missingDir = Path.Combine(Path.GetTempPath(), "twp-missing-" + Guid.NewGuid().ToString("N"), "out.har");
            picker.SavePath = missingDir;
            await ExecuteAsync(vm.ExportHarCommand);
            StringAssert.Contains(vm.StatusText, "Export HAR failed");

            picker.SavePath = missingDir;
            await ExecuteAsync(vm.ExportSelectedHarCommand);
            StringAssert.Contains(vm.StatusText, "Export HAR failed");

            var missingZip = Path.Combine(
                Path.GetTempPath(),
                "twp-missing-" + Guid.NewGuid().ToString("N"),
                "out.zip");
            picker.SavePath = missingZip;
            await ExecuteAsync(vm.ExportArchiveCommand);
            StringAssert.Contains(vm.StatusText, "Export archive failed");

            picker.SavePath = missingZip;
            await ExecuteAsync(vm.ExportSelectedArchiveCommand);
            StringAssert.Contains(vm.StatusText, "Export archive failed");

            var badZip = Path.Combine(Path.GetTempPath(), "twp-bad-" + Guid.NewGuid().ToString("N") + ".zip");
            await File.WriteAllTextAsync(badZip, "not a zip");
            try
            {
                picker.OpenPath = badZip;
                await ExecuteAsync(vm.ImportArchiveCommand);
                StringAssert.Contains(vm.StatusText, "Import archive failed");
            }
            finally
            {
                if (File.Exists(badZip))
                {
                    File.Delete(badZip);
                }
            }
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
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
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);
            vm.BindPort = 0;
            vm.BindAddress = "127.0.0.1";

            vm.SystemProxy = true;
            Assert.IsFalse(vm.SystemProxy);
            StringAssert.Contains(vm.StatusText, "Start the proxy");

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
                TrustRecoveryResult = TrustRecoveryChoice.Cancel,
                DeviceCaSetupResult = false,
            };
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            await ExecuteAsync(vm.InstallCaCommand);
            StringAssert.Contains(vm.StatusText, "Start the proxy");

            await ExecuteAsync(vm.UntrustCaCommand);
            StringAssert.Contains(vm.StatusText, "Start the proxy");

            await ExecuteAsync(vm.ExportCaCommand);
            StringAssert.Contains(vm.StatusText, "Start the proxy");

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

            vm.BindPort = 0;
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
            Assert.IsTrue(vm.CanFilterByHost);
            Assert.IsTrue(vm.CanFilterByProcess);
            Assert.IsTrue(vm.HasSingleSelectedSession);
            Assert.IsTrue(vm.HasSelectedSessions);
            Assert.IsTrue(vm.CanCopyUrl);

            var otherHost = new SessionSnapshot
            {
                Id = 43,
                Method = "GET",
                Url = "https://other.test/",
                Host = "other.test",
                ProcessName = "chrome",
                ProcessId = 99,
            };
            vm.SeedSession(otherHost);
            vm.SetSelectedSessions([snap, otherHost]);
            Assert.IsFalse(vm.CanFilterByHost);
            Assert.IsTrue(vm.CanFilterByProcess);
            Assert.IsFalse(vm.HasSingleSelectedSession);
            Assert.IsTrue(vm.HasSelectedSessions);
            Assert.IsTrue(vm.CanCopyUrl);
            vm.SetSelectedSessions([snap]);
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
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);
            vm.BindPort = 0;
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

    [TestMethod]
    public async Task HideHostAndProcess_AppendTokens_ClearFiltersResets()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-hide-filter-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            var snap = new SessionSnapshot
            {
                Id = 1,
                Method = "CONNECT",
                Url = "https://api2.cursor.sh/",
                Host = "api2.cursor.sh",
                ProcessName = "Cursor",
                ProcessId = 42,
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            vm.SetSelectedSessions([snap]);
            vm.SearchQuery = "status:200";

            await ExecuteAsync(vm.HideHostCommand);
            Assert.AreEqual("status:200 -host:api2.cursor.sh", vm.SearchQuery);
            StringAssert.Contains(vm.StatusText, "Hidden host:api2.cursor.sh");

            await ExecuteAsync(vm.HideHostCommand);
            Assert.AreEqual("status:200 -host:api2.cursor.sh", vm.SearchQuery);

            await ExecuteAsync(vm.HideProcessCommand);
            Assert.AreEqual("status:200 -host:api2.cursor.sh -process:Cursor", vm.SearchQuery);
            StringAssert.Contains(vm.StatusText, "Hidden process:Cursor");
            Assert.IsTrue(vm.HasHiddenChips);
            CollectionAssert.AreEqual(new[] { "api2.cursor.sh" }, vm.HiddenHostChips.ToList());
            CollectionAssert.AreEqual(new[] { "Cursor" }, vm.HiddenProcessChips.ToList());
            Assert.IsTrue(vm.ShowProcessChipPrefix);

            vm.RemoveHiddenHostCommand.Execute("api2.cursor.sh");
            Assert.AreEqual("status:200 -process:Cursor", vm.SearchQuery);
            Assert.AreEqual(0, vm.HiddenHostChips.Count);
            Assert.IsFalse(vm.ShowProcessChipPrefix);

            await ExecuteAsync(vm.ClearFiltersCommand);
            Assert.AreEqual("", vm.SearchQuery);
            Assert.IsFalse(vm.HasHiddenChips);

            // A name with a space cannot be one search token: say so instead of hiding more in silence.
            var spaced = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://example.com/",
                Host = "example.com",
                ProcessName = "Google Chrome",
                ProcessId = 7,
            };
            vm.SeedSession(spaced);
            vm.SelectedSession = spaced;
            vm.SetSelectedSessions([spaced]);
            await ExecuteAsync(vm.HideProcessCommand);
            Assert.AreEqual("-process:Google", vm.SearchQuery);
            StringAssert.Contains(vm.StatusText, "shortened");
            Assert.AreEqual(1, vm.HiddenProcessChips.Count);
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
    public void SearchFiltersWindow_ApplyWritesSnapshot_CancelLeavesItNull()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DeferredUiCoverageBootstrap));
        session.Dispatch(() =>
        {
            var filters = new SearchFiltersWindow("login -host:cursor.sh hide:tunnel -process:Cursor");
            Assert.IsNull(filters.AppliedQuery);
            filters.ApplyButtonForTests.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.AreEqual("login hide:tunnel -host:cursor.sh -process:Cursor", filters.AppliedQuery);

            var pending = new SearchFiltersWindow("login");
            pending.RemainderBoxForTests.Text = "status:200";
            // A host typed but not yet added is included when Apply is clicked.
            pending.HostAddBoxForTests.Text = "api2.cursor.sh";
            pending.ApplyButtonForTests.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.AreEqual("status:200 -host:api2.cursor.sh", pending.AppliedQuery);

            var cancelled = new SearchFiltersWindow("login -host:keep.me");
            cancelled.RemainderBoxForTests.Text = "changed";
            cancelled.CancelButtonForTests.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.IsNull(cancelled.AppliedQuery);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await Task.Delay(150);
    }
}
