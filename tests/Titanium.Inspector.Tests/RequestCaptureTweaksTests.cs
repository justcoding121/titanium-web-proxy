using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Http;

namespace Titanium.Inspector.Tests;

[TestClass]
public class RequestCaptureTweaksTests
{
    [TestMethod]
    public void Off_LeavesHeadersByteIdentical()
    {
        var request = Sample();
        var before = Dump(request.Headers);
        RequestCaptureTweaks.Apply(request, disableCaching: false, forceUncompressed: false);
        Assert.AreEqual(before, Dump(request.Headers));
    }

    [TestMethod]
    public void DisableCaching_ChangesOnlyCacheHeaders()
    {
        var request = Sample();
        RequestCaptureTweaks.Apply(request, disableCaching: true, forceUncompressed: false);

        Assert.IsFalse(request.Headers.HeaderExists("If-None-Match"));
        Assert.IsFalse(request.Headers.HeaderExists("If-Modified-Since"));
        Assert.AreEqual("no-cache", request.Headers.GetFirstHeader("Cache-Control")!.Value);
        Assert.AreEqual("gzip", request.Headers.GetFirstHeader("Accept-Encoding")!.Value);
        Assert.AreEqual("gzip", request.Headers.GetFirstHeader("grpc-accept-encoding")!.Value);
        Assert.AreEqual("app.example", request.Headers.GetFirstHeader("Host")!.Value);
    }

    [TestMethod]
    public void ForceUncompressed_ReplacesAcceptEncodingOnly()
    {
        var request = Sample();
        RequestCaptureTweaks.Apply(request, disableCaching: false, forceUncompressed: true);

        Assert.AreEqual("identity", request.Headers.GetFirstHeader("Accept-Encoding")!.Value);
        Assert.AreEqual("gzip", request.Headers.GetFirstHeader("grpc-accept-encoding")!.Value);
        Assert.AreEqual("\"abc\"", request.Headers.GetFirstHeader("If-None-Match")!.Value);
        Assert.AreEqual("Wed, 21 Oct 2015 07:28:00 GMT", request.Headers.GetFirstHeader("If-Modified-Since")!.Value);
        Assert.IsFalse(request.Headers.HeaderExists("Cache-Control"));
        Assert.AreEqual("app.example", request.Headers.GetFirstHeader("Host")!.Value);
    }

    [TestMethod]
    public void ExcludedKinds_AreUntouched()
    {
        AssertUntouched(Sample("CONNECT"), disableCaching: true, forceUncompressed: true);
        AssertUntouched(Sample("connect"), disableCaching: true, forceUncompressed: true);

        var socket = Sample();
        socket.Headers.AddHeader("Upgrade", "websocket");
        AssertUntouched(socket, disableCaching: true, forceUncompressed: true);

        var range = Sample();
        range.Headers.AddHeader("Range", "bytes=0-99");
        AssertUntouched(range, disableCaching: true, forceUncompressed: true);

        var ifRange = Sample();
        ifRange.Headers.AddHeader("If-Range", "\"abc\"");
        AssertUntouched(ifRange, disableCaching: true, forceUncompressed: true);
    }

    [TestMethod]
    public void Toggle_IsSessionOnly_AndDoesNotRewriteSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-tweaks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        var settings = new SettingsService(path);
        settings.Current.AutoStartCapture = false;
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
        var before = File.ReadAllText(path);

        vm.ToggleDisableCachingCommand.Execute(null);
        vm.ToggleForceUncompressedCommand.Execute(null);

        Assert.IsTrue(vm.DisableCaching);
        Assert.IsTrue(vm.ForceUncompressed);
        Assert.IsTrue(interception.DisableCaching);
        Assert.IsTrue(interception.ForceUncompressed);
        Assert.AreEqual(before, File.ReadAllText(path));

        using var reloadedInterception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        var reloadedRegistry = new SessionRegistry();
        var reloadedSettings = new SettingsService(path);
        var reloaded = new MainWindowViewModel(
            new SessionStreamBuffer(reloadedRegistry),
            reloadedRegistry,
            new UpdateService(reloadedSettings),
            reloadedSettings,
            reloadedInterception,
            new ScriptedInspectorDialogs(),
            new ScriptedInspectorPathPicker());
        Assert.IsFalse(reloaded.DisableCaching);
        Assert.IsFalse(reloaded.ForceUncompressed);
    }

    [TestMethod]
    public async Task Tweaks_ReachOriginAndGrid_AndSkipRange()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var seen = new TaskCompletionSource<string>[3];
        for (var i = 0; i < seen.Length; i++)
            seen[i] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ServeCount(origin, seen, seen.Length));

        using var interception = new InterceptionService(new RecordingSystemProxyController());
        var snaps = new List<string>();
        interception.SessionCaptured += (_, snap) =>
        {
            lock (snaps)
                snaps.Add(snap.RequestHeadersText ?? string.Empty);
        };
        await interception.StartAsync(IPAddress.Loopback, 0);

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        await SendAsync(http, originPort, "/off", range: false);
        var off = await seen[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(off, "If-None-Match: \"abc\"");
        Assert.IsFalse(off.Contains("Cache-Control: no-cache", StringComparison.OrdinalIgnoreCase));

        interception.DisableCaching = true;
        interception.ForceUncompressed = true;
        await SendAsync(http, originPort, "/on", range: false);
        var on = await seen[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(on, "Cache-Control: no-cache");
        StringAssert.Contains(on, "Accept-Encoding: identity");
        StringAssert.Contains(on, "grpc-accept-encoding: gzip");
        Assert.IsFalse(on.Contains("If-None-Match", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(on.Contains("If-Modified-Since", StringComparison.OrdinalIgnoreCase));

        string grid;
        lock (snaps)
            grid = snaps[^1];
        StringAssert.Contains(grid, "Cache-Control: no-cache");
        StringAssert.Contains(grid, "Accept-Encoding: identity");
        Assert.IsFalse(grid.Contains("If-None-Match", StringComparison.OrdinalIgnoreCase));

        await SendAsync(http, originPort, "/range", range: true);
        var ranged = await seen[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(ranged, "If-None-Match: \"abc\"");
        StringAssert.Contains(ranged, "Range: bytes=0-99");
        Assert.IsFalse(ranged.Contains("Cache-Control: no-cache", StringComparison.OrdinalIgnoreCase));

        interception.Stop();
        origin.Stop();
    }

    private static async Task SendAsync(HttpClient http, int originPort, string path, bool range)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{originPort}{path}");
        message.Headers.TryAddWithoutValidation("If-None-Match", "\"abc\"");
        message.Headers.TryAddWithoutValidation("If-Modified-Since", "Wed, 21 Oct 2015 07:28:00 GMT");
        message.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        message.Headers.TryAddWithoutValidation("grpc-accept-encoding", "gzip");
        if (range)
            message.Headers.TryAddWithoutValidation("Range", "bytes=0-99");
        using var response = await http.SendAsync(message);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ServeCount(TcpListener listener, TaskCompletionSource<string>[] seen, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var received = new MemoryStream();
            var buf = new byte[1024];
            while (true)
            {
                var n = await stream.ReadAsync(buf);
                if (n == 0)
                    break;
                received.Write(buf, 0, n);
                var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    seen[i].TrySetResult(text);
                    break;
                }

                if (received.Length > 65536)
                    break;
            }

            var resp = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
            await stream.WriteAsync(resp);
        }
    }

    private static void AssertUntouched(Request request, bool disableCaching, bool forceUncompressed)
    {
        var before = Dump(request.Headers);
        RequestCaptureTweaks.Apply(request, disableCaching, forceUncompressed);
        Assert.AreEqual(before, Dump(request.Headers));
    }

    private static Request Sample(string method = "GET")
    {
        var request = new Request { Method = method };
        request.Headers.AddHeader("Host", "app.example");
        request.Headers.AddHeader("If-None-Match", "\"abc\"");
        request.Headers.AddHeader("If-Modified-Since", "Wed, 21 Oct 2015 07:28:00 GMT");
        request.Headers.AddHeader("Accept-Encoding", "gzip");
        request.Headers.AddHeader("grpc-accept-encoding", "gzip");
        request.Headers.AddHeader("User-Agent", "tweaks-test");
        return request;
    }

    private static string Dump(HeaderCollection headers)
    {
        var sb = new StringBuilder();
        foreach (var header in headers)
            sb.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
        return sb.ToString();
    }
}
