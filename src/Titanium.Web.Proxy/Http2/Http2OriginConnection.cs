using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Thrown when a leased stream is known - from an origin GOAWAY naming a lower last-stream-id - to have
///     never been processed by the origin at all. The caller may safely retry the exact same request on a
///     fresh connection, since the origin guarantees (RFC 7540 §6.8) it took no action for any stream above
///     the announced last-stream-id.
/// </summary>
internal sealed class Http2OriginGoAwayException : IOException
{
    internal Http2OriginGoAwayException(string message) : base(message)
    {
    }
}

/// <summary>
///     One real, persistent HTTP/2 connection to one origin server, used as the target of the
///     H1→H2 and H3→H2 translation bridges. Leases an odd, strictly increasing stream id
///     (RFC 7540 §5.1.1) for each translated request. Instances are shared across independent client
///     connections via <see cref="Http2OriginConnectionPool" /> so many clients multiplex onto a small
///     set of origin TLS+H2 sessions.
///     <para>
///         Response bodies are streamed through a <see cref="BoundedBodyPipe" /> to enforce
///         <c>MaxBufferedBodyBytes</c> and propagate RST_STREAM/GOAWAY cancellation promptly.
///     </para>
/// </summary>
internal sealed class Http2OriginConnection : IDisposable
{
    /// <summary>Every HTTP/2 endpoint must accept frames up to this size (RFC 7540 §4.2), so it is always safe to send.</summary>
    private const int SafeMaxFrameSize = 16384;

    /// <summary>Maximum total header block (HEADERS + CONTINUATION fragments) we accept from origin before treating it as a protocol violation.</summary>
    private const int MaxHeaderBlockBytes = 256 * 1024;

    /// <summary>Retire the connection before odd client stream ids wrap (RFC 7540 §5.1.1).</summary>
    private const int StreamIdExhaustionThreshold = int.MaxValue - 10_000;

    private readonly TcpServerConnection connection;
    private readonly Stream socket;
    private Stream stream;
    private Http2FrameWriter? frameWriter;
    private readonly ILogger logger;
    private readonly long maxBufferedBodyBytes;
    private readonly ProxyResourceLimits resourceLimits;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Http2FlowController sendFlow = new();
    private readonly Http2Settings originSettings = new();
    private readonly ConcurrentDictionary<int, PendingStream> streams = new();
    private readonly CancellationTokenSource connectionCts = new();
    private readonly TaskCompletionSource<bool> initialSettingsReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Batched receive credit (read loop is single-threaded). Matches Http2Helper half-window batch threshold.
    private int pendingConnectionReceiveCredit;
    private readonly Dictionary<int, int> pendingStreamReceiveCredit = new();

    private SemaphoreSlim? concurrencyGate;
    private int concurrencyGateCapacity;
    private Decoder? decoder;
    private readonly HeaderCollectorListener headerCollector = new();
    private int lastStreamId = -1;
    private volatile bool faulted;
    private volatile bool goingAway;
    private int goAwayLastStreamId = int.MaxValue;
    private int disposed;
    private int pendingDispose;
    private int leaseCount;
    private int activeStreamCount;
    private long lastUsedUtcTicks = DateTime.UtcNow.Ticks;

    // Reused only under writeLock (single encoder critical section).
    private Http2FrameHeader? encodeFrameHeader;
    private readonly byte[] encodeFrameHeaderBuffer = new byte[9];

    private Http2OriginConnection(TcpServerConnection connection, ILogger logger, long maxBufferedBodyBytes,
        ProxyResourceLimits resourceLimits)
    {
        this.connection = connection;
        socket = connection.Stream;
        stream = socket;
        this.logger = logger;
        this.maxBufferedBodyBytes = maxBufferedBodyBytes;
        this.resourceLimits = resourceLimits;
    }

    /// <summary>True while this connection may still be leased for a new request.</summary>
    internal bool IsUsable => !faulted && !goingAway && !connection.IsClosed;

    /// <summary>
    ///     In-flight streams currently registered on this connection. Interlocked — do not use
    ///     <c>streams.Count</c> (that takes every ConcurrentDictionary lock).
    /// </summary>
    internal int ActiveStreamCount => Volatile.Read(ref activeStreamCount);

    /// <summary>Rents currently pinned on this connection (SendAsync / tunnel in progress).</summary>
    internal int LeaseCount => Volatile.Read(ref leaseCount);

    /// <summary>
    ///     Historical SoftGrow=16 TLS constant. Live TLS grow uses <see cref="SoftStreamCapacity"/> —
    ///     long 20s Mac pairs: SoftGrow=16 ~0.90× H1 / ~0.89× H3; SoftGrow=SoftPick ~0.92× H1 /
    ///     ~0.96× H3. SoftGrow=8 peaked short-arm H1 ~0.83× but hurt H3. Cap remains
    ///     <see cref="ProxyResourceLimits.MaxOriginHttp2ConnectionsPerAuthority"/>.
    /// </summary>
    internal const int PoolGrowActiveStreamThresholdTls = 16;

    /// <summary>
    ///     Early-grow threshold for cleartext h2c. SoftGrow=4 was the Soft=16-as-pick-cap era;
    ///     with SoftPick=SETTINGS, SoftGrow=8 lifts Mac H3→h2c (~0.88→~0.94×). SoftGrow=12
    ///     regresses (~0.88×). SoftGrow=16 on cleartext starved ReadLoops under Soft=16-as-pick.
    /// </summary>
    internal const int PoolGrowActiveStreamThresholdCleartext = 8;

    /// <summary>Alias — TLS SoftGrow (tests / wiki that reference the historical name).</summary>
    internal const int PoolGrowActiveStreamThreshold = PoolGrowActiveStreamThresholdTls;

    /// <summary>
    ///     Soft multiplex pick capacity used by <see cref="Http2OriginConnectionPool" /> —
    ///     <c>SETTINGS_MAX_CONCURRENT_STREAMS</c> / concurrency gate (not the early-grow dial).
    ///     Prefer filling under this cap; grow is driven separately by
    ///     <see cref="PoolGrowActiveStreamThreshold"/> /
    ///     <see cref="PoolGrowActiveStreamThresholdCleartext"/>.
    /// </summary>
    internal int SoftStreamCapacity
    {
        get
        {
            var cap = concurrencyGateCapacity;
            if (cap <= 0)
                cap = resourceLimits.MaxConcurrentStreamsPerConnection;
            return Math.Max(1, cap);
        }
    }

    /// <summary>
    ///     Early-grow dial for this connection (TLS vs cleartext). Both use SoftStreamCapacity
    ///     (SETTINGS/gate SoftPick): long Mac SoftGrow=16 TLS left H3→H2 ~0.89×; SoftPick TLS
    ///     ~0.96×. Cleartext SoftGrow=8 left H3→h2c ~0.83–0.88× on long arms — SoftPick under test.
    /// </summary>
    internal int PoolGrowThreshold => SoftStreamCapacity;

    /// <summary>True when the next odd stream id would approach int wraparound.</summary>
    internal bool IsNearStreamIdExhaustion => Volatile.Read(ref lastStreamId) >= StreamIdExhaustionThreshold;

    /// <summary>Last time this connection was selected for a new stream (UTC).</summary>
    internal DateTime LastUsedUtc => new(Volatile.Read(ref lastUsedUtcTicks), DateTimeKind.Utc);

    /// <summary>
    ///     Whether the origin advertised <c>SETTINGS_ENABLE_CONNECT_PROTOCOL=1</c> (RFC 8441).
    ///     Valid only after the initial SETTINGS exchange has completed.
    /// </summary>
    internal bool EnableConnectProtocol => originSettings.EnableConnectProtocol;

    /// <summary>
    ///     The underlying TCP connection, exposed so callers can attribute
    ///     <see cref="ProxyServer.EnableRequestTimingCapture" /> timing (connection id, reuse, and
    ///     establishment timing) to each request leased from this shared, persistent origin connection.
    /// </summary>
    internal TcpServerConnection ServerConnection => connection;

    private Http2FrameWriter Writer =>
        frameWriter ?? throw new InvalidOperationException("Origin frame writer is not attached.");

    /// <summary>
    ///     Exclusive drain (no lock around <c>WriteAsync</c>). After this, <see cref="stream"/> rejects
    ///     writes so mixed direct socket I/O cannot interleave with the writer.
    /// </summary>
    private void AttachExclusiveFrameWriter()
    {
        frameWriter = new Http2FrameWriter(socket);
        stream = new WriteForbiddenStream(socket);
    }

    internal void Touch() => Volatile.Write(ref lastUsedUtcTicks, DateTime.UtcNow.Ticks);

    /// <summary>Pins this connection so idle sweep / prune will not dispose it mid-request.</summary>
    internal void AcquireLease() => Interlocked.Increment(ref leaseCount);

    /// <summary>Drops a pin from <see cref="AcquireLease"/> and disposes if the pool asked to retire.</summary>
    internal void ReleaseLease()
    {
        Interlocked.Decrement(ref leaseCount);
        TryDisposeIfRetiredAndIdle();
    }

    private void RegisterOpenedStream(int streamId, PendingStream pending)
    {
        if (!streams.TryAdd(streamId, pending))
            throw new InvalidOperationException($"HTTP/2 stream {streamId} is already registered on this origin connection.");
        Interlocked.Increment(ref activeStreamCount);
    }

    private bool TryUnregisterStream(int streamId, out PendingStream? pending)
    {
        if (!streams.TryRemove(streamId, out pending))
            return false;
        Interlocked.Decrement(ref activeStreamCount);
        return true;
    }

    /// <summary>
    ///     Stops handing this connection out. Does not fail in-flight streams. Dispose runs when the
    ///     last lease/stream drains, so siblings on a shared connection survive GOAWAY/CloseServerConnection.
    /// </summary>
    internal void Retire()
    {
        goingAway = true;
        Volatile.Write(ref pendingDispose, 1);
        TryDisposeIfRetiredAndIdle();
    }

    private void TryDisposeIfRetiredAndIdle()
    {
        if (Volatile.Read(ref pendingDispose) == 0)
            return;
        if (Volatile.Read(ref leaseCount) > 0 || Volatile.Read(ref activeStreamCount) > 0)
            return;
        Dispose();
    }

    /// <summary>
    ///     Establishes a new origin h2 connection over an already-opened <see cref="TcpServerConnection" />
    ///     (TLS ALPN <c>h2</c>, or cleartext h2c prior-knowledge when <see cref="TcpServerConnection.Http2Cleartext"/>
    ///     is set): writes the client connection preface, this proxy's own SETTINGS, and an initial
    ///     connection-level WINDOW_UPDATE (matching Chrome's preface), starts the background
    ///     frame-reading loop, and waits for the origin's own initial SETTINGS frame so
    ///     <see cref="SendAsync" /> always has a real <c>MAX_CONCURRENT_STREAMS</c>/frame-size budget to honor.
    /// </summary>
    internal static async Task<Http2OriginConnection> CreateAsync(TcpServerConnection connection,
        ILogger logger, long maxBufferedBodyBytes, CancellationToken cancellationToken,
        ProxyResourceLimits? resourceLimits = null)
    {
        var instance = new Http2OriginConnection(connection, logger, maxBufferedBodyBytes,
            resourceLimits ?? ProxyResourceLimits.Default);

        var preface = Http2Helper.ConnectionPreface;
        connection.Http2SessionStarted = true;
        await instance.socket.WriteAsync(preface.AsMemory(), cancellationToken);
        // Shared with the H2↔H2 MITM path (SendHttp2ClientConnectionStartupAsync).
        await Http2Helper.SendHttp2ClientConnectionStartupAsync(instance.socket, cancellationToken);
        instance.AttachExclusiveFrameWriter();

        _ = instance.ReadLoopAsync(instance.connectionCts.Token);

        try
        {
            await instance.initialSettingsReceived.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http2OriginConnection settings wait failed; failing connection and rethrowing", ex);
            instance.Fail(ex);
            throw;
        }

        return instance;
    }

    /// <summary>
    ///     Translates <paramref name="request" /> onto a freshly leased stream and returns once final
    ///     response headers are available. When <paramref name="copyRequestBody"/> is set (or the request
    ///     body was not buffered), request DATA is streamed; the response body is delivered via
    ///     <see cref="Response.StreamBodyWriter"/> instead of a fully materialized <c>byte[]</c>.
    /// </summary>
    internal async Task<Http2OriginExchange> SendAsync(Request request, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        Func<int, HeaderCollection, CancellationToken, Task>? on1xx,
        CancellationToken cancellationToken,
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task>? copyRequestBody =
            null)
    {
        if (!IsUsable) throw new Http2OriginGoAwayException("The origin h2 connection is no longer usable.");

        Touch();
        AcquireLease();
        var leaseOwned = true;
        try
        {
        await initialSettingsReceived.Task.WaitAsync(cancellationToken);

        var gate = concurrencyGate ?? throw new InvalidOperationException("Origin settings were never processed.");
        // Non-allocating uncontended take; WaitAsync alone allocates even when free.
        if (!gate.Wait(0, CancellationToken.None)) // NOSONAR S6966 -- intentional sync try-take before WaitAsync
            await gate.WaitAsync(cancellationToken);

        // Allocate InterimChannel only when the caller will drain 1xx (on1xx != null). Passthrough
        // bridges wait on HeadersReceived instead — avoids per-request Channel/segment Gen0.
        var pending = new PendingStream(maxBufferedBodyBytes, createInterimChannel: on1xx != null);
        var streamId = 0;
        var streamOpened = false;
        var bodyHandedOff = false;
        try
        {
            var frameHeader = encodeFrameHeader ??= new Http2FrameHeader();
            var frameHeaderBuffer = encodeFrameHeaderBuffer;

            var streamRequest = copyRequestBody != null && !request.IsBodyRead && !request.BodyAvailable;
            byte[]? bufferedBody = null;
            var enqueueBufferedTrailers = false;
            if (!streamRequest)
            {
                // Tiny GET: HasBody is false — skip CompressBodyAndUpdateContentLength (header scans).
                if (request.HasBody)
                {
                    var body = request.CompressBodyAndUpdateContentLength();
                    bufferedBody = request.IsBodyRead ? body : null;
                }

                enqueueBufferedTrailers = request.HasTrailingHeaders && request.TrailingHeaders.Any();
            }

            // RFC 7540 §5.1.1: allocate stream id + encode first HEADERS + enqueue as one
            // critical section. Socket I/O happens on the exclusive frame-writer drain.
            await WaitWriteLockAsync(cancellationToken);
            try
            {
                streamId = Interlocked.Add(ref lastStreamId, 2);
                if (goingAway && streamId > goAwayLastStreamId)
                    throw new Http2OriginGoAwayException(
                        $"The origin sent GOAWAY before stream {streamId} could be opened; it was never processed.");

                RegisterOpenedStream(streamId, pending);
                sendFlow.RegisterStream(streamId);
                streamOpened = true;
                frameHeader.StreamId = streamId;

                var headersEndStream = !streamRequest && bufferedBody == null && !enqueueBufferedTrailers;
                Http2Helper.EnqueueHeader(originSettings, frameHeader, frameHeaderBuffer, request,
                    headersEndStream, Writer, encoderAlreadyExclusive: true);
            }
            finally
            {
                writeLock.Release();
            }

            if (bufferedBody != null)
            {
                await EnqueueDataWithFlowAsync(streamId, bufferedBody, endStream: !enqueueBufferedTrailers,
                    cancellationToken);
            }

            if (enqueueBufferedTrailers)
            {
                await WaitWriteLockAsync(cancellationToken);
                try
                {
                    Http2Helper.EnqueueTrailer(originSettings, frameHeader, frameHeaderBuffer, streamId,
                        request.TrailingHeaders, true, Writer);
                }
                finally
                {
                    writeLock.Release();
                }
            }

            if (streamRequest && copyRequestBody is { } copyBody)
            {
                await copyBody(
                    async (data, ct) =>
                    {
                        if (data.IsEmpty) return;
                        await EnqueueDataWithFlowAsync(streamId, data, endStream: false, ct);
                    },
                    cancellationToken);
                request.IsBodyReceived = true;

                if (request.HasTrailingHeaders && request.TrailingHeaders.Any())
                {
                    await WaitWriteLockAsync(cancellationToken);
                    try
                    {
                        Http2Helper.EnqueueTrailer(originSettings, frameHeader, frameHeaderBuffer, streamId,
                            request.TrailingHeaders, true, Writer);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }
                else
                {
                    await EnqueueDataWithFlowAsync(streamId, ReadOnlyMemory<byte>.Empty, endStream: true,
                        cancellationToken);
                }
            }
        }
        catch (Exception sendEx) when (streamOpened)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http2OriginConnection SendAsync failed before response; cleaning up stream and rethrowing",
                sendEx);
            TryUnregisterStream(streamId, out _);
            pending.Dispose();
            sendFlow.RemoveStream(streamId);
            gate.Release();
            throw;
        }
        finally
        {
            // Gate was taken but the stream never opened (cancel / GOAWAY before HEADERS).
            if (!streamOpened)
            {
                pending.Dispose();
                gate.Release();
            }
        }

        try
        {
            // Do not Register on BodyPipe until after headers: WaitAsync already cancels the TTFB
            // wait, and probe GETs dispose the registration immediately on no-body responses.
            CancellationTokenRegistration bodyCancelRegistration = default;

            // Relay 1xx interim responses (e.g. 103 Early Hints) to the HTTP/1.1 client before reading
            // the body. ReadLoopAsync writes each interim into pending.InterimChannel and completes the
            // writer as soon as final response headers arrive, so this loop exits cleanly before
            // body drainage begins. When on1xx is null (passthrough lite / no 1xx relay), wait on the
            // HeadersReceived TCS instead — otherwise we race ProcessHeaderBlock and synthesize 502.
            // Inline tiny-CL delays HeadersReceived until END_STREAM; always await it after interims
            // so the body buffer is complete before TakeInlineBody.
            if (on1xx != null)
            {
                var interimReader = pending.InterimChannel?.Reader
                    ?? throw new InvalidOperationException("InterimChannel required when on1xx is set.");
                await foreach (var interim in interimReader.ReadAllAsync(cancellationToken))
                    await on1xx(interim.StatusCode, interim.Headers, cancellationToken);
            }

            await pending.HeadersReceived.Task.WaitAsync(cancellationToken);

            var response = pending.Response ??
                           new Response
                           {
                               StatusCode = 502,
                               StatusDescription = string.Empty,
                               HttpVersion = HttpHeader.Version11
                           };

            // An h2 body is delimited by END_STREAM (RFC 9113 §8.1), never by HTTP/1.1 framing headers,
            // and this response object is deliberately stamped HttpVersion 1.1 for the HTTP/1.1 client.
            // Response.HasBody's H1 framing rules (Content-Length / chunked / Connection-close) would
            // therefore misclassify a content-length-less h2 response as bodiless and silently drop its
            // DATA frames. Only the status/method exclusions and an explicit `content-length: 0` mean
            // "no body" here (1xx never reaches this point; the interim channel consumed those).
            var noBody = response.StatusCode is 204 or 304
                         || request.Method == "HEAD"
                         || (request.Method == "CONNECT" && response.StatusCode is >= 200 and < 300)
                         || response.ContentLength == 0;
            if (noBody)
            {
                response.IsBodyRead = true;
                response.Body = Array.Empty<byte>();
                return new Http2OriginExchange(response, Array.Empty<byte>(), pending.TrailingHeaders);
            }

            var trailers = pending.TrailingHeaders;

            // Tiny fixed-length bodies (probe GET ~56 B): ReadLoop already filled InlineBody and
            // delayed HeadersReceived until END_STREAM — skip Pipe + second byte[] alloc.
            if (pending.InlineBody != null)
            {
                var body = pending.TakeInlineBody();
                response.IsBodyRead = true;
                response.Body = body;
                response.BodyIsWireEncoded = true;
                if (trailers != null)
                {
                    foreach (var header in trailers)
                        response.TrailingHeaders.AddHeader(header);
                }

                return new Http2OriginExchange(response, body, trailers);
            }

            var bodyPipe = pending.EnsureBodyPipe();

            // Known-CL bodies that exceeded the inline threshold still buffer then return so H1
            // deliver can coalesce headers+body. Streaming via StreamBodyWriter pays an extra
            // pipe+async hop per request for these.
            if (response.ContentLength is >= 0 and <= 8 * 1024)
            {
                var expected = (int)response.ContentLength;
                byte[] body;
                if (expected == 0)
                {
                    body = Array.Empty<byte>();
                    await bodyPipe.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    body = new byte[expected];
                    var read = await bodyPipe.ReadExactAsync(body, cancellationToken).ConfigureAwait(false);
                    if (read != expected)
                    {
                        if (read == 0)
                            body = Array.Empty<byte>();
                        else
                            Array.Resize(ref body, read);
                    }
                }

                response.IsBodyRead = true;
                response.Body = body;
                // H2 DATA payload is already content-encoded when Content-Encoding is present.
                response.BodyIsWireEncoded = true;
                if (trailers != null)
                {
                    foreach (var header in trailers)
                        response.TrailingHeaders.AddHeader(header);
                }

                return new Http2OriginExchange(response, body, trailers);
            }

            bodyCancelRegistration = cancellationToken.Register(() =>
                bodyPipe.CompleteWriter(new OperationCanceledException(cancellationToken)));
            bodyHandedOff = true;

            response.StreamBodyWriter = async (dest, ct) =>
            {
                try
                {
                    await bodyPipe.CopyToAsync(dest, ct);
                }
                finally
                {
                    await bodyCancelRegistration.DisposeAsync();
                    TryUnregisterStream(streamId, out _);
                    pending.Dispose();
                    sendFlow.RemoveStream(streamId);
                    gate.Release();
                    ReleaseLease();
                }
            };
            leaseOwned = false;

            if (trailers != null)
            {
                foreach (var header in trailers)
                    response.TrailingHeaders.AddHeader(header);
            }

            return new Http2OriginExchange(response, Array.Empty<byte>(), trailers);
        }
        finally
        {
            if (!bodyHandedOff)
            {
                TryUnregisterStream(streamId, out _);
                pending.Dispose();
                sendFlow.RemoveStream(streamId);
                gate.Release();
            }
        }
        }
        finally
        {
            if (leaseOwned)
                ReleaseLease();
        }
    }

    /// <summary>
    ///     Opens an RFC 8441 extended CONNECT tunnel on a freshly leased stream. The request must already
    ///     have <c>Method = CONNECT</c> and <see cref="Request.ExtendedConnectProtocol" /> set (and hop-by-hop
    ///     headers stripped). On a final 2xx response, returns an <see cref="Http2TunnelStream" /> that
    ///     speaks raw DATA for the life of the tunnel; on any other status, resets the stream and returns
    ///     the response headers without a stream.
    /// </summary>
    internal async Task<Http2OriginTunnelResult> OpenTunnelAsync(Request request,
        CancellationToken cancellationToken)
    {
        if (!IsUsable) throw new Http2OriginGoAwayException("The origin h2 connection is no longer usable.");

        Touch();
        AcquireLease();
        var leaseOwned = true;
        try
        {
        await initialSettingsReceived.Task.WaitAsync(cancellationToken);

        if (!originSettings.EnableConnectProtocol)
        {
            throw new InvalidOperationException(
                "The origin did not advertise SETTINGS_ENABLE_CONNECT_PROTOCOL=1; " +
                "extended CONNECT cannot be opened on this connection.");
        }

        var gate = concurrencyGate ?? throw new InvalidOperationException("Origin settings were never processed.");
        await gate.WaitAsync(cancellationToken);

        var pending = PendingStream.CreateTunnel();
        var streamId = 0;
        var streamOpened = false;
        try
        {
            var frameHeader = new Http2FrameHeader();
            var frameHeaderBuffer = new byte[9];

            await WaitWriteLockAsync(cancellationToken);
            try
            {
                streamId = Interlocked.Add(ref lastStreamId, 2);
                if (goingAway && streamId > goAwayLastStreamId)
                    throw new Http2OriginGoAwayException(
                        $"The origin sent GOAWAY before stream {streamId} could be opened; it was never processed.");

                RegisterOpenedStream(streamId, pending);
                sendFlow.RegisterStream(streamId);
                streamOpened = true;
                frameHeader.StreamId = streamId;

                // Must use SendHeader with endStream=false: SendBody derives END_STREAM from the body
                // and would half-close a bodiless CONNECT before the first tunnel byte.
                Http2Helper.EnqueueHeader(originSettings, frameHeader, frameHeaderBuffer, request,
                    endStream: false, Writer, encoderAlreadyExclusive: true);
            }
            finally
            {
                writeLock.Release();
            }
        }
        catch (Exception connectHeadersEx) when (streamOpened)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http2OriginConnection CONNECT headers send failed; cleaning up stream and rethrowing",
                connectHeadersEx);
            TryUnregisterStream(streamId, out _);
            pending.Dispose();
            sendFlow.RemoveStream(streamId);
            gate.Release();
            throw;
        }
        finally
        {
            if (!streamOpened)
            {
                pending.Dispose();
                gate.Release();
            }
        }

        try
        {
            await using var registration = cancellationToken.Register(() =>
            {
                pending.HeadersReceived.TrySetCanceled(cancellationToken);
                pending.TunnelDataChannel?.Writer.TryComplete(new OperationCanceledException(cancellationToken));
            });

            await pending.HeadersReceived.Task.WaitAsync(cancellationToken);

            var response = pending.Response ??
                           new Response
                           {
                               StatusCode = 502,
                               StatusDescription = string.Empty,
                               HttpVersion = HttpHeader.Version11
                           };

            if (response.StatusCode is < 200 or >= 300)
            {
                await ResetStreamAsync(streamId, Http2ErrorCode.Cancel, CancellationToken.None);
                leaseOwned = false;
                ReleaseTunnelBookkeeping(streamId, pending, gate);
                return new Http2OriginTunnelResult(response, null);
            }

            var tunnelStream = new Http2TunnelStream(
                pending.TunnelDataChannel!.Reader,
                (payload, endStream, ct) => WriteTunnelDataAsync(streamId, payload, endStream, ct),
                (errorCode, ct) => ResetStreamAsync(streamId, errorCode, ct),
                () => ReleaseTunnelBookkeeping(streamId, pending, gate));

            // Ownership of gate/sendFlow/pending/lease transfers to the tunnel stream until Dispose.
            leaseOwned = false;
            return new Http2OriginTunnelResult(response, tunnelStream);
        }
        catch (Exception tunnelEx)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http2OriginConnection CONNECT tunnel setup failed; resetting stream and rethrowing",
                tunnelEx);
            try
            {
                await ResetStreamAsync(streamId, Http2ErrorCode.Cancel, CancellationToken.None);
            }
            catch (Exception resetEx)
            {
                ProxyDiagnostics.ReportCaught(logger,
                    "Http2OriginConnection best-effort RST_STREAM after tunnel failure", resetEx);
            }

            leaseOwned = false;
            ReleaseTunnelBookkeeping(streamId, pending, gate);
            throw;
        }
        }
        finally
        {
            if (leaseOwned)
                ReleaseLease();
        }
    }

    private async Task WriteTunnelDataAsync(int streamId, ReadOnlyMemory<byte> payload, bool endStream,
        CancellationToken cancellationToken)
    {
        if (!streams.ContainsKey(streamId) && !endStream)
            throw new IOException($"HTTP/2 tunnel stream {streamId} is no longer open.");

        await EnqueueDataWithFlowAsync(streamId, payload, endStream, cancellationToken);
    }

    private Task ResetStreamAsync(int streamId, Http2ErrorCode errorCode,
        CancellationToken cancellationToken) // NOSONAR S1172 -- signature matches tunnel reset delegate
    {
        Http2Helper.EnqueueRstStream(Writer, streamId, errorCode);
        return Task.CompletedTask;
    }

    private void ReleaseTunnelBookkeeping(int streamId, PendingStream pending, SemaphoreSlim gate)
    {
        if (TryUnregisterStream(streamId, out var removed) && removed != null)
        {
            removed.TunnelDataChannel?.Writer.TryComplete();
            removed.Dispose();
        }
        else
        {
            pending.Dispose();
        }

        sendFlow.RemoveStream(streamId);
        ReleaseLease();
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // connection already torn down
        }
        catch (SemaphoreFullException)
        {
            // already released
        }
    }

    private Task SendSettingsAckAsync(CancellationToken cancellationToken) // NOSONAR S1172 -- kept for await-site symmetry with other frame helpers
    {
        Http2Helper.EnqueueSettingsAck(Writer);
        return Task.CompletedTask;
    }

    private Task SendPingAckAsync(byte[] payload, CancellationToken cancellationToken) // NOSONAR S1172 -- kept for await-site symmetry with other frame helpers
    {
        Http2Helper.EnqueuePingAck(Writer, payload);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Re-grants flow-control credit for DATA frame on-wire payload (RFC 7540 §6.9). Batched at
    ///     <see cref="Http2Helper.ReceiveCreditBatchThreshold" /> (half of the 768 KiB stream window),
    ///     matching <see cref="Http2Helper" /> so credit is not drip-fed under the write lock per frame.
    /// </summary>
    private Task GrantReceiveCreditAsync(int streamId, int bytes, bool forceFlush, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        CancellationToken cancellationToken) // NOSONAR S1172 -- enqueue is sync; token reserved for future async flush
    {
        if (bytes <= 0 && !forceFlush)
            return Task.CompletedTask;

        if (bytes > 0)
        {
            pendingConnectionReceiveCredit += bytes;
            if (pendingStreamReceiveCredit.TryGetValue(streamId, out var streamPending))
                pendingStreamReceiveCredit[streamId] = streamPending + bytes;
            else
                pendingStreamReceiveCredit[streamId] = bytes;
        }

        var flushConnection = forceFlush
                              || pendingConnectionReceiveCredit >= Http2Helper.ReceiveCreditBatchThreshold;
        var flushStream = forceFlush
                          || (pendingStreamReceiveCredit.TryGetValue(streamId, out var streamCredit)
                              && streamCredit >= Http2Helper.ReceiveCreditBatchThreshold);

        if (!flushConnection && !flushStream)
            return Task.CompletedTask;

        var connectionBytes = flushConnection ? pendingConnectionReceiveCredit : 0;
        var streamBytes = 0;
        if (flushStream && pendingStreamReceiveCredit.TryGetValue(streamId, out streamBytes))
            pendingStreamReceiveCredit.Remove(streamId);
        if (flushConnection)
            pendingConnectionReceiveCredit = 0;

        if (connectionBytes <= 0 && streamBytes <= 0)
            return Task.CompletedTask;

        var streamStillTracked = streamBytes > 0 && streams.ContainsKey(streamId);
        if (connectionBytes > 0)
            Http2Helper.EnqueueWindowUpdate(Writer, 0, connectionBytes);
        if (streamStillTracked)
            Http2Helper.EnqueueWindowUpdate(Writer, streamId, streamBytes);
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var frameHeaderBuffer = new byte[9];
        var isFirstFrame = true;
        var intake = new Http2FrameIntake(stream);

        int? headerBlockStreamId = null;
        var headerBlockBuffer = new MemoryStream();
        var headerBlockEndStream = false;

        try
        {
            while (true)
            {
                if (!await intake.ReadExactAsync(frameHeaderBuffer, 0, 9, cancellationToken)
                        .ConfigureAwait(false))
                {
                    Fail(new IOException("The origin h2 connection was closed."));
                    return;
                }

                var length = (frameHeaderBuffer[0] << 16) + (frameHeaderBuffer[1] << 8) + frameHeaderBuffer[2];
                var type = (Http2FrameType)frameHeaderBuffer[3];
                var flags = (Http2FrameFlag)frameHeaderBuffer[4];
                var streamId = ((frameHeaderBuffer[5] & 0x7f) << 24) + (frameHeaderBuffer[6] << 16) +
                               (frameHeaderBuffer[7] << 8) + frameHeaderBuffer[8];

                if (isFirstFrame)
                {
                    isFirstFrame = false;
                    if (type != Http2FrameType.Settings)
                    {
                        Fail(new IOException(
                            $"HTTP/2 protocol error: expected a SETTINGS frame first from the origin, got {type}."));
                        return;
                    }
                }

                // RFC 7540 §4.2: we never advertised a SETTINGS_MAX_FRAME_SIZE above the default 16,384,
                // so any frame larger than that is a protocol violation. Reject before buffering.
                if (length > SafeMaxFrameSize)
                {
                    Fail(new IOException(
                        $"HTTP/2 protocol error: origin sent a {length}-byte frame payload, exceeding the {SafeMaxFrameSize}-byte limit this proxy advertised."));
                    return;
                }

                if (length > 0 &&
                    !await intake.EnsureAsync(length, cancellationToken).ConfigureAwait(false))
                {
                    Fail(new IOException("The origin h2 connection was closed mid-frame."));
                    return;
                }

                var payloadSpan = length == 0
                    ? ReadOnlySpan<byte>.Empty
                    : intake.ActiveSpan.Slice(0, length);

                switch (type)
                {
                    case Http2FrameType.Settings:
                        if (streamId != 0)
                        {
                            Fail(new IOException("HTTP/2 protocol error: SETTINGS frame must have stream ID 0."));
                            return;
                        }

                        if ((flags & Http2FrameFlag.Ack) != 0)
                        {
                            if (length != 0)
                            {
                                Fail(new IOException("HTTP/2 protocol error: SETTINGS ACK must have empty payload."));
                                return;
                            }

                            break;
                        }

                        if (length % 6 != 0)
                        {
                            Fail(new IOException(
                                $"HTTP/2 protocol error: SETTINGS payload must be a multiple of 6 bytes, got {length}."));
                            return;
                        }

                        ApplySettings(payloadSpan);
                        intake.Advance(length);
                        if (faulted) return;
                        await SendSettingsAckAsync(cancellationToken).ConfigureAwait(false);
                        if (!initialSettingsReceived.Task.IsCompleted)
                        {
                            // Consolidated with Http2Helper's client-facing admission check: both now
                            // derive from the same proxy-owned ProxyResourceLimits.MaxConcurrentStreamsPerConnection
                            // rather than each hard-coding its own default, so the two mechanisms cannot
                            // silently drift apart. This gate additionally respects a lower
                            // origin-advertised value (never a higher one) since it self-throttles the
                            // proxy's own outbound usage of this shared origin connection - unlike
                            // Http2Helper's check, there is no wire value here to keep in sync with.
                            var proxyCap = resourceLimits.MaxConcurrentStreamsPerConnection;
                            var cap = originSettings.MaxConcurrentStreams == int.MaxValue
                                ? proxyCap
                                : Math.Max(1, Math.Min(originSettings.MaxConcurrentStreams, proxyCap));
                            concurrencyGateCapacity = cap;
                            concurrencyGate = new SemaphoreSlim(cap, cap);
                            initialSettingsReceived.TrySetResult(true);
                        }

                        continue;

                    case Http2FrameType.WindowUpdate:
                        if (length != 4)
                        {
                            Fail(new IOException("HTTP/2 protocol error: WINDOW_UPDATE frame must be exactly 4 bytes."));
                            return;
                        }

                        {
                            var increment = (int)(BinaryPrimitives.ReadUInt32BigEndian(payloadSpan) & 0x7fffffff);
                            intake.Advance(length);
                            if (increment == 0)
                            {
                                // RFC 7540 §6.9.1: a zero-increment WINDOW_UPDATE is a connection error PROTOCOL_ERROR.
                                Fail(new IOException("HTTP/2 protocol error: WINDOW_UPDATE increment must not be zero."));
                                return;
                            }

                            sendFlow.OnWindowUpdate(streamId, increment);
                        }

                        continue;

                    case Http2FrameType.Headers:
                        {
                            headerBlockStreamId = streamId;
                            headerBlockEndStream = (flags & Http2FrameFlag.EndStream) != 0;
                            var data = StripHeadersFraming(payloadSpan, flags);
                            if (data.Length > MaxHeaderBlockBytes)
                            {
                                Fail(new IOException(
                                    $"HTTP/2 protocol error: origin header block exceeded {MaxHeaderBlockBytes} bytes."));
                                return;
                            }

                            if ((flags & Http2FrameFlag.EndHeaders) != 0)
                            {
                                // Common path: single END_HEADERS frame — decode in place (SHH ActiveSpan).
                                ProcessHeaderBlock(streamId, data, headerBlockEndStream);
                                headerBlockStreamId = null;
                                intake.Advance(length);
                            }
                            else
                            {
                                // CONTINUATION will follow — stage only in that case.
                                headerBlockBuffer.SetLength(0);
                                headerBlockBuffer.Write(data);
                                intake.Advance(length);
                            }

                            continue;
                        }

                    case Http2FrameType.Continuation:
                        if (headerBlockStreamId == null)
                        {
                            Fail(new IOException("HTTP/2 protocol error: received CONTINUATION frame outside a header block."));
                            return;
                        }

                        if (headerBlockStreamId != streamId)
                        {
                            Fail(new IOException(
                                $"HTTP/2 protocol error: CONTINUATION frame stream ID {streamId} does not match open header block stream {headerBlockStreamId}."));
                            return;
                        }

                        if (headerBlockBuffer.Length + length > MaxHeaderBlockBytes)
                        {
                            Fail(new IOException(
                                $"HTTP/2 protocol error: origin CONTINUATION block exceeded {MaxHeaderBlockBytes} bytes."));
                            return;
                        }

                        headerBlockBuffer.Write(payloadSpan);
                        intake.Advance(length);
                        if ((flags & Http2FrameFlag.EndHeaders) != 0)
                        {
                            ProcessHeaderBlock(streamId,
                                headerBlockBuffer.GetBuffer().AsSpan(0, (int)headerBlockBuffer.Length),
                                headerBlockEndStream);
                            headerBlockStreamId = null;
                        }

                        continue;

                    case Http2FrameType.Data:
                        {
                            byte[]? rented = null;
                            try
                            {
                                if (streams.TryGetValue(streamId, out var pendingData))
                                {
                                    if (pendingData.IsTunnel)
                                    {
                                        var data = StripDataFraming(payloadSpan, flags);
                                        intake.Advance(length);
                                        if (data.Length > 0)
                                        {
                                            var tunnelDataChannel = pendingData.TunnelDataChannel ??
                                                throw new InvalidOperationException("A tunnel stream has no data channel.");

                                            // Prefer TryWrite so a full channel does not park the connection
                                            // read loop in front of other streams' HEADERS.
                                            if (!tunnelDataChannel.Writer.TryWrite(data))
                                            {
                                                var writeVt = tunnelDataChannel.Writer.WriteAsync(data, cancellationToken);
                                                if (writeVt.IsCompletedSuccessfully)
                                                {
                                                    writeVt.GetAwaiter().GetResult();
                                                }
                                                else
                                                {
                                                    try
                                                    {
                                                        await writeVt.ConfigureAwait(false);
                                                    }
                                                    catch (ChannelClosedException)
                                                    {
                                                        // Tunnel already closed; ignore stale DATA.
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else if (pendingData.InlineBody != null)
                                    {
                                        // Known tiny CL: copy straight into the pre-sized buffer — no Pipe /
                                        // ArrayPool Gen0 on the probe GET path (H1→H2 / H3→H2 Mac residual).
                                        var bodyData = StripDataFramingSpan(payloadSpan, flags);
                                        intake.Advance(length);
                                        if (!bodyData.IsEmpty)
                                            pendingData.TryWriteInline(bodyData);
                                    }
                                    else
                                    {
                                        // Copy out of intake before Advance so BodyPipe may hold the memory
                                        // if a rare backpressured write yields.
                                        rented = ArrayPool<byte>.Shared.Rent(length);
                                        payloadSpan.CopyTo(rented);
                                        intake.Advance(length);
                                        var bodyData = StripDataFramingMemory(rented, length, flags);
                                        if (!bodyData.IsEmpty)
                                        {
                                            try
                                            {
                                                var writeVt = pendingData.EnsureBodyPipe()
                                                    .WriteAsync(bodyData, cancellationToken);
                                                if (writeVt.IsCompletedSuccessfully)
                                                {
                                                    writeVt.GetAwaiter().GetResult();
                                                }
                                                else
                                                {
                                                    // Preserve per-stream DATA order; unlimited pipes complete sync.
                                                    await writeVt.ConfigureAwait(false);
                                                }
                                            }
                                            catch (BodySizeLimitExceededException)
                                            {
                                                // WriteAsync already faulted the pipe writer; CopyToAsync in SendAsync will
                                                // propagate the exception. Continue the read loop for other streams.
                                            }
                                            catch (InvalidOperationException)
                                            {
                                                // Writer already completed (cancelled or stream failed); ignore stale frames.
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    intake.Advance(length);
                                }
                            }
                            finally
                            {
                                if (rented != null)
                                    ArrayPool<byte>.Shared.Return(rented);
                            }

                            if ((flags & Http2FrameFlag.EndStream) != 0)
                            {
                                // Tiny-GET hot path: END_STREAM closes the stream — skip stream
                                // WINDOW_UPDATE and do not force-flush connection credit (was one
                                // WINDOW_UPDATE pair per ~56 B response). Matches Http2Helper
                                // compressed-relay DATA END_STREAM (h2c→h2c gain).
                                if (length > 0)
                                    pendingConnectionReceiveCredit += length;
                                pendingStreamReceiveCredit.Remove(streamId);
                                if (pendingConnectionReceiveCredit >= Http2Helper.ReceiveCreditBatchThreshold)
                                {
                                    await GrantReceiveCreditAsync(0, 0, forceFlush: true, cancellationToken)
                                        .ConfigureAwait(false);
                                }

                                CompleteStream(streamId);
                            }
                            else
                            {
                                await GrantReceiveCreditAsync(streamId, length, forceFlush: false,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            continue;
                        }

                    case Http2FrameType.RstStream:
                        intake.Advance(length);
                        FailStream(streamId,
                            new IOException($"HTTP/2 stream {streamId} was reset by the origin (RST_STREAM)."));
                        continue;

                    case Http2FrameType.GoAway:
                        if (streamId != 0)
                        {
                            Fail(new IOException("HTTP/2 protocol error: GOAWAY frame must have stream ID 0."));
                            return;
                        }

                        if (length >= 8)
                        {
                            var lastId = (int)(BinaryPrimitives.ReadUInt32BigEndian(payloadSpan.Slice(0, 4)) & 0x7fffffff);
                            var errorCode = (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(payloadSpan.Slice(4, 4));
                            intake.Advance(length);
                            goAwayLastStreamId = lastId;
                            goingAway = true;
                            foreach (var kvp in streams)
                            {
                                if (kvp.Key > lastId)
                                {
                                    var goAwayEx = new Http2OriginGoAwayException(
                                        $"The origin sent GOAWAY ({errorCode}) before stream {kvp.Key} was processed; it is safe to retry.");
                                    FailPending(kvp.Value, goAwayEx);
                                }
                            }
                        }
                        else
                        {
                            intake.Advance(length);
                        }

                        continue;

                    case Http2FrameType.Ping:
                        if ((flags & Http2FrameFlag.Ack) == 0)
                        {
                            var pingPayload = payloadSpan.ToArray();
                            intake.Advance(length);
                            await SendPingAckAsync(pingPayload, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            intake.Advance(length);
                        }

                        continue;

                    case Http2FrameType.PushPromise:
                        // This bridge always advertises SETTINGS_ENABLE_PUSH=0 - a PUSH_PROMISE anyway is a
                        // direct protocol violation (RFC 7540 §6.6); tear the connection down rather than
                        // decode a header block that would desync every subsequent HPACK block.
                        Fail(new IOException("HTTP/2 protocol error: unexpected PUSH_PROMISE from the origin."));
                        return;

                    default:
                        // PRIORITY and any unknown/reserved frame types are simply ignored.
                        intake.Advance(length);
                        continue;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void ApplySettings(byte[] payload) => ApplySettings(payload.AsSpan()); // NOSONAR S1144 -- reflection test seam

    private void ApplySettings(ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i + 6 <= payload.Length; i += 6)
        {
            var identifier = (payload[i] << 8) | payload[i + 1];
            var value = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(i + 2, 4));

            if (identifier == (int)Http2SettingsId.HeaderTableSize)
                originSettings.UpdateHeaderTableSize(value);
            else if (identifier == (int)Http2SettingsId.MaxFrameSize)
            {
                // RFC 7540 §6.5.2: values outside [16384, 16777215] are a connection-level PROTOCOL_ERROR.
                if (value < 16384 || value > 16777215)
                {
                    Fail(new IOException(
                        $"HTTP/2 protocol error: SETTINGS_MAX_FRAME_SIZE value {value} is out of range [16384, 16777215]."));
                    return;
                }

                originSettings.MaxFrameSize = value;
            }
            else if (identifier == (int)Http2SettingsId.InitialWindowSize)
            {
                // RFC 7540 §6.5.2: values above 2^31-1 are a connection-level FLOW_CONTROL_ERROR.
                // A wire value > 2^31-1 wraps to a negative int when cast; checking < 0 catches that.
                if (value < 0)
                {
                    Fail(new IOException(
                        $"HTTP/2 protocol error: SETTINGS_INITIAL_WINDOW_SIZE value exceeds the maximum of 2,147,483,647."));
                    return;
                }

                sendFlow.OnInitialWindowSizeChanged(value);
            }
            else if (identifier == (int)Http2SettingsId.MaxConcurrentStreams)
                originSettings.MaxConcurrentStreams = value;
            else if (identifier == (int)Http2SettingsId.EnableConnectProtocol)
                ApplyEnableConnectProtocolSetting(value);
        }
    }

    /// <summary>RFC 8441 §3: value MUST be 0 or 1; a sender MUST NOT send 0 after previously sending 1.</summary>
    private void ApplyEnableConnectProtocolSetting(int value)
    {
        var error = ValidateEnableConnectProtocolSetting(value, originSettings.EnableConnectProtocolEverSet);
        if (error != null)
        {
            Fail(new IOException(error));
            return;
        }

        originSettings.EnableConnectProtocol = value == 1;
        if (value == 1) originSettings.EnableConnectProtocolEverSet = true;
    }

    /// <summary>
    ///     Returns a protocol-error message when <paramref name="value"/> is illegal for
    ///     <c>SETTINGS_ENABLE_CONNECT_PROTOCOL</c>; otherwise null.
    /// </summary>
    internal static string? ValidateEnableConnectProtocolSetting(int value, bool previouslyEnabled)
    {
        if (value is not (0 or 1))
            return $"HTTP/2 protocol error: SETTINGS_ENABLE_CONNECT_PROTOCOL value {value} is not 0 or 1.";

        if (value == 0 && previouslyEnabled)
            return "HTTP/2 protocol error: SETTINGS_ENABLE_CONNECT_PROTOCOL must not be downgraded from 1 to 0.";

        return null;
    }

    /// <summary>Strips HEADERS PADDED/PRIORITY framing; returns a slice into <paramref name="payload"/> (no copy).</summary>
    private static ReadOnlySpan<byte> StripHeadersFraming(ReadOnlySpan<byte> payload, Http2FrameFlag flags)
    {
        var offset = 0;
        var end = payload.Length;

        if ((flags & Http2FrameFlag.Padded) != 0 && payload.Length > 0)
        {
            var padLength = payload[0];
            offset = 1;
            end = Math.Max(offset, payload.Length - padLength);
        }

        if ((flags & Http2FrameFlag.Priority) != 0 && end - offset >= 5) offset += 5;

        if (offset >= end) return ReadOnlySpan<byte>.Empty;

        return payload.Slice(offset, end - offset);
    }

    /// <summary>Byte[] overload kept for unit tests via reflection; allocates a copy of the header block.</summary>
    private static byte[] StripHeadersFraming(byte[] payload, Http2FrameFlag flags) // NOSONAR S1144 -- reflection test seam
        => StripHeadersFraming(payload.AsSpan(), flags).ToArray();

    /// <summary>Strips DATA PADDED framing without allocating (inline-body hot path).</summary>
    private static ReadOnlySpan<byte> StripDataFramingSpan(ReadOnlySpan<byte> payload, Http2FrameFlag flags)
    {
        if ((flags & Http2FrameFlag.Padded) == 0 || payload.Length == 0)
            return payload;

        var padLength = payload[0];
        var end = Math.Max(1, payload.Length - padLength);
        return payload.Slice(1, end - 1);
    }

    /// <summary>Strips DATA PADDED framing into a new array (tunnel channel ownership).</summary>
    private static byte[] StripDataFraming(ReadOnlySpan<byte> payload, Http2FrameFlag flags)
    {
        if ((flags & Http2FrameFlag.Padded) == 0 || payload.Length == 0)
            return payload.ToArray();

        var padLength = payload[0];
        var end = Math.Max(1, payload.Length - padLength);
        return payload.Slice(1, end - 1).ToArray();
    }

    /// <summary>
    ///     Byte[] overload kept for unit tests via reflection. Unpadded / empty-padded returns the same
    ///     instance (historical contract); padded otherwise allocates.
    /// </summary>
    private static byte[] StripDataFraming(byte[] payload, Http2FrameFlag flags) // NOSONAR S1144 -- reflection test seam
    {
        if ((flags & Http2FrameFlag.Padded) == 0 || payload.Length == 0) return payload;

        var padLength = payload[0];
        var end = Math.Max(1, payload.Length - padLength);
        return payload.AsSpan(1, end - 1).ToArray();
    }

    /// <summary>Strips DATA padding while retaining ownership of a pooled payload buffer.</summary>
    private static ReadOnlyMemory<byte> StripDataFramingMemory(
        byte[] payload, int payloadLength, Http2FrameFlag flags)
    {
        if ((flags & Http2FrameFlag.Padded) == 0 || payloadLength == 0)
            return payload.AsMemory(0, payloadLength);

        var padLength = payload[0];
        var end = Math.Max(1, payloadLength - padLength);
        return payload.AsMemory(1, end - 1);
    }

    private void ProcessHeaderBlock(int streamId, ReadOnlySpan<byte> compressed, bool endStream) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        // Decode into the Response's own HeaderCollection (or a temporary for 1xx) so we do not
        // allocate a second HeaderCollection and copy every field — H3→H2 tiny-GET pays this
        // once per request on the origin ReadLoop. Reuse the connection's HeaderCollectorListener
        // (no per-HEADERS lambda/listener Gen0 on the shared ReadLoop).
        headerCollector.Begin();

        try
        {
            decoder ??= new Decoder(8192, 4096);
            decoder.Decode(compressed, headerCollector);
            decoder.EndHeaderBlock();
        }
        catch (Exception ex)
        {
            Fail(new ProxyHttpException("Failed to decode HTTP/2 headers from the origin.", ex, null));
            return;
        }

        if (!streams.TryGetValue(streamId, out var pending)) return;

        var status = headerCollector.Status;
        var buildingResponse = headerCollector.BuildingResponse;
        var interimHeaders = headerCollector.InterimHeaders;

        if (status.Length > 0)
        {
            var statusCode = TryParseAsciiStatusCode(status.Span, out var parsed) ? parsed : 502;

            if (statusCode is >= 100 and <= 199)
            {
                // Queue this interim response for relay when a caller registered on1xx (InterimChannel
                // present). Passthrough lite drops 1xx — probe/no-intercept paths do not forward them.
                pending.InterimChannel?.Writer.TryWrite((statusCode, interimHeaders ?? new HeaderCollection()));
                return;
            }

            if (pending.Response == null)
            {
                var response = buildingResponse ?? new Response
                {
                    StatusCode = statusCode,
                    StatusDescription = string.Empty,
                    HttpVersion = HttpHeader.Version11,
                    // HPACK field names are lowercase (RFC 9113); QPACK EncodeResponse can skip ToLower.
                    HeaderNamesAreHttp2Normalized = true
                };
                response.StatusCode = statusCode;
                pending.Response = response;
                // Signal that no more interim responses will arrive; unblocks SendAsync's interim drain loop.
                pending.InterimChannel?.Writer.TryComplete();

                // Probe-shaped tiny GET (known CL ≤ 8 KiB): buffer DATA into InlineBody and delay
                // HeadersReceived until END_STREAM so SendAsync skips Pipe + second alloc.
                // Unknown / large CL: signal headers immediately (streaming BodyPipe path).
                var delayForInline = pending.TryPrepareInlineBody(response) && !endStream
                                     && response.ContentLength > 0
                                     && statusCode is not (204 or 304);
                if (!delayForInline)
                    pending.HeadersReceived.TrySetResult(true);
            }
        }
        else
        {
            // A HEADERS block without a ":status" pseudo-header, following the main response headers, is a
            // trailer block (RFC 7540 §8.1.2.1 / RFC 7230 §4.1.2).
            // RFC 9113 §8.5: trailers on an established extended CONNECT tunnel are a protocol error —
            // complete the inbound side so the tunnel reader observes EOF rather than hanging.
            if (pending.IsTunnel)
            {
                pending.TunnelDataChannel?.Writer.TryComplete(
                    new IOException("HTTP/2 protocol error: HEADERS received on an established extended CONNECT tunnel."));
            }
            else
            {
                pending.TrailingHeaders ??= interimHeaders ?? new HeaderCollection();
                if (interimHeaders != null && !ReferenceEquals(pending.TrailingHeaders, interimHeaders))
                {
                    foreach (var header in interimHeaders)
                        pending.TrailingHeaders.AddHeader(header);
                }
            }
        }

        if (endStream) CompleteStream(streamId);
    }

    private static bool TryParseAsciiStatusCode(ReadOnlySpan<byte> digits, out int statusCode)
    {
        statusCode = 0;
        if (digits.Length is 0 or > 3) return false;
        for (var i = 0; i < digits.Length; i++)
        {
            var c = digits[i];
            if (c is < (byte)'0' or > (byte)'9') return false;
            statusCode = statusCode * 10 + (c - (byte)'0');
        }

        return true;
    }

    private void CompleteStream(int streamId)
    {
        if (!streams.TryGetValue(streamId, out var pending)) return;

        if (pending.IsTunnel)
        {
            // Keep the stream registered so the tunnel can still write outbound DATA; just half-close inbound.
            pending.TunnelDataChannel?.Writer.TryComplete();
            return;
        }

        // Use TryRemove so subsequent DATA frames for this stream-id are ignored in the read loop.
        if (!TryUnregisterStream(streamId, out pending) || pending == null) return;
        if (pending.InlineBody != null)
            pending.HeadersReceived.TrySetResult(true);
        else
            pending.BodyPipeOrNull?.CompleteWriter();
        TryDisposeIfRetiredAndIdle();
    }

    private void FailStream(int streamId, Exception ex)
    {
        // Use TryRemove so subsequent DATA frames for this stream are ignored in the read loop.
        if (TryUnregisterStream(streamId, out var pending) && pending != null)
            FailPending(pending, ex);
    }

    private static void FailPending(PendingStream pending, Exception ex)
    {
        pending.BodyPipeOrNull?.CompleteWriter(ex);
        pending.InterimChannel?.Writer.TryComplete(ex);
        pending.TunnelDataChannel?.Writer.TryComplete(ex);
        pending.HeadersReceived.TrySetException(ex);
    }

    /// <param name="ex">The failure to fault every in-flight/future stream with.</param>
    /// <param name="report">
    ///     Whether this is a genuine, previously-unobserved origin failure that should be surfaced via
    ///     the logging gateway (I/O errors, protocol violations, HPACK decode failures encountered by the
    ///     read loop). Pass <c>false</c> for an expected, caller-initiated teardown (see <see cref="Dispose" />) -
    ///     that is not itself a failure worth reporting; any request that was genuinely in flight when it happened
    ///     still surfaces its own failure through the normal per-request exception handling in
    ///     <c>Http11ToHttp2BridgeHandler</c> when its <see cref="PendingStream.Completion" /> task faults.
    /// </param>
    private void Fail(Exception ex, bool report = true)
    {
        if (faulted) return;
        faulted = true;

        if (report)
        {
            // Peer idle/GOAWAY/EOF arrives as a plain IOException and is expected under normal browsing.
            // This class also encodes HTTP/2 protocol violations as IOException("HTTP/2 protocol error:…")
            // — those must stay Error. Explicit ProxyHttpException / other unexpected types likewise.
            var wrapped = ex is ProxyHttpException proxyEx
                ? proxyEx
                : new ProxyHttpException("The HTTP/1.1-to-HTTP/2 origin bridge connection failed.", ex, null);

            if (IsHttp2ProtocolViolation(ex))
                ProxyDiagnostics.ReportUnexpected(logger,
                    "The HTTP/1.1-to-HTTP/2 origin bridge connection failed.", wrapped);
            else
                ProxyDiagnostics.ReportException(logger,
                    "The HTTP/1.1-to-HTTP/2 origin bridge connection failed.", wrapped);
        }

        foreach (var kvp in streams)
            FailPending(kvp.Value, ex);

        initialSettingsReceived.TrySetException(ex);
    }

    private static bool IsHttp2ProtocolViolation(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current.Message.StartsWith("HTTP/2 protocol error", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Closes this origin connection. Any streams still awaiting a response are failed. This is a normal,
    ///     expected part of the bridge's connection lifecycle (e.g. the HTTP/1.1 client connection ended, the
    ///     bridge is replacing this connection after a GOAWAY, or the user asked to discard it via
    ///     <c>CloseServerConnection</c>) and must not, by itself, be reported through the logging gateway
    ///     - see <see cref="Fail" />.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        Fail(new ObjectDisposedException(nameof(Http2OriginConnection)), false);
        connectionCts.Cancel();
        var writer = frameWriter;
        frameWriter = null;
        if (writer != null)
            _ = writer.DisposeAsync().AsTask(); // fire-and-forget; sync Dispose cannot await
        connectionCts.Dispose();
        writeLock.Dispose();
        concurrencyGate?.Dispose();
        connection.Dispose();
    }

    /// <summary>
    ///     Prefer a non-allocating uncontended take. WaitAsync alone allocates a Task waiter even when
    ///     the lock is free; tiny-GET H1→H2 hits this on every stream open.
    /// </summary>
    private ValueTask WaitWriteLockAsync(CancellationToken cancellationToken)
    {
        // Explicit None: zero-timeout poll must not observe cancellation (would throw before WaitAsync).
        if (writeLock.Wait(0, CancellationToken.None)) // NOSONAR S6966 -- intentional sync try-take before WaitAsync
            return default;

        // Brief spin before WaitAsync: under SoftGrow=SoftPick a single TLS origin conn sees
        // heavy writeLock convoy at c=64; WaitAsync alone allocates Task nodes per miss.
        var spinner = new SpinWait();
        while (!spinner.NextSpinWillYield)
        {
            spinner.SpinOnce();
            if (writeLock.Wait(0, CancellationToken.None)) // NOSONAR S6966 -- same sync try-take
                return default;
        }

        return new ValueTask(writeLock.WaitAsync(cancellationToken));
    }

    private async ValueTask EnqueueDataWithFlowAsync(int streamId, ReadOnlyMemory<byte> data, bool endStream,
        CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            if (endStream)
                Http2Helper.EnqueueDataFrames(Writer, streamId, data, endStream: true, SafeMaxFrameSize);
            return;
        }

        var pos = 0;
        while (pos < data.Length)
        {
            var frameLength = Math.Min(SafeMaxFrameSize, data.Length - pos);
            await sendFlow.ReserveAsync(streamId, frameLength, cancellationToken);
            var isLast = pos + frameLength >= data.Length;
            Http2Helper.EnqueueDataFrames(Writer, streamId, data.Slice(pos, frameLength),
                endStream && isLast, SafeMaxFrameSize);
            pos += frameLength;
        }
    }

    /// <summary>
    ///     Read-only view of the origin socket. Writes throw so a missed conversion cannot interleave
    ///     bytes with <see cref="Http2FrameWriter"/>.
    /// </summary>
    private sealed class WriteForbiddenStream : Stream
    {
        private readonly Stream inner;

        public WriteForbiddenStream(Stream inner) => this.inner = inner;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => throw Create();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw Create();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw Create();

        private static InvalidOperationException Create() => new(
            "Origin socket writes must go through Http2FrameWriter; mixed writes corrupt the HTTP/2 byte stream.");
    }

    private sealed class PendingStream : IDisposable
    {
        /// <summary>
        ///     Known Content-Length bodies ≤ 8 KiB are filled here on the ReadLoop (no <see cref="BoundedBodyPipe" />).
        /// </summary>
        internal const int InlineBodyThresholdBytes = 8 * 1024;

        private BoundedBodyPipe? bodyPipe;
        private readonly long maxBodyBytes;
        private byte[]? inlineBody;
        private int inlineWritten;

        internal BoundedBodyPipe? BodyPipeOrNull => bodyPipe;

        /// <summary>Pre-sized body for known tiny Content-Length; null when using <see cref="EnsureBodyPipe"/>.</summary>
        internal byte[]? InlineBody => inlineBody;

        internal readonly bool IsTunnel;

        /// <summary>
        ///     Queue of 1xx interim responses written by <see cref="ProcessHeaderBlock" /> as they arrive from
        ///     the origin, and drained by <see cref="SendAsync" />'s <c>on1xx</c> callback relay loop.
        ///     Null on passthrough lite (<c>on1xx</c> null) — those streams wait on
        ///     <see cref="HeadersReceived" /> only. The writer is completed (without exception) when the
        ///     final response headers are processed, or completed with an exception on stream/connection failure.
        /// </summary>
        internal readonly Channel<(int StatusCode, HeaderCollection Headers)>? InterimChannel;

        /// <summary>
        ///     Completed when the final (non-1xx) response HEADERS arrive — or, for known tiny Content-Length
        ///     bodies, when the body has been fully buffered into <see cref="InlineBody" /> (END_STREAM).
        ///     Used by <see cref="OpenTunnelAsync" /> and by <see cref="SendAsync" /> when <c>on1xx</c> is null.
        /// </summary>
        internal readonly TaskCompletionSource<bool> HeadersReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Inbound DATA payloads for an RFC 8441 tunnel. Null for ordinary request/response streams,
        ///     which use <see cref="EnsureBodyPipe" /> or <see cref="InlineBody" /> instead.
        /// </summary>
        internal readonly Channel<byte[]>? TunnelDataChannel;

        internal Response? Response;
        internal HeaderCollection? TrailingHeaders;

        internal PendingStream(long maxBodyBytes)
            : this(maxBodyBytes, createInterimChannel: true)
        {
        }

        /// <param name="maxBodyBytes">Max buffered body bytes for <see cref="EnsureBodyPipe" />.</param>
        /// <param name="createInterimChannel">
        ///     When <see langword="true"/>, allocate the 1xx relay channel. Tests and interception paths
        ///     that expect 1xx use this; <see cref="SendAsync" /> passes <see langword="false"/> when
        ///     <c>on1xx</c> is null.
        /// </param>
        internal PendingStream(long maxBodyBytes, bool createInterimChannel)
        {
            IsTunnel = false;
            this.maxBodyBytes = maxBodyBytes;
            // BodyPipe is lazy: probe tiny-GET uses InlineBody; streaming / unknown CL creates on demand.
            if (createInterimChannel)
            {
                InterimChannel = Channel.CreateUnbounded<(int, HeaderCollection)>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            }
        }

        private PendingStream(bool isTunnel)
        {
            IsTunnel = isTunnel;
            maxBodyBytes = 0;
            // Tunnel streams never buffer a finite HTTP body; BodyPipe unused.
            TunnelDataChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        internal static PendingStream CreateTunnel() => new(true);

        /// <summary>
        ///     When Content-Length is known and ≤ <see cref="InlineBodyThresholdBytes"/>, allocate a
        ///     single body buffer for the ReadLoop. Returns true when inline mode is active.
        /// </summary>
        internal bool TryPrepareInlineBody(Response response)
        {
            if (IsTunnel || response.ContentLength is < 0 or > InlineBodyThresholdBytes)
                return false;

            var expected = (int)response.ContentLength;
            inlineBody = expected == 0 ? Array.Empty<byte>() : new byte[expected];
            inlineWritten = 0;
            return true;
        }

        internal void TryWriteInline(ReadOnlySpan<byte> data)
        {
            if (inlineBody == null || data.IsEmpty) return;
            var space = inlineBody.Length - inlineWritten;
            if (space <= 0) return;
            var toCopy = Math.Min(space, data.Length);
            data.Slice(0, toCopy).CopyTo(inlineBody.AsSpan(inlineWritten));
            inlineWritten += toCopy;
            // Full buffer: unblock SendAsync even if END_STREAM is slightly delayed.
            if (inlineWritten >= inlineBody.Length)
                HeadersReceived.TrySetResult(true);
        }

        internal byte[] TakeInlineBody()
        {
            var body = inlineBody ?? Array.Empty<byte>();
            if (inlineWritten > 0 && inlineWritten < body.Length)
                Array.Resize(ref body, inlineWritten);
            else if (inlineWritten == 0 && body.Length > 0)
                body = Array.Empty<byte>();
            inlineBody = null;
            return body;
        }

        internal BoundedBodyPipe EnsureBodyPipe() =>
            bodyPipe ??= new BoundedBodyPipe(maxBodyBytes);

        public void Dispose()
        {
            bodyPipe?.Dispose();
            // Release any reader blocking on WaitToReadAsync if Dispose is called without a prior Complete.
            InterimChannel?.Writer.TryComplete();
            TunnelDataChannel?.Writer.TryComplete();
            HeadersReceived.TrySetCanceled();
        }
    }

    private sealed class HeaderCollectorListener : IHeaderListener
    {
        internal ByteString Status;
        internal Response? BuildingResponse;
        internal HeaderCollection? InterimHeaders;

        internal void Begin()
        {
            Status = default;
            BuildingResponse = null;
            InterimHeaders = null;
        }

        public void AddHeader(ByteString name, ByteString value, bool sensitive)
        {
            if (name.Length > 0 && name.Span[0] == (byte)':')
            {
                if (name.Equals(StaticTable.KnownHeaderStatus)) Status = value;
                return;
            }

            if (Status.Length == 0)
            {
                InterimHeaders ??= new HeaderCollection();
                InterimHeaders.AddHeader(new HttpHeader(name, value));
                return;
            }

            if (InterimHeaders != null)
            {
                InterimHeaders.AddHeader(new HttpHeader(name, value));
                return;
            }

            if (BuildingResponse == null)
            {
                var statusCodeEarly = TryParseAsciiStatusCode(Status.Span, out var early) ? early : 0;
                if (statusCodeEarly is >= 100 and <= 199)
                {
                    InterimHeaders = new HeaderCollection();
                    InterimHeaders.AddHeader(new HttpHeader(name, value));
                    return;
                }

                BuildingResponse = new Response
                {
                    StatusCode = statusCodeEarly != 0 ? statusCodeEarly : 502,
                    StatusDescription = string.Empty,
                    HttpVersion = HttpHeader.Version11,
                    HeaderNamesAreHttp2Normalized = true
                };
            }

            BuildingResponse.Headers.AddHeader(new HttpHeader(name, value));
        }
    }
}

/// <summary>The fully materialized result of one <see cref="Http2OriginConnection.SendAsync" /> exchange.</summary>
internal sealed class Http2OriginExchange
{
    internal Http2OriginExchange(Response response, byte[] body, HeaderCollection? trailingHeaders)
    {
        Response = response;
        Body = body;
        TrailingHeaders = trailingHeaders;
    }

    internal Response Response { get; }

    internal byte[] Body { get; }

    internal HeaderCollection? TrailingHeaders { get; }
}

/// <summary>
///     Result of <see cref="Http2OriginConnection.OpenTunnelAsync" />. When the origin accepts the
///     extended CONNECT (<see cref="IsEstablished" />), <see cref="Stream" /> is the duplex tunnel;
///     otherwise <see cref="Stream" /> is null and <see cref="Response" /> carries the rejection.
/// </summary>
internal sealed class Http2OriginTunnelResult
{
    internal Http2OriginTunnelResult(Response response, Http2TunnelStream? stream)
    {
        Response = response;
        Stream = stream;
    }

    internal Response Response { get; }
    internal Http2TunnelStream? Stream { get; }
    internal bool IsEstablished => Stream != null && Response.StatusCode is >= 200 and < 300;
}
