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
///     Fat <see cref="OriginFailureResponses" /> edges: nested socket timeouts map to 504.
/// </summary>
[TestClass]
public class OriginFailureResponsesSocketTimeoutFatGapTests
{
    [TestMethod]
    public void IsTimeout_SocketTimedOut_IsTrue()
    {
        var ex = new IOException("outer", new SocketException((int)SocketError.TimedOut));
        Assert.IsTrue(OriginFailureResponses.IsTimeout(ex));
    }

    [TestMethod]
    public void StatusFor_SocketTimedOut_Is504()
    {
        var ex = new IOException("read", new SocketException((int)SocketError.TimedOut));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, OriginFailureResponses.StatusFor(ex));
    }

    [TestMethod]
    public void Apply_SocketTimedOut_Is504()
    {
        using var proxy = new ProxyServer();
        var args = MakeSession(proxy);
        var ex = new IOException("gap-io", new SocketException((int)SocketError.TimedOut));
        OriginFailureResponses.Apply(args, ex);

        Assert.AreEqual((int)HttpStatusCode.GatewayTimeout, args.HttpClient.Response.StatusCode);
        StringAssert.Contains(args.HttpClient.Response.BodyString ?? string.Empty, "Gateway Timeout");
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
