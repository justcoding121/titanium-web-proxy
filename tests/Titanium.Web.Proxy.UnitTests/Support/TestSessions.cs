using System.IO;
using System.Net;
using System.Threading;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests.Support;

/// <summary>Builds an in-memory session whose client address the test controls.</summary>
internal static class TestSessions
{
    public static SessionEventArgs Create(ProxyServer proxy, string clientAddress = "127.0.0.1")
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Parse(clientAddress), 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "http://old.test/old?x=1";
        return session;
    }
}
