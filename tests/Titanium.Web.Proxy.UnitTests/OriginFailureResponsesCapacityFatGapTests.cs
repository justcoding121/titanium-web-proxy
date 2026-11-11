#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
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
///     Fat <see cref="OriginFailureResponses" /> edge: upstream capacity exhaustion
///     becomes 503 with Retry-After and preserves the capacity exception on the session.
/// </summary>
[TestClass]
public class OriginFailureResponsesCapacityFatGapTests
{
    [TestMethod]
    public void UpstreamCapacity_SyntheticResponse_Is503_WithRetryAfter()
    {
        using var proxy = new ProxyServer();
        var args = MakeSession(proxy);
        var ex = new UpstreamCapacityException("gap-cap", 8);
        OriginFailureResponses.Apply(args, ex);

        Assert.AreEqual((int)HttpStatusCode.ServiceUnavailable, args.HttpClient.Response.StatusCode);
        Assert.IsInstanceOfType<UpstreamCapacityException>(args.Exception);
        Assert.AreEqual("1", args.HttpClient.Response.Headers.GetHeaderValueOrNull("Retry-After"));
        StringAssert.Contains(args.HttpClient.Response.BodyString ?? string.Empty, "connection limit");
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
