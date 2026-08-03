using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyServerLifecycleCoverageTests
{
    [TestMethod]
    public void UpdateConnectionCounts_FireEvents_AndSwallowHandlerExceptions()
    {
        using var proxy = new ProxyServer(false, false, false);
        var client = 0;
        var server = 0;
        var h3Client = 0;
        var h3Server = 0;

        proxy.ClientConnectionCountChanged += (_, _) =>
        {
            client++;
            throw new InvalidOperationException("client handler boom");
        };
        proxy.ServerConnectionCountChanged += (_, _) =>
        {
            server++;
            throw new InvalidOperationException("server handler boom");
        };
        proxy.Http3ClientConnectionCountChanged += (_, _) =>
        {
            h3Client++;
            throw new InvalidOperationException("h3 client boom");
        };
        proxy.Http3ServerConnectionCountChanged += (_, _) =>
        {
            h3Server++;
            throw new InvalidOperationException("h3 server boom");
        };

        proxy.UpdateClientConnectionCount(true);
        proxy.UpdateClientConnectionCount(false);
        proxy.UpdateServerConnectionCount(true);
        proxy.UpdateServerConnectionCount(false);
        proxy.UpdateHttp3ClientConnectionCount(true);
        proxy.UpdateHttp3ClientConnectionCount(false);
        proxy.UpdateHttp3ServerConnectionCount(true);
        proxy.UpdateHttp3ServerConnectionCount(false);

        Assert.AreEqual(2, client);
        Assert.AreEqual(2, server);
        Assert.AreEqual(2, h3Client);
        Assert.AreEqual(2, h3Server);
        Assert.AreEqual(0, proxy.ClientConnectionCount);
        Assert.AreEqual(0, proxy.ServerConnectionCount);
        Assert.AreEqual(0, proxy.Http3ClientConnectionCount);
        Assert.AreEqual(0, proxy.Http3ServerConnectionCount);
    }

    [TestMethod]
    public void GenerateUpstreamProxyWinAuthToken_UsesCustomGeneratorWhenSet()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.UpstreamProxyWinAuthTokenGenerator = (_, scheme, challenge, _) =>
            $"{scheme}:{challenge ?? "init"}";

        var token = proxy.GenerateUpstreamProxyWinAuthToken(
            new ExternalProxy { HostName = "proxy.example", Port = 8080 },
            "Negotiate",
            null,
            new InternalDataStore());

        Assert.AreEqual("Negotiate:init", token);

        token = proxy.GenerateUpstreamProxyWinAuthToken(
            new ExternalProxy { HostName = "proxy.example", Port = 8080 },
            "NTLM",
            "chal",
            new InternalDataStore());
        Assert.AreEqual("NTLM:chal", token);
    }

    [TestMethod]
    public void TrimOriginCapabilityCaches_IsSafeWhenCachesUnused()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.TrimOriginCapabilityCaches();
        Assert.IsNotNull(proxy.Http2OriginCapabilityCache);
        Assert.IsNotNull(proxy.Http3OriginCapabilityCache);
    }

    [TestMethod]
    public async Task RegisterSessionCancellation_IsCancelledOnStop_UnregisterSkipsCancel()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);

        using var cancelled = new CancellationTokenSource();
        using var kept = new CancellationTokenSource();
        proxy.RegisterSessionCancellation(cancelled);
        proxy.RegisterSessionCancellation(kept);
        proxy.UnregisterSessionCancellation(kept);

        proxy.Start(changeSystemProxySettings: false);
        await proxy.StopAsync();

        Assert.IsTrue(cancelled.IsCancellationRequested);
        Assert.IsFalse(kept.IsCancellationRequested);
    }

    [TestMethod]
    public void RemoveEndPoint_Unknown_Throws_AndRemoveWhileStopped_Works()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);
        proxy.RemoveEndPoint(ep);
        Assert.AreEqual(0, proxy.ProxyEndPoints.Count);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            proxy.RemoveEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 1, false)));
    }

    [TestMethod]
    public void AddEndPoint_DuplicateFixedAddressAndPort_Throws_ButEphemeralPortsAreAllowed()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 18080, false));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 18080, false)));

        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        Assert.AreEqual(3, proxy.ProxyEndPoints.Count);
    }

    [TestMethod]
    public async Task Server_CanRestartSameEphemeralEndpoint_WithoutCertificateStore()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(endPoint);

        proxy.Start(changeSystemProxySettings: false);
        Assert.IsTrue(proxy.ProxyRunning);
        await proxy.StopAsync(TimeSpan.Zero);
        Assert.IsFalse(proxy.ProxyRunning);
        Assert.AreSame(endPoint, proxy.ProxyEndPoints[0]);

        proxy.Start(changeSystemProxySettings: false);
        Assert.IsTrue(proxy.ProxyRunning);
        proxy.Stop();
        Assert.IsFalse(proxy.ProxyRunning);
    }

    [TestMethod]
    public void DoubleStart_AndStopWhenNotRunning_Throw()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start(changeSystemProxySettings: false);
        Assert.ThrowsExactly<InvalidOperationException>(() => proxy.Start(changeSystemProxySettings: false));
        proxy.Stop();
        Assert.ThrowsExactly<InvalidOperationException>(() => proxy.Stop());
    }
}
