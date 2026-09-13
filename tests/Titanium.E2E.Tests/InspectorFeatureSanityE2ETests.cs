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
        var pathPicker = new ScriptedInspectorPathPicker();
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception, dialogs, pathPicker);
        var exportCaPath = Path.Combine(Path.GetTempPath(), "twp-feat-ca-" + Guid.NewGuid().ToString("N") + ".cer");

        try
        {
            vm.BindPort = 0;
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
            // Wait for Composer *completion*. Matching bare "Composer" is wrong: SendComposerAsync
            // immediately sets "Composer sending…", so the wait would return while ReplayAsync is
            // still in flight and race ExportCaAsync on StatusText (clobbering "Exported CA").
            vm.SendComposerCommand.Execute(null);
            await WaitAsync(
                () => IsComposerSettled(vm),
                () => "Composer did not settle. Status=" + vm.StatusText + " sessions=" + vm.Sessions.Count);

            pathPicker.SavePath = exportCaPath;
            var savesBefore = pathPicker.SaveCalls;
            vm.ExportCaCommand.Execute(null);
            // Prefer durable signals (picker + file). StatusText alone is racy with transient revert
            // and any concurrent command that calls SetStatus / SetOutcomeStatus.
            await WaitAsync(
                () => pathPicker.SaveCalls > savesBefore && File.Exists(exportCaPath),
                () => "Export CA did not finish. Status=" + vm.StatusText
                      + " saves=" + pathPicker.SaveCalls
                      + " exists=" + File.Exists(exportCaPath));
            Assert.IsTrue(File.Exists(exportCaPath), vm.StatusText);
            Assert.AreEqual(savesBefore + 1, pathPicker.SaveCalls);
            Assert.IsNotNull(pathPicker.LastSaveFileTypes);
            Assert.AreEqual(2, pathPicker.LastSaveFileTypes!.Count);
            Assert.AreEqual("*.cer", pathPicker.LastSaveFileTypes[0].Pattern);
            Assert.AreEqual("*.pem", pathPicker.LastSaveFileTypes[1].Pattern);

            vm.ClearSessionsCommand.Execute(null);
            await Task.Delay(50);

            dialogs.RemoveRootCaResult = true;
            vm.UntrustCaCommand.Execute(null);
            await WaitAsync(
                () => dialogs.RemoveRootCaCalls > 0 && !vm.DecryptHttps && !interception.IsRootTrusted,
                () => "Untrust CA did not finish. DecryptHttps=" + vm.DecryptHttps
                      + " trusted=" + interception.IsRootTrusted
                      + " status=" + vm.StatusText
                      + " removeCalls=" + dialogs.RemoveRootCaCalls);
            Assert.IsFalse(vm.DecryptHttps);
            Assert.IsFalse(interception.IsRootTrusted);

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
            try { File.Delete(exportCaPath); } catch { /* ignore */ }
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
        var dialogs = new ScriptedInspectorDialogs { TrustRecoveryResult = TrustRecoveryChoice.Primary };
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception, dialogs);

        try
        {
            vm.BindPort = 0;
            vm.StartCaptureCommand.Execute(null);
            await WaitAsync(() => interception.IsRunning);

            vm.InstallCaCommand.Execute(null);
            await WaitAsync(() => dialogs.TrustRecoveryCalls >= 1 || interception.IsRootTrusted);

            Assert.AreEqual(1, dialogs.TrustRecoveryCalls);
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
        var dialogs = new ScriptedInspectorDialogs { TrustRecoveryResult = TrustRecoveryChoice.Cancel };
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception, dialogs);

        try
        {
            vm.BindPort = 0;
            vm.StartCaptureCommand.Execute(null);
            await WaitAsync(() => interception.IsRunning);

            vm.InstallCaCommand.Execute(null);
            await WaitAsync(
                () => dialogs.TrustRecoveryCalls >= 1
                      && vm.StatusText.Contains("cancel", StringComparison.OrdinalIgnoreCase),
                () => "Install CA cancel did not settle. Status=" + vm.StatusText
                      + " recoveryCalls=" + dialogs.TrustRecoveryCalls
                      + " trusted=" + interception.IsRootTrusted);

            Assert.AreEqual(1, dialogs.TrustRecoveryCalls);
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

    private static bool IsComposerSettled(MainWindowViewModel vm)
    {
        // Final outcomes from SendComposerAsync — never the in-flight "Composer sending…" busy text.
        var status = vm.StatusText;
        return status.Contains("Composer →", StringComparison.Ordinal)
               || status.Contains("Composer failed", StringComparison.OrdinalIgnoreCase)
               || vm.Sessions.Count > 0;
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string>? detail = null, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(40);
        Assert.IsTrue(condition(), detail?.Invoke() ?? "Timed out waiting for condition");
    }
}
