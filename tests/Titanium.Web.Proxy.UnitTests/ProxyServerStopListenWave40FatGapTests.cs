#pragma warning disable TWP001
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer" /> lifecycle: duplicate start/stop and port listen helpers.
/// </summary>
[TestClass]
public class ProxyServerStopListenWave40FatGapTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task StopAsync_WhenNeverStarted_ThrowsInvalidOperation()
    {
        using var proxy = new ProxyServer(false, false, false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => proxy.StopAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task StartStopExplicitEndpoint_LeavesPortFree()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);
        proxy.Start();
        var port = ep.Port;
        Assert.IsTrue(proxy.ProxyRunning);
        await proxy.StopAsync();
        Assert.IsFalse(proxy.ProxyRunning);

        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [TestMethod]
    public void EnableHttp2_EnableHttp3_TogglesIndependently()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = false;
        Assert.IsTrue(proxy.EnableHttp2);
        Assert.IsFalse(proxy.EnableHttp3);
        proxy.EnableHttp3 = true;
        Assert.IsTrue(proxy.EnableHttp3);
    }
}
#pragma warning restore TWP001
