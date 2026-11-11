using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
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

    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

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
    public void PrepareRequestHeaders_StripsQualityValues_KeepsSupportedCodings()
    {
        // Chrome often sends "gzip;q=1.0, deflate;q=0.9, br;q=0.8". Matching the full token
        // (including ;q=) against ProxySupportedCompressions used to drop every coding and leave
        // only identity — origins then omit Content-Encoding or change payload shape.
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.AcceptEncoding, "gzip;q=1.0, deflate;q=0.9, br;q=0.8, zstd;q=0.7");

        typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!
            .Invoke(null, [headers]);

        var accept = headers.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);
        Assert.IsNotNull(accept);
        StringAssert.Contains(accept, "gzip");
        StringAssert.Contains(accept, "deflate");
        StringAssert.Contains(accept, "br");
        StringAssert.Contains(accept, "identity");
        Assert.IsFalse(accept.Contains("zstd", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(accept.Contains(";q=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PrepareRequestHeaders_DropsCodingsTheClientForbidsWithQZero()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.AcceptEncoding, "gzip;q=0, br;q=0.0, deflate;q=0.5, identity");

        typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!
            .Invoke(null, [headers]);

        var accept = headers.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);
        Assert.IsNotNull(accept);
        Assert.IsFalse(accept.Contains("gzip", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(accept.Contains("br", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(accept, "deflate");
        StringAssert.Contains(accept, "identity");
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

    [TestMethod]
    public async Task OnBeforeRequest_IsFastPath_SkipsHandlers()
    {
        using var proxy = new ProxyServer(false, false, false);
        var called = false;
        proxy.BeforeRequest += (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        };

        using var session = MakeSession(proxy);
        session.IsFastPath = true;

        await (Task)typeof(ProxyServer).GetMethod("OnBeforeRequest", PrivateInstance)!
            .Invoke(proxy, [session])!;

        Assert.IsFalse(called, "Fast-path sessions must skip BeforeRequest.");
    }

    [TestMethod]
    public async Task OnBeforeRequest_WithMiddleware_RunsChainThenBeforeRequest()
    {
        using var proxy = new ProxyServer(false, false, false);
        var middleware = new RecordingMiddleware();
        proxy.ReverseProxy = new ReverseProxyOptions { Middleware = [middleware] };

        var beforeRequest = false;
        proxy.BeforeRequest += (_, _) =>
        {
            beforeRequest = true;
            return Task.CompletedTask;
        };

        using var session = MakeSession(proxy);
        await (Task)typeof(ProxyServer).GetMethod("OnBeforeRequest", PrivateInstance)!
            .Invoke(proxy, [session])!;

        Assert.AreEqual(1, middleware.Calls);
        Assert.IsTrue(beforeRequest, "Middleware terminus must still invoke BeforeRequest.");
    }

    [TestMethod]
    public async Task OnBeforeRequest_WithoutHandlers_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        await (Task)typeof(ProxyServer).GetMethod("OnBeforeRequest", PrivateInstance)!
            .Invoke(proxy, [session])!;
        Assert.IsFalse(session.HttpClient.Request.HasBody);
    }

    [TestMethod]
    public async Task OnBeforeRequest_WithTranscoder_RewritesThenRunsBeforeRequest()
    {
        using var proxy = new ProxyServer(false, false, false);
        var transcoder = new RecordingTranscoder();
        proxy.ReverseProxy = new ReverseProxyOptions { GrpcJsonTranscoder = transcoder };

        var beforeRequest = false;
        proxy.BeforeRequest += (_, _) =>
        {
            beforeRequest = true;
            return Task.CompletedTask;
        };

        using var session = MakeSession(proxy);
        await (Task)typeof(ProxyServer).GetMethod("OnBeforeRequest", PrivateInstance)!
            .Invoke(proxy, [session])!;

        Assert.AreEqual(1, transcoder.Calls);
        Assert.IsTrue(beforeRequest, "Transcoder path must still invoke BeforeRequest.");
    }

    [TestMethod]
    public async Task OnBeforeRequest_WithTranscoderAndMiddleware_RunsBoth()
    {
        using var proxy = new ProxyServer(false, false, false);
        var transcoder = new RecordingTranscoder();
        var middleware = new RecordingMiddleware();
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            GrpcJsonTranscoder = transcoder,
            Middleware = [middleware],
        };

        using var session = MakeSession(proxy);
        await (Task)typeof(ProxyServer).GetMethod("OnBeforeRequest", PrivateInstance)!
            .Invoke(proxy, [session])!;

        Assert.AreEqual(1, transcoder.Calls);
        Assert.AreEqual(1, middleware.Calls);
    }

    [TestMethod]
    public async Task OnBeforeRequestBodyWrite_WithoutHandler_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        var args = new BeforeBodyWriteEventArgs(session, Encoding.ASCII.GetBytes("x"), false, true);
        await proxy.OnBeforeRequestBodyWrite(args);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("x"), args.BodyBytes);
    }

    [TestMethod]
    public async Task OnBeforeResponse_IsFastPath_SkipsHandlers()
    {
        using var proxy = new ProxyServer(false, false, false);
        var called = false;
        proxy.BeforeResponse += (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        };

        using var session = MakeSession(proxy);
        session.IsFastPath = true;

        await (Task)typeof(ProxyServer).GetMethod("OnBeforeResponse", PrivateInstance,
            [typeof(SessionEventArgs)])!
            .Invoke(proxy, [session])!;

        Assert.IsFalse(called, "Fast-path sessions must skip BeforeResponse.");
    }

    [TestMethod]
    public async Task OnBeforeResponse_WithTranscoder_RewritesThenRunsBeforeResponse()
    {
        using var proxy = new ProxyServer(false, false, false);
        var transcoder = new RecordingTranscoder();
        proxy.ReverseProxy = new ReverseProxyOptions { GrpcJsonTranscoder = transcoder };

        var beforeResponse = false;
        proxy.BeforeResponse += (_, _) =>
        {
            beforeResponse = true;
            return Task.CompletedTask;
        };

        using var session = MakeSession(proxy);
        session.HttpClient.Response.StatusCode = 200;
        session.HttpClient.Response.HttpVersion = HttpHeader.Version11;

        await (Task)typeof(ProxyServer).GetMethod("OnBeforeResponse", PrivateInstance,
            [typeof(SessionEventArgs)])!
            .Invoke(proxy, [session])!;

        Assert.AreEqual(1, transcoder.ResponseCalls);
        Assert.IsTrue(beforeResponse, "Transcoder path must still invoke BeforeResponse.");
    }

    [TestMethod]
    public async Task OnBeforeResponse_MayCompressFalse_StillInvokesHandler()
    {
        using var proxy = new ProxyServer(false, false, false);
        var called = false;
        proxy.BeforeResponse += (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        };

        using var session = MakeSession(proxy);
        session.HttpClient.Response.StatusCode = 200;
        session.HttpClient.Response.HttpVersion = HttpHeader.Version11;

        await (Task)typeof(ProxyServer).GetMethod("OnBeforeResponse", PrivateInstance,
            [typeof(SessionEventArgs), typeof(bool)])!
            .Invoke(proxy, [session, false])!;

        Assert.IsTrue(called);
    }

    [TestMethod]
    public async Task DowngradeChunkedFramingForHttp10Origin_UnreadBody_BuffersAndClearsChunked()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version10;
        session.HttpClient.Request.IsChunked = true;
        session.HttpClient.Request.IsBodyRead = false;

        var method = typeof(ProxyServer).GetMethod("DowngradeChunkedFramingForHttp10OriginIfNeeded",
            PrivateStatic)!;
        await (Task)method.Invoke(null, [session, CancellationToken.None])!;

        Assert.IsFalse(session.HttpClient.Request.IsChunked,
            "HTTP/1.0 origin downgrade must buffer (possibly empty) and clear chunked framing.");
        Assert.AreEqual(0, session.HttpClient.Request.ContentLength);
    }

    [TestMethod]
    public void ProxyServer_MaxBufferedBodyBytes_RoundTrips()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxBufferedBodyBytes = 12345;
        Assert.AreEqual(12345, proxy.MaxBufferedBodyBytes);
        proxy.MaxDecodedHeaderListBytes = 8192;
        Assert.AreEqual(8192, proxy.MaxDecodedHeaderListBytes);
    }

    [TestMethod]
    public void ShouldBufferBodyForReplay_False_WithoutConnectionOrWhenChunked()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.ContentLength = 32;
        session.HttpClient.Request.IsChunked = false;

        var method = typeof(ProxyServer).GetMethod("ShouldBufferBodyForReplay", PrivateStatic)!;
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "No upstream connection → never buffer for replay.");

        session.HttpClient.Request.IsChunked = true;
        using var serverConn = MakeServerConnection(proxy);
        session.HttpClient.SetConnection(serverConn);
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "Chunked bodies must not be buffered for keep-alive replay.");
    }

    [TestMethod]
    public void ShouldBufferBodyForReplay_False_WhenContentEncodingOrExpectContinue()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        using var serverConn = MakeServerConnection(proxy);
        session.HttpClient.SetConnection(serverConn);
        serverConn.MarkExchangeStarted();

        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.ContentLength = 32;
        session.HttpClient.Request.IsChunked = false;
        session.HttpClient.Request.Headers.AddHeader(KnownHeaders.ContentEncoding, "gzip");

        var method = typeof(ProxyServer).GetMethod("ShouldBufferBodyForReplay", PrivateStatic)!;
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "Content-Encoding bodies must not be re-encoded for replay.");

        session.HttpClient.Request.Headers.RemoveHeader(KnownHeaders.ContentEncoding);
        session.HttpClient.Request.Headers.AddHeader(KnownHeaders.Expect, KnownHeaders.Expect100Continue);
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "Expect: 100-continue must not buffer for replay.");
    }

    [TestMethod]
    public void PrepareRequestHeaders_DropsOnlyZeroWeightCodings_KeepsPositiveQ()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.AcceptEncoding, "gzip;q=0, deflate;q=0.5, br;q=0.0, identity;q=1");

        typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!
            .Invoke(null, [headers]);

        var accept = headers.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);
        Assert.IsNotNull(accept);
        Assert.IsFalse(accept.Contains("gzip", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(accept.Contains("br", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(accept, "deflate");
        StringAssert.Contains(accept, "identity");
    }

    [TestMethod]
    public void ProxyServer_TimeoutAndPoolProperties_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.ConnectionTimeOutSeconds = 42;
        proxy.ConnectTimeOutSeconds = 17;
        proxy.ClientHeaderTimeoutSeconds = 9;
        proxy.ResponseHeaderTimeoutSeconds = 11;
        proxy.EnableConnectionPool = false;
        proxy.EnableTcpServerConnectionPrefetch = false;
        Assert.AreEqual(42, proxy.ConnectionTimeOutSeconds);
        Assert.AreEqual(17, proxy.ConnectTimeOutSeconds);
        Assert.AreEqual(9, proxy.ClientHeaderTimeoutSeconds);
        Assert.AreEqual(11, proxy.ResponseHeaderTimeoutSeconds);
        Assert.IsFalse(proxy.EnableConnectionPool);
        Assert.IsFalse(proxy.EnableTcpServerConnectionPrefetch);
    }

    private static TcpServerConnection MakeServerConnection(ProxyServer proxy)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var stream = new HttpServerStream(proxy, Stream.Null, proxy.BufferPool, CancellationToken.None);
        return new TcpServerConnection(proxy, client, stream, "origin.test", 80, false,
            default, HttpHeader.Version11, null, null, "cache-key");
    }

    private sealed class RecordingMiddleware : IProxyMiddleware
    {
        public int Calls { get; private set; }

        public ValueTask InvokeAsync(
            ProxyMiddlewareContext context,
            ProxyMiddlewareDelegate next,
            CancellationToken cancellationToken)
        {
            Calls++;
            return next(context, cancellationToken);
        }
    }

    private sealed class RecordingTranscoder : IGrpcJsonTranscoder
    {
        public int Calls { get; private set; }
        public int ResponseCalls { get; private set; }

        public ValueTask<bool> TryRewriteRequestAsync(object session, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(false);
        }

        public ValueTask<bool> TryRewriteResponseAsync(object session, CancellationToken cancellationToken = default)
        {
            ResponseCalls++;
            return ValueTask.FromResult(false);
        }
    }
}
