using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

#pragma warning disable CA2022 // These tests read a single chunk on purpose.

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>
///     Behavior tests for bridge response compression: which responses are eligible on each protocol leg, and
///     whether the pump stream that turns a <see cref="Response.StreamBodyWriter" /> into reads is lossless.
/// </summary>
[TestClass]
public class BridgeResponseCompressionBehaviorTests
{
    private static readonly ProxyServer.BridgeCompressionShape H1Origin =
        new(OriginLegIsHttp1: true, ClientIsHttp2: false, ClientIsHttp3: false);

    private static SessionEventArgs Session(ProxyServer proxy, Version clientVersion, string acceptEncoding = "gzip")
    {
        var request = new Request { Method = "GET", HttpVersion = clientVersion };
        if (acceptEncoding.Length > 0)
            request.Headers.AddHeader("Accept-Encoding", acceptEncoding);

        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts, request);

        var response = session.HttpClient.Response;
        response.StatusCode = 200;
        response.HttpVersion = HttpHeader.Version11;
        response.Headers.AddHeader("Content-Type", "text/html");
        response.Headers.AddHeader("Content-Length", "2048");
        return session;
    }

    private static byte[] Compressible() => Encoding.ASCII.GetBytes(new string('a', 2048));

    private static ProxyServer Proxy(string mode = ResponseCompressionConfig.ModeBuffered)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            ResponseCompression = new ResponseCompressionConfig { Enabled = true, Mode = mode },
        };
        return proxy;
    }

    private static async Task<bool> BufferedCompressedAsync(
        ProxyServer proxy, SessionEventArgs session, ProxyServer.BridgeCompressionShape shape,
        byte[]? knownBody = null)
    {
        await proxy.TryCompressBridgeBufferedResponseAsync(session, shape, knownBody);
        return session.HttpClient.Response.Headers.HeaderExists("Content-Encoding");
    }

    // ---- buffered eligibility: every guard must keep a response untouched ----------------------------

    [TestMethod]
    public async Task Buffered_EligibleResponse_IsCompressedAndSmaller()
    {
        // Bug class: the happy path regresses to never compressing (guard order inverted).
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        var body = Compressible();

        Assert.IsTrue(await BufferedCompressedAsync(proxy, session, H1Origin, body));

        var response = session.HttpClient.Response;
        Assert.IsTrue(response.Body.Length < body.Length);
        Assert.AreEqual(response.Body.Length, response.ContentLength);
        Assert.AreEqual("Accept-Encoding", response.Headers.GetHeaderValueOrNull("Vary"));
    }

    [TestMethod]
    public async Task Buffered_LockedResponse_IsNotRewritten()
    {
        // Bug class: compressing after headers were committed corrupts the already-sent framing.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        session.HttpClient.Response.Locked = true;

        Assert.IsFalse(await BufferedCompressedAsync(proxy, session, H1Origin, Compressible()));
        Assert.AreEqual("2048", session.HttpClient.Response.Headers.GetHeaderValueOrNull("Content-Length"));
    }

    [TestMethod]
    public async Task Buffered_SyntheticResponse_IsNotRewritten()
    {
        // Bug class: a plugin-authored response gets re-encoded behind the plugin's back.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        session.HttpClient.Response.IsSynthetic = true;

        Assert.IsFalse(await BufferedCompressedAsync(proxy, session, H1Origin, Compressible()));
    }

    [TestMethod]
    public async Task Buffered_RawHttp3BodyDrain_IsNotRewritten()
    {
        // Bug class: a response whose body is still an undrained raw H3 stream is "compressed" from an empty buffer.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        session.HttpClient.Response.Http3RawBodyDrain = (_, _) => Task.CompletedTask;

        Assert.IsFalse(await BufferedCompressedAsync(proxy, session, H1Origin, Compressible()));
    }

    [TestMethod]
    public async Task Buffered_StreamedBodyWithoutKnownBuffer_IsNotRewritten_ButKnownBodyIs()
    {
        // Bug class: a streamed body is forced through buffering (unbounded memory) or, conversely,
        // a caller-supplied buffer is ignored just because a writer is installed.
        using var proxy = Proxy();
        var streamed = Session(proxy, HttpHeader.Version11);
        streamed.HttpClient.Response.StreamBodyWriter = (_, _) => Task.CompletedTask;
        Assert.IsFalse(await BufferedCompressedAsync(proxy, streamed, H1Origin));

        var withBuffer = Session(proxy, HttpHeader.Version11);
        withBuffer.HttpClient.Response.StreamBodyWriter = (_, _) => Task.CompletedTask;
        Assert.IsTrue(await BufferedCompressedAsync(proxy, withBuffer, H1Origin, Compressible()));
    }

    [TestMethod]
    public async Task Buffered_BodyWriteSubscriber_DisablesCompression()
    {
        // Bug class: a BeforeResponseBodyWrite observer is shown compressed bytes it cannot interpret.
        using var proxy = Proxy();
        proxy.OnResponseBodyWrite += (_, _) => Task.CompletedTask;
        var session = Session(proxy, HttpHeader.Version11);

        Assert.IsFalse(await BufferedCompressedAsync(proxy, session, H1Origin, Compressible()));
    }

    [TestMethod]
    public async Task Buffered_RouteCompressionOff_OptsOut()
    {
        // Bug class: the per-route "compression: off" switch is ignored on bridge paths.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        session.MatchedRoute = new RouteConfig
        {
            Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/" }, Compression = "off",
        };

        Assert.IsFalse(await BufferedCompressedAsync(proxy, session, H1Origin, Compressible()));
    }

    [TestMethod]
    public async Task Buffered_IncompressibleBody_KeepsOriginalBytes()
    {
        // Bug class: the "only keep the compressed copy when it is smaller" check is dropped, so random data
        // grows on the wire and Content-Length no longer matches the original.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        var random = RandomNumberGenerator.GetBytes(2048);

        Assert.IsFalse(await BufferedCompressedAsync(proxy, session, H1Origin, random));
        Assert.AreEqual("2048", session.HttpClient.Response.Headers.GetHeaderValueOrNull("Content-Length"));
    }

    [TestMethod]
    public async Task Buffered_ClientLeg_Http3NeedsHttp3Request_AndHttp3RequestNeedsHttp3Shape()
    {
        // Bug class: an H1/H2 client is sent a response compressed under an H3-only decision (or the reverse),
        // so Content-Encoding disagrees with what the client negotiated on its own leg.
        using var proxy = Proxy();
        var h3Shape = new ProxyServer.BridgeCompressionShape(true, false, ClientIsHttp3: true);

        Assert.IsFalse(await BufferedCompressedAsync(
            proxy, Session(proxy, HttpHeader.Version11), h3Shape, Compressible()),
            "H3 shape with an HTTP/1.1 request");
        Assert.IsFalse(await BufferedCompressedAsync(
            proxy, Session(proxy, HttpHeader.Version30), H1Origin, Compressible()),
            "H3 request without the H3 shape");
        Assert.IsTrue(await BufferedCompressedAsync(
            proxy, Session(proxy, HttpHeader.Version30), h3Shape, Compressible()),
            "H3 shape with an HTTP/3 request");
    }

    [TestMethod]
    public async Task Buffered_NonH1Origin_WithoutAnyBody_IsNotRewritten()
    {
        // Bug class: an H2/H3 origin response that carried no body is compressed from an empty buffer.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        var response = session.HttpClient.Response;
        response.HttpVersion = HttpHeader.Version20;
        response.OriginalHasBody = false;
        response.Headers.RemoveHeader("Content-Length");
        var shape = new ProxyServer.BridgeCompressionShape(false, false, false);

        await proxy.TryCompressBridgeBufferedResponseAsync(session, shape);

        Assert.IsFalse(response.Headers.HeaderExists("Content-Encoding"));
    }

    [TestMethod]
    public async Task Buffered_BodyOverBudget_IsNotBufferedForCompression()
    {
        // Bug class: compression buffers a body larger than MaxBufferedBodyBytes, defeating the memory cap.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        session.MaxBufferedBodyBytes = 100;

        // No known body and no origin to read from: reading would throw, so a clean return proves the budget gate.
        Assert.IsFalse(await BufferedCompressedAsync(proxy, session, H1Origin));
        Assert.IsFalse(session.HttpClient.Response.IsBodyRead);
    }

    [TestMethod]
    public async Task Buffered_AlreadyReadBody_IsCompressedWithoutAnotherRead()
    {
        // Bug class: an already-buffered body is re-read from the origin (or lost) instead of reused.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        var response = session.HttpClient.Response;
        var body = Compressible();
        response.Body = body;
        response.IsBodyRead = true;
        response.Headers.RemoveHeader("Content-Length");
        response.Headers.AddHeader("Content-Length", body.Length.ToString());

        Assert.IsTrue(await BufferedCompressedAsync(proxy, session, H1Origin));
        Assert.IsTrue(response.Body.Length < body.Length);
    }

    // ---- streaming plan --------------------------------------------------------------------------

    [TestMethod]
    public void Streaming_PolicyOffOrBufferedMode_PlansNothing()
    {
        // Bug class: buffered mode accidentally enters the streaming relay and changes framing.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        session.HttpClient.Response.OriginalHasBody = true;

        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(session, H1Origin));

        using var off = new ProxyServer(false, false, false);
        Assert.IsNull(off.TryBeginBridgeStreamingCompression(Session(off, HttpHeader.Version11), H1Origin));
    }

    [TestMethod]
    public void Streaming_Ineligible_ReturnsNull_AndLeavesHeadersAlone()
    {
        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);

        var bodyRead = Session(proxy, HttpHeader.Version11);
        bodyRead.HttpClient.Response.OriginalHasBody = true;
        bodyRead.HttpClient.Response.IsBodyRead = true;
        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(bodyRead, H1Origin), "body already read");

        var synthetic = Session(proxy, HttpHeader.Version11);
        synthetic.HttpClient.Response.OriginalHasBody = true;
        synthetic.HttpClient.Response.IsSynthetic = true;
        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(synthetic, H1Origin), "synthetic");

        var drain = Session(proxy, HttpHeader.Version11);
        drain.HttpClient.Response.OriginalHasBody = true;
        drain.HttpClient.Response.Http3RawBodyDrain = (_, _) => Task.CompletedTask;
        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(drain, H1Origin), "raw H3 drain");

        var noBody = Session(proxy, HttpHeader.Version11);
        noBody.HttpClient.Response.OriginalHasBody = false;
        noBody.HttpClient.Response.Headers.RemoveHeader("Content-Length");
        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(noBody, H1Origin), "no body");

        var http10 = Session(proxy, HttpHeader.Version10);
        http10.HttpClient.Response.OriginalHasBody = true;
        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(http10, H1Origin),
            "HTTP/1.0 client cannot take the chunked framing streaming compression needs");
        Assert.IsFalse(http10.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));

        proxy.OnResponseBodyWrite += (_, _) => Task.CompletedTask;
        var hooked = Session(proxy, HttpHeader.Version11);
        hooked.HttpClient.Response.OriginalHasBody = true;
        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(hooked, H1Origin), "body-write subscriber");
    }

    [TestMethod]
    public void Streaming_RouteOff_ReturnsNull()
    {
        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);
        var session = Session(proxy, HttpHeader.Version11);
        session.HttpClient.Response.OriginalHasBody = true;
        session.MatchedRoute = new RouteConfig
        {
            Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/" }, Compression = "OFF",
        };

        Assert.IsNull(proxy.TryBeginBridgeStreamingCompression(session, H1Origin));
    }

    [TestMethod]
    public void Streaming_ForMultiplexedClient_FramesWithoutChunked_AndRecordsOriginFraming()
    {
        // Bug class: an H2/H3 client is left with Transfer-Encoding: chunked (illegal framing there), or the
        // plan loses the origin's framing so the relay cannot tell where the body ends.
        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);
        var session = Session(proxy, HttpHeader.Version30, acceptEncoding: "br, gzip");
        var response = session.HttpClient.Response;
        response.OriginalHasBody = true;
        response.OriginalContentLength = 2048;
        response.OriginalIsChunked = false;
        response.ContentLength = 2048;
        response.IsChunked = false;

        var shape = new ProxyServer.BridgeCompressionShape(
            OriginLegIsHttp1: true, ClientIsHttp2: false, ClientIsHttp3: true);
        var plan = proxy.TryBeginBridgeStreamingCompression(session, shape);

        Assert.IsNotNull(plan, "streaming plan for an eligible H3 client response");
        Assert.IsFalse(response.IsChunked, "multiplexed client must not be marked chunked");
        Assert.IsFalse(plan.Value.OriginChunked);
        Assert.AreEqual(2048, plan.Value.OriginLength);
        Assert.IsNotNull(response.Headers.GetHeaderValueOrNull("Content-Encoding"), "Content-Encoding announced");
        Assert.IsFalse(response.Headers.HeaderExists("Content-Length"),
            "compressed length is unknown, a stale Content-Length would truncate the body");
    }

    [TestMethod]
    public void Streaming_ForHttp1Client_UsesChunkedFraming()
    {
        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);
        var session = Session(proxy, HttpHeader.Version11);
        session.HttpClient.Response.OriginalHasBody = true;

        var plan = proxy.TryBeginBridgeStreamingCompression(session, H1Origin);

        Assert.IsNotNull(plan);
        Assert.IsTrue(session.HttpClient.Response.IsChunked);
    }

    // ---- HTTP/3 client + H1->H2 entry points ----------------------------------------------------------

    [TestMethod]
    public async Task Http3Client_FastPathOrNoPolicy_LeavesResponseUntouched()
    {
        using var noPolicy = new ProxyServer(false, false, false);
        var plain = Session(noPolicy, HttpHeader.Version30);
        plain.HttpClient.Response.OriginalHasBody = true;
        await noPolicy.ApplyHttp3ClientResponseCompressionAsync(plain);
        Assert.IsNull(plain.HttpClient.Response.StreamBodyWriter);

        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);
        var fast = Session(proxy, HttpHeader.Version30);
        fast.HttpClient.Response.OriginalHasBody = true;
        fast.IsFastPath = true;
        await proxy.ApplyHttp3ClientResponseCompressionAsync(fast);
        Assert.IsNull(fast.HttpClient.Response.StreamBodyWriter);
        Assert.IsFalse(fast.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));
    }

    [TestMethod]
    public async Task Http3Client_LockedOrRawDrain_LeavesResponseUntouched()
    {
        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);

        var locked = Session(proxy, HttpHeader.Version30);
        locked.HttpClient.Response.OriginalHasBody = true;
        locked.HttpClient.Response.Locked = true;
        await proxy.ApplyHttp3ClientResponseCompressionAsync(locked);
        Assert.IsFalse(locked.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));

        var drain = Session(proxy, HttpHeader.Version30);
        drain.HttpClient.Response.OriginalHasBody = true;
        drain.HttpClient.Response.Http3RawBodyDrain = (_, _) => Task.CompletedTask;
        await proxy.ApplyHttp3ClientResponseCompressionAsync(drain);
        Assert.IsFalse(drain.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));
    }

    [TestMethod]
    public async Task Http3Client_BufferedMode_CompressesTheBufferedBody()
    {
        // Bug class: buffered mode takes the streaming branch for an H3 client and never compresses.
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version30);
        var response = session.HttpClient.Response;
        var body = Compressible();
        response.Body = body;
        response.IsBodyRead = true;
        response.OriginalHasBody = true;

        await proxy.ApplyHttp3ClientResponseCompressionAsync(session);

        Assert.IsTrue(response.Headers.HeaderExists("Content-Encoding"));
        Assert.IsTrue(response.Body.Length < body.Length);
    }

    [TestMethod]
    public async Task Http3Client_StreamingWithoutOriginWriter_PullsTheBodyFromTheSessionRelay()
    {
        // Bug class: with no origin StreamBodyWriter installed the wrapper is null-dereferenced or skipped,
        // leaving a compressed Content-Encoding header with an uncompressed (or missing) body.
        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);
        var session = Session(proxy, HttpHeader.Version30);
        session.HttpClient.Response.OriginalHasBody = true;

        await proxy.ApplyHttp3ClientResponseCompressionAsync(session);

        Assert.IsNotNull(session.HttpClient.Response.Headers.GetHeaderValueOrNull("Content-Encoding"));
        Assert.IsNotNull(session.HttpClient.Response.StreamBodyWriter,
            "the compressing writer must be installed whenever Content-Encoding was announced");
    }

    [TestMethod]
    public async Task H1ToH2Bridge_BufferedMode_NeverInstallsAStreamingWriter()
    {
        using var proxy = Proxy();
        var session = Session(proxy, HttpHeader.Version11);
        session.HttpClient.Response.OriginalHasBody = true;
        session.HttpClient.Response.StreamBodyWriter = (_, _) => Task.CompletedTask;
        var before = session.HttpClient.Response.StreamBodyWriter;

        await proxy.ApplyH1ToH2BridgeResponseCompressionAsync(session, null!, CancellationToken.None);

        Assert.AreSame(before, session.HttpClient.Response.StreamBodyWriter);
        Assert.IsFalse(session.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));
    }

    [TestMethod]
    public async Task H1ToH2Bridge_LockedFastPathOrNoPolicy_LeavesResponseUntouched()
    {
        using var noPolicy = new ProxyServer(false, false, false);
        var a = Session(noPolicy, HttpHeader.Version11);
        a.HttpClient.Response.OriginalHasBody = true;
        await noPolicy.ApplyH1ToH2BridgeResponseCompressionAsync(a, null!, CancellationToken.None);
        Assert.IsFalse(a.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));

        using var proxy = Proxy(ResponseCompressionConfig.ModeStreaming);
        var fast = Session(proxy, HttpHeader.Version11);
        fast.HttpClient.Response.OriginalHasBody = true;
        fast.IsFastPath = true;
        await proxy.ApplyH1ToH2BridgeResponseCompressionAsync(fast, null!, CancellationToken.None);
        Assert.IsFalse(fast.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));

        var locked = Session(proxy, HttpHeader.Version11);
        locked.HttpClient.Response.OriginalHasBody = true;
        locked.HttpClient.Response.Locked = true;
        await proxy.ApplyH1ToH2BridgeResponseCompressionAsync(locked, null!, CancellationToken.None);
        Assert.IsFalse(locked.HttpClient.Response.Headers.HeaderExists("Content-Encoding"));
    }

    // ---- StreamBodyWriterPumpStream ------------------------------------------------------------------

    private static async Task<byte[]> ReadAllAsync(Stream stream, int chunk = 7)
    {
        using var output = new MemoryStream();
        var buffer = new byte[chunk];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
            output.Write(buffer, 0, n);
        return output.ToArray();
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_AsyncWriter_DeliversEveryByteInOrder()
    {
        var expected = RandomNumberGenerator.GetBytes(100_000);
        await using var pump = new StreamBodyWriterPumpStream(async (sink, ct) =>
        {
            for (var offset = 0; offset < expected.Length; offset += 1000)
                await sink.WriteAsync(expected.AsMemory(offset, 1000), ct);
        }, CancellationToken.None);

        CollectionAssert.AreEqual(expected, await ReadAllAsync(pump, 333));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_SynchronousWriterFasterThanReader_LosesNoChunks()
    {
        // Bug class: the sink used TryWrite on a bounded channel, so once more than four chunks were queued
        // a synchronous writer silently dropped data (a truncated/corrupt body with a clean EOF).
        const int chunks = 200;
        var expected = new byte[chunks * 16];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = (byte)(i * 31 + i / 256);

        await using var pump = new StreamBodyWriterPumpStream((sink, _) =>
        {
            for (var i = 0; i < chunks; i++)
                sink.Write(expected, i * 16, 16);
            return Task.CompletedTask;
        }, CancellationToken.None);

        CollectionAssert.AreEqual(expected, await ReadAllAsync(pump, 5));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_ZeroLengthWrite_DoesNotEndTheBodyEarly()
    {
        // Bug class: an empty write was queued as a chunk and the reader treated the empty chunk as EOF.
        var tail = Encoding.ASCII.GetBytes("after-empty");
        await using var pump = new StreamBodyWriterPumpStream(async (sink, ct) =>
        {
            await sink.WriteAsync(Encoding.ASCII.GetBytes("before-"), ct);
            await sink.WriteAsync(ReadOnlyMemory<byte>.Empty, ct);
            sink.Write([], 0, 0);
            await sink.WriteAsync(tail, ct);
        }, CancellationToken.None);

        Assert.AreEqual("before-after-empty", Encoding.ASCII.GetString(await ReadAllAsync(pump)));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_WriterFault_SurfacesToTheReaderAfterTheDeliveredBytes()
    {
        // Bug class: the writer's exception was swallowed in a fire-and-forget task, so an origin failure
        // mid-body looked like a complete (truncated) response to the client.
        await using var pump = new StreamBodyWriterPumpStream(async (sink, ct) =>
        {
            await sink.WriteAsync(Encoding.ASCII.GetBytes("partial"), ct);
            throw new IOException("origin reset");
        }, CancellationToken.None);

        var buffer = new byte[64];
        var first = await pump.ReadAsync(buffer);
        Assert.AreEqual("partial", Encoding.ASCII.GetString(buffer, 0, first));
        var ex = await Assert.ThrowsExactlyAsync<IOException>(async () => await pump.ReadAsync(buffer));
        StringAssert.Contains(ex.Message, "origin reset");
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_ReadAfterEnd_KeepsReturningZero()
    {
        await using var pump = new StreamBodyWriterPumpStream(
            (sink, ct) => sink.WriteAsync(new byte[] { 1, 2, 3 }, ct).AsTask(), CancellationToken.None);
        var buffer = new byte[16];

        Assert.AreEqual(3, await pump.ReadAsync(buffer));
        Assert.AreEqual(0, await pump.ReadAsync(buffer));
        Assert.AreEqual(0, await pump.ReadAsync(buffer));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_EmptyBody_ReadsZeroImmediately()
    {
        await using var pump = new StreamBodyWriterPumpStream((_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.AreEqual(0, await pump.ReadAsync(new byte[8]));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_SynchronousRead_UsesTheSameChunksAsAsyncRead()
    {
        await using var pump = new StreamBodyWriterPumpStream(
            (sink, ct) => sink.WriteAsync(Encoding.ASCII.GetBytes("sync-read"), ct).AsTask(),
            CancellationToken.None);

        var buffer = new byte[4];
        var collected = new StringBuilder();
        int n;
        while ((n = await Task.Run(() => pump.Read(buffer, 0, buffer.Length))) > 0)
            collected.Append(Encoding.ASCII.GetString(buffer, 0, n));

        Assert.AreEqual("sync-read", collected.ToString());
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        await using var pump = new StreamBodyWriterPumpStream(
            async (_, ct) => await gate.Task.WaitAsync(ct), cts.Token);

        var read = pump.ReadAsync(new byte[4], cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
    }

    [TestMethod]
    public async Task Pump_Capabilities_AreReadOnlyForwardOnly()
    {
        await using var pump = new StreamBodyWriterPumpStream((_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.IsTrue(pump.CanRead);
        Assert.IsFalse(pump.CanSeek);
        Assert.IsFalse(pump.CanWrite);
        pump.Flush();
        Assert.ThrowsExactly<NotSupportedException>(() => _ = pump.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = pump.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => pump.Position = 1);
        Assert.ThrowsExactly<NotSupportedException>(() => pump.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => pump.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => pump.Write(new byte[1], 0, 1));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_WriterSinkIsWriteOnly()
    {
        // Bug class: the sink handed to the origin writer is readable/seekable, letting a writer corrupt state.
        Stream? captured = null;
        await using var pump = new StreamBodyWriterPumpStream((sink, _) =>
        {
            captured = sink;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.AreEqual(0, await pump.ReadAsync(new byte[1]));
        Assert.IsNotNull(captured);
        Assert.IsTrue(captured.CanWrite);
        Assert.IsFalse(captured.CanRead);
        Assert.IsFalse(captured.CanSeek);
        captured.Flush();
        Assert.ThrowsExactly<NotSupportedException>(() => _ = captured.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = captured.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => captured.Position = 1);
        Assert.ThrowsExactly<NotSupportedException>(() => captured.Read(new byte[1], 0, 1));
        Assert.ThrowsExactly<NotSupportedException>(() => captured.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => captured.SetLength(1));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Pump_DisposedEarly_ReleasesABlockedWriter()
    {
        // Bug class: the client goes away mid-body and the origin writer stays parked on a full channel forever.
        var writerEnded = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new StreamBodyWriterPumpStream(async (sink, ct) =>
        {
            try
            {
                for (var i = 0; i < 1000; i++)
                    await sink.WriteAsync(new byte[64], ct);
                writerEnded.TrySetResult(null);
            }
            catch (Exception ex)
            {
                writerEnded.TrySetResult(ex);
            }
        }, CancellationToken.None);

        Assert.AreEqual(64, await pump.ReadAsync(new byte[64]));
        await pump.DisposeAsync();

        var ended = await writerEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(ended, "writer must observe the closed channel instead of finishing all 1000 writes");
        Assert.IsTrue(ended.GetType().Name.Contains("Closed", StringComparison.Ordinal)
                      || ended is OperationCanceledException,
            ended.GetType().Name);
    }
}
