#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler / ProxyServer helper edges still soft on develop:
///     ShouldBufferBodyForReplay false arms, PrepareRequestHeaders identity collapse,
///     AddViaHeader HTTP/1.1, and HasLoopedVia empty/whitespace.
/// </summary>
[TestClass]
public class RequestHandlerHelperFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void ShouldBufferBodyForReplay_False_WhenNoBodyOrHttp2()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        using var serverConn = MakeServerConnection(proxy);
        session.HttpClient.SetConnection(serverConn);
        Assert.IsFalse(serverConn.MarkExchangeStarted());

        var method = typeof(ProxyServer).GetMethod("ShouldBufferBodyForReplay", PrivateStatic)!;

        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.ContentLength = -1;
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "GET without a body must not arm replay buffering.");

        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.ContentLength = 8;
        session.HttpClient.Request.HttpVersion = HttpHeader.Version20;
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "HTTP/2 must not arm HTTP/1 keep-alive body replay buffering.");
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ShouldBufferBodyForReplay_False_WhenConnectionFresh()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        using var serverConn = MakeServerConnection(proxy);
        session.HttpClient.SetConnection(serverConn);
        // Do not MarkExchangeStarted — fresh connection is not a keep-alive reuse candidate.
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.ContentLength = 16;
        await Task.Delay(50);

        var method = typeof(ProxyServer).GetMethod("ShouldBufferBodyForReplay", PrivateStatic)!;
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "A never-used pooled connection must not buffer for replay.");
    }

    [TestMethod]
    public void PrepareRequestHeaders_CollapsesIdentityAndKeepsCustom()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "identity, gzip");
        headers.AddHeader("X-Keep", "1");
        method.Invoke(null, [headers]);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(headers.HeaderExists("X-Keep"));
    }

    [TestMethod]
    public void AddViaHeader_Http11_UsesOneDotOneToken()
    {
        var headers = new HeaderCollection();
        ProxyServer.AddViaHeader(headers, HttpHeader.Version11, "gap-via");
        var via = headers.GetHeaderValueOrNull("Via");
        Assert.IsNotNull(via);
        Assert.IsTrue(via!.Contains("1.1 gap-via", StringComparison.Ordinal), via);
    }

    [TestMethod]
    public void HasLoopedVia_EmptyOrWhitespacePseudonym_IsFalse()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 something");
        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, ""));
        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, "   "));
        Assert.IsFalse(ProxyServer.HasLoopedVia(new HeaderCollection(), "gap-via"));
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

    private static TcpServerConnection MakeServerConnection(ProxyServer proxy)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var stream = new HttpServerStream(proxy, Stream.Null, proxy.BufferPool, CancellationToken.None);
        return new TcpServerConnection(proxy, client, stream, "origin.test", 80, false,
            default, HttpHeader.Version11, null, null, "cache-key");
    }
}
#pragma warning restore TWP001
