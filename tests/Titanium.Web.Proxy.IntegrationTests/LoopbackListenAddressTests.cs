#pragma warning disable CA1416
#pragma warning disable TWP001

using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Test and loopback listeners must bind 127.0.0.1 / ::1. A wildcard bind
///     (0.0.0.0 or ::) raises the Windows inbound firewall prompt for testhost.exe.
/// </summary>
[TestClass]
public class LoopbackListenAddressTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task GetProxy_ListensOnIpv4Loopback_AndProxies()
    {
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("loopback-ok"));

        var proxy = suite.GetProxy();
        var local = (IPEndPoint)proxy.ProxyEndPoints[0].Listener!.LocalEndpoint;
        AssertExactLoopback(local.Address);
        Assert.AreEqual(IPAddress.Loopback, local.Address);

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpUrl);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("loopback-ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public void GetReverseProxy_ListensOnIpv4Loopback()
    {
        using var suite = new TestSuite();
        var proxy = suite.GetReverseProxy();
        var local = (IPEndPoint)proxy.ProxyEndPoints[0].Listener!.LocalEndpoint;
        AssertExactLoopback(local.Address);
        Assert.AreEqual(IPAddress.Loopback, local.Address);
    }

    [TestMethod]
    public void LoopbackQuicEndpoints_BindBothFamilies()
    {
        if (!QuicListener.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");

        AssertQuicFamilies(new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0));
        AssertQuicFamilies(new TransparentQuicProxyEndPoint(IPAddress.IPv6Loopback, 0));
        AssertQuicFamilies(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true
        });
        AssertQuicFamilies(new TransparentProxyEndPoint(IPAddress.IPv6Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true
        });
    }

    [TestMethod]
    public async Task OriginServer_BindsBothLoopbackFamilies()
    {
        if (!QuicListener.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        Assert.AreEqual(2, origin.LocalEndPoints.Count);
        AssertSamePortLoopbackPair(origin.LocalEndPoints[0], origin.LocalEndPoints[1]);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void LoopbackQuic_Ipv4BindFails_ReleasesIpv6Listener()
    {
        if (!QuicListener.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
        if (!System.Net.Sockets.Socket.OSSupportsIPv6)
            Assert.Inconclusive("Needs IPv6 loopback.");

        // Hold the IPv4 side of a fixed port so the paired bind fails after ::1 was already bound.
        using var blocker = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)blocker.LocalEndPoint!).Port;

        var endPoint = new TransparentQuicProxyEndPoint(IPAddress.Loopback, port);
        using var proxy = CreateProxy();
        proxy.AddEndPoint(endPoint);
        Assert.ThrowsExactly<System.InvalidOperationException>(() => proxy.Start());

        // Checked while the proxy is still alive: the failed Start itself must not leave ::1 bound.
        var quic = (IQuicInboundEndPoint)endPoint;
        Assert.IsNull(quic.QuicListener);
        Assert.IsNull(quic.LoopbackV4QuicListener);

        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetworkV6,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
    }

    private static void AssertQuicFamilies(ProxyEndPoint endPoint)
    {
        using var proxy = CreateProxy();
        proxy.AddEndPoint(endPoint);
        proxy.Start();

        if (endPoint.Listener != null)
        {
            var tcp = (IPEndPoint)endPoint.Listener.LocalEndpoint;
            AssertExactLoopback(tcp.Address);
            Assert.AreEqual(endPoint.IpAddress, tcp.Address);
        }

        var quic = (IQuicInboundEndPoint)endPoint;
        Assert.IsNotNull(quic.QuicListener);
        Assert.IsNotNull(quic.LoopbackV4QuicListener);
        AssertSamePortLoopbackPair(quic.QuicListener.LocalEndPoint, quic.LoopbackV4QuicListener.LocalEndPoint);
        Assert.AreEqual(endPoint.Port, quic.QuicListener.LocalEndPoint.Port);
        if (endPoint.Listener != null)
            Assert.AreEqual(endPoint.Port, ((IPEndPoint)endPoint.Listener.LocalEndpoint).Port);
    }

    private static ProxyServer CreateProxy()
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        return proxy;
    }

    private static void AssertSamePortLoopbackPair(IPEndPoint v6, IPEndPoint v4)
    {
        AssertExactLoopback(v6.Address);
        AssertExactLoopback(v4.Address);
        Assert.IsTrue(IPAddress.IPv6Loopback.Equals(v6.Address), v6.ToString());
        Assert.IsTrue(IPAddress.Loopback.Equals(v4.Address), v4.ToString());
        Assert.AreEqual(v6.Port, v4.Port);
    }

    private static void AssertExactLoopback(IPAddress address)
    {
        Assert.IsTrue(
            IPAddress.Loopback.Equals(address) || IPAddress.IPv6Loopback.Equals(address),
            $"Expected 127.0.0.1 or ::1, got {address}.");
        Assert.IsFalse(IPAddress.Any.Equals(address), address.ToString());
        Assert.IsFalse(IPAddress.IPv6Any.Equals(address), address.ToString());
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
