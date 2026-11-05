#pragma warning disable TWP001
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ExtendedConnectProtocolsTests
{
    [TestMethod]
    public void IsSupported_WebsocketOnly_WhenRfc8441Enabled()
    {
        var support = ExtendedConnectSupport.ForProcess(enableRfc8441: true);

        Assert.IsTrue(ExtendedConnectProtocols.IsSupported("websocket", support));
        Assert.IsTrue(ExtendedConnectProtocols.IsSupported("WebSocket", support));
        Assert.IsFalse(ExtendedConnectProtocols.IsSupported("webtransport", support));
        Assert.IsFalse(ExtendedConnectProtocols.IsSupported("connect-tcp", support));
        Assert.IsFalse(ExtendedConnectProtocols.IsSupported("connect-udp", support));
        Assert.IsFalse(ExtendedConnectProtocols.IsSupported("unknown-token", support));
        Assert.IsFalse(ExtendedConnectProtocols.IsSupported(null, support));
    }

    [TestMethod]
    public void IsSupported_RejectsWebsocket_WhenRfc8441Disabled()
    {
        var support = ExtendedConnectSupport.ForProcess(enableRfc8441: false);

        Assert.IsFalse(ExtendedConnectProtocols.IsSupported("websocket", support));
        Assert.IsFalse(support.HasReservedTokens);
    }

    [TestMethod]
    public void ReservedFlags_DoNotAcceptTokensYet()
    {
        var support = new ExtendedConnectSupport(webSocket: false, webTransport: true, connectTcp: true, connectUdp: true);

        Assert.IsTrue(support.HasReservedTokens);
        Assert.IsTrue(support.WebTransport);
        Assert.IsTrue(support.ConnectTcp);
        Assert.IsTrue(support.ConnectUdp);
        Assert.IsTrue(ExtendedConnectProtocols.IsSupported(ExtendedConnectProtocols.WebTransport, support));
        Assert.IsTrue(ExtendedConnectProtocols.IsSupported(ExtendedConnectProtocols.ConnectTcp, support));
        Assert.IsFalse(ExtendedConnectProtocols.IsSupported(ExtendedConnectProtocols.ConnectUdp, support));
        Assert.IsTrue(ExtendedConnectProtocols.IsWebTransport("WebTransport"));
        Assert.IsTrue(ExtendedConnectProtocols.IsConnectTcp("CONNECT-TCP"));
        Assert.IsTrue(ExtendedConnectProtocols.IsConnectUdp("Connect-UDP"));
    }

    [TestMethod]
    public void ConnectionAdvertisesConnect_IsPerCall_NotAProcessOr()
    {
        Assert.IsFalse(ExtendedConnectProtocols.ConnectionAdvertisesConnect(false, false));
        Assert.IsTrue(ExtendedConnectProtocols.ConnectionAdvertisesConnect(true, false));
        Assert.IsTrue(ExtendedConnectProtocols.ConnectionAdvertisesConnect(false, true));
    }

    [TestMethod]
    public void UnsupportedMessage_KeepsHistoricalWebsocketOnlyText()
    {
        var support = ExtendedConnectSupport.ForProcess(false);
        var message = ExtendedConnectProtocols.UnsupportedMessage("connect-tcp", support);

        StringAssert.Contains(message, "protocol: connect-tcp");
        StringAssert.Contains(message, "Only 'websocket' is implemented.");
        StringAssert.StartsWith(message, "RFC 8441");

        var h3 = ExtendedConnectProtocols.UnsupportedMessage(null, support, rfc: "9220");
        StringAssert.StartsWith(h3, "RFC 9220");
        StringAssert.Contains(h3, "protocol: unknown");
    }

    [TestMethod]
    public void MasqueListener_DoesNotFlipProcessRfc8441()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.EnableRfc8441);

        proxy.AddEndPoint(new MasqueProxyEndPoint(IPAddress.Loopback, 0));

        Assert.IsFalse(proxy.EnableRfc8441,
            "A masque listener advertises CONNECT only on its own SendHttp2 call.");
        Assert.IsFalse(ExtendedConnectProtocols.ConnectionAdvertisesConnect(proxy.EnableRfc8441, false),
            "Other listeners keep compressed-relay eligibility when the process flag stays false.");
    }

    [TestMethod]
    public void ConnectTcp_ProcessFlag_AppliesToExplicitListenersOnly()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.EnableConnectTcp = true;
        var explicitEp = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var reverseEp = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false);

        Assert.IsTrue(proxy.AdvertisesConnectTcp(explicitEp));
        Assert.IsFalse(proxy.AdvertisesConnectTcp(reverseEp),
            "server.enableConnectTcp must not advertise CONNECT on a reverse listener.");
        Assert.IsTrue(proxy.ConnectSupportFor(explicitEp).ConnectTcp);
        Assert.IsFalse(proxy.ConnectSupportFor(reverseEp).WebSocket);

        reverseEp.AcceptConnectTcp = true;
        Assert.IsTrue(proxy.AdvertisesConnectTcp(reverseEp));
        Assert.IsFalse(proxy.EnableRfc8441);
    }
}
