using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests;

/// <summary>
/// UI-layer smoke without Avalonia headless session (avoids platform hangs on CI).
/// Drives ViewModel commands the same way the Avalonia UI binds them.
/// </summary>
[TestClass]
public class InspectorHeadlessUiE2ETests
{
    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task Commands_StartInstallProxy_AutoResponder_Composer()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-insp-e2e-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new SettingsService(settingsPath);
        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(settings);
        var recorder = new RecordingSystemProxyController();
        var interception = new InterceptionService(recorder);
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception);

        Assert.IsNotNull(vm.StartCaptureCommand);
        Assert.IsNotNull(vm.InstallCaCommand);
        Assert.IsNotNull(vm.ToggleSystemProxyCommand);

        vm.BindPort = CliProcessHarness.GetFreePort();
        vm.BindAddress = "127.0.0.1";

        // Execute commands like the Avalonia bindings do (RelayCommand is async void).
        vm.StartCaptureCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!interception.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(interception.IsRunning, vm.StatusText);
        Assert.IsTrue(vm.Capturing);
        Assert.IsTrue(vm.StatusText.Contains("quic", StringComparison.OrdinalIgnoreCase), vm.StatusText);

        vm.InstallCaCommand.Execute(null);
        await Task.Delay(100);

        vm.ToggleSystemProxyCommand.Execute(null);
        await Task.Delay(100);
        Assert.AreEqual(1, recorder.SetCount, "System proxy should go through controller seam");
        Assert.IsTrue(vm.SystemProxy);
        Assert.IsTrue(vm.StatusText.Contains("quic", StringComparison.OrdinalIgnoreCase), vm.StatusText);

        vm.AutoResponderMatch = "*ui-e2e*";
        vm.AutoResponderStatus = 201;
        vm.AutoResponderBody = "from-ui";
        vm.AddAutoResponderRuleCommand.Execute(null);
        Assert.AreEqual(1, vm.AutoResponder.Rules.Count);

        using var origin = new EchoOrigin();
        vm.ComposerMethod = "GET";
        vm.ComposerUrl = origin.BaseUrl + "ui-composer";
        vm.SendComposerCommand.Execute(null);
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (!vm.StatusText.Contains("Composer", StringComparison.OrdinalIgnoreCase) &&
               !vm.StatusText.Contains("HTTP", StringComparison.OrdinalIgnoreCase) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(
            vm.StatusText.Contains("Composer", StringComparison.OrdinalIgnoreCase) ||
            vm.StatusText.Contains("HTTP", StringComparison.OrdinalIgnoreCase) ||
            vm.Sessions.Count > 0,
            vm.StatusText);

        vm.StopCaptureCommand.Execute(null);
        await Task.Delay(100);
        Assert.IsFalse(interception.IsRunning);

        try
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
        catch
        {
            // ignore
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void ViewModel_ExposesBindAndInterception()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-insp-vm-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new SettingsService(settingsPath);
        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(settings);
        var vm = new MainWindowViewModel(
            buffer,
            registry,
            updates,
            settings,
            new InterceptionService(new RecordingSystemProxyController()));

        Assert.AreEqual("127.0.0.1", vm.BindAddress);
        Assert.AreEqual(8866, vm.BindPort);
        Assert.IsNotNull(vm.Interception);
        Assert.IsFalse(vm.Interception.IsRunning);
    }
}
