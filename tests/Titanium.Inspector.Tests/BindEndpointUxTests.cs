using System.ComponentModel;
using System.Linq;
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
            await WaitUntil(() => interception.IsRunning &&
                vm.EndpointStatusText.StartsWith("Proxy running", StringComparison.Ordinal));

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
            await WaitUntil(() => recorder.SetCount >= 1);
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
    public async Task InvalidBindAddress_ClearsStartBusy_SoNextStartSucceeds()
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
            vm.BindAddress = "::::";
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Invalid bind address", StringComparison.Ordinal));
            Assert.IsFalse(interception.IsRunning);
            StringAssert.Contains(vm.StatusText, MainWindowViewModel.InvalidBindAddressMessage("::::"));

            vm.BindAddress = "*";
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning &&
                vm.EndpointStatusText.StartsWith("Proxy running", StringComparison.Ordinal));
            Assert.AreEqual($"Proxy running on 0.0.0.0:{vm.BindPort}", vm.EndpointStatusText);

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task LocalhostAlias_StartsLoopbackListener()
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
            vm.BindAddress = "localhost";
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning &&
                vm.EndpointStatusText.StartsWith("Proxy running", StringComparison.Ordinal));
            Assert.AreEqual($"Proxy running on localhost:{vm.BindPort}", vm.EndpointStatusText);

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void BindPortText_ParsesStarEmptyAndRejectsJunk()
    {
        Assert.IsTrue(MainWindowViewModel.TryParseBindPort("", out var empty));
        Assert.AreEqual(0, empty);
        Assert.IsTrue(MainWindowViewModel.TryParseBindPort("  *  ", out var star));
        Assert.AreEqual(0, star);
        Assert.IsTrue(MainWindowViewModel.TryParseBindPort("0", out var zero));
        Assert.AreEqual(0, zero);
        Assert.IsTrue(MainWindowViewModel.TryParseBindPort("65535", out var max));
        Assert.AreEqual(65535, max);
        Assert.IsFalse(MainWindowViewModel.TryParseBindPort("abc", out _));
        Assert.IsFalse(MainWindowViewModel.TryParseBindPort("99999", out _));
        Assert.AreEqual("*", MainWindowViewModel.FormatBindPortText(0));
        Assert.AreEqual("8866", MainWindowViewModel.FormatBindPortText(8866));
    }

    [TestMethod]
    public void BindPortText_EmptyHasNoError_InvalidCharsAreFriendly()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);
            var errors = (INotifyDataErrorInfo)vm;

            vm.BindPortText = "";
            Assert.IsFalse(vm.HasErrors);
            Assert.AreEqual(0, vm.BindPort);
            Assert.AreEqual(0, errors.GetErrors(nameof(MainWindowViewModel.BindPortText)).Cast<object>().Count());

            vm.BindPortText = "abc";
            Assert.IsTrue(vm.HasErrors);
            var messages = errors.GetErrors(nameof(MainWindowViewModel.BindPortText)).Cast<string>().ToList();
            Assert.AreEqual(1, messages.Count);
            Assert.AreEqual(MainWindowViewModel.InvalidBindPortMessage("abc"), messages[0]);

            vm.BindPort = 0;
            Assert.AreEqual("*", vm.BindPortText);
            Assert.IsFalse(vm.HasErrors);

            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task InvalidBindPort_ClearsStartBusy_StarStartsEphemeral()
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

            vm.BindAddress = "127.0.0.1";
            vm.BindPortText = "nope";
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Invalid port", StringComparison.Ordinal));
            Assert.IsFalse(interception.IsRunning);
            StringAssert.Contains(vm.StatusText, MainWindowViewModel.InvalidBindPortMessage("nope"));

            vm.BindPortText = "*";
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning &&
                vm.EndpointStatusText.StartsWith("Proxy running", StringComparison.Ordinal) &&
                vm.BindPort > 0);
            Assert.AreEqual($"Proxy running on 127.0.0.1:{vm.BindPort}", vm.EndpointStatusText);
            Assert.AreEqual(vm.BindPort.ToString(), vm.BindPortText);

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
            await WaitUntil(() => recorder.SetCount >= 1 &&
                vm.StatusText.Contains("System proxy enabled", StringComparison.Ordinal));

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
