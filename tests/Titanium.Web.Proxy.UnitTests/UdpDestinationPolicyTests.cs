#pragma warning disable TWP001
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Server-wide outbound destination policy also covers UDP relays, and the 501 body names the
///     tokens a listener accepts.
/// </summary>
[TestClass]
public class UdpDestinationPolicyTests
{
    [TestMethod]
    public void IsUdpDestinationBlocked_FollowsTheServerFlags()
    {
        using var proxy = new ProxyServer(false, false, false);
        var loopback = IPAddress.Loopback;
        var privateAddress = IPAddress.Parse("10.1.2.3");
        var publicAddress = IPAddress.Parse("8.8.8.8");

        Assert.IsFalse(proxy.IsUdpDestinationBlocked(loopback));
        Assert.IsFalse(proxy.IsUdpDestinationBlocked(privateAddress));

        proxy.BlockLoopbackDestinations = true;
        Assert.IsTrue(proxy.IsUdpDestinationBlocked(loopback));
        Assert.IsFalse(proxy.IsUdpDestinationBlocked(privateAddress));
        Assert.IsFalse(proxy.IsUdpDestinationBlocked(publicAddress));

        proxy.BlockLoopbackDestinations = false;
        proxy.BlockPrivateNetworkDestinations = true;
        Assert.IsTrue(proxy.IsUdpDestinationBlocked(privateAddress));
        Assert.IsTrue(proxy.IsUdpDestinationBlocked(loopback));
        Assert.IsFalse(proxy.IsUdpDestinationBlocked(publicAddress));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task OpenAsync_RejectsWhenTheServerPolicyBlocksEveryAddress()
    {
        var endPoint = ListenerThatAllowsLoopback(9);

        var (status, _, open) = await MasqueTunnel.OpenAsync(endPoint, "127.0.0.1", 9, NullLogger.Instance,
            CancellationToken.None, _ => true);

        Assert.AreEqual(403, status);
        Assert.IsNull(open);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task OpenAsync_KeepsTheAddressesThePolicyPermits()
    {
        var endPoint = ListenerThatAllowsLoopback(9);

        var (status, _, open) = await MasqueTunnel.OpenAsync(endPoint, "127.0.0.1", 9, NullLogger.Instance,
            CancellationToken.None, _ => false);

        Assert.AreEqual(0, status);
        Assert.IsNotNull(open);
        open.Release();
    }

    [TestMethod]
    public void UnsupportedMessage_NamesTheTokensTheListenerAccepts()
    {
        var onlyUdp = new ExtendedConnectSupport(webSocket: false, connectUdp: true);
        var message = ExtendedConnectProtocols.UnsupportedMessage("webtransport", onlyUdp);

        StringAssert.Contains(message, "protocol: webtransport");
        StringAssert.Contains(message, "Accepted: connect-udp.");
        Assert.IsFalse(message.Contains("Only 'websocket'"));

        var all = new ExtendedConnectSupport(webSocket: true, webTransport: true, connectTcp: true,
            connectUdp: true);
        StringAssert.Contains(ExtendedConnectProtocols.UnsupportedMessage("other", all),
            "Accepted: websocket, webtransport, connect-tcp, connect-udp.");
    }

    private static MasqueProxyEndPoint ListenerThatAllowsLoopback(int port)
    {
        var endPoint = new MasqueProxyEndPoint(IPAddress.Loopback, 0);
        endPoint.Options.BlockPrivateTargets = false;
        endPoint.Options.AllowedPorts = [port];
        return endPoint;
    }
}
