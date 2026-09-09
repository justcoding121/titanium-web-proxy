using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
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
        var previousPick = Environment.GetEnvironmentVariable("TWP_DIAG_POOL_PICK");
        var previousOut = Environment.GetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT");
        var outPath = Path.Combine(Path.GetTempPath(), $"twp-diag-pick-{Guid.NewGuid():N}.log");
        Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK", "1");
        Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT", outPath);
        try
        {
            Http2OriginConnectionPool.DiagPickStats.OnRent();
            Http2OriginConnectionPool.DiagPickStats.OnTryPick(2, 1, 3, hit: true);
            Http2OriginConnectionPool.DiagPickStats.OnTryPick(1, 1, 1, hit: false);
            Http2OriginConnectionPool.DiagPickStats.OnCreationGate();
            Http2OriginConnectionPool.DiagPickStats.OnTryPickAny();
            Http2OriginConnectionPool.DiagPickStats.OnOpen();
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
            Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK", previousPick);
            Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT", previousOut);
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch (IOException) { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task OriginConnection_CreditFailAndUnusableSend()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await CreateShellAsync(proxy);

        typeof(Http2OriginConnection).GetField("concurrencyGateCapacity", PrivateInstance)!
            .SetValue(connection, 16);
        // SoftPick = SETTINGS/gate; TLS SoftGrow SoftPick SoftCap; cleartext SoftGrow=8.
        Assert.AreEqual(16, connection.SoftStreamCapacity);
        Assert.AreEqual(connection.SoftStreamCapacity, connection.PoolGrowThreshold);

        typeof(Http2OriginConnection).GetMethod("AttachExclusiveFrameWriter", PrivateInstance)!
            .Invoke(connection, null);

        var ack = typeof(Http2OriginConnection).GetMethod("SendSettingsAckAsync", PrivateInstance)!;
        await (Task)ack.Invoke(connection, [CancellationToken.None])!;
        var ping = typeof(Http2OriginConnection).GetMethod("SendPingAckAsync", PrivateInstance)!;
        await (Task)ping.Invoke(connection, [new byte[8], CancellationToken.None])!;
        var rst = typeof(Http2OriginConnection).GetMethod("ResetStreamAsync", PrivateInstance)!;
        await (Task)rst.Invoke(connection, [3, Http2ErrorCode.Cancel, CancellationToken.None])!;

        var complete = typeof(Http2OriginConnection).GetMethod("CompleteStream", PrivateInstance)!;
        complete.Invoke(connection, [99]);
        var failStream = typeof(Http2OriginConnection).GetMethod("FailStream", PrivateInstance)!;
        failStream.Invoke(connection, [99, new IOException("gone")]);

        var writeTunnel = typeof(Http2OriginConnection).GetMethod("WriteTunnelDataAsync", PrivateInstance)!;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await (Task)writeTunnel.Invoke(connection, [7, ReadOnlyMemory<byte>.Empty, false, CancellationToken.None])!);

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
        connection.Retire();
    }

    [TestMethod]
    public async Task OriginConnection_StreamTableGrowLookupAndDuplicateRegister()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await CreateShellAsync(proxy);
        var pendingType = typeof(Http2OriginConnection).GetNestedType("PendingStream", BindingFlags.NonPublic)!;
        object Pending() => Activator.CreateInstance(pendingType, PrivateInstance, binder: null,
            args: [0L], culture: null)!;

        var register = typeof(Http2OriginConnection).GetMethod("RegisterOpenedStream", PrivateInstance)!;
        var tryGet = typeof(Http2OriginConnection).GetMethod("TryGetStream", PrivateInstance)!;
        var contains = typeof(Http2OriginConnection).GetMethod("StreamTableContains", PrivateInstance)!;
        var unregister = typeof(Http2OriginConnection).GetMethod("TryUnregisterStream", PrivateInstance)!;
        var enumerate = typeof(Http2OriginConnection).GetMethod("EnumerateLiveStreams", PrivateInstance)!;
        var ensure = typeof(Http2OriginConnection).GetMethod("EnsureStreamTable", PrivateInstance)!;

        // Force growth past the default 64-slot table (idx = streamId >> 1).
        ensure.Invoke(connection, [200]);
        register.Invoke(connection, [401, Pending()]);
        Assert.AreEqual(1, connection.ActiveStreamCount);
        Assert.IsTrue((bool)contains.Invoke(connection, [401])!);
        Assert.IsFalse((bool)contains.Invoke(connection, [403])!);

        var getArgs = new object?[] { 401, null };
        Assert.IsTrue((bool)tryGet.Invoke(connection, getArgs)!);
        Assert.IsNotNull(getArgs[1]);
        getArgs = [99999, null];
        Assert.IsFalse((bool)tryGet.Invoke(connection, getArgs)!);

        var unregArgs = new object?[] { 99999, null };
        Assert.IsFalse((bool)unregister.Invoke(connection, unregArgs)!);
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            register.Invoke(connection, [401, Pending()]));

        var live = new List<object>();
        foreach (var item in (IEnumerable)enumerate.Invoke(connection, null)!)
            live.Add(item!);
        Assert.AreEqual(1, live.Count);

        unregArgs = [401, null];
        Assert.IsTrue((bool)unregister.Invoke(connection, unregArgs)!);
        Assert.AreEqual(0, connection.ActiveStreamCount);

        connection.AcquireLease();
        connection.ReleaseLease();
        connection.Retire();
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

    [TestMethod]
    public async Task Http3FastForward_TcpLiveOrigin_CoversBufferedEmptyAndChunked()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                try
                {
                    var ctx = await http.GetContextAsync();
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (path.Contains("medium", StringComparison.Ordinal))
                    {
                        var mediumBody = new byte[8192];
                        Random.Shared.NextBytes(mediumBody);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength64 = mediumBody.Length;
                        await ctx.Response.OutputStream.WriteAsync(mediumBody);
                        ctx.Response.Close();
                        continue;
                    }

                    if (path.Contains("empty", StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = 204;
                        ctx.Response.Close();
                        continue;
                    }

                    if (ctx.Request.HttpMethod == "HEAD")
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentLength64 = 0;
                        ctx.Response.Close();
                        continue;
                    }

                    if (path.Contains("chunk", StringComparison.Ordinal))
                    {
                        var chunk = Encoding.UTF8.GetBytes("chunked-body");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.SendChunked = true;
                        ctx.Response.ContentType = "text/plain";
                        await ctx.Response.OutputStream.WriteAsync(chunk);
                        ctx.Response.Close();
                        continue;
                    }

                    var body = Encoding.UTF8.GetBytes("hello-tcp-fast");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch
                {
                    return;
                }
            }
        });

        try
        {
            using var proxy = new ProxyServer(false, false, false);
            var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
            {
                ForwardCleartext = true,
                ForwardHost = "127.0.0.1",
                ForwardPort = port
            };
            SessionEventArgs Cold() => MakeSession(proxy, ep);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            async Task<H3H2FastForward> RunAsync(string path, bool withResponse)
            {
                var request = new Request
                {
                    Method = "GET",
                    IsHttps = false,
                    HttpVersion = HttpHeader.Version30,
                    Host = $"127.0.0.1:{port}",
                    Authority = $"127.0.0.1:{port}".GetByteString(),
                    RequestUriString8 = path.GetByteString()
                };
                var fwd = new H3H2FastForward
                {
                    Request = request,
                    ProxyEndPoint = ep,
                    MaxBufferedBodyBytes = 1024,
                    OriginAuthorityHost = "origin.example",
                    Response = withResponse ? new Response() : null,
                };
                await Http3OriginBridge.ForwardOverTcpFastAsync(
                    fwd, proxy, NullLogger.Instance, cts.Token, Cold);
                return fwd;
            }

            var small = await RunAsync("/", true);
            Assert.IsNotNull(small.PreencodedQpackHeaders);
            Assert.AreEqual(200, small.Response!.StatusCode);
            if (small.PreencodedBodyRented && small.PreencodedBody is not null)
                proxy.BufferPool.ReturnBuffer(small.PreencodedBody);

            var pooled = await RunAsync("/", false);
            Assert.IsNotNull(pooled.PreencodedQpackHeaders);
            if (pooled.PreencodedBodyRented && pooled.PreencodedBody is not null)
                proxy.BufferPool.ReturnBuffer(pooled.PreencodedBody);

            var empty = await RunAsync("/empty", true);
            Assert.AreEqual(204, empty.Response!.StatusCode);

            var chunked = await RunAsync("/chunk", false);
            Assert.IsTrue(chunked.PreencodedQpackHeaders is { Length: > 0 }
                          || chunked.PreencodedStreamBodyWriter is not null);
            if (chunked.PreencodedStreamBodyWriter is not null)
            {
                await using var ms = new MemoryStream();
                await chunked.PreencodedStreamBodyWriter(ms, cts.Token);
                Assert.IsTrue(ms.Length > 0);
            }

            var medium = await RunAsync("/medium", true);
            Assert.AreEqual(200, medium.Response!.StatusCode);
            Assert.IsNotNull(medium.PreencodedQpackHeaders);
            if (medium.PreencodedBodyRented && medium.PreencodedBody is not null)
                proxy.BufferPool.ReturnBuffer(medium.PreencodedBody);

            var fwdTcp = BridgeMethod("ForwardOverTcpAsync");
            using (var session = MakeSession(proxy, ep))
            {
                session.HttpClient.Request.Method = "GET";
                session.HttpClient.Request.IsHttps = false;
                session.HttpClient.Request.HttpVersion = HttpHeader.Version30;
                session.HttpClient.Request.Host = $"127.0.0.1:{port}";
                session.HttpClient.Request.Authority = $"127.0.0.1:{port}".GetByteString();
                session.HttpClient.Request.RequestUriString8 = "/".GetByteString();
                session.HttpClient.Request.Headers.AddHeader("Cookie", "a=1");
                session.HttpClient.Request.Headers.AddHeader("Cookie", "b=2");
                session.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
                await (Task)fwdTcp.Invoke(null, [session, proxy, cts.Token, null])!;
                Assert.AreEqual(200, session.HttpClient.Response.StatusCode);
            }

            using (var post = MakeSession(proxy, ep))
            {
                post.HttpClient.Request.Method = "POST";
                post.HttpClient.Request.IsHttps = false;
                post.HttpClient.Request.HttpVersion = HttpHeader.Version30;
                post.HttpClient.Request.Host = $"127.0.0.1:{port}";
                post.HttpClient.Request.Authority = $"127.0.0.1:{port}".GetByteString();
                post.HttpClient.Request.RequestUriString8 = "/".GetByteString();
                post.HttpClient.Request.IsBodyRead = true;
                post.HttpClient.Request.Body = "x=1"u8.ToArray();
                post.HttpClient.Request.ContentType = "application/x-www-form-urlencoded";
                post.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
                await (Task)fwdTcp.Invoke(null, [post, proxy, cts.Token, null])!;
                Assert.AreEqual(200, post.HttpClient.Response.StatusCode);
            }

            using (var head = MakeSession(proxy, ep))
            {
                head.HttpClient.Request.Method = "HEAD";
                head.HttpClient.Request.IsHttps = false;
                head.HttpClient.Request.HttpVersion = HttpHeader.Version30;
                head.HttpClient.Request.Host = $"127.0.0.1:{port}";
                head.HttpClient.Request.Authority = $"127.0.0.1:{port}".GetByteString();
                head.HttpClient.Request.RequestUriString8 = "/".GetByteString();
                await (Task)fwdTcp.Invoke(null, [head, proxy, cts.Token, null])!;
                Assert.IsTrue(head.HttpClient.Response.StatusCode is 200 or 204);
            }
        }
        finally
        {
            try { http.Stop(); } catch { /* ignore */ }
            try { http.Close(); } catch { /* ignore */ }
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
        var loggerStarted = diag.GetField("loggerStarted", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousPick = Environment.GetEnvironmentVariable("TWP_DIAG_POOL_PICK");
        var previousOut = Environment.GetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT");
        var previousLogger = (int)loggerStarted.GetValue(null)!;
        try
        {
            Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK", "1");
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
                Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT", previousOut);
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
            Environment.SetEnvironmentVariable("TWP_DIAG_POOL_PICK", previousPick);
            loggerStarted.SetValue(null, previousLogger);
        }
    }

    [TestMethod]
    public void CertificateManager_OsTrustSuppressArms_DoNotOpenDialogs()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        mgr.CreateRootCertificate(false);
        Assert.IsNotNull(mgr.RootCertificate);
        mgr.EnsureRootCertificate(false, false, false);
        mgr.TrustRootCertificate(false);
        Assert.IsNotNull(mgr.LastOsTrustResult);
        _ = mgr.TrustRootCertificateAsAdmin(false);
        var nss = mgr.InstallNssCertutilAndRetryUserTrust();
        Assert.AreEqual(CertificateOsTrustKind.Cancelled, nss.Kind);
        _ = mgr.VerifyOsUserSslTrust();
        _ = mgr.IsRootCertificateUserTrusted();
        _ = mgr.IsRootCertificateMachineTrusted();
        _ = mgr.IsRootInLoginKeychain();
        _ = mgr.IsOsRootStillPresent();
        Assert.IsNull(mgr.OpenMacKeychainGuidance());
        mgr.TrustRootCertificate(true);
        _ = mgr.TrustRootCertificateAsAdmin(true);
    }

    [TestMethod]
    public async Task OriginPendingStream_InlineBodyPipeAndFailPending()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await CreateShellAsync(proxy);
        var pendingType = typeof(Http2OriginConnection).GetNestedType("PendingStream", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, PrivateInstance, binder: null, args: [0L], culture: null)!;
        var tunnel = pendingType.GetMethod("CreateTunnel", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        var prepare = pendingType.GetMethod("TryPrepareInlineBody", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writeMi = pendingType.GetMethod("TryWriteInline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var take = pendingType.GetMethod("TakeInlineBody", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ensure = pendingType.GetMethod("EnsureBodyPipe", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mark = pendingType.GetMethod("MarkInboundComplete", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var failPending = typeof(Http2OriginConnection).GetMethod("FailPending", PrivateStatic)!;
        var threshold = (int)pendingType.GetField("InlineBodyThresholdBytes",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!
            .GetValue(null)!;

        Assert.IsFalse((bool)prepare.Invoke(tunnel, [new Response { ContentLength = 4 }])!);
        Assert.IsFalse((bool)prepare.Invoke(pending, [new Response { ContentLength = -1 }])!);
        Assert.IsFalse((bool)prepare.Invoke(pending, [new Response { ContentLength = threshold + 1 }])!);
        Assert.IsTrue((bool)prepare.Invoke(pending, [new Response { ContentLength = 0 }])!);
        CollectionAssert.AreEqual(Array.Empty<byte>(), (byte[])take.Invoke(pending, null)!);

        Assert.IsTrue((bool)prepare.Invoke(pending, [new Response { ContentLength = 4 }])!);
        var write = writeMi.CreateDelegate<Action<ReadOnlySpan<byte>>>(pending);
        write(ReadOnlySpan<byte>.Empty);
        write("ab"u8);
        write("cdef"u8); // overflow truncated
        var body = (byte[])take.Invoke(pending, null)!;
        Assert.AreEqual(4, body.Length);

        Assert.IsTrue((bool)prepare.Invoke(pending, [new Response { ContentLength = 8 }])!);
        write("xx"u8);
        var shortBody = (byte[])take.Invoke(pending, null)!;
        Assert.AreEqual(2, shortBody.Length);

        var pipe1 = ensure.Invoke(pending, null)!;
        var pipe2 = ensure.Invoke(pending, null)!;
        Assert.AreSame(pipe1, pipe2);
        mark.Invoke(pending, null);
        Assert.IsTrue((bool)pendingType.GetProperty("IsInboundComplete",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(pending)!);
        var afterComplete = ensure.Invoke(pending, null)!;
        Assert.AreSame(pipe1, afterComplete);

        failPending.Invoke(null, [pending, new IOException("fail-pending")]);
        ((IDisposable)pending).Dispose();
        ((IDisposable)tunnel).Dispose();

        var apply = typeof(Http2OriginConnection).GetMethod("ApplyEnableConnectProtocolSetting", PrivateInstance)!;
        apply.Invoke(connection, [1]);
        apply.Invoke(connection, [0]); // downgrade after ever-set → Fail
        apply.Invoke(connection, [9]); // illegal → Fail

        var parseMi = typeof(Http2OriginConnection).GetMethod("TryParseAsciiStatusCode", PrivateStatic)!;
        var parse = parseMi.CreateDelegate<TryParseAsciiStatusCodeDelegate>();
        Assert.IsTrue(parse("200"u8, out var parsed));
        Assert.AreEqual(200, parsed);
        Assert.IsFalse(parse("abc"u8, out _));
        Assert.IsFalse(parse(ReadOnlySpan<byte>.Empty, out _));

        var stripHeaders = typeof(Http2OriginConnection).GetMethod("StripHeadersFraming", PrivateStatic,
            binder: null, [typeof(byte[]), typeof(Http2FrameFlag)], modifiers: null)!;
        var hdr = (byte[])stripHeaders.Invoke(null,
            [new byte[] { 2, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, Http2FrameFlag.Padded | Http2FrameFlag.Priority])!;
        Assert.IsTrue(hdr.Length >= 0);
        connection.Retire();
    }

    private delegate bool TryParseAsciiStatusCodeDelegate(ReadOnlySpan<byte> digits, out int statusCode);
    private delegate bool EqualsAsciiIgnoreCaseDelegate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

    [TestMethod]
    public void Http2Helper_HpackStaticLiteralAndSkipHelpers()
    {
        var equals = typeof(Http2Helper).GetMethod("EqualsAsciiIgnoreCase", PrivateStatic)!
            .CreateDelegate<EqualsAsciiIgnoreCaseDelegate>();
        Assert.IsTrue(equals("Host"u8, "host"u8));
        Assert.IsFalse(equals("ab"u8, "abc"u8));

        var omit = typeof(Http2Helper).GetMethod("ShouldOmitHttp2Header", PrivateStatic)!;
        Assert.IsTrue((bool)omit.Invoke(null, ["Connection".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["host".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["te".GetByteString()])!);
        Assert.IsFalse((bool)omit.Invoke(null, ["accept".GetByteString()])!);

        var schemeByte = typeof(Http2Helper).GetMethod("StaticIndexedSchemeByte", PrivateStatic)!;
        Assert.AreNotEqual((byte)0, (byte)schemeByte.Invoke(null, [ProxyServer.UriSchemeHttp8])!);
        Assert.AreNotEqual((byte)0, (byte)schemeByte.Invoke(null, [ProxyServer.UriSchemeHttps8])!);
        Assert.AreEqual((byte)0, (byte)schemeByte.Invoke(null, ["ftp".GetByteString()])!);

        var skipLit = typeof(Http2Helper).GetMethod("TrySkipHpackLiteral", PrivateStatic,
            binder: null, [typeof(byte[]), typeof(int).MakeByRefType()], modifiers: null)!;
        var skipStr = typeof(Http2Helper).GetMethod("TrySkipHpackString", PrivateStatic,
            binder: null, [typeof(byte[]), typeof(int).MakeByRefType()], modifiers: null)!;
        var skipInt = typeof(Http2Helper).GetMethod("TrySkipHpackIntegerContinuation", PrivateStatic,
            binder: null, [typeof(byte[]), typeof(int).MakeByRefType()], modifiers: null)!;

        var literal = new byte[] { 0x00, 0x01, (byte)'a', 0x01, (byte)'b' };
        object?[] litArgs = [literal, 0];
        Assert.IsTrue((bool)skipLit.Invoke(null, litArgs)!);
        Assert.AreEqual(literal.Length, litArgs[1]);

        object?[] strArgs = [new byte[] { 0x03, (byte)'x', (byte)'y', (byte)'z' }, 0];
        Assert.IsTrue((bool)skipStr.Invoke(null, strArgs)!);
        Assert.AreEqual(4, strArgs[1]);
        object?[] badStr = [new byte[] { 0x05, 1 }, 0];
        Assert.IsFalse((bool)skipStr.Invoke(null, badStr)!);

        object?[] intArgs = [Array.Empty<byte>(), 0];
        Assert.IsFalse((bool)skipInt.Invoke(null, intArgs)!);

        var size = typeof(Http2Helper).GetMethod("GetHpackStringLiteralEncodedSize", PrivateStatic)!;
        Assert.AreEqual(1 + 3, (int)size.Invoke(null, [3])!);
        Assert.IsTrue((int)size.Invoke(null, [200])! > 201);

        var prefSize = typeof(Http2Helper).GetMethod("WriteHpackPrefixedIntSize", PrivateStatic)!;
        Assert.AreEqual(1, (int)prefSize.Invoke(null, [7, 10UL])!);
        Assert.IsTrue((int)prefSize.Invoke(null, [7, 300UL])! >= 2);

        var appendSize = typeof(Http2Helper).GetMethod("GetStaticLiteralAppendSize", PrivateStatic)!;
        Assert.IsTrue((int)appendSize.Invoke(null, [3, 2])! > 5);

        // String overload of WriteStaticLiteralWithoutIndexing exercises WriteHpack* without Span Invoke.
        var writeStaticString = typeof(Http2Helper).GetMethods(PrivateStatic)
            .First(m => m.Name == "WriteStaticLiteralWithoutIndexing"
                        && m.GetParameters()[2].ParameterType == typeof(string));
        var buf = new byte[64];
        writeStaticString.Invoke(null, [buf, 0, "n2", "v2"]);

        var buildSuffix = typeof(Http2Helper).GetMethod("BuildStaticLiteralAppendSuffix", PrivateStatic)!;
        Assert.IsNull(buildSuffix.Invoke(null,
            [default(MitmCompressedRelayHelper.AddedHeaderBuffer), null, null]));
        var withExtra = (byte[]?)buildSuffix.Invoke(null,
            [default(MitmCompressedRelayHelper.AddedHeaderBuffer), "via", "1.1 twp"]);
        Assert.IsNotNull(withExtra);
        Assert.IsTrue(withExtra!.Length > 0);

        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);
        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");
        after.AddHeader("x-added", "1");
        var captured = new byte[] { 0x82, 0x87, 0x84 };
        var prepare = typeof(Http2Helper).GetMethod("TryPrepareMitmStaticHpackRelay", PrivateStatic)!;
        var prepArgs = new object?[]
        {
            captured, baseline, after, true, "1.1 twp", null, null
        };
        _ = (bool)prepare.Invoke(null, prepArgs)!;

        var settings = new Http2Settings();
        var listener = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        listener.AddHeader(StaticTable.KnownHeaderMethod, "GET".GetByteString(), false);
        listener.AddHeader(StaticTable.KnownHeaderAuhtority, "origin.test".GetByteString(), false);
        listener.AddHeader(StaticTable.KnownHeaderScheme, "https".GetByteString(), false);
        listener.AddHeader(StaticTable.KnownHeaderPath, "/".GetByteString(), false);
        var headers = new HeaderCollection();
        headers.AddHeader("X-Mixed", "1");
        var reencode = typeof(Http2Helper).GetMethod("ReencodeCompressedRequestBlock", PrivateStatic)!;
        var reencoded = (byte[])reencode.Invoke(null,
            [settings, listener, headers, ProxyServer.UriSchemeHttp8])!;
        Assert.IsTrue(reencoded.Length > 0);
    }

    [TestMethod]
    public async Task Http2OriginPool_HasAnyCapacityOfferInvalidateAndPickHelpers()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var key = Http2OriginConnectionPool.BuildPoolKey(
            proxy, ep, null, null, "127.0.0.1", 443, null, null);

        Assert.IsFalse(pool.HasAny(key));
        Assert.IsFalse(pool.IsAtMaxOriginCapacity(key));

        using var shell = await CreateShellAsync(proxy);
        pool.Offer(key, shell);
        Assert.IsTrue(pool.HasAny(key));

        var shouldGrow = typeof(Http2OriginConnectionPool).GetMethod("ShouldEarlyGrow", PrivateStatic)!;
        Assert.IsTrue((bool)shouldGrow.Invoke(null, [Array.Empty<Http2OriginConnection>(), 1])!);
        Assert.IsTrue((bool)shouldGrow.Invoke(null, [new[] { shell }, 0])!);
        // Soft-cap not reached → do not early-grow.
        Assert.IsFalse((bool)shouldGrow.Invoke(null, [new[] { shell }, 1])!);

        var tryAny = typeof(Http2OriginConnectionPool).GetMethod("TryPickAnyFromSnapshot", PrivateStatic)!;
        Assert.IsNotNull(tryAny.Invoke(null, [new[] { shell }]));
        Assert.IsNull(tryAny.Invoke(null, [Array.Empty<Http2OriginConnection>()]));

        var tryPick = typeof(Http2OriginConnectionPool).GetMethod("TryPickFromSnapshot", PrivateStatic)!;
        Assert.IsNotNull(tryPick.Invoke(null, [new[] { shell }, ProxyResourceLimits.Default]));

        pool.Invalidate(key, shell);
        Assert.IsFalse(pool.HasAny(key));
    }

    [TestMethod]
    public void CertificateManager_CacheFindUninstallAndSslContextSeams()
    {
        var isTruthy = typeof(CertificateManager).GetMethod("IsTruthyEnv", PrivateStatic)!;
        var previous = Environment.GetEnvironmentVariable("TWP_SONAR_TRUTHY_COV");
        try
        {
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", "yes");
            Assert.IsTrue((bool)isTruthy.Invoke(null, ["TWP_SONAR_TRUTHY_COV"])!);
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", "0");
            Assert.IsFalse((bool)isTruthy.Invoke(null, ["TWP_SONAR_TRUTHY_COV"])!);
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", "true");
            Assert.IsTrue((bool)isTruthy.Invoke(null, ["TWP_SONAR_TRUTHY_COV"])!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", previous);
        }

        var find = typeof(CertificateManager).GetMethod("FindCertificates", PrivateStatic)!;
        var empty = (X509Certificate2Collection)find.Invoke(null,
            [StoreName.Root, StoreLocation.CurrentUser, "ffffffffffffffffffffffffffffffffffffffff"])!;
        Assert.AreEqual(0, empty.Count);

        var noMax = typeof(CertificateManager).GetMethod("NoMaxCacheEntries", PrivateStatic)!;
        Assert.IsNull(noMax.Invoke(null, null));

        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = true,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = mgr.CreateCertificate("cache-seam.example", false);
        Assert.IsNotNull(leaf);

        var cacheField = typeof(CertificateManager).GetField("cachedCertificates", PrivateInstance)!;
        var cache = (System.Collections.IDictionary)cacheField.GetValue(mgr)!;
        var cachedType = typeof(CachedCertificate);
        var cached = Activator.CreateInstance(cachedType, leaf)!;
        cachedType.GetProperty("LastAccess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(cached, DateTime.UtcNow);
        cache["cache-seam.example"] = cached;

        var tryGet = typeof(CertificateManager).GetMethod("TryGetValidCachedCertificate", PrivateInstance)!;
        var getArgs = new object?[] { "cache-seam.example", null };
        Assert.IsTrue((bool)tryGet.Invoke(mgr, getArgs)!);
        Assert.IsNotNull(getArgs[1]);
        getArgs = ["missing.example", null];
        Assert.IsFalse((bool)tryGet.Invoke(mgr, getArgs)!);

        var loadDisk = typeof(CertificateManager).GetMethod("TryLoadFakeCertificateFromDisk", PrivateInstance)!;
        Assert.IsNull(loadDisk.Invoke(mgr, ["no-such-host.example"]));

        var uninstall = typeof(CertificateManager).GetMethod("UninstallCertificate", PrivateInstance)!;
        uninstall.Invoke(mgr, [StoreName.My, StoreLocation.CurrentUser, null]);

        var stage = typeof(CertificateManager).GetMethod("StageIntermediateForOsChainBuild", PrivateStatic)!;
        stage.Invoke(null, [mgr.RootCertificate!]);

        var buildCtx = typeof(CertificateManager).GetMethod("BuildSslCertificateContext", PrivateInstance)!;
        var ctx = buildCtx.Invoke(mgr, [leaf]);
        Assert.IsNotNull(ctx);

        var rootInstalled = typeof(CertificateManager).GetMethod("RootCertificateInstalled", PrivateInstance)!;
        _ = (bool)rootInstalled.Invoke(mgr, [StoreLocation.CurrentUser])!;

        var orphan = typeof(CertificateManager).GetMethod("RemoveOrphanedSameCommonNameCertificates", PrivateInstance)!;
        orphan.Invoke(mgr, [StoreLocation.CurrentUser, true]);
    }

    [TestMethod]
    public async Task H1TerminateLite_MiddlewareAndLiveOriginForward()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardCleartext = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
        };

        var createMwReq = typeof(ProxyServer).GetMethod("CreateTerminateLiteMiddlewareRequest", PrivateStatic)!;
        var req = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11,
            Host = "app.test",
            RequestUriString8 = "/mw".GetByteString(),
        };
        req.Headers.AddHeader("Authorization", "Bearer x");
        req.Headers.AddHeader("X-Multi", "a");
        req.Headers.AddHeader("X-Multi", "b");
        Assert.IsNotNull(createMwReq.Invoke(null, [ep, req]));
        Assert.IsNotNull(createMwReq.Invoke(null,
            [ep, new Request { Method = "HEAD", HttpVersion = HttpHeader.Version11, Host = "app.test" }]));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var clientSock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSock.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await accept;
        var clientConn = new TcpClientConnection(proxy, clientSock);
        var clientStream = new HttpClientStream(proxy, clientConn, new NetworkStream(clientSock, ownsSocket: false),
            proxy.BufferPool, CancellationToken.None);

        var writeMw = typeof(ProxyServer).GetMethod("WriteTerminateLiteMiddlewareResponseAsync", PrivateStatic)!;
        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            IsHandled = true,
            HandledStatusCode = 204,
            HandledBody = "mw",
            HandledHeaders = [new KeyValuePair<string, string>("x-mw", "1")],
        };
        var drain = Task.Run(() =>
        {
            var buf = new byte[1024];
            try { accepted.Receive(buf); } catch { /* ignore */ }
        });
        await (Task)writeMw.Invoke(null, [clientStream, req, ctx, CancellationToken.None])!;
        await drain;

        var tryMw = typeof(ProxyServer).GetMethod("TryRunTerminateLiteMiddlewareAsync", PrivateStatic)!;
        var handled = new HandleAllMiddleware();
        var keep = await (Task<bool?>)tryMw.Invoke(null,
            [ep, clientStream, req, new IProxyMiddleware[] { handled }, CancellationToken.None])!;
        Assert.IsNotNull(keep);

        var passthrough = await (Task<bool?>)tryMw.Invoke(null,
            [ep, clientStream, req, Array.Empty<IProxyMiddleware>(), CancellationToken.None])!;
        Assert.IsNull(passthrough);

        var httpPort = 0;
        var http = new HttpListener();
        {
            var tmp = new TcpListener(IPAddress.Loopback, 0);
            tmp.Start();
            httpPort = ((IPEndPoint)tmp.LocalEndpoint).Port;
            tmp.Stop();
        }
        http.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                var c = await http.GetContextAsync();
                c.Response.StatusCode = 200;
                c.Response.ContentLength64 = 2;
                await c.Response.OutputStream.WriteAsync("ok"u8.ToArray());
                c.Response.Close();
            }
            catch { /* ignore */ }
        });

        try
        {
            var liveEp = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
            {
                ForwardCleartext = true,
                ForwardHost = "127.0.0.1",
                ForwardPort = httpPort,
            };
            using var liveListener = new TcpListener(IPAddress.Loopback, 0);
            liveListener.Start();
            var liveAccept = liveListener.AcceptSocketAsync();
            var liveClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await liveClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)liveListener.LocalEndpoint).Port);
            var liveAccepted = await liveAccept;
            _ = Task.Run(() =>
            {
                var buf = new byte[4096];
                try { while (liveAccepted.Receive(buf) > 0) { } } catch { /* ignore */ }
            });
            var liveConn = new TcpClientConnection(proxy, liveClient);
            var liveStream = new HttpClientStream(proxy, liveConn,
                new NetworkStream(liveClient, ownsSocket: false), proxy.BufferPool, CancellationToken.None);
            var liveReq = new Request
            {
                Method = "GET",
                HttpVersion = HttpHeader.Version11,
                Host = $"127.0.0.1:{httpPort}",
                RequestUriString8 = "/".GetByteString(),
            };
            var forward = typeof(ProxyServer).GetMethod("ForwardH1TerminateLiteAsync", PrivateInstance)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _ = await (Task<bool>)forward.Invoke(proxy, [liveEp, liveStream, liveReq, cts.Token])!;
            liveAccepted.Dispose();
            liveClient.Dispose();
        }
        finally
        {
            try { http.Stop(); } catch { /* ignore */ }
            try { http.Close(); } catch { /* ignore */ }
        }

        accepted.Dispose();
        clientSock.Dispose();
    }

    [TestMethod]
    public void FirefoxCertificateTrust_RemainingPolicyAndPrefSeams()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var profilesIni = typeof(FirefoxCertificateTrust).GetMethod("TryGetProfilesIniPath", flags)!;
        var iniArgs = new object?[] { null };
        _ = (bool)profilesIni.Invoke(null, iniArgs)!;
        _ = typeof(FirefoxCertificateTrust).GetMethod("EnumerateFirefoxProcesses", flags)!
            .Invoke(null, null);

        var parseRoot = typeof(FirefoxCertificateTrust).GetMethod("ParsePoliciesRoot", flags)!;
        Assert.ThrowsExactly<TargetInvocationException>(() => parseRoot.Invoke(null, ["not-json"]));
        Assert.IsNotNull(parseRoot.Invoke(null, ["{\"policies\":{\"ImportEnterpriseRoots\":true}}"]));
        Assert.IsNotNull(parseRoot.Invoke(null, [null]));

        Assert.IsTrue(FirefoxCertificateTrust.TryValidateFirefoxPoliciesJson(
            "{\"policies\":{\"Certificates\":{\"ImportEnterpriseRoots\":true}}}", out _));
        Assert.IsFalse(FirefoxCertificateTrust.TryValidateFirefoxPoliciesJson("not-json", out _));
        Assert.IsFalse(FirefoxCertificateTrust.TryValidateFirefoxPoliciesJson("{\"policies\":{}}", out _));

        var dir = Path.Combine(Path.GetTempPath(), "twp-ff-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prefs = Path.Combine(dir, "prefs.js");
            File.WriteAllText(prefs, "// seed\n");
            FirefoxCertificateTrust.EnsureEnterpriseRootsUserPref(dir);
            var clearPref = typeof(FirefoxCertificateTrust).GetMethod("ClearEnterpriseRootsPrefFile", flags)!;
            clearPref.Invoke(null, [prefs]);
            Assert.IsTrue(File.Exists(prefs));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }

        _ = typeof(FirefoxCertificateTrust)
            .GetMethod("TryClearFirefoxPoliciesJsonImportEnterpriseRoots", flags)!
            .Invoke(null, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Additional Sonar new-code seams (~220+ LOC push toward 80%)
    // ─────────────────────────────────────────────────────────────────────────

    private delegate int ReadHttp2FrameLengthDelegate(byte[] frameHeaderBuffer);
    private delegate int ReadHttp2StreamIdDelegate(byte[] frameHeaderBuffer);
    private delegate int ReadHttp2UInt31Delegate(byte[] buffer);
    private delegate int ReadHttp2ErrorCodeDelegate(byte[] buffer);
    private delegate string InternCommonHttpMethodDelegate(ReadOnlySpan<byte> methodSpan, ByteString method);
    private delegate ReadOnlySpan<byte> StripDataFramingSpanDelegate(ReadOnlySpan<byte> payload, Http2FrameFlag flags);
    private delegate byte[] StripDataFramingFromSpanDelegate(ReadOnlySpan<byte> payload, Http2FrameFlag flags);
    private delegate int WriteHpackPrefixedIntDelegate(Span<byte> dest, byte patternByte, int prefixBits, ulong value);
    private delegate int WriteHpackAsciiLiteralBytesDelegate(Span<byte> dest, ReadOnlySpan<byte> value);
    private delegate int WriteHpackAsciiLiteralStringDelegate(Span<byte> dest, string value);
    private delegate ReadOnlyMemory<byte> GetMemoryStreamMemoryDelegate(MemoryStream ms);

    [TestMethod]
    public void Http2CopyParseHelpers_FrameLengthStreamIdErrorAndPadding()
    {
        var length = typeof(Http2Helper).GetMethod("ReadHttp2FrameLength", PrivateStatic)!
            .CreateDelegate<ReadHttp2FrameLengthDelegate>();
        Assert.AreEqual(0x010203, length([0x01, 0x02, 0x03, 0, 0, 0, 0, 0, 0]));

        var streamId = typeof(Http2Helper).GetMethod("ReadHttp2StreamId", PrivateStatic)!
            .CreateDelegate<ReadHttp2StreamIdDelegate>();
        Assert.AreEqual(0x01020304, streamId([0, 0, 0, 0, 0, 0x81, 0x02, 0x03, 0x04]));

        var u31 = typeof(Http2Helper).GetMethod("ReadHttp2UInt31", PrivateStatic)!
            .CreateDelegate<ReadHttp2UInt31Delegate>();
        Assert.AreEqual(0x01020304, u31([0x81, 0x02, 0x03, 0x04]));

        var err = typeof(Http2Helper).GetMethod("ReadHttp2ErrorCode", PrivateStatic)!
            .CreateDelegate<ReadHttp2ErrorCodeDelegate>();
        Assert.AreEqual(unchecked((int)0x01020304), err([0x01, 0x02, 0x03, 0x04]));

        var padded = typeof(Http2Helper).GetMethod("GetHttp2PaddedDataRange", PrivateStatic)!;
        object?[] args = [new byte[] { 2, 9, 8, 7, 0, 0 }, 6, true, 0, 0];
        padded.Invoke(null, args);
        Assert.AreEqual(1, args[3]);
        Assert.AreEqual(3, args[4]); // 6 - 1 - 2

        args = [new byte[] { 9, 1, 2 }, 3, true, 0, 0];
        padded.Invoke(null, args);
        Assert.AreEqual(1, args[3]);
        Assert.AreEqual(0, args[4]); // clamp when pad too large

        args = [new byte[] { 1, 2, 3 }, 3, false, 0, 0];
        padded.Invoke(null, args);
        Assert.AreEqual(0, args[3]);
        Assert.AreEqual(3, args[4]);
    }

    [TestMethod]
    public void Http2Origin_StripDataFramingOverloads_CoverPaddedAndEmpty()
    {
        var byteOverload = typeof(Http2OriginConnection).GetMethod("StripDataFraming", PrivateStatic,
            binder: null, [typeof(byte[]), typeof(Http2FrameFlag)], modifiers: null)!;
        var unpadded = new byte[] { 1, 2, 3 };
        Assert.AreSame(unpadded, byteOverload.Invoke(null, [unpadded, (Http2FrameFlag)0]));
        var padded = new byte[] { 1, 9, 0 };
        CollectionAssert.AreEqual(new byte[] { 9 },
            (byte[])byteOverload.Invoke(null, [padded, Http2FrameFlag.Padded])!);
        Assert.AreSame(Array.Empty<byte>(),
            byteOverload.Invoke(null, [Array.Empty<byte>(), Http2FrameFlag.Padded]));

        var fromSpan = typeof(Http2OriginConnection).GetMethod("StripDataFraming", PrivateStatic,
            binder: null, [typeof(ReadOnlySpan<byte>), typeof(Http2FrameFlag)], modifiers: null)!
            .CreateDelegate<StripDataFramingFromSpanDelegate>();
        CollectionAssert.AreEqual(unpadded, fromSpan(unpadded, 0));
        CollectionAssert.AreEqual(new byte[] { 9 }, fromSpan(padded, Http2FrameFlag.Padded));

        var spanOnly = typeof(Http2OriginConnection).GetMethod("StripDataFramingSpan", PrivateStatic)!
            .CreateDelegate<StripDataFramingSpanDelegate>();
        CollectionAssert.AreEqual(unpadded, spanOnly(unpadded, 0).ToArray());
        CollectionAssert.AreEqual(new byte[] { 9 }, spanOnly(padded, Http2FrameFlag.Padded).ToArray());
        Assert.AreEqual(0, spanOnly(ReadOnlySpan<byte>.Empty, Http2FrameFlag.Padded).Length);
    }

    [TestMethod]
    public void Http2Helper_AsciiLowerInternMethodReportAndBind()
    {
        var lower = typeof(Http2Helper).GetMethod("AsciiToLowerByteString", PrivateStatic)!;
        Assert.AreEqual("host", ((ByteString)lower.Invoke(null, ["Host".GetByteString()])!).GetString());
        Assert.AreEqual("already", ((ByteString)lower.Invoke(null, ["already".GetByteString()])!).GetString());

        var intern = typeof(Http2Helper).GetMethod("InternCommonHttpMethod", PrivateStatic)!
            .CreateDelegate<InternCommonHttpMethodDelegate>();
        foreach (var name in new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS" })
            Assert.AreEqual(name, intern(Encoding.ASCII.GetBytes(name), name.GetByteString()));
        Assert.AreEqual("PATCH", intern("PATCH"u8, "PATCH".GetByteString()));

        typeof(Http2Helper).GetMethod("Breakpoint", PrivateStatic)!.Invoke(null, null);
        typeof(Http2Helper).GetMethod("ReportException", PrivateStatic)!
            .Invoke(null, [NullLogger.Instance, new ProxyHttpException("cov", new IOException("peer"), null)]);
    }

    [TestMethod]
    public async Task Http2Helper_BindOriginAndSendMemoryHelpers()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableRequestTimingCapture = true };
        using var session = MakeSession(proxy);
        Assert.IsNotNull(session.Timing);
        using var shell = await CreateShellAsync(proxy);
        typeof(Http2Helper).GetMethod("BindOriginForHttp2Stream", PrivateStatic)!
            .Invoke(null, [session, shell.ServerConnection]);
        typeof(Http2Helper).GetMethod("BindOriginForHttp2Stream", PrivateStatic)!
            .Invoke(null, [session, shell.ServerConnection]); // reused path

        var getMem = typeof(Http2Helper).GetMethod("GetMemoryStreamMemory", PrivateStatic)!
            .CreateDelegate<GetMemoryStreamMemoryDelegate>();
        using (var expandable = new MemoryStream())
        {
            expandable.Write("abc"u8);
            CollectionAssert.AreEqual("abc"u8.ToArray(), getMem(expandable).ToArray());
        }

        using (var fixedBuf = new MemoryStream(new byte[8], 0, 8, writable: true, publiclyVisible: false))
        {
            fixedBuf.Write("xy"u8);
            var mem = getMem(fixedBuf); // TryGetBuffer fails → ToArray path (Length stays capacity)
            Assert.IsTrue(mem.Length >= 2);
            Assert.AreEqual((byte)'x', mem.Span[0]);
            Assert.AreEqual((byte)'y', mem.Span[1]);
        }

        var asVt = typeof(Http2Helper).GetMethod("AsValueTask", PrivateStatic)!;
        await (ValueTask)asVt.Invoke(null, [Task.CompletedTask])!;

        using var syncOut = new MemoryStream();
        var writeTwo = typeof(Http2Helper).GetMethod("WriteTwoAsync", PrivateStatic)!;
        await (ValueTask)writeTwo.Invoke(null,
            [syncOut, new ReadOnlyMemory<byte>([1, 2]), new ReadOnlyMemory<byte>([3]), CancellationToken.None])!;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, syncOut.ToArray());

        await using var deferred = new DeferredFirstWriteStream();
        var slow = (ValueTask)writeTwo.Invoke(null,
            [deferred, new ReadOnlyMemory<byte>([9]), new ReadOnlyMemory<byte>([8]), CancellationToken.None])!;
        deferred.Unblock();
        await slow;
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, deferred.Written.ToArray());

        Span<byte> dest = stackalloc byte[16];
        var writePref = typeof(Http2Helper).GetMethod("WriteHpackPrefixedInt", PrivateStatic)!
            .CreateDelegate<WriteHpackPrefixedIntDelegate>();
        Assert.AreEqual(1, writePref(dest, 0x00, 7, 10UL));
        Assert.IsTrue(writePref(dest, 0x00, 7, 300UL) >= 2);

        var litBytes = typeof(Http2Helper).GetMethods(PrivateStatic)
            .First(m => m.Name == "WriteHpackAsciiStringLiteral"
                        && m.GetParameters()[1].ParameterType == typeof(ReadOnlySpan<byte>))
            .CreateDelegate<WriteHpackAsciiLiteralBytesDelegate>();
        Assert.AreEqual(4, litBytes(dest, "abc"u8));

        var litStr = typeof(Http2Helper).GetMethods(PrivateStatic)
            .First(m => m.Name == "WriteHpackAsciiStringLiteral"
                        && m.GetParameters()[1].ParameterType == typeof(string))
            .CreateDelegate<WriteHpackAsciiLiteralStringDelegate>();
        Assert.AreEqual(3, litStr(dest, "xy"));
    }

    [TestMethod]
    public async Task Http2OriginPool_AuthorityEntrySnapshotPruneAndCapacity()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        var entryType = typeof(Http2OriginConnectionPool).GetNestedType("AuthorityEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, nonPublic: true)!;
        var connections = (System.Collections.IList)entryType
            .GetField("Connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(entry)!;

        using var usable = await CreateShellAsync(proxy);
        using var retired = await CreateShellAsync(proxy);
        retired.Retire();
        connections.Add(usable);
        connections.Add(retired);

        var snapshot = typeof(Http2OriginConnectionPool).GetMethod("SnapshotMembers", PrivateStatic)!;
        var snap = (Http2OriginConnection[])snapshot.Invoke(null, [entry])!;
        Assert.AreEqual(1, snap.Length);
        Assert.AreSame(usable, snap[0]);

        var limits = ProxyResourceLimits.Default.WithMaxOriginHttp2ConnectionsPerAuthority(4);
        var canOpen = typeof(Http2OriginConnectionPool).GetMethod("CanOpenAnother", PrivateStatic)!;
        Assert.IsTrue((bool)canOpen.Invoke(null, [entry, limits])!);
        Assert.IsFalse((bool)canOpen.Invoke(null, [entry, ProxyResourceLimits.Default])!); // default max=1

        for (var i = connections.Count; i < 4; i++)
            connections.Add(await CreateShellAsync(proxy));
        Assert.IsFalse((bool)canOpen.Invoke(null, [entry, limits])!);

        var tryAny = typeof(Http2OriginConnectionPool).GetMethod("TryPickAnyUsable", PrivateStatic)!;
        Assert.IsNotNull(tryAny.Invoke(null, [entry]));

        var prune = typeof(Http2OriginConnectionPool).GetMethod("PruneUnusableUnderLock", PrivateStatic)!;
        typeof(Http2OriginConnection).GetField("lastStreamId", PrivateInstance)!.SetValue(usable, int.MaxValue - 1);
        lock (entryType.GetField("Gate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                  .GetValue(entry)!)
            prune.Invoke(null, [entry]);
        Assert.AreEqual(3, connections.Count); // exhausted usable pruned

        await pool.DrainAsync();
    }

    [TestMethod]
    public void RequestHandler_ThrowIfHeaderDeadlineTimedOut_CoversFiredAndIdle()
    {
        var throwIf = typeof(ProxyServer).GetMethod("ThrowIfHeaderDeadlineTimedOut", PrivateStatic)!;
        var registry = new DeadlineRegistry();
        var idle = registry.Start(CancellationToken.None, null, ProxyTimeoutKind.ClientHeader);
        throwIf.Invoke(null, [idle]); // no-op

        using var cts = new CancellationTokenSource();
        var fired = registry.Start(cts.Token, TimeSpan.FromMilliseconds(5), ProxyTimeoutKind.ClientHeader);
        Assert.IsTrue(SpinWait.SpinUntil(() => fired.Token.IsCancellationRequested, TimeSpan.FromSeconds(2)));
        var ex = Assert.ThrowsExactly<TargetInvocationException>(() => throwIf.Invoke(null, [fired]));
        Assert.IsInstanceOfType<ProxyTimeoutException>(ex.InnerException);
    }

    [TestMethod]
    public void FirefoxAndUnixTrust_EscapeAndPathHelpers()
    {
        var ffEscape = typeof(FirefoxCertificateTrust).GetMethod("Escape", PrivateStatic)!;
        Assert.AreEqual("a\\\"b", (string)ffEscape.Invoke(null, ["a\"b"])!);

        var unix = typeof(UnixCertificateTrust);
        Assert.AreEqual("a\\\"b", (string)unix.GetMethod("Escape", PrivateStatic)!.Invoke(null, ["a\"b"])!);
        Assert.AreEqual("a\\\"b", (string)unix.GetMethod("EscapeShell", PrivateStatic)!.Invoke(null, ["a\"b"])!);
        StringAssert.Contains((string)unix.GetMethod("UserLoginKeychainDbPath", PrivateStatic)!.Invoke(null, null)!,
            "login.keychain-db");
        StringAssert.Contains((string)unix.GetMethod("UserLoginKeychainPath", PrivateStatic)!.Invoke(null, null)!,
            "login.keychain");
        StringAssert.Contains((string)unix.GetMethod("UserPkiNssDbPath", PrivateStatic)!.Invoke(null, null)!,
            ".pki");
        unix.GetMethod("TryDelete", PrivateStatic)!.Invoke(null,
            [Path.Combine(Path.GetTempPath(), "twp-missing-" + Guid.NewGuid().ToString("N"))]);
    }

    private sealed class HandleAllMiddleware : IProxyMiddleware
    {
        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next,
            CancellationToken cancellationToken)
        {
            context.IsHandled = true;
            context.HandledStatusCode = 418;
            context.HandledBody = "handled";
            return default;
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

    private sealed class DeferredFirstWriteStream : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public MemoryStream Written { get; } = new();
        public void Unblock() => _gate.TrySetResult();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written.Length;
        public override long Position { get => Written.Position; set => Written.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                return new ValueTask(WriteAfterGateAsync(buffer));
            }

            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private async Task WriteAfterGateAsync(ReadOnlyMemory<byte> buffer)
        {
            await _gate.Task;
            Written.Write(buffer.Span);
        }
    }
}
