using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionPipelineTests
{
    [TestMethod]
    public async Task SessionStreamBuffer_PublishesToRegistry()
    {
        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var snap = buffer.CreatePlaceholder("GET", "https://example.com/");
        var tcs = new TaskCompletionSource();
        buffer.SessionAdded += _ => tcs.TrySetResult();
        buffer.Publish(snap);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, registry.VisibleSessions.Count);
        Assert.AreEqual(snap.Id, registry.TryGet(snap.Id)?.Id);
    }

    [TestMethod]
    public void AutoResponder_DefaultsDisabled()
    {
        var vm = new AutoResponderViewModel();
        Assert.IsFalse(vm.Enabled);
        Assert.AreEqual(0, vm.Rules.Count);
    }

    [TestMethod]
    public void BreakpointViewModel_TimeoutIs120Seconds()
    {
        var vm = new BreakpointViewModel();
        Assert.AreEqual(TimeSpan.FromSeconds(120), vm.Timeout);
    }

    [TestMethod]
    public void SettingsService_RoundTripsChannel()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(path);
            svc.Current.UpdateChannel = "Beta";
            svc.Save();
            var loaded = new SettingsService(path);
            Assert.AreEqual("Beta", loaded.Current.UpdateChannel);
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
    public void ShowSessionDetails_DefaultsTrue_AndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-details-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            Assert.IsTrue(settings.Current.ShowSessionDetails);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));
            Assert.IsTrue(vm.ShowSessionDetails);
            vm.ShowSessionDetails = false;
            var reloaded = new SettingsService(path);
            Assert.IsFalse(reloaded.Current.ShowSessionDetails);
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
    public void ShowSessionDetails_MissingJsonKey_DefaultsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-details-old-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"bindPort":8866,"decryptHttps":false}""");
            var loaded = new SettingsService(path);
            Assert.IsTrue(loaded.Current.ShowSessionDetails);
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
    public void HttpProtocols_DefaultAllOn_MissingJsonKeys_AndRejectLastOff()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-proto-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            Assert.IsTrue(settings.Current.EnableHttp11);
            Assert.IsTrue(settings.Current.EnableHttp2);
            Assert.IsTrue(settings.Current.EnableHttp3);

            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));
            Assert.IsTrue(vm.EnableHttp11);
            Assert.IsTrue(vm.EnableHttp2);
            Assert.IsTrue(vm.EnableHttp3);

            vm.EnableHttp2 = false;
            if (MainWindowViewModel.Http3Supported)
            {
                vm.EnableHttp3 = false;
            }

            vm.EnableHttp11 = false;
            Assert.IsTrue(vm.EnableHttp11);
            StringAssert.Contains(vm.StatusText, "at least one");

            var reloaded = new SettingsService(path);
            Assert.IsTrue(reloaded.Current.EnableHttp11);
            Assert.IsFalse(reloaded.Current.EnableHttp2);
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
    public void HttpProtocols_MissingJsonKeys_DefaultTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-proto-old-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"bindPort":8866,"decryptHttps":false}""");
            var loaded = new SettingsService(path);
            Assert.IsTrue(loaded.Current.EnableHttp11);
            Assert.IsTrue(loaded.Current.EnableHttp2);
            Assert.IsTrue(loaded.Current.EnableHttp3);
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
    public async Task Start_WithHttp2Disabled_ThenLiveToggle()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.EnableHttp2 = false;
        interception.EnableHttp3 = false;
        var port = GetFreeTcpPort();
        await interception.StartAsync(System.Net.IPAddress.Loopback, port);
        Assert.IsTrue(interception.IsRunning);
        Assert.IsFalse(interception.Http2Enabled);
        Assert.IsFalse(interception.Http3Enabled);

        interception.EnableHttp2 = true;
        interception.ApplyHttpProtocols();
        Assert.IsTrue(interception.Http2Enabled);

        interception.Stop();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
