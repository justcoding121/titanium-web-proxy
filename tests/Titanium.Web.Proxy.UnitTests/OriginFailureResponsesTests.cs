using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;


namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
[TestCategory("Regression-2026-10-07")]
public class OriginFailureResponsesTests
{
    [TestMethod]
    public void ProxyTimeout_Maps_To504()
    {
        var ex = new ProxyTimeoutException("connect timed out", ProxyTimeoutKind.Connect);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, OriginFailureResponses.StatusFor(ex));
    }

    [TestMethod]
    public void NestedProxyTimeout_Maps_To504()
    {
        var ex = new IOException("round trip", new ProxyTimeoutException("deadline", ProxyTimeoutKind.Connect));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, OriginFailureResponses.StatusFor(ex));
    }

    [TestMethod]
    public void SocketTimedOut_Maps_To504()
    {
        var ex = new IOException("read", new SocketException((int)SocketError.TimedOut));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, OriginFailureResponses.StatusFor(ex));
    }

    [TestMethod]
    public void OtherFailures_Map_To502()
    {
        Assert.AreEqual(HttpStatusCode.BadGateway,
            OriginFailureResponses.StatusFor(new IOException("reset",
                new SocketException((int)SocketError.ConnectionReset))));
        Assert.AreEqual(HttpStatusCode.BadGateway,
            OriginFailureResponses.StatusFor(new RetryableServerConnectionException("closed")));
        Assert.AreEqual(HttpStatusCode.BadGateway,
            OriginFailureResponses.StatusFor(new SocketException((int)SocketError.ConnectionRefused)));
    }
}
