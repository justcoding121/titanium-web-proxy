using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class RequestHandlerAndBridgeHelperTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;

    private static SessionEventArgs MakeSession(ProxyServer proxy, ProxyEndPoint? endPoint = null)
    {
        endPoint ??= new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    [TestMethod]
    public void PrepareRequestHeaders_FiltersUnsupportedEncodingsAndAddsIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.AcceptEncoding, "gzip, br, zstd, identity, bogus");
        headers.AddHeader("Proxy-Connection", "keep-alive");

        typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!
            .Invoke(null, [headers]);

        var accept = headers.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);
        Assert.IsNotNull(accept);
        StringAssert.Contains(accept, "gzip");
        StringAssert.Contains(accept, "identity");
        Assert.IsFalse(accept.Contains("bogus", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(accept.Contains("zstd", StringComparison.OrdinalIgnoreCase));
        // FixProxyHeaders strips Proxy-Connection
        Assert.IsNull(headers.GetFirstHeader("Proxy-Connection"));
    }

    [TestMethod]
    public void PrepareRequestHeaders_MissingAcceptEncoding_StillFixesProxyHeaders()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Proxy-Connection", "close");
        typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!
            .Invoke(null, [headers]);
        Assert.IsNull(headers.GetFirstHeader("Proxy-Connection"));
        Assert.IsNull(headers.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding));
    }

    [TestMethod]
    public void ResolveHttp1WireFramingSource_MapsEndpointKinds()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var explicitSession = MakeSession(proxy);
        Assert.AreEqual(FramingSource.Http1Wire,
            ProxyServer.ResolveHttp1WireFramingSource(explicitSession));

        using var transparentSession = MakeSession(proxy,
            new TransparentProxyEndPoint(IPAddress.Loopback, 0, false));
        Assert.AreEqual(FramingSource.Http1WireTransparent,
            ProxyServer.ResolveHttp1WireFramingSource(transparentSession));

        using var socksSession = MakeSession(proxy,
            new SocksProxyEndPoint(IPAddress.Loopback, 0, false));
        Assert.AreEqual(FramingSource.Http1WireSocks,
            ProxyServer.ResolveHttp1WireFramingSource(socksSession));
    }

    [TestMethod]
    public async Task DowngradeChunkedFramingForHttp10Origin_BuffersBodyAndSetsContentLength()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version10;
        session.HttpClient.Request.IsChunked = true;
        session.HttpClient.Request.Body = Encoding.ASCII.GetBytes("abcdefgh");
        session.HttpClient.Request.IsBodyRead = true;

        var method = typeof(ProxyServer).GetMethod("DowngradeChunkedFramingForHttp10OriginIfNeeded",
            PrivateStatic)!;
        await (Task)method.Invoke(null, [session, CancellationToken.None])!;

        Assert.IsFalse(session.HttpClient.Request.IsChunked);
        Assert.AreEqual(8, session.HttpClient.Request.ContentLength);
    }

    [TestMethod]
    public async Task DowngradeChunkedFramingForHttp10Origin_Http11_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.IsChunked = true;
        session.HttpClient.Request.Body = Encoding.ASCII.GetBytes("x");
        session.HttpClient.Request.IsBodyRead = true;

        var method = typeof(ProxyServer).GetMethod("DowngradeChunkedFramingForHttp10OriginIfNeeded",
            PrivateStatic)!;
        await (Task)method.Invoke(null, [session, CancellationToken.None])!;

        Assert.IsTrue(session.HttpClient.Request.IsChunked);
    }

    [TestMethod]
    public void ShouldCallBeforeRequestBodyWrite_TracksSubscription()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.ShouldCallBeforeRequestBodyWrite());
        proxy.OnRequestBodyWrite += (_, _) => Task.CompletedTask;
        Assert.IsTrue(proxy.ShouldCallBeforeRequestBodyWrite());
    }

    [TestMethod]
    public async Task OnBeforeRequestBodyWrite_InvokesHandler()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        var called = false;
        proxy.OnRequestBodyWrite += (_, e) =>
        {
            called = true;
            e.BodyBytes = Encoding.ASCII.GetBytes("Y");
            return Task.CompletedTask;
        };

        var args = new BeforeBodyWriteEventArgs(session, Encoding.ASCII.GetBytes("X"), false, true);
        await proxy.OnBeforeRequestBodyWrite(args);
        Assert.IsTrue(called);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("Y"), args.BodyBytes);
    }

    [TestMethod]
    [DataRow(null, false, 0)]
    [DataRow("HTTP/1.0 200 OK", false, 0)]
    [DataRow("HTTP/1.1 200", true, 200)]
    [DataRow("HTTP/1.1 103 Early Hints", true, 103)]
    [DataRow("HTTP/1.1 20 OK", false, 0)]
    [DataRow("HTTP/1.1 ABC OK", false, 0)]
    public void TryParseHttp11StatusLine_ParsesValidLinesOnly(string? line, bool ok, int code)
    {
        var method = typeof(ProxyServer).GetMethod("TryParseHttp11StatusLine", PrivateStatic)!;
        var args = new object?[] { line, 0 };
        var result = (bool)method.Invoke(null, args)!;
        Assert.AreEqual(ok, result);
        Assert.AreEqual(code, (int)args[1]!);
    }

    [TestMethod]
    public void LowercaseHeaderNames_PreservesValuesAndOrder()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("X-Foo", "1");
        headers.AddHeader("Content-Type", "text/plain");
        headers.AddHeader("x-bar", "2");

        typeof(ProxyServer).GetMethod("LowercaseHeaderNames", PrivateStatic)!
            .Invoke(null, [headers]);

        var list = headers.GetAllHeaders();
        Assert.AreEqual(3, list.Count);
        Assert.AreEqual("x-foo", list[0].Name);
        Assert.AreEqual("1", list[0].Value);
        Assert.AreEqual("content-type", list[1].Name);
        Assert.AreEqual("x-bar", list[2].Name);
    }
}
