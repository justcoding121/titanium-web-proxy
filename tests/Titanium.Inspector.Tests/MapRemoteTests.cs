using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class MapRemoteTests
{
    [TestMethod]
    public void TryApplyRewrite_PreservesWildcardSuffix()
    {
        Assert.IsTrue(MapRemoteViewModel.TryApplyRewrite(
            "https://prod.example/api/v1/users?x=1",
            "https://prod.example/*",
            "http://127.0.0.1:5000/*",
            out var rewritten));
        Assert.AreEqual("http://127.0.0.1:5000/api/v1/users?x=1", rewritten);
    }

    [TestMethod]
    public void TryRewrite_RequiresEnabledAndAbsoluteTarget()
    {
        var vm = new MapRemoteViewModel();
        vm.Rules.Add(new MapRemoteRule
        {
            MatchUrl = "*map-remote*",
            TargetUrl = "http://127.0.0.1:9/rewritten",
            Enabled = true,
        });
        Assert.IsFalse(vm.TryRewrite("http://127.0.0.1:9/map-remote", out _, out _));
        vm.Enabled = true;
        Assert.IsTrue(vm.TryRewrite("http://127.0.0.1:9/map-remote", out var url, out var rule));
        Assert.AreEqual("http://127.0.0.1:9/rewritten", url);
        Assert.IsNotNull(rule);
        StringAssert.Contains(rule!.Display, "→");

        var dtos = vm.ToDtos();
        Assert.AreEqual(1, dtos.Count);
        vm.LoadFromDtos(dtos);
        Assert.AreEqual(1, vm.Rules.Count);
    }

    [TestMethod]
    public async Task MapRemote_Integration_RewritesToLocalOrigin()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var gotPath = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            using var client = await origin.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buf = new byte[4096];
            var n = await stream.ReadAsync(buf);
            var req = Encoding.ASCII.GetString(buf, 0, n);
            var firstLine = req.Split('\n')[0];
            gotPath.TrySetResult(firstLine);
            var body = Encoding.UTF8.GetBytes("mapped-ok");
            var resp =
                "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length +
                "\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
            await stream.WriteAsync(body);
        });

        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.MapRemote = new MapRemoteViewModel { Enabled = true };
        interception.MapRemote.Rules.Add(new MapRemoteRule
        {
            MatchUrl = "*map-remote-int*",
            TargetUrl = $"http://127.0.0.1:{originPort}/from-map",
            Enabled = true,
        });
        await interception.StartAsync(IPAddress.Loopback, 0);

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var response = await http.GetAsync("http://127.0.0.1:9/map-remote-int");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("mapped-ok", await response.Content.ReadAsStringAsync());
        var line = await gotPath.Task.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(line, "/from-map");
        interception.Stop();
        origin.Stop();
    }
}
