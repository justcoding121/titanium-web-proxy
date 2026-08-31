using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionPipelineTests
{
    [TestMethod]
    public async Task SessionStreamBuffer_PublishesSessionAdded()
    {
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        var buffer = new SessionStreamBuffer(registry);
        var snap = buffer.CreatePlaceholder("GET", "https://example.com/");
        var tcs = new TaskCompletionSource();
        buffer.SessionAdded += s =>
        {
            registry.Add(s);
            tcs.TrySetResult();
        };
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
    public async Task SessionAdded_UpdatesSessionCount_WithoutOverwritingStatusText()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var buffer = new SessionStreamBuffer(registry);
            var dialogs = new ScriptedInspectorDialogs { DeviceCaSetupResult = false };
            var vm = new MainWindowViewModel(
                buffer,
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()),
                dialogs);

            vm.StatusText = "Pinned tip";
            Assert.AreEqual("Sessions: 0", vm.SessionCountText);

            var tcs = new TaskCompletionSource();
            buffer.SessionAdded += _ => tcs.TrySetResult();
            var snap = buffer.CreatePlaceholder("GET", "https://example.com/");
            buffer.Publish(snap);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual("Pinned tip", vm.StatusText);
            Assert.AreEqual("Sessions: 1", vm.SessionCountText);

            vm.HideTunnelsFilter = true;
            Assert.IsTrue(vm.SearchQuery.Contains("hide:tunnel", StringComparison.Ordinal));
            Assert.IsTrue(vm.HideTunnelsFilter);
            vm.ErrorsOnlyFilter = true;
            Assert.IsTrue(vm.SearchQuery.Contains("is:error", StringComparison.Ordinal));
            Assert.AreEqual("Sessions: 0 / 1", vm.SessionCountText);

            vm.ClearFiltersCommand.Execute(null);
            await Task.Delay(50);
            Assert.AreEqual("", vm.SearchQuery);
            Assert.IsFalse(vm.HideTunnelsFilter);
            Assert.AreEqual("Sessions: 1", vm.SessionCountText);

            vm.DeviceCaSetupCommand.Execute(null);
            await Task.Delay(50);
            Assert.AreEqual(1, dialogs.DeviceCaSetupCalls);
            Assert.AreEqual("Pinned tip", vm.StatusText);
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
    public void SelectingSession_OpensDetails_CloseHidesPane()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-details-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));
            Assert.IsFalse(vm.ShowSessionDetails);

            var snap = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                Host = "example.com",
            };
            vm.SelectedSession = snap;
            Assert.IsTrue(vm.ShowSessionDetails);

            Assert.IsTrue(vm.CloseSessionDetailsCommand.CanExecute(null));
            vm.CloseSessionDetailsCommand.Execute(null);
            Assert.IsFalse(vm.ShowSessionDetails);
            Assert.AreSame(snap, vm.SelectedSession);

            vm.SelectedSession = new SessionSnapshot
            {
                Id = 2,
                Method = "POST",
                Url = "https://example.com/api",
                Host = "example.com",
            };
            Assert.IsTrue(vm.ShowSessionDetails);
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
    public void WsFramesTab_OnlyForWebSocket_AndToolsMenuOpensPane()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-ws-tools-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            vm.SelectedSession = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                IsWebSocket = false,
            };
            Assert.IsFalse(vm.ShowWsFramesTab);
            Assert.AreEqual(0, vm.SelectedOuterPaneIndex);
            Assert.IsTrue(vm.HasSelectedSession);
            Assert.IsFalse(vm.ShowInspectEmpty);

            vm.SelectedInspectTabIndex = 3;
            vm.SelectedSession = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://example.com/ws",
                IsWebSocket = false,
            };
            Assert.AreEqual(0, vm.SelectedInspectTabIndex);

            vm.SelectedSession = new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "wss://example.com/ws",
                IsWebSocket = true,
            };
            Assert.IsTrue(vm.ShowWsFramesTab);

            vm.CloseSessionDetailsCommand.Execute(null);
            Assert.IsFalse(vm.ShowSessionDetails);
            Assert.IsTrue(vm.OpenToolsAutoResponderCommand.CanExecute(null));
            vm.OpenToolsAutoResponderCommand.Execute(null);
            Assert.IsTrue(vm.ShowSessionDetails);
            Assert.AreEqual(1, vm.SelectedOuterPaneIndex);
            Assert.AreEqual(2, vm.SelectedToolsTabIndex);
            Assert.AreEqual(6, vm.SelectedDetailTabIndex);

            vm.SelectedDetailTabIndex = 5;
            Assert.AreEqual(1, vm.SelectedOuterPaneIndex);
            Assert.AreEqual(1, vm.SelectedToolsTabIndex);
            vm.SelectedDetailTabIndex = 2;
            Assert.AreEqual(0, vm.SelectedOuterPaneIndex);
            Assert.AreEqual(2, vm.SelectedInspectTabIndex);
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
    public async Task Start_EnablesHttp2Http3AndFastEcdsaLeafCertificates()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        await interception.StartAsync(System.Net.IPAddress.Loopback, 0);
        try
        {
            Assert.IsTrue(interception.IsRunning);
            Assert.IsTrue(interception.BoundPort > 0);
            Assert.IsTrue(interception.Http2Enabled);
            Assert.AreEqual(InterceptionService.IsHttp3Supported, interception.Http3Enabled);

            var field = typeof(InterceptionService).GetField("_proxy",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            var proxy = (Titanium.Web.Proxy.ProxyServer?)field!.GetValue(interception);
            Assert.IsNotNull(proxy);
            Assert.AreEqual(Titanium.Web.Proxy.Network.CertificateEngine.BouncyCastleFast,
                proxy!.CertificateManager.CertificateEngine);
            Assert.AreEqual(Titanium.Web.Proxy.Network.CertificateKeyAlgorithm.EcdsaP256,
                proxy.CertificateManager.LeafCertificateKeyAlgorithm);
            Assert.IsTrue(proxy.CertificateManager.SaveFakeCertificates);
        }
        finally
        {
            interception.Stop();
        }
    }

    [TestMethod]
    public async Task FirstCapturedHttpSession_HasIdOne_EvenWithBreakpointsAssigned()
    {
        using var origin = new System.Net.HttpListener();
        // Use Echo via TcpListener pattern from harness is elsewhere; keep local listener here.
        var portListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        portListener.Start();
        var originPort = ((System.Net.IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();
        origin.Prefixes.Add($"http://127.0.0.1:{originPort}/");
        origin.Start();
        _ = Task.Run(async () =>
        {
            while (origin.IsListening)
            {
                try
                {
                    var ctx = await origin.GetContextAsync();
                    var bytes = System.Text.Encoding.UTF8.GetBytes("ok");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch
                {
                    return;
                }
            }
        });

        using var interception = new InterceptionService(new RecordingSystemProxyController());
        // Same wiring as the desktop VM: Breakpoints instance present but disabled.
        interception.Breakpoints = new BreakpointViewModel();
        SessionSnapshot? captured = null;
        interception.SessionCaptured += (_, s) => captured = s;

        await interception.StartAsync(System.Net.IPAddress.Loopback, 0);
        try
        {
            using var handler = new System.Net.Http.HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
            };
            using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetAsync($"http://127.0.0.1:{originPort}/first-id");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (captured is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.IsNotNull(captured);
            Assert.AreEqual(1L, captured!.Id);
        }
        finally
        {
            interception.Stop();
            origin.Stop();
            origin.Close();
        }
    }
}
