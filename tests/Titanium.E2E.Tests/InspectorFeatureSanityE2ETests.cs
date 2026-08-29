using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests;

/// <summary>
/// Cross-platform Inspector feature sanity + CA elevation UX (portable E2E-UI).
/// </summary>
[TestClass]
public class InspectorFeatureSanityE2ETests
{
    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task FeatureSanity_CaptureProxyCaToolsComposerExport()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-feat-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new SettingsService(settingsPath);
        settings.Current.AutoStartCapture = false;
        settings.Current.AutoSystemProxyOnStart = false;
        settings.Current.IgnoreServerCertificateErrors = true;
        settings.Save();

        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(settings);
        var recorder = new RecordingSystemProxyController();
        var interception = new InterceptionService(recorder) { UseInMemoryTrustState = true };
        var dialogs = new ScriptedInspectorDialogs();
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception, dialogs);

        try
        {
            vm.BindPort = CliProcessHarness.GetFreePort();
            vm.BindAddress = "127.0.0.1";

            vm.StartCaptureCommand.Execute(null);
            await WaitAsync(() => interception.IsRunning);
            Assert.IsTrue(interception.IsRunning, vm.StatusText);
            Assert.IsTrue(vm.Capturing);

            vm.ToggleSystemProxyCommand.Execute(null);
            await WaitAsync(() => recorder.SetCount >= 1);
            Assert.AreEqual(1, recorder.SetCount);
            Assert.IsTrue(vm.SystemProxy);

            vm.InstallCaCommand.Execute(null);
            await Task.Delay(80);
            Assert.IsTrue(interception.IsRootTrusted, vm.StatusText);

            vm.DecryptHttps = true;
            await WaitAsync(() => vm.DecryptHttps);
            Assert.IsTrue(interception.DecryptHttps);

            vm.OpenToolsComposerCommand.Execute(null);
            Assert.AreEqual(0, vm.SelectedToolsTabIndex);
            vm.OpenToolsBreakpointsCommand.Execute(null);
            Assert.AreEqual(1, vm.SelectedToolsTabIndex);
            vm.OpenToolsAutoResponderCommand.Execute(null);
            Assert.AreEqual(2, vm.SelectedToolsTabIndex);
            vm.OpenToolsScriptsCommand.Execute(null);
            Assert.AreEqual(3, vm.SelectedToolsTabIndex);

            vm.AutoResponderMatch = "*sanity*";
            vm.AutoResponderStatus = 209;
            vm.AutoResponderBody = "sanity-ok";
            vm.AddAutoResponderRuleCommand.Execute(null);
            Assert.IsTrue(vm.AutoResponder.Rules.Count >= 1);

            using var origin = new EchoOrigin();
            vm.ComposerMethod = "GET";
            vm.ComposerUrl = origin.BaseUrl + "sanity-composer";
            vm.SendComposerCommand.Execute(null);
            await WaitAsync(() =>
                vm.StatusText.Contains("Composer", StringComparison.OrdinalIgnoreCase) ||
                vm.StatusText.Contains("HTTP", StringComparison.OrdinalIgnoreCase) ||
                vm.Sessions.Count > 0);

            vm.ExportCaCommand.Execute(null);
            await Task.Delay(50);
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.StatusText));

            vm.ClearSessionsCommand.Execute(null);
            await Task.Delay(50);

            dialogs.RemoveRootCaResult = true;
            vm.UntrustCaCommand.Execute(null);
            await WaitAsync(() => dialogs.RemoveRootCaCalls > 0);
            Assert.IsFalse(vm.DecryptHttps);

            vm.ToggleSystemProxyCommand.Execute(null);
            await WaitAsync(() => recorder.RestoreCount >= 1);
            Assert.AreEqual(1, recorder.RestoreCount);

            vm.StopCaptureCommand.Execute(null);
            await WaitAsync(() => !interception.IsRunning);
        }
        finally
        {
            try { vm.EnsureShutdown(); } catch { /* ignore */ }
            try { File.Delete(settingsPath); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task InstallCa_UserTrustFails_ElevationAccepted_Trusts()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-elev-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new SettingsService(settingsPath);
        settings.Current.AutoStartCapture = false;
        settings.Current.AutoSystemProxyOnStart = false;
        settings.Save();

        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(settings);
        var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            FailNextUserTrustInstall = true,
        };
        var dialogs = new ScriptedInspectorDialogs { ElevateRootCaResult = true };
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception, dialogs);

        try
        {
            vm.BindPort = CliProcessHarness.GetFreePort();
            vm.StartCaptureCommand.Execute(null);
            await WaitAsync(() => interception.IsRunning);

            vm.InstallCaCommand.Execute(null);
            await WaitAsync(() => dialogs.ElevateRootCaCalls >= 1 || interception.IsRootTrusted);

            Assert.AreEqual(1, dialogs.ElevateRootCaCalls);
            Assert.IsTrue(interception.IsRootTrusted, vm.StatusText);
        }
        finally
        {
            try { vm.EnsureShutdown(); } catch { /* ignore */ }
            try { File.Delete(settingsPath); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task InstallCa_UserTrustFails_ElevationCancelled_StaysUntrusted()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-elev-cancel-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new SettingsService(settingsPath);
        settings.Current.AutoStartCapture = false;
        settings.Current.AutoSystemProxyOnStart = false;
        settings.Save();

        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(settings);
        var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            FailNextUserTrustInstall = true,
        };
        var dialogs = new ScriptedInspectorDialogs { ElevateRootCaResult = false };
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception, dialogs);

        try
        {
            vm.BindPort = CliProcessHarness.GetFreePort();
            vm.StartCaptureCommand.Execute(null);
            await WaitAsync(() => interception.IsRunning);

            vm.InstallCaCommand.Execute(null);
            await WaitAsync(() => dialogs.ElevateRootCaCalls >= 1);

            Assert.AreEqual(1, dialogs.ElevateRootCaCalls);
            Assert.IsFalse(interception.IsRootTrusted);
            StringAssert.Contains(vm.StatusText.ToLowerInvariant(), "cancel");
        }
        finally
        {
            try { vm.EnsureShutdown(); } catch { /* ignore */ }
            try { File.Delete(settingsPath); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void CurrentOs_ReportsPlatformForDiagnostics()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(RuntimeInformation.OSDescription));
        Assert.IsTrue(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(),
            RuntimeInformation.OSDescription);
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(40);
        Assert.IsTrue(condition(), "Timed out waiting for condition");
    }
}
