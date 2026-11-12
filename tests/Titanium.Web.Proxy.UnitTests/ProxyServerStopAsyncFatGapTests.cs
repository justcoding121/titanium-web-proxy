#pragma warning disable TWP001
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>ProxyServer</c> lifecycle suite: <see cref="ProxyServer.StopAsync" /> drain
///     and pool clearing distinct from synchronous <see cref="ProxyServer.Stop" />.
/// </summary>
[TestClass]
public class ProxyServerStopAsyncFatGapTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task StopAsync_WhenRunning_SetsProxyNotRunning()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        await proxy.StopAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(proxy.ProxyRunning);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task StopAsync_WhenNotRunning_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => proxy.StopAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task StopAsync_ThenStart_AcceptsAgain()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        var port = proxy.ProxyEndPoints[0].Port;
        await proxy.StopAsync(TimeSpan.FromSeconds(2));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        Assert.IsTrue(proxy.ProxyEndPoints[0].Port > 0);
        Assert.AreNotEqual(0, port);
    }
}
#pragma warning restore TWP001
