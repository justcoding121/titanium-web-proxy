using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Ensures user-changed Inspector settings survive disk save and a fresh ViewModel launch
/// (not reset to factory defaults).
/// </summary>
[TestClass]
public class SettingsPersistenceTests
{
    [TestMethod]
    public void SettingsService_RoundTripsAllUserFacingFields()
    {
        var path = TempSettingsPath();
        try
        {
            var svc = new SettingsService(path);
            svc.Current.AutoStartCapture = false;
            svc.Current.AutoSystemProxyOnStart = false;
            svc.Current.DecryptHttps = true;
            svc.Current.BindAddress = "0.0.0.0";
            svc.Current.BindPort = 9123;
            svc.Current.BreakpointEnabled = true;
            svc.Current.BreakpointUrlFilter = "*/api/*";
            svc.Current.BreakpointOnResponse = true;
            svc.Current.ScriptOnRequest = "abort";
            svc.Current.ScriptOnResponse = "set-status 418";
            svc.Current.AutoResponderEnabled = true;
            svc.Current.AutoResponderRules =
            [
                new AutoResponderRuleDto
                {
                    MatchUrl = "https://example.com/*",
                    StatusCode = 201,
                    Body = "ok",
                    ContentType = "text/plain",
                    Enabled = true,
                },
            ];
            svc.Current.IgnoreServerCertificateErrors = true;
            svc.Current.UpdateChannel = "Beta";
            svc.Current.LoggingEnableFile = true;
            svc.Current.LoggingMinimumLevel = "Debug";
            svc.Current.LoggingFilePath = @"C:\logs\inspector.log";
            svc.Current.SessionGridLayout = new SessionGridLayoutDto
            {
                SortColumnKey = "Host",
                SortDirection = ListSortDirection.Ascending,
                Columns =
                [
                    new SessionGridColumnStateDto { Key = "Host", Width = 200, DisplayIndex = 0 },
                    new SessionGridColumnStateDto { Key = "URL", Width = 360, DisplayIndex = 1 },
                ],
            };
            svc.Save();

            var loaded = new SettingsService(path).Current;
            Assert.IsFalse(loaded.AutoStartCapture);
            Assert.IsFalse(loaded.AutoSystemProxyOnStart);
            Assert.IsTrue(loaded.DecryptHttps);
            Assert.AreEqual("0.0.0.0", loaded.BindAddress);
            Assert.AreEqual(9123, loaded.BindPort);
            Assert.IsTrue(loaded.BreakpointEnabled);
            Assert.AreEqual("*/api/*", loaded.BreakpointUrlFilter);
            Assert.IsTrue(loaded.BreakpointOnResponse);
            Assert.AreEqual("abort", loaded.ScriptOnRequest);
            Assert.AreEqual("set-status 418", loaded.ScriptOnResponse);
            Assert.IsTrue(loaded.AutoResponderEnabled);
            Assert.AreEqual(1, loaded.AutoResponderRules.Count);
            Assert.AreEqual(201, loaded.AutoResponderRules[0].StatusCode);
            Assert.IsTrue(loaded.IgnoreServerCertificateErrors);
            Assert.AreEqual("Beta", loaded.UpdateChannel);
            Assert.IsTrue(loaded.LoggingEnableFile);
            Assert.AreEqual("Debug", loaded.LoggingMinimumLevel);
            Assert.AreEqual(@"C:\logs\inspector.log", loaded.LoggingFilePath);
            Assert.IsNotNull(loaded.SessionGridLayout);
            Assert.AreEqual("Host", loaded.SessionGridLayout!.SortColumnKey);
            Assert.AreEqual(ListSortDirection.Ascending, loaded.SessionGridLayout.SortDirection);
            Assert.AreEqual(2, loaded.SessionGridLayout.Columns.Count);
            Assert.AreEqual(360, loaded.SessionGridLayout.Columns[1].Width);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void InspectorSettings_DefaultLogging_MatchesBuildPosture()
    {
        var settings = new InspectorSettings();
        Assert.IsTrue(settings.LoggingEnabled);
        Assert.IsFalse(settings.IgnoreServerCertificateErrors);
#if DEBUG
        Assert.IsTrue(settings.LoggingEnableFile);
        Assert.AreEqual("Debug", settings.LoggingMinimumLevel);
#else
        Assert.IsFalse(settings.LoggingEnableFile);
        Assert.AreEqual("Error", settings.LoggingMinimumLevel);
#endif
    }

    [TestMethod]
    public void ViewModel_ImmediateSetters_PersistWithoutShutdown()
    {
        var path = TempSettingsPath();
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            Assert.IsTrue(vm.AutoStartCapture);
            Assert.IsTrue(vm.AutoSystemProxyOnStart);

            vm.AutoStartCapture = false;
            vm.AutoSystemProxyOnStart = false;
            vm.BreakpointOnResponse = true;
            vm.Breakpoints.Enabled = true;
            vm.Breakpoints.UrlFilter = "*/persist/*";

            var disk = new SettingsService(path).Current;
            Assert.IsFalse(disk.AutoStartCapture);
            Assert.IsFalse(disk.AutoSystemProxyOnStart);
            Assert.IsTrue(disk.BreakpointOnResponse);
            Assert.IsTrue(disk.BreakpointEnabled);
            Assert.AreEqual("*/persist/*", disk.BreakpointUrlFilter);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ViewModel_EnsureShutdown_PersistsBindAndScripts_ForNextLaunch()
    {
        var path = TempSettingsPath();
        try
        {
            using (var interception = new InterceptionService(new RecordingSystemProxyController()))
            {
                var settings = new SettingsService(path);
                var registry = new SessionRegistry();
                var vm = new MainWindowViewModel(
                    new SessionStreamBuffer(registry),
                    registry,
                    new UpdateService(settings),
                    settings,
                    interception);

                vm.AutoStartCapture = false;
                vm.AutoSystemProxyOnStart = false;
                vm.BindAddress = "0.0.0.0";
                vm.BindPort = 9456;
                vm.ScriptOnRequest = "abort";
                vm.ScriptOnResponse = "set-status 503";
                vm.Breakpoints.Enabled = true;
                vm.Breakpoints.UrlFilter = "*/next-launch/*";
                vm.BreakpointOnResponse = true;

                // Bind/scripts only flush via PersistSettings (start/stop/shutdown).
                vm.EnsureShutdown();
            }

            using var interception2 = new InterceptionService(new RecordingSystemProxyController());
            var settings2 = new SettingsService(path);
            var registry2 = new SessionRegistry();
            var vm2 = new MainWindowViewModel(
                new SessionStreamBuffer(registry2),
                registry2,
                new UpdateService(settings2),
                settings2,
                interception2);

            Assert.IsFalse(vm2.AutoStartCapture);
            Assert.IsFalse(vm2.AutoSystemProxyOnStart);
            Assert.AreEqual("0.0.0.0", vm2.BindAddress);
            Assert.AreEqual(9456, vm2.BindPort);
            Assert.AreEqual("abort", vm2.ScriptOnRequest);
            Assert.AreEqual("set-status 503", vm2.ScriptOnResponse);
            Assert.IsTrue(vm2.Breakpoints.Enabled);
            Assert.AreEqual("*/next-launch/*", vm2.Breakpoints.UrlFilter);
            Assert.IsTrue(vm2.BreakpointOnResponse);
            Assert.IsFalse(vm2.DecryptHttps);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task ViewModel_DecryptHttps_PersistsAcrossLaunch_WhenTrusted()
    {
        var path = TempSettingsPath();
        try
        {
            using (var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            })
            {
                var settings = new SettingsService(path);
                settings.Current.AutoStartCapture = false;
                settings.Current.AutoSystemProxyOnStart = false;
                settings.Save();

                var registry = new SessionRegistry();
                var dialogs = new ScriptedInspectorDialogs();
                var vm = new MainWindowViewModel(
                    new SessionStreamBuffer(registry),
                    registry,
                    new UpdateService(settings),
                    settings,
                    interception,
                    dialogs);

                vm.BindPort = 0;
                vm.StartCaptureCommand.Execute(null);
                await WaitUntil(() => interception.IsRunning);

                Assert.IsTrue(interception.InstallRootCertificate(false));
                vm.DecryptHttps = true;
                await WaitUntil(() => vm.DecryptHttps);

                Assert.IsTrue(vm.DecryptHttps);
                vm.EnsureShutdown();
            }

            var settings2 = new SettingsService(path);
            Assert.IsTrue(settings2.Current.DecryptHttps, "DecryptHttps should be on disk after enable");

            using var interception2 = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var registry2 = new SessionRegistry();
            var vm2 = new MainWindowViewModel(
                new SessionStreamBuffer(registry2),
                registry2,
                new UpdateService(settings2),
                settings2,
                interception2);

            Assert.IsTrue(vm2.DecryptHttps, "Fresh VM should load DecryptHttps=true from settings");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ViewModel_AutoResponder_PersistsAcrossLaunch()
    {
        var path = TempSettingsPath();
        try
        {
            using (var interception = new InterceptionService(new RecordingSystemProxyController()))
            {
                var settings = new SettingsService(path);
                var registry = new SessionRegistry();
                var vm = new MainWindowViewModel(
                    new SessionStreamBuffer(registry),
                    registry,
                    new UpdateService(settings),
                    settings,
                    interception);

                vm.AutoResponder.Enabled = true;
                vm.AutoResponderMatch = "https://persist.example/*";
                vm.AutoResponderStatus = 209;
                vm.AutoResponderBody = "persisted-body";
                vm.AutoResponderContentType = "application/json";
                vm.AddAutoResponderRuleCommand.Execute(null);
                vm.EnsureShutdown();
            }

            using var interception2 = new InterceptionService(new RecordingSystemProxyController());
            var settings2 = new SettingsService(path);
            var registry2 = new SessionRegistry();
            var vm2 = new MainWindowViewModel(
                new SessionStreamBuffer(registry2),
                registry2,
                new UpdateService(settings2),
                settings2,
                interception2);

            Assert.IsTrue(vm2.AutoResponder.Enabled);
            Assert.AreEqual(1, vm2.AutoResponder.Rules.Count);
            Assert.AreEqual("https://persist.example/*", vm2.AutoResponder.Rules[0].MatchUrl);
            Assert.AreEqual(209, vm2.AutoResponder.Rules[0].StatusCode);
            Assert.AreEqual("persisted-body", vm2.AutoResponder.Rules[0].Body);
            Assert.AreEqual("application/json", vm2.AutoResponder.Rules[0].ContentType);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task TryAutoStart_IgnoresUiClobber_OfAutoSystemProxyPreference()
    {
        var path = TempSettingsPath();
        try
        {
            // Disk says: auto-start + auto system proxy (factory / first-launch intent).
            var settings = new SettingsService(path);
            Assert.IsTrue(settings.Current.AutoStartCapture);
            Assert.IsTrue(settings.Current.AutoSystemProxyOnStart);

            var recorder = new RecordingSystemProxyController();
            using var interception = new InterceptionService(recorder);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = 0;

            // Simulate Avalonia MenuItem TwoWay writing false before Opened and persisting it.
            vm.AutoSystemProxyOnStart = false;
            Assert.IsFalse(new SettingsService(path).Current.AutoSystemProxyOnStart);

            await vm.TryAutoStartAsync();

            Assert.IsTrue(vm.AutoSystemProxyOnStart, "Launch snapshot should restore clobbered preference");
            Assert.IsTrue(interception.IsRunning, vm.StatusText);
            Assert.IsTrue(vm.SystemProxy, vm.StatusText);
            Assert.AreEqual(1, recorder.SetCount);
            Assert.IsTrue(new SettingsService(path).Current.AutoSystemProxyOnStart);

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ToggleCommands_AndSessionGridLayout_CoverNewLaunchSeams()
    {
        var path = TempSettingsPath();
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            Assert.IsTrue(vm.AutoStartCapture);
            Assert.IsTrue(vm.ToggleAutoStartCaptureCommand.CanExecute(null));
            vm.ToggleAutoStartCaptureCommand.Execute(null);
            Assert.IsFalse(vm.AutoStartCapture);

            Assert.IsTrue(vm.AutoSystemProxyOnStart);
            vm.ToggleAutoSystemProxyOnStartCommand.Execute(null);
            Assert.IsFalse(vm.AutoSystemProxyOnStart);

            Assert.IsFalse(vm.DecryptHttps);
            // DecryptHttps toggle when interception is stopped should stay off / set status.
            vm.ToggleDecryptHttpsCommand.Execute(null);

            var layout = new SessionGridLayoutDto
            {
                SortColumnKey = "Id",
                SortDirection = ListSortDirection.Ascending,
                Columns =
                [
                    new SessionGridColumnStateDto { Key = "Id", Width = 80, DisplayIndex = 0 },
                ],
            };
            Assert.IsNull(vm.GetSessionGridLayout());
            vm.PersistSessionGridLayout(layout);
            Assert.AreEqual("Id", vm.GetSessionGridLayout()?.SortColumnKey);
            Assert.AreEqual("Id", new SettingsService(path).Current.SessionGridLayout?.SortColumnKey);

            // System proxy without capture running hits the early-return path.
            Assert.IsFalse(interception.IsRunning);
            vm.SystemProxy = true;
            Assert.IsFalse(vm.SystemProxy);
            StringAssert.Contains(vm.StatusText, "Start the proxy");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void SettingsService_MissingFile_UsesFactoryDefaults()
    {
        var path = TempSettingsPath();
        TryDelete(path);
        var fresh = new SettingsService(path);
        Assert.IsTrue(fresh.Current.AutoStartCapture);
        Assert.IsTrue(fresh.Current.AutoSystemProxyOnStart);
        Assert.IsFalse(fresh.Current.DecryptHttps);
        Assert.AreEqual("127.0.0.1", fresh.Current.BindAddress);
        Assert.AreEqual(8866, fresh.Current.BindPort);
        TryDelete(path);
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "twp-settings-persist-" + Guid.NewGuid().ToString("N") + ".json");

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

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(condition(), "Timed out waiting for condition");
    }
}
