using System;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyAuthorizationExceptionTests
{
    [TestMethod]
    public void Constructor_RedactsAuthorizationHeaders()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        using var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, System.IO.Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);

        var headers = new[]
        {
            new HttpHeader("Authorization", "Bearer secret-token"),
            new HttpHeader("Proxy-Authorization", "Basic dXNlcjpwYXNz"),
            new HttpHeader("Host", "example.com")
        };

        var ex = new ProxyAuthorizationException("auth failed", session, new Exception("inner"), headers);

        Assert.AreSame(session, ex.Session);
        var list = ex.Headers.ToList();
        Assert.AreEqual(3, list.Count);
        Assert.AreEqual("[REDACTED]", list[0].Value);
        Assert.AreEqual("[REDACTED]", list[1].Value);
        Assert.AreEqual("example.com", list[2].Value);
    }
}
