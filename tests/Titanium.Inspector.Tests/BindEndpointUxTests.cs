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
            using var interception = new InterceptionService(recorder);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = GetFreePort();

            Assert.IsTrue(vm.BindFieldsEnabled);
            Assert.AreEqual("Not listening", vm.EndpointStatusText);
            Assert.AreEqual("Start interception", vm.InterceptToggleText);

            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning && !vm.BindFieldsEnabled);

            Assert.IsFalse(vm.BindFieldsEnabled);
            Assert.AreEqual($"Listening {vm.BindAddress}:{vm.BindPort}", vm.EndpointStatusText);
            Assert.AreEqual("Stop interception", vm.InterceptToggleText);

            vm.StopCaptureCommand.Execute(null);
            await WaitUntil(() => !interception.IsRunning && vm.EndpointStatusText == "Not listening");

            Assert.IsTrue(vm.BindFieldsEnabled);
            Assert.AreEqual("Not listening", vm.EndpointStatusText);
            Assert.AreEqual("Start interception", vm.InterceptToggleText);

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
            using var interception = new InterceptionService(recorder);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = GetFreePort();

            vm.ToggleInterceptCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning && vm.InterceptToggleText == "Stop interception");
            Assert.IsFalse(vm.BindFieldsEnabled);

            vm.ToggleInterceptCommand.Execute(null);
            await WaitUntil(() =>
                !interception.IsRunning &&
                vm.EndpointStatusText == "Not listening" &&
                vm.InterceptToggleText == "Start interception");

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
            using var interception = new InterceptionService(recorder);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = GetFreePort();

            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning);

            vm.SystemProxy = true;
            Assert.IsTrue(vm.SystemProxy, vm.StatusText);
            Assert.AreEqual(1, recorder.SetCount);

            vm.StopCaptureCommand.Execute(null);
            await WaitUntil(() => !interception.IsRunning && vm.EndpointStatusText == "Not listening");
            Assert.IsFalse(vm.SystemProxy);
            Assert.IsTrue(recorder.RestoreCount >= 1);

            var setAfterStop = recorder.SetCount;
            vm.BindPort = GetFreePort();
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
            using var interception = new InterceptionService(recorder);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = GetFreePort();
            Assert.IsTrue(vm.AutoSystemProxyOnStart);

            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning && vm.SystemProxy);

            Assert.IsTrue(vm.SystemProxy, vm.StatusText);
            Assert.AreEqual(1, recorder.SetCount);

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

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
