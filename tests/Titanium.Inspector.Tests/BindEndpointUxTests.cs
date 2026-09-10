using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class BindEndpointUxTests
{
    [TestMethod]
    public async Task BindFields_DisabledWhileRunning_EndpointStatusTracksLifecycle()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var recorder = new RecordingSystemProxyController();
            using var interception = new InterceptionService(recorder) { UseInMemoryTrustState = true };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = 0;

            Assert.IsTrue(vm.BindFieldsEnabled);
            Assert.IsFalse(vm.IsIntercepting);
            Assert.AreEqual("Proxy stopped", vm.EndpointStatusText);
            Assert.AreEqual("Start proxy", vm.InterceptToggleText);

            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning && !vm.BindFieldsEnabled);

            Assert.IsFalse(vm.BindFieldsEnabled);
            Assert.IsTrue(vm.IsIntercepting);
            Assert.AreEqual($"Proxy running on {vm.BindAddress}:{vm.BindPort}", vm.EndpointStatusText);
            Assert.AreEqual("Stop proxy", vm.InterceptToggleText);

            vm.StopCaptureCommand.Execute(null);
            await WaitUntil(() => !interception.IsRunning && vm.EndpointStatusText == "Proxy stopped");

            Assert.IsTrue(vm.BindFieldsEnabled);
            Assert.IsFalse(vm.IsIntercepting);
            Assert.AreEqual("Proxy stopped", vm.EndpointStatusText);
            Assert.AreEqual("Start proxy", vm.InterceptToggleText);

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task ToggleInterceptCommand_StartsAndStops()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var recorder = new RecordingSystemProxyController();
            using var interception = new InterceptionService(recorder) { UseInMemoryTrustState = true };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = 0;

            vm.ToggleInterceptCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning && vm.InterceptToggleText == "Stop proxy");
            Assert.IsFalse(vm.BindFieldsEnabled);

            vm.ToggleInterceptCommand.Execute(null);
            await WaitUntil(() =>
                !interception.IsRunning &&
                vm.EndpointStatusText == "Proxy stopped" &&
                vm.InterceptToggleText == "Start proxy");

            Assert.IsTrue(vm.BindFieldsEnabled);

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task StopWithSystemProxy_ReenablesOnNextStart()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var recorder = new RecordingSystemProxyController();
            using var interception = new InterceptionService(recorder) { UseInMemoryTrustState = true };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = 0;

            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning);

            vm.SystemProxy = true;
            Assert.IsTrue(vm.SystemProxy, vm.StatusText);
            Assert.AreEqual(1, recorder.SetCount);

            vm.StopCaptureCommand.Execute(null);
            await WaitUntil(() => !interception.IsRunning && vm.EndpointStatusText == "Proxy stopped");
            Assert.IsFalse(vm.SystemProxy);
            Assert.IsTrue(recorder.RestoreCount >= 1);

            var setAfterStop = recorder.SetCount;
            vm.BindPort = 0;
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning && vm.SystemProxy);

            Assert.IsTrue(vm.SystemProxy, vm.StatusText);
            Assert.IsTrue(recorder.SetCount > setAfterStop, "System proxy should be re-applied on the new endpoint");

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task ManualStart_WithAutoSystemProxyOnStart_EnablesSystemProxy()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = true;
            settings.Save();

            var recorder = new RecordingSystemProxyController();
            using var interception = new InterceptionService(recorder) { UseInMemoryTrustState = true };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = 0;
            Assert.IsTrue(vm.AutoSystemProxyOnStart);

            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning && vm.SystemProxy);

            Assert.IsTrue(vm.SystemProxy, vm.StatusText);
            Assert.AreEqual(1, recorder.SetCount);
            StringAssert.Contains(
                vm.StatusText,
                "System proxy enabled",
                "Auto system proxy on start should surface enable guidance, not wipe it with Ready");

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "twp-bind-ux-" + Guid.NewGuid().ToString("N") + ".json");

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

        Assert.IsTrue(condition(), "Condition not met within timeout");
    }
}
