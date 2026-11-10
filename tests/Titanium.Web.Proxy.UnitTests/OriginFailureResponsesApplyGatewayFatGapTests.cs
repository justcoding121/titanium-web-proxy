#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp; // QuicClientConnection

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="OriginFailureResponses" /> edges: Apply maps timeouts to 504
///     and other failures to 502 on the synthetic response.
/// </summary>
[TestClass]
public class OriginFailureResponsesApplyGatewayFatGapTests
{
    [TestMethod]
    public void Apply_ProxyTimeout_Is504_WithGatewayTimeoutBody()
    {
        using var proxy = new ProxyServer();
        var args = MakeSession(proxy);
        var ex = new ProxyTimeoutException("gap-timeout", ProxyTimeoutKind.Connect);
        OriginFailureResponses.Apply(args, ex);

        Assert.AreEqual((int)HttpStatusCode.GatewayTimeout, args.HttpClient.Response.StatusCode);
        StringAssert.Contains(args.HttpClient.Response.BodyString ?? string.Empty, "Gateway Timeout");
    }

    [TestMethod]
    public void Apply_ConnectionReset_Is502_WithBadGatewayBody()
    {
        using var proxy = new ProxyServer();
        var args = MakeSession(proxy);
        var ex = new IOException("reset", new SocketException((int)SocketError.ConnectionReset));
        OriginFailureResponses.Apply(args, ex);

        Assert.AreEqual((int)HttpStatusCode.BadGateway, args.HttpClient.Response.StatusCode);
        StringAssert.Contains(args.HttpClient.Response.BodyString ?? string.Empty, "Bad Gateway");
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, ep, clientStream, null, cts);
    }
}
#pragma warning restore TWP001
