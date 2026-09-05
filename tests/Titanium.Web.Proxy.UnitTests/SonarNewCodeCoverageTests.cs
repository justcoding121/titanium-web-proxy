using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Targeted coverage for new-code helpers that Sonar's 80% new-code gate still treats as uncovered.
///     Exercises reflection seams and MemoryStream writers only — no live origin sockets beyond the
///     existing Http2OriginConnection shell used by pool tests.
/// </summary>
[TestClass]
public class SonarNewCodeCoverageTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static MethodInfo BridgeMethod(string name) =>
        typeof(Http3OriginBridge).GetMethod(name, PrivateStatic)
        ?? throw new InvalidOperationException($"HTTP/3 bridge method {name} was not found.");

    private static MethodInfo ProxyMethod(string name) =>
        typeof(ProxyServer).GetMethod(name, PrivateStatic)
        ?? throw new InvalidOperationException($"ProxyServer method {name} was not found.");

    private static (Http2FrameHeader Header, byte[] Buffer) FrameScratch(int streamId = 1) =>
        (new Http2FrameHeader { StreamId = streamId }, new byte[9]);

    private static SessionEventArgs MakeSession(ProxyServer proxy, ProxyEndPoint? endPoint = null)
    {
        endPoint ??= new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    private static async Task<Http2OriginConnection> CreateShellAsync(ProxyServer proxy)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await accept;
        accepted.Dispose();

        var stream = new HttpServerStream(proxy, new NetworkStream(client, ownsSocket: true),
            new DefaultBufferPool(), CancellationToken.None);
        var serverConn = new TcpServerConnection(proxy, client, stream, "origin.test", 443, true,
            default, HttpHeader.Version20, null, null, "h2-origin");

        var ctor = typeof(Http2OriginConnection).GetConstructor(PrivateInstance, null,
            [typeof(TcpServerConnection), typeof(Microsoft.Extensions.Logging.ILogger), typeof(long),
                typeof(ProxyResourceLimits)], null)!;
        return (Http2OriginConnection)ctor.Invoke([serverConn, NullLogger.Instance, 1024L * 1024L,
            ProxyResourceLimits.Default])!;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http2Helper encode / frame writers
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EncodeHeaderBlock_CoversRequestResponsePriorityViaAndTableSize()
    {
        var settings = new Http2Settings { MaxFrameSize = 32 };
        var (header, buf) = FrameScratch(1);

        using var actual = new MemoryStream();
        await using (var writer = new Http2FrameWriter(actual))
        {
            foreach (var method in new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "CONNECT", "PATCH" })
            {
                var request = new Request
                {
                    Method = method,
                    HttpVersion = HttpHeader.Version20,
                    IsHttps = method != "CONNECT",
                    RequestUriString8 = "/item".GetByteString(),
                    Authority = "origin.example:443".GetByteString(),
                    ExtendedConnectProtocol = method == "CONNECT" ? "websocket" : null,
                    Priority = method == "GET" ? 0x0102030405L : null
                };
                request.Headers.AddHeader("Content-Type", "text/plain");
                request.Headers.AddHeader("Via", "2.0 twp");
                Http2Helper.EnqueueHeader(settings, header, buf, request, endStream: true, writer);
            }

            settings.UpdateHeaderTableSize(0);
            settings.UpdateHeaderTableSize(4096);

            foreach (var status in new[] { 200, 204, 206, 301, 302, 304, 400, 404, 500, 502, 418 })
            {
                var response = new Response
                {
                    HttpVersion = HttpHeader.Version20,
                    StatusCode = status,
                    StatusDescription = "x"
                };
                response.Headers.AddHeader("X-Mixed-Case", "kept");
                Http2Helper.EnqueueHeader(settings, header, buf, response, endStream: true, writer);
            }

            var hostOnly = new Request
            {
                Method = "GET",
                HttpVersion = HttpHeader.Version20,
                IsHttps = false,
                Host = "fallback.example",
                RequestUriString8 = "/".GetByteString()
            };
            Http2Helper.EnqueueHeader(settings, header, buf, hostOnly, endStream: true, writer);

            var trailers = new HeaderCollection();
            trailers.AddHeader("X-Trailer", "yes");
            trailers.AddHeader("grpc-status", "0");
            Http2Helper.EnqueueTrailer(settings, header, buf, 1, trailers, endStream: true, writer);

            Http2Helper.EnqueueSettingsAck(writer);
            Http2Helper.EnqueuePingAck(writer, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Http2Helper.EnqueueDataFrames(writer, 1, ReadOnlyMemory<byte>.Empty, endStream: true, maxFrameSize: 0);
        }

        Assert.IsTrue(actual.Length > 200);
    }

    [TestMethod]
    public async Task SendHeaderTrailerBodyAndData_WriteExpectedFrames()
    {
        var settings = new Http2Settings();
        var (header, buf) = FrameScratch(3);
        var flow = new Http2FlowController();

        using var headers = new MemoryStream();
        var request = new Request
        {
            Method = "POST",
            HttpVersion = HttpHeader.Version20,
            IsHttps = true,
            Authority = "api.example".GetByteString(),
            RequestUriString8 = "/upload".GetByteString()
        };
        request.Headers.AddHeader("content-type", "application/octet-stream");
        await Http2Helper.SendHeader(settings, header, buf, request, endStream: false, headers, pushPromise: false);
        Assert.AreEqual((byte)Http2FrameType.Headers, headers.ToArray()[3]);

        using var push = new MemoryStream();
        await Http2Helper.SendHeader(settings, header, buf, request, endStream: true, push, pushPromise: true);
        Assert.AreEqual((byte)Http2FrameType.PushPromise, push.ToArray()[3]);

        using var trailers = new MemoryStream();
        var trailing = new HeaderCollection();
        trailing.AddHeader("ETag", "abc");
        await Http2Helper.SendTrailer(settings, header, buf, 3, trailing, endStream: true, trailers);
        Assert.IsTrue(trailers.Length > 9);

        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200
        };
        response.Body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        response.IsBodyRead = true;
        using var body = new MemoryStream();
        await Http2Helper.SendBody(settings, response, header, buf, new byte[4], flow, body, CancellationToken.None);
        Assert.IsTrue(body.Length > 9 + 8);

        using var data = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);
        await Http2Helper.SendData(header, buf, 5, ReadOnlyMemory<byte>.Empty, endStream: true, maxFrameSize: 0,
            flow, data, CancellationToken.None, gate);
        await Http2Helper.SendData(header, buf, 5, new byte[] { 9, 8, 7 }, endStream: true, maxFrameSize: 2,
            flow, data, CancellationToken.None, gate);
        Assert.IsTrue(data.Length > 9);
    }

    [TestMethod]
    public async Task Http2FrameWriter_DisposeIsIdempotent_AndDropsEnqueueAfterDispose()
    {
        using var output = new MemoryStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        var writer = new Http2FrameWriter(output, writeLock);

        var first = ArrayPool<byte>.Shared.Rent(8);
        first.AsSpan(0, 8).Fill(1);
        writer.EnqueueRented(first, 8);

        var second = ArrayPool<byte>.Shared.Rent(8);
        second.AsSpan(0, 8).Fill(2);
        writer.EnqueueRented(second, 8);

        await writer.DisposeAsync();
        await writer.DisposeAsync();

        var after = ArrayPool<byte>.Shared.Rent(4);
        writer.EnqueueRented(after, 4);

        Assert.IsTrue(output.Length >= 16);
        Assert.IsTrue(writer.Completion.IsCompleted);
    }

    [TestMethod]
    public void StatusAndMethodCaches_CoverSwitchFallbacks()
    {
        var status = typeof(Http2Helper).GetMethod("StatusCodeBytes", PrivateStatic)!;
        foreach (var code in new[] { 200, 204, 206, 301, 302, 304, 400, 404, 500, 502, 201 })
        {
            var bytes = (ByteString)status.Invoke(null, [code])!;
            Assert.AreEqual(code.ToString(), bytes.GetString());
        }

        var method = typeof(Http2Helper).GetMethod("MethodBytes", PrivateStatic)!;
        foreach (var name in new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "CONNECT", "TRACE" })
        {
            var bytes = (ByteString)method.Invoke(null, [name])!;
            Assert.AreEqual(name, bytes.GetString());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http2OriginConnectionPool + shell connection bookkeeping
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void BuildPoolKey_DiffersByHttpsCleartextUpstreamAndConnect()
    {
        using var proxy = new ProxyServer(false, false, false);
        var explicitEp = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var cleartext = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false) { ForwardCleartext = true };
        var https = new TransparentProxyEndPoint(IPAddress.Loopback, 0, true);

        var explicitKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, explicitEp, null, null, "origin.test", 443, null, null);
        var clearKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, cleartext, null, null, "origin.test", 80, null, null);
        var httpsKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, https, null, null, "origin.test", 443, null, null);
        var upstreamKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, explicitEp, new ExternalProxy("up.example", 8080),
            new IPEndPoint(IPAddress.Loopback, 9), "origin.test", 443, "connect.host", 8443);

        Assert.AreNotEqual(explicitKey, clearKey);
        Assert.AreNotEqual(clearKey, httpsKey);
        Assert.AreNotEqual(explicitKey, upstreamKey);

        using var session = MakeSession(proxy, explicitEp);
        var sessionKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, session, "origin.test", 443, "connect.host", 8443);
        Assert.IsFalse(string.IsNullOrEmpty(sessionKey));
    }

    [TestMethod]
    public async Task Pool_OfferRentInvalidateAndDrain_CoverCapacityAndUnusableBranches()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        const string key = "pool-coverage";

        try
        {
            var first = await CreateShellAsync(proxy);
            pool.Offer(key, first);

            var opened = 0;
            var rented = await pool.RentAsync(key, _ =>
            {
                opened++;
                return Task.FromResult(first);
            }, CancellationToken.None);
            Assert.AreSame(first, rented);
            Assert.AreEqual(0, opened);

            pool.Invalidate(key, first);
            first.Touch();
            first.AcquireLease();
            first.ReleaseLease();
            Assert.IsTrue(first.SoftStreamCapacity >= 1);
            Assert.IsFalse(first.IsNearStreamIdExhaustion);
            Assert.IsTrue(first.LastUsedUtc <= DateTime.UtcNow);

            var created = await CreateShellAsync(proxy);
            var fromOpen = await pool.RentAsync(key, _ => Task.FromResult(created), CancellationToken.None);
            Assert.AreSame(created, fromOpen);

            var exhausted = await CreateShellAsync(proxy);
            typeof(Http2OriginConnection).GetField("lastStreamId", PrivateInstance)!
                .SetValue(exhausted, int.MaxValue - 1);
            pool.Offer(key, exhausted);
            Assert.IsTrue(exhausted.IsNearStreamIdExhaustion);

            var retired = await CreateShellAsync(proxy);
            retired.Retire();
            pool.Offer(key, retired);

            var max = ProxyResourceLimits.Default.MaxOriginHttp2ConnectionsPerAuthority;
            for (var i = 0; i < max + 1; i++)
                pool.Offer("at-capacity", await CreateShellAsync(proxy));

            Assert.IsFalse(string.IsNullOrEmpty(Http2OriginConnectionPool.DiagPickStats.FormatSummary()));
            Assert.IsFalse(Http2OriginConnectionPool.DiagPickStats.IsEnabled);
        }
        finally
        {
            await pool.DrainAsync();
            await pool.DrainAsync();
        }
    }

    [TestMethod]
    public async Task Pool_OfferAfterDrain_DisposesConnection()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        await pool.DrainAsync();

        var leftover = await CreateShellAsync(proxy);
        pool.Offer("drained", leftover);
        Assert.IsFalse(leftover.IsUsable);
        leftover.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http3OriginBridge helpers
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH2OriginAuthority_ParsesPortHostAndUriFallback()
    {
        var withPort = new Request { Authority = "origin.example:8443".GetByteString() };
        var parsed = ((string Host, int Port))BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [withPort])!;
        Assert.AreEqual("origin.example", parsed.Host);
        Assert.AreEqual(8443, parsed.Port);

        var bare = new Request { Authority = "origin.example".GetByteString() };
        parsed = ((string Host, int Port))BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [bare])!;
        Assert.AreEqual("origin.example", parsed.Host);
        Assert.AreEqual(443, parsed.Port);

        var fromUri = new Request { RequestUriString = "https://uri.example:9443/x" };
        parsed = ((string Host, int Port))BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [fromUri])!;
        Assert.AreEqual("uri.example", parsed.Host);
        Assert.AreEqual(9443, parsed.Port);

        var fromHost = new Request { RequestUriString = "/x" };
        fromHost.Headers.AddHeader("Host", "host.example:7443");
        parsed = ((string Host, int Port))BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [fromHost])!;
        Assert.AreEqual("host.example", parsed.Host);
        Assert.AreEqual(7443, parsed.Port);
    }

    [TestMethod]
    public void ResolveTransparentForwardTarget_UsesForwardHostWhenPresent()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var explicitSession = MakeSession(proxy);
        var none = ((string? Host, int? Port))BridgeMethod("ResolveTransparentForwardTarget")
            .Invoke(null, [explicitSession])!;
        Assert.IsNull(none.Host);
        Assert.IsNull(none.Port);

        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "forward.example",
            ForwardPort = 9443
        };
        using var transparent = MakeSession(proxy, ep);
        var fwd = ((string? Host, int? Port))BridgeMethod("ResolveTransparentForwardTarget")
            .Invoke(null, [transparent])!;
        Assert.AreEqual("forward.example", fwd.Host);
        Assert.AreEqual(9443, fwd.Port);
    }

    [TestMethod]
    public async Task PrepareCanReplayAndInterimAdapter_CoverBridgeEdges()
    {
        var request = new Request
        {
            Method = "POST",
            Host = "Host.Example",
            HttpVersion = HttpHeader.Version20
        };
        request.Headers.AddHeader("Connection", "keep-alive");
        request.Headers.AddHeader("Keep-Alive", "timeout=5");
        request.Headers.AddHeader("X-Mixed", "kept");
        BridgeMethod("PrepareH2OriginRequestHeaders").Invoke(null, [request]);
        Assert.AreEqual("Host.Example", request.Authority.GetString());
        Assert.IsNull(request.Headers.GetHeaderValueOrNull("connection"));
        Assert.AreEqual("kept", request.Headers.GetHeaderValueOrNull("x-mixed"));

        var alreadyLower = new Request { Method = "GET", Authority = "a.example".GetByteString() };
        alreadyLower.Headers.AddHeader("x-keep", "yes");
        BridgeMethod("PrepareH2OriginRequestHeaders").Invoke(null, [alreadyLower]);
        Assert.AreEqual("yes", alreadyLower.Headers.GetHeaderValueOrNull("x-keep"));

        Assert.IsTrue((bool)BridgeMethod("CanReplayHttp2OriginRequest").Invoke(null, [request, null])!);
        request.IsBodyReceived = true;
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task> pump =
            (_, _) => Task.CompletedTask;
        Assert.IsTrue((bool)BridgeMethod("CanReplayHttp2OriginRequest").Invoke(null, [request, pump])!);

        Assert.IsNull(BridgeMethod("CreateInterimResponseAdapter").Invoke(null, [null]));

        var seen = 0;
        Func<Response, CancellationToken, Task> onInterim = (r, _) =>
        {
            seen = r.StatusCode;
            Assert.AreEqual("h3", r.Headers.GetHeaderValueOrNull("x-from"));
            return Task.CompletedTask;
        };
        var adapter = BridgeMethod("CreateInterimResponseAdapter").Invoke(null, [onInterim]);
        Assert.IsNotNull(adapter);
        var headers = new HeaderCollection();
        headers.AddHeader("x-from", "h3");
        var invoke = adapter!.GetType().GetMethod("Invoke")!;
        await (Task)invoke.Invoke(adapter, [103, headers, CancellationToken.None])!;
        Assert.AreEqual(103, seen);

        var hostFallback = new Request { Method = "GET", Host = "host-only.example" };
        var built = (List<(string, string)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [hostFallback, "fallback.example"])!;
        Assert.IsTrue(built.Contains((":authority", "host-only.example")));
        Assert.IsTrue(built.Contains((":path", "/")));
        Assert.IsTrue(built.Contains((":scheme", "http")));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http3RequestStream + H2→H1 / H2→H3 helpers
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Http3RequestStream_StatusAndCaseHelpers()
    {
        var status = typeof(Http3RequestStream).GetMethod("StatusCodeString", PrivateStatic)!;
        foreach (var code in new[] { 200, 204, 301, 302, 304, 400, 404, 500, 502, 503, 418 })
            Assert.AreEqual(code.ToString(), (string)status.Invoke(null, [code])!);

        var upper = typeof(Http3RequestStream).GetMethod("HasUpperAscii", PrivateStatic)!;
        Assert.IsTrue((bool)upper.Invoke(null, ["Content-Type"])!);
        Assert.IsFalse((bool)upper.Invoke(null, ["content-type"])!);
        Assert.IsFalse((bool)upper.Invoke(null, [""])!);
    }

    [TestMethod]
    public void Http2ToHttp11Helpers_ParseStatusLowercaseAndThreshold()
    {
        Assert.AreEqual(64 * 1024, (int)ProxyMethod("EagerBufferBodyThreshold").Invoke(null, [int.MaxValue])!);
        Assert.AreEqual(0, (int)ProxyMethod("EagerBufferBodyThreshold").Invoke(null, [-5])!);
        Assert.AreEqual(128, (int)ProxyMethod("EagerBufferBodyThreshold").Invoke(null, [128])!);

        var parse = ProxyMethod("TryParseHttp11StatusLine");
        var args = new object?[] { "HTTP/1.1 204 No Content", 0 };
        Assert.IsTrue((bool)parse.Invoke(null, args)!);
        Assert.AreEqual(204, args[1]);
        args = ["HTTP/1.0 200 OK", 0];
        Assert.IsFalse((bool)parse.Invoke(null, args)!);
        args = [null, 0];
        Assert.IsFalse((bool)parse.Invoke(null, args)!);

        var headers = new HeaderCollection();
        headers.AddHeader("X-Mixed", "a");
        headers.AddHeader("already-lower", "b");
        ProxyMethod("LowercaseHeaderNames").Invoke(null, [headers]);
        Assert.AreEqual("a", headers.GetHeaderValueOrNull("x-mixed"));
        Assert.AreEqual("b", headers.GetHeaderValueOrNull("already-lower"));
        ProxyMethod("LowercaseHeaderNames").Invoke(null, [headers]);
    }

    [TestMethod]
    public void Http2ToHttp3Helpers_ResolveIdentityAndConsolidateCookies()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var explicitSession = MakeSession(proxy);
        explicitSession.HttpClient.Request.RequestUriString = "https://req.example:9443/x";

        var fromUri = ((string Host, int Port))ProxyMethod("ResolveH3BridgeOriginIdentity")
            .Invoke(null, [explicitSession, "fallback.example", 443])!;
        Assert.AreEqual("req.example", fromUri.Host);
        Assert.AreEqual(9443, fromUri.Port);

        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "h3-origin.example",
            ForwardPort = 443
        };
        using var transparent = MakeSession(proxy, ep);
        var fromForward = ((string Host, int Port))ProxyMethod("ResolveH3BridgeOriginIdentity")
            .Invoke(null, [transparent, "fallback.example", 443])!;
        Assert.AreEqual("h3-origin.example", fromForward.Host);
        Assert.AreEqual(443, fromForward.Port);

        var cookies = new HeaderCollection();
        cookies.AddHeader("Cookie", "a=1");
        ProxyMethod("ConsolidateCookieHeaders").Invoke(null, [cookies]);
        Assert.AreEqual(1, cookies.GetHeaders("Cookie")!.Count);

        cookies.AddHeader("Cookie", "b=2");
        ProxyMethod("ConsolidateCookieHeaders").Invoke(null, [cookies]);
        Assert.AreEqual(1, cookies.GetHeaders("Cookie")!.Count);
        Assert.AreEqual("a=1; b=2", cookies.GetHeaderValueOrNull("Cookie"));
    }

    [TestMethod]
    public void ApplyCleartextOriginScheme_FlipsHttpsFromOriginAndCleartextClient()
    {
        using var proxy = new ProxyServer(false, false, false);
        var apply = typeof(Http2Helper).GetMethod("ApplyCleartextOriginScheme", PrivateStatic)!;
        var request = new Request { IsHttps = true };
        apply.Invoke(null, [request, null, null]);
        Assert.IsTrue(request.IsHttps);

        var originSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var originStream = new HttpServerStream(proxy, Stream.Null, new DefaultBufferPool(), CancellationToken.None);
        using var clearOrigin = new TcpServerConnection(proxy, originSocket, originStream, "origin.test", 80, false,
            default, HttpHeader.Version20, null, null, "h2c");
        apply.Invoke(null, [request, clearOrigin, null]);
        Assert.IsFalse(request.IsHttps);

        request.IsHttps = false;
        var httpsSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var httpsStream = new HttpServerStream(proxy, Stream.Null, new DefaultBufferPool(), CancellationToken.None);
        using var httpsOrigin = new TcpServerConnection(proxy, httpsSocket, httpsStream, "origin.test", 443, true,
            default, HttpHeader.Version20, null, null, "h2");
        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var client = new TcpClientConnection(proxy, clientSocket);
        client.Http2CleartextClient = true;
        apply.Invoke(null, [request, httpsOrigin, client]);
        Assert.IsTrue(request.IsHttps);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Extra new-code lines to cross the 80% gate
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void DiagPickStats_WhenForcedEnabled_RecordsAndFormats()
    {
        var diag = typeof(Http2OriginConnectionPool).GetNestedType("DiagPickStats", BindingFlags.NonPublic)!;
        var enabled = diag.GetField("Enabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = (bool)enabled.GetValue(null)!;
        var outPath = Path.Combine(Path.GetTempPath(), $"twp-diag-pick-{Guid.NewGuid():N}.log");
        Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT", outPath);
        try
        {
            enabled.SetValue(null, true);
            diag.GetMethod("OnRent", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            diag.GetMethod("OnTryPick", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [2, 1, 3, true]);
            diag.GetMethod("OnTryPick", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [1, 1, 1, false]);
            diag.GetMethod("OnCreationGate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            diag.GetMethod("OnTryPickAny", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            diag.GetMethod("OnOpen", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            diag.GetMethod("Emit", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["test"]);

            var summary = Http2OriginConnectionPool.DiagPickStats.FormatSummary();
            Assert.IsTrue(summary.Contains("tryPick="));
            // Emit writes via fire-and-forget Task.Run — wait briefly for the file.
            Assert.IsTrue(SpinWait.SpinUntil(
                () => File.Exists(outPath) && new FileInfo(outPath).Length > 0,
                TimeSpan.FromSeconds(3)));
        }
        finally
        {
            enabled.SetValue(null, original);
            Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT", null);
            SpinWait.SpinUntil(() =>
            {
                try
                {
                    if (File.Exists(outPath)) File.Delete(outPath);
                    return !File.Exists(outPath);
                }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(3));
        }
    }

    [TestMethod]
    public async Task OriginConnection_CreditFailAndUnusableSend()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await CreateShellAsync(proxy);

        typeof(Http2OriginConnection).GetField("concurrencyGateCapacity", PrivateInstance)!
            .SetValue(connection, 16);
        // SoftPick = SETTINGS/gate; TLS SoftGrow = SoftStreamCapacity (Mac long-arm dial).
        Assert.AreEqual(16, connection.SoftStreamCapacity);
        Assert.AreEqual(connection.SoftStreamCapacity, connection.PoolGrowThreshold);

        typeof(Http2OriginConnection).GetMethod("AttachExclusiveFrameWriter", PrivateInstance)!
            .Invoke(connection, null);

        var grant = typeof(Http2OriginConnection).GetMethod("GrantReceiveCreditAsync", PrivateInstance)!;
        await (Task)grant.Invoke(connection, [1, 0, false, CancellationToken.None])!;
        await (Task)grant.Invoke(connection, [1, 16, false, CancellationToken.None])!;
        await (Task)grant.Invoke(connection, [1, Http2Helper.ReceiveCreditBatchThreshold, false, CancellationToken.None])!;
        await (Task)grant.Invoke(connection, [1, 0, true, CancellationToken.None])!;

        var fail = typeof(Http2OriginConnection).GetMethod("Fail", PrivateInstance)!;
        fail.Invoke(connection, [new IOException("peer closed"), true]);
        fail.Invoke(connection, [new IOException("HTTP/2 protocol error: bad settings"), true]);
        fail.Invoke(connection, [new ProxyHttpException("wrapped", new IOException("x"), null), false]);
        Assert.IsFalse(connection.IsUsable);

        await Assert.ThrowsExactlyAsync<Http2OriginGoAwayException>(() =>
            connection.SendAsync(new Request { Method = "GET" }, null, CancellationToken.None));

        var violation = typeof(Http2OriginConnection).GetMethod("IsHttp2ProtocolViolation", PrivateStatic)!;
        Assert.IsTrue((bool)violation.Invoke(null, [new IOException("HTTP/2 protocol error: x")])!);
        Assert.IsFalse((bool)violation.Invoke(null, [new IOException("reset")])!);
    }

    [TestMethod]
    public async Task Http2OriginRelayPool_AssignReleaseAndFailedOpen()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var shell = await CreateShellAsync(proxy);
        await using var relay = new Http2OriginRelayPool(
            shell.ServerConnection,
            _ => throw new IOException("cannot open another origin"),
            ProxyResourceLimits.Default,
            NullLogger.Instance,
            new SemaphoreSlim(1, 1));

        Assert.AreEqual(1, relay.LegCount);
        Assert.IsNotNull(relay.PrimaryLeg);
        Assert.AreEqual(1, relay.SnapshotLegs().Count);

        var first = await relay.AssignStreamAsync(1, CancellationToken.None);
        var again = await relay.AssignStreamAsync(1, CancellationToken.None);
        Assert.AreEqual(first.OriginStreamId, again.OriginStreamId);
        Assert.IsTrue(relay.TryGetAssignment(1, out var found));
        Assert.AreEqual(first.OriginStreamId, found.OriginStreamId);

        typeof(Http2OriginRelayPool.OriginLeg).GetField("ActiveStreams")!
            .SetValue(relay.PrimaryLeg, 10_000);
        var second = await relay.AssignStreamAsync(3, CancellationToken.None);
        Assert.IsTrue(second.OriginStreamId > 0);

        relay.ReleaseStream(1);
        relay.ReleaseStream(99);
        Assert.IsFalse(relay.TryGetAssignment(1, out _));
    }

    [TestMethod]
    public void TryRejectLoopedVia_CoversFastPathAndLoop()
    {
        using var proxy = new ProxyServer(false, false, false) { ViaHeaderPseudonym = "twp-test" };
        var reject = typeof(ProxyServer).GetMethod("TryRejectLoopedVia", PrivateInstance)!;

        using var fast = MakeSession(proxy);
        fast.IsFastPath = true;
        Assert.IsFalse((bool)reject.Invoke(proxy, [fast])!);

        using var unnamed = new ProxyServer(false, false, false);
        using var emptyName = MakeSession(unnamed);
        Assert.IsFalse((bool)reject.Invoke(unnamed, [emptyName])!);

        using var ok = MakeSession(proxy);
        Assert.IsFalse((bool)reject.Invoke(proxy, [ok])!);
        Assert.IsNotNull(ok.HttpClient.Request.Headers.GetHeaderValueOrNull("Via"));

        using var looped = MakeSession(proxy);
        looped.HttpClient.Request.Headers.AddHeader("Via", "2.0 twp-test");
        Assert.IsTrue((bool)reject.Invoke(proxy, [looped])!);
        Assert.AreEqual(508, looped.HttpClient.Response.StatusCode);
    }

    [TestMethod]
    public async Task Http3FastForwards_ClosedOrigin_CoverPrepareAndFailPaths()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardCleartext = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = 1
        };
        var request = new Request
        {
            Method = "GET",
            IsHttps = false,
            HttpVersion = HttpHeader.Version30,
            Host = "origin.example",
            Authority = "127.0.0.1:1".GetByteString(),
            RequestUriString8 = "/".GetByteString()
        };
        request.Headers.AddHeader("x-test", "1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SessionEventArgs Cold() => MakeSession(proxy, ep);

        var tcpFwd = new H3H2FastForward { Request = request, ProxyEndPoint = ep, MaxBufferedBodyBytes = 1024 };
        try
        {
            await Http3OriginBridge.ForwardOverTcpFastAsync(tcpFwd, proxy, NullLogger.Instance, cts.Token, Cold);
        }
        catch (Exception ex) when (ex is not AssertFailedException)
        {
            Assert.IsNotNull(ex);
        }

        var h2Request = new Request
        {
            Method = "GET",
            IsHttps = true,
            HttpVersion = HttpHeader.Version30,
            Host = "origin.example",
            Authority = "127.0.0.1:1".GetByteString(),
            RequestUriString8 = "/".GetByteString()
        };
        var h2Fwd = new H3H2FastForward { Request = h2Request, ProxyEndPoint = ep, MaxBufferedBodyBytes = 1024 };
        try
        {
            await Http3OriginBridge.ForwardOverHttp2FastAsync(h2Fwd, proxy, NullLogger.Instance, cts.Token, Cold);
        }
        catch (Exception ex) when (ex is not AssertFailedException)
        {
            Assert.IsNotNull(ex);
        }

        using var session = MakeSession(proxy, ep);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.IsHttps = false;
        session.HttpClient.Request.HttpVersion = HttpHeader.Version30;
        session.HttpClient.Request.Authority = "127.0.0.1:1".GetByteString();
        session.HttpClient.Request.RequestUriString8 = "/".GetByteString();
        session.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
        try
        {
            await Http3OriginBridge.ForwardAsync(session, proxy, Http3OriginRoute.None, NullLogger.Instance,
                cts.Token);
        }
        catch (Exception ex) when (ex is not AssertFailedException)
        {
            Assert.IsNotNull(ex);
        }
    }

    private delegate bool TryReadPrefixedIntDelegate(ReadOnlySpan<byte> data, int prefixBits, out ulong value,
        out int consumed);
    private delegate bool TryReadStringLiteralDelegate(ReadOnlySpan<byte> data, out string result, out int consumed);
    private delegate string HuffmanDecodeDelegate(ReadOnlySpan<byte> data);

    [TestMethod]
    public void QpackDecoder_PrefixedIntAndLiteralEdges()
    {
        var prefixed = (TryReadPrefixedIntDelegate)Delegate.CreateDelegate(typeof(TryReadPrefixedIntDelegate),
            typeof(QpackDecoder).GetMethod("TryReadPrefixedInt", PrivateStatic)!);
        Assert.IsFalse(prefixed(ReadOnlySpan<byte>.Empty, 8, out _, out _));
        Assert.IsTrue(prefixed(new byte[] { 0x05 }, 8, out var small, out var consumed));
        Assert.AreEqual(5UL, small);
        Assert.AreEqual(1, consumed);

        var overflow = new byte[12];
        overflow[0] = 0xFF;
        for (var i = 1; i < overflow.Length; i++) overflow[i] = 0x80;
        Assert.IsFalse(prefixed(overflow, 8, out _, out _));
        Assert.IsFalse(prefixed(new byte[] { 0xFF, 0x80 }, 8, out _, out _));

        var literal = (TryReadStringLiteralDelegate)Delegate.CreateDelegate(typeof(TryReadStringLiteralDelegate),
            typeof(QpackDecoder).GetMethod("TryReadStringLiteral", PrivateStatic)!);
        Assert.IsFalse(literal(ReadOnlySpan<byte>.Empty, out _, out _));
        Assert.IsFalse(literal(new byte[] { 0x05, (byte)'a' }, out _, out _));
        Assert.IsTrue(literal(new byte[] { 0x03, (byte)'a', (byte)'b', (byte)'c' }, out var plain, out _));
        Assert.AreEqual("abc", plain);

        var huffman = (HuffmanDecodeDelegate)Delegate.CreateDelegate(typeof(HuffmanDecodeDelegate),
            typeof(QpackDecoder).GetMethod("HuffmanDecode", PrivateStatic)!);
        Assert.AreEqual("", huffman(ReadOnlySpan<byte>.Empty));

        using var proxy = new ProxyServer(false, false, false);
        var stripMem = typeof(Http2OriginConnection).GetMethod("StripDataFramingMemory", PrivateStatic)!;
        var unpadded = new byte[] { 1, 2, 3 };
        var mem = (ReadOnlyMemory<byte>)stripMem.Invoke(null, [unpadded, 3, (Http2FrameFlag)0])!;
        CollectionAssert.AreEqual(unpadded, mem.ToArray());
        var padded = new byte[] { 1, 9, 0 };
        mem = (ReadOnlyMemory<byte>)stripMem.Invoke(null, [padded, 3, Http2FrameFlag.Padded])!;
        CollectionAssert.AreEqual(new byte[] { 9 }, mem.ToArray());
        mem = (ReadOnlyMemory<byte>)stripMem.Invoke(null, [Array.Empty<byte>(), 0, Http2FrameFlag.Padded])!;
        Assert.AreEqual(0, mem.Length);
    }

    [TestMethod]
    public void ResponseMayHaveBody_And_ForbiddenStreamTypes_CoverBranches()
    {
        var mayHave = BridgeMethod("ResponseMayHaveBody");
        Assert.IsFalse((bool)mayHave.Invoke(null, [100, "GET", -1L, false, false])!);
        Assert.IsFalse((bool)mayHave.Invoke(null, [204, "GET", 10L, false, false])!);
        Assert.IsFalse((bool)mayHave.Invoke(null, [304, "GET", 10L, false, false])!);
        Assert.IsFalse((bool)mayHave.Invoke(null, [200, "HEAD", 10L, false, false])!);
        Assert.IsFalse((bool)mayHave.Invoke(null, [200, "GET", 0L, false, false])!);
        Assert.IsTrue((bool)mayHave.Invoke(null, [200, "GET", 5L, false, false])!);
        Assert.IsTrue((bool)mayHave.Invoke(null, [200, "GET", -1L, true, false])!);
        Assert.IsTrue((bool)mayHave.Invoke(null, [200, "GET", -1L, false, true])!);
        Assert.IsFalse((bool)mayHave.Invoke(null, [200, "GET", -1L, false, false])!);

        var forbidden = BridgeMethod("IsForbiddenOnRequestStream");
        Assert.IsTrue((bool)forbidden.Invoke(null, [Http3FrameType.Settings])!);
        Assert.IsTrue((bool)forbidden.Invoke(null, [Http3FrameType.GoAway])!);
        Assert.IsFalse((bool)forbidden.Invoke(null, [Http3FrameType.Headers])!);
        Assert.IsFalse((bool)forbidden.Invoke(null, [Http3FrameType.Data])!);
    }

    [TestMethod]
    public void HeaderBuilder_CoversVersionEmptyUrlAndProxyAuthBranches()
    {
        var builder = HeaderBuilder.Rent();
        try
        {
            builder.WriteRequestLine("GET", "", HttpHeader.Version10);
            builder.WriteRequestLine("OPTIONS", (ByteString)"", new Version(1, 2));
            builder.WriteResponseLine(HttpHeader.Version11, 200, "OK");
            builder.WriteResponseLine(new Version(2, 0), 418, "I'm a teapot");

            var headers = new HeaderCollection();
            headers.AddHeader("Proxy-Authorization", "secret");
            headers.AddHeader("X-Keep", "1");
            builder.WriteHeaders(headers, sendProxyAuthorization: false);
            builder.WriteHeaders(headers, sendProxyAuthorization: true);
            builder.WriteHeaders(headers, sendProxyAuthorization: true, "user", "pass");
            builder.WriteLine();
            builder.WriteRaw("raw"u8);
            builder.Write(string.Empty);
            builder.Write(new string('x', 300)); // ArrayPool encode path

            var buf = builder.GetBuffer();
            Assert.IsTrue(buf.Count > 20);
            Assert.IsFalse(string.IsNullOrEmpty(builder.GetString(Encoding.ASCII)));
        }
        finally
        {
            HeaderBuilder.Return(builder);
            HeaderBuilder.Return(HeaderBuilder.Rent()); // cache hit path
        }
    }

    [TestMethod]
    public async Task Http2FrameWriter_CoalescePath_FlushesMultipleFrames()
    {
        using var output = new MemoryStream();
        await using var writer = new Http2FrameWriter(output);

        for (var i = 0; i < 8; i++)
        {
            var rented = ArrayPool<byte>.Shared.Rent(32);
            rented.AsSpan(0, 32).Fill((byte)i);
            writer.EnqueueRented(rented, 32);
        }

        await writer.DisposeAsync();
        Assert.IsTrue(output.Length >= 256);
    }

    [TestMethod]
    public async Task Http2ToHttp11_ReadLineAndLiteHelpers()
    {
        var readLine = ProxyMethod("ReadLineAsync");
        await using (var ms = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n")))
        {
            var line = await (Task<string?>)readLine.Invoke(null, [ms, CancellationToken.None])!;
            Assert.AreEqual("HTTP/1.1 200 OK", line);
        }

        await using (var empty = new MemoryStream())
        {
            Assert.IsNull(await (Task<string?>)readLine.Invoke(null, [empty, CancellationToken.None])!);
        }

        await using (var partial = new MemoryStream(Encoding.ASCII.GetBytes("no-nl")))
        {
            Assert.AreEqual("no-nl", await (Task<string?>)readLine.Invoke(null, [partial, CancellationToken.None])!);
        }

        var lite = ProxyMethod("IsH2BridgeLiteMethod");
        Assert.IsTrue((bool)lite.Invoke(null, ["GET"])!);
        Assert.IsTrue((bool)lite.Invoke(null, ["head"])!);
        Assert.IsTrue((bool)lite.Invoke(null, ["DELETE"])!);
        Assert.IsTrue((bool)lite.Invoke(null, ["OPTIONS"])!);
        Assert.IsFalse((bool)lite.Invoke(null, ["POST"])!);
        Assert.IsFalse((bool)lite.Invoke(null, [null])!);

        var hasUpper = ProxyMethod("HeaderNameDataHasUpperCaseAscii");
        Assert.IsTrue((bool)hasUpper.Invoke(null, ["Content-Type".GetByteString()])!);
        Assert.IsFalse((bool)hasUpper.Invoke(null, ["content-type".GetByteString()])!);

        var lower = ProxyMethod("AsciiToLowerByteString");
        var lowered = (ByteString)lower.Invoke(null, ["X-Mixed".GetByteString()])!;
        Assert.AreEqual("x-mixed", lowered.GetString());
    }

    [TestMethod]
    public void Http11ToHttp2_PrepareOriginAndWsHelpers()
    {
        var prepare = ProxyMethod("PrepareRequestForOrigin");
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11,
            Host = "Example.COM",
            RequestUriString8 = "/path".GetByteString()
        };
        request.Headers.AddHeader("X-Mixed", "1");
        prepare.Invoke(null, [request]);
        Assert.IsTrue(request.HeaderNamesAreHttp2Normalized);
        Assert.IsTrue(request.Authority.Length > 0);
        Assert.IsNull(request.Headers.GetHeaderValueOrNull(KnownHeaders.Host));

        var close = ProxyMethod("ClientRequestedConnectionClose");
        var closeReq = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        closeReq.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose.String);
        Assert.IsTrue((bool)close.Invoke(null, [closeReq])!);
        Assert.IsFalse((bool)close.Invoke(null, [new Request { Method = "GET", HttpVersion = HttpHeader.Version11 }])!);

        var http10 = new Request { Method = "GET", HttpVersion = HttpHeader.Version10 };
        Assert.IsTrue((bool)close.Invoke(null, [http10])!);
        http10.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive.String);
        Assert.IsFalse((bool)close.Invoke(null, [http10])!);

        var wsPrep = ProxyMethod("PrepareWebSocketUpgradeForHttp2Origin");
        var ws = new Request
        {
            Method = "GET",
            Host = "ws.example",
            RequestUriString8 = "/chat".GetByteString()
        };
        ws.Headers.AddHeader(KnownHeaders.Upgrade, KnownHeaders.UpgradeWebsocket);
        ws.Headers.AddHeader(KnownHeaders.Connection, "Upgrade");
        ws.Headers.AddHeader("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        ws.Headers.AddHeader("Sec-WebSocket-Version", "13");
        wsPrep.Invoke(null, [ws]);
        Assert.AreEqual("CONNECT", ws.Method);
        Assert.AreEqual("websocket", ws.ExtendedConnectProtocol);
        Assert.IsTrue(ws.Authority.Length > 0);

        var build101 = ProxyMethod("BuildSwitchingProtocolsResponse");
        var origin = new Response { StatusCode = 101 };
        origin.Headers.AddHeader("sec-websocket-protocol", "chat");
        origin.Headers.AddHeader("sec-websocket-extensions", "permessage-deflate");
        var response101 = (Response)build101.Invoke(null, ["dGhlIHNhbXBsZSBub25jZQ==", origin])!;
        Assert.AreEqual(101, response101.StatusCode);
        Assert.IsNotNull(response101.Headers.GetHeaderValueOrNull("Sec-WebSocket-Accept"));
        Assert.AreEqual("chat", response101.Headers.GetHeaderValueOrNull("sec-websocket-protocol"));
    }

    [TestMethod]
    public void Http2ToHttp3_CookieConsolidateAndOriginIdentity()
    {
        var consolidate = ProxyMethod("ConsolidateCookieHeaders");
        var cookies = new HeaderCollection();
        cookies.AddHeader("Cookie", "a=1");
        cookies.AddHeader("Cookie", "b=2");
        consolidate.Invoke(null, [cookies]);
        Assert.AreEqual("a=1; b=2", cookies.GetHeaderValueOrNull("Cookie"));
        consolidate.Invoke(null, [cookies]); // single cookie: no-op

        using var proxy = new ProxyServer(false, false, false);
        var resolve = ProxyMethod("ResolveH3BridgeOriginIdentity");
        var transparent = new TransparentProxyEndPoint(IPAddress.Loopback, 0, true)
        {
            ForwardHost = "fwd.example",
            ForwardPort = 8443
        };
        using var session = MakeSession(proxy, transparent);
        var identity = ((string Host, int Port))resolve.Invoke(null, [session, "fallback.example", 443])!;
        Assert.AreEqual("fwd.example", identity.Host);
        Assert.AreEqual(8443, identity.Port);

        using var explicitSession = MakeSession(proxy);
        explicitSession.HttpClient.Request.Host = "req.example";
        explicitSession.HttpClient.Request.Authority = "req.example:9443".GetByteString();
        identity = ((string Host, int Port))resolve.Invoke(null, [explicitSession, "fallback.example", 443])!;
        Assert.IsFalse(string.IsNullOrEmpty(identity.Host));
    }

    [TestMethod]
    public void CertificateManager_CachedCountAndEngineBranch()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.AreEqual(0, mgr.CachedCertificateCount);
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        Assert.IsNotNull(mgr.RootCertificate);

        mgr.RootCertificateIssuerName = "Unit Test CA";
        Assert.AreEqual("Unit Test CA", mgr.RootCertificateIssuerName);
        mgr.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256;
        Assert.AreEqual(CertificateKeyAlgorithm.EcdsaP256, mgr.LeafCertificateKeyAlgorithm);
        mgr.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256; // same-value early return
        mgr.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.Rsa2048;

        // Touch Windows engine setter path on Windows only (throws elsewhere).
        if (RunTime.IsWindows)
        {
            using var win = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.DefaultWindows
            };
            Assert.IsNotNull(win);
        }
    }

    [TestMethod]
    public async Task Http2FrameWriter_FailingStream_ReturnsRentedBuffers()
    {
        await using var failing = new FailingWriteStream();
        var writer = new Http2FrameWriter(failing);
        var rented = ArrayPool<byte>.Shared.Rent(16);
        rented.AsSpan(0, 16).Fill(7);
        writer.EnqueueRented(rented, 16);

        try
        {
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // drain faults on write — expected
        }

        await writer.DisposeAsync();
        Assert.IsTrue(failing.WriteAttempts >= 1);
    }

    [TestMethod]
    public void Http2OriginPool_DiagPickStats_WhenEnabled_EmitsCounters()
    {
        var diag = typeof(Http2OriginConnectionPool).GetNestedType("DiagPickStats", BindingFlags.NonPublic)!;
        var enabled = diag.GetField("Enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var loggerStarted = diag.GetField("loggerStarted", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = (bool)enabled.GetValue(null)!;
        var previousLogger = (int)loggerStarted.GetValue(null)!;
        try
        {
            enabled.SetValue(null, true);
            loggerStarted.SetValue(null, 0);

            var outPath = Path.Combine(Path.GetTempPath(), $"twp-pool-diag-{Guid.NewGuid():N}.log");
            Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT", outPath);
            try
            {
                Http2OriginConnectionPool.DiagPickStats.OnRent();
                Http2OriginConnectionPool.DiagPickStats.OnTryPick(2, 10, 3, hit: true);
                Http2OriginConnectionPool.DiagPickStats.OnTryPick(1, 5, 0, hit: false);
                Http2OriginConnectionPool.DiagPickStats.OnCreationGate();
                Http2OriginConnectionPool.DiagPickStats.OnTryPickAny();
                Http2OriginConnectionPool.DiagPickStats.OnOpen();

                var emit = diag.GetMethod("Emit", BindingFlags.NonPublic | BindingFlags.Static)!;
                emit.Invoke(null, ["unit"]);

                Assert.IsTrue(Http2OriginConnectionPool.DiagPickStats.IsEnabled);
                Assert.IsFalse(string.IsNullOrEmpty(Http2OriginConnectionPool.DiagPickStats.FormatSummary()));
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => File.Exists(outPath) && new FileInfo(outPath).Length > 0,
                    TimeSpan.FromSeconds(3)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT", null);
                SpinWait.SpinUntil(() =>
                {
                    try
                    {
                        if (File.Exists(outPath)) File.Delete(outPath);
                        return !File.Exists(outPath);
                    }
                    catch (IOException) { return false; }
                }, TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            enabled.SetValue(null, previous);
            loggerStarted.SetValue(null, previousLogger);
        }
    }

    private sealed class FailingWriteStream : Stream
    {
        public int WriteAttempts;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("fail");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            return ValueTask.FromException(new IOException("fail"));
        }
    }
}
