using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class NetworkSettingsTests
{
    [TestMethod]
    public void Validate_RejectsSelfLoop_AndAcceptsAnotherProxy()
    {
        Assert.IsNull(InspectorNetworkSettings.Validate(false, "", 0, "127.0.0.1", 8866));
        Assert.AreEqual(
            InspectorNetworkSettings.HostRequired,
            InspectorNetworkSettings.Validate(true, "  ", 8080, "127.0.0.1", 8866));
        Assert.AreEqual(
            InspectorNetworkSettings.PortInvalid,
            InspectorNetworkSettings.Validate(true, "127.0.0.1", 0, "127.0.0.1", 8866));
        Assert.AreEqual(
            InspectorNetworkSettings.HostInvalid,
            InspectorNetworkSettings.Validate(true, "not a host", 8080, "127.0.0.1", 8866));
        Assert.AreEqual(
            InspectorNetworkSettings.SelfLoop,
            InspectorNetworkSettings.Validate(true, "[127.0.0.1]", 8866, "127.0.0.1", 8866));
        Assert.IsNull(InspectorNetworkSettings.Validate(true, "127.0.0.1", 8888, "127.0.0.1", 8866));
        Assert.AreEqual("http://127.0.0.1:8888", InspectorNetworkSettings.FormatUpstreamUri("127.0.0.1", 8888));
        Assert.AreEqual("http://[::1]:8888", InspectorNetworkSettings.FormatUpstreamUri("::1", 8888));
    }

    [TestMethod]
    public void SettingsFile_WithoutNewFields_KeepsTodayBehavior_AndResetClearsUpstream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-net-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{"bindAddress":"127.0.0.1","bindPort":8866}""");
        var settings = new SettingsService(path);
        Assert.IsFalse(settings.Current.UpstreamProxyEnabled);
        Assert.AreEqual(string.Empty, settings.Current.UpstreamProxyHost);
        Assert.AreEqual(InspectorNetworkSettings.DefaultUpstreamPort, settings.Current.UpstreamProxyPort);
        Assert.IsTrue(settings.Current.EnableHttp2);
        Assert.IsTrue(settings.Current.EnableHttp3);
        Assert.IsTrue(settings.Current.ReuseServerConnections);
        Assert.AreEqual(InspectorNetworkSettings.TlsDefault, settings.Current.OutboundTlsPreset);
        Assert.IsNull(InspectorNetworkSettings.FormatUpstreamUri(settings.Current));

        settings.Current.UpstreamProxyEnabled = true;
        settings.Current.UpstreamProxyHost = "10.0.0.2";
        settings.Current.UpstreamProxyPort = 3128;
        settings.Save();

        var reloaded = new SettingsService(path);
        Assert.IsTrue(reloaded.Current.UpstreamProxyEnabled);
        Assert.AreEqual("http://10.0.0.2:3128", InspectorNetworkSettings.FormatUpstreamUri(reloaded.Current));

        reloaded.ResetToFactoryDefaults();
        Assert.IsFalse(reloaded.Current.UpstreamProxyEnabled);
        Assert.AreEqual(InspectorNetworkSettings.DefaultUpstreamPort, reloaded.Current.UpstreamProxyPort);

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        Assert.IsFalse(doc.RootElement.GetProperty("upstreamProxyEnabled").GetBoolean());
    }

    [TestMethod]
    public void TlsPreset_MapsOutboundOnly()
    {
        Assert.AreEqual(SslProtocols.None, InspectorNetworkSettings.MapOutboundTls("Default"));
        Assert.AreEqual(SslProtocols.Tls12, InspectorNetworkSettings.MapOutboundTls("tls12"));
        var legacy = InspectorNetworkSettings.MapOutboundTls("Legacy");
        Assert.IsTrue(legacy.HasFlag(SslProtocols.Tls12));
        Assert.IsTrue(legacy.HasFlag(SslProtocols.Tls13));
#pragma warning disable SYSLIB0039
        Assert.IsTrue(legacy.HasFlag(SslProtocols.Tls));
        Assert.IsTrue(legacy.HasFlag(SslProtocols.Tls11));
#pragma warning restore SYSLIB0039
        Assert.AreEqual("network.summaryH1", InspectorNetworkSettings.ProtocolSummaryKey(false, false));
        Assert.AreEqual("network.summaryH3", InspectorNetworkSettings.ProtocolSummaryKey(false, true));
        Assert.AreEqual("network.summaryBoth", InspectorNetworkSettings.ProtocolSummaryKey(true, true));
    }

    [TestMethod]
    public async Task ProtocolToggles_ReachProxyServer_AndHttp3OffStaysTcp()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var originSaw = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ServeOnce(origin, originSaw, "proto-ok"));

        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.PreferHttp2 = false;
        interception.PreferHttp3 = false;
        interception.ReuseServerConnections = false;
        interception.OutboundTlsProtocols = SslProtocols.Tls12;
        await interception.StartAsync(IPAddress.Loopback, 0);
        var proxy = ProxyOf(interception);
        Assert.IsFalse(proxy.EnableHttp2);
#pragma warning disable TWP001
        Assert.IsFalse(proxy.EnableHttp3);
#pragma warning restore TWP001
        Assert.IsFalse(proxy.EnableConnectionPool);
        Assert.AreEqual(SslProtocols.Tls12, proxy.SupportedServerSslProtocols);
        Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, proxy.SupportedSslProtocols);

        proxy.Http3OriginCapabilityCache.Set($"127.0.0.1:{originPort}", originPort);
        SessionSnapshot? seen = null;
        interception.SessionCaptured += (_, snap) => seen = snap;
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        Assert.AreEqual("proto-ok", await http.GetStringAsync($"http://127.0.0.1:{originPort}/plain"));
        Assert.IsNotNull(seen);
        StringAssert.Contains(seen!.Protocol, "h1.1");
        Assert.IsFalse(seen.Protocol.Contains("h3", StringComparison.Ordinal));

        interception.PreferHttp2 = true;
        interception.PreferHttp3 = true;
        interception.ReuseServerConnections = true;
        interception.OutboundTlsProtocols = SslProtocols.None;
        interception.ApplyHttpProtocols();
        Assert.IsTrue(proxy.EnableHttp2);
#pragma warning disable TWP001
        Assert.AreEqual(InterceptionService.IsHttp3Supported, proxy.EnableHttp3);
#pragma warning restore TWP001
        Assert.IsTrue(proxy.EnableConnectionPool);
        Assert.AreEqual(SslProtocols.None, proxy.SupportedServerSslProtocols);
        interception.Stop();
        origin.Stop();
    }

    private static Titanium.Web.Proxy.ProxyServer ProxyOf(InterceptionService interception) =>
        (Titanium.Web.Proxy.ProxyServer)typeof(InterceptionService)
            .GetField("_proxy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(interception)!;

    [TestMethod]
    public async Task OpenNetworkSettings_WithoutWindow_DoesNotChangeUpstream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-net-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Current.UpstreamProxyEnabled = true;
        settings.Current.UpstreamProxyHost = "10.1.1.1";
        settings.Current.UpstreamProxyPort = 8080;
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
            interception,
            new ScriptedInspectorDialogs(),
            new ScriptedInspectorPathPicker());

        Assert.AreEqual("http://10.1.1.1:8080", interception.UpstreamProxyAddress);
        vm.OpenNetworkSettingsCommand.Execute(null);
        await Task.Delay(150);
        StringAssert.Contains(vm.StatusText, "main window");
        Assert.AreEqual("http://10.1.1.1:8080", interception.UpstreamProxyAddress);
    }

    [TestMethod]
    public async Task UpstreamProxy_ChainsThroughStub_OnlyAfterRestart()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var originSaw = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ServeOnce(origin, originSaw, "origin-ok"));

        using var stub = new TcpListener(IPAddress.Loopback, 0);
        stub.Start();
        var stubPort = ((IPEndPoint)stub.LocalEndpoint).Port;
        var stubSaw = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ServeOnce(stub, stubSaw, "via-stub"));

        using var interception = new InterceptionService(new RecordingSystemProxyController());
        await interception.StartAsync(IPAddress.Loopback, 0);
        interception.UpstreamProxyAddress = $"http://127.0.0.1:{stubPort}";

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var direct = await http.GetStringAsync($"http://127.0.0.1:{originPort}/direct");
        Assert.AreEqual("origin-ok", direct);
        Assert.IsFalse(stubSaw.Task.IsCompleted, "upstream is read when capture starts, not while it runs");

        interception.Stop();
        await interception.StartAsync(IPAddress.Loopback, 0);

        using var handler2 = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http2 = new HttpClient(handler2) { Timeout = TimeSpan.FromSeconds(10) };
        var chained = await http2.GetStringAsync($"http://127.0.0.1:{originPort}/via-upstream");
        Assert.AreEqual("via-stub", chained);
        var stubLine = await stubSaw.Task.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(stubLine, $"http://127.0.0.1:{originPort}/via-upstream");
        interception.Stop();
        origin.Stop();
        stub.Stop();
    }

    private static async Task ServeOnce(TcpListener listener, TaskCompletionSource<string> saw, string bodyText)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buf = new byte[4096];
        var n = await stream.ReadAsync(buf);
        saw.TrySetResult(Encoding.ASCII.GetString(buf, 0, n));
        var body = Encoding.UTF8.GetBytes(bodyText);
        var resp = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
        await stream.WriteAsync(body);
    }
}
