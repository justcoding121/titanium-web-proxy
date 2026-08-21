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
    private int lastStreamId = -1;
    private volatile bool faulted;
    private volatile bool goingAway;
    private int goAwayLastStreamId = int.MaxValue;
    private int disposed;
    private int pendingDispose;
    private int leaseCount;
    private int activeStreamCount;
    private long lastUsedUtcTicks = DateTime.UtcNow.Ticks;

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
    ///     Grow the origin pool once every member has this many active streams, well before
    ///     <c>SETTINGS_MAX_CONCURRENT_STREAMS</c> (common default 100). One connection serializes
    ///     encode+enqueue under <c>writeLock</c>; spreading across a few TLS+H2 sessions matches
    ///     SocketsHttpHandler <c>EnableMultipleHttp2Connections</c>.
    ///     Profiled at c=16 with threshold 16: dumpasync showed a single
    ///     <c>ReadLoopAsync</c> and hundreds of <c>SemaphoreSlim</c> waiters — grow earlier so
    ///     low concurrency is not pinned to one origin TLS+H2 session.
    ///     Soft=1 (was 2←4): open another origin session as soon as any member is busy — maximizes
    ///     parallel writeLocks for HPACK encode under multiplex (YARP EnableMultipleHttp2Connections
    ///     fan-out mechanism, without changing TWP architecture). Cap remains
    ///     <see cref="ProxyResourceLimits.MaxOriginHttp2ConnectionsPerAuthority"/>.
    /// </summary>
    internal const int PoolGrowActiveStreamThreshold = 1;

    /// <summary>
    ///     Soft multiplex capacity used by <see cref="Http2OriginConnectionPool" /> to decide when to
    ///     open another origin connection. Prefers filling existing connections before growing the pool.
    /// </summary>
    internal int SoftStreamCapacity
    {
        get
        {
            var cap = concurrencyGateCapacity;
            if (cap <= 0)
                cap = resourceLimits.MaxConcurrentStreamsPerConnection;
            return Math.Max(1, Math.Min(cap, PoolGrowActiveStreamThreshold));
        }
    }

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
    internal async Task<Http2OriginExchange> SendAsync(Request request,
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
        if (!gate.Wait(0))
            await gate.WaitAsync(cancellationToken);

        // Allocate InterimChannel only when the caller will drain 1xx (on1xx != null). Passthrough
        // bridges wait on HeadersReceived instead — avoids per-request Channel/segment Gen0.
        var pending = new PendingStream(maxBufferedBodyBytes, createInterimChannel: on1xx != null);
        var streamId = 0;
        var streamOpened = false;
        var bodyHandedOff = false;
        try
        {
            var frameHeader = new Http2FrameHeader();
            var frameHeaderBuffer = new byte[9];

            var streamRequest = copyRequestBody != null && !request.IsBodyRead && !request.BodyAvailable;
            byte[]? bufferedBody = null;
            var enqueueBufferedTrailers = false;
            if (!streamRequest)
            {
                var body = request.CompressBodyAndUpdateContentLength();
                bufferedBody = request.HasBody && request.IsBodyRead ? body : null;
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
                    headersEndStream, Writer);
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

            if (streamRequest)
            {
                await copyRequestBody!(
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
            if (on1xx != null)
            {
                var interimReader = pending.InterimChannel?.Reader
                    ?? throw new InvalidOperationException("InterimChannel required when on1xx is set.");
                await foreach (var interim in interimReader.ReadAllAsync(cancellationToken))
                    await on1xx(interim.StatusCode, interim.Headers, cancellationToken);
            }
            else
            {
                await pending.HeadersReceived.Task.WaitAsync(cancellationToken);
            }

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

            // Stream origin DATA to the caller instead of materializing ToArray().
            var bodyPipe = pending.BodyPipe;
            var trailers = pending.TrailingHeaders;
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
                    bodyCancelRegistration.Dispose();
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
                    endStream: false, Writer);
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
        CancellationToken cancellationToken)
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

    private Task SendSettingsAckAsync(CancellationToken cancellationToken)
    {
        Http2Helper.EnqueueSettingsAck(Writer);
        return Task.CompletedTask;
    }

    private Task SendPingAckAsync(byte[] payload, CancellationToken cancellationToken)
    {
        Http2Helper.EnqueuePingAck(Writer, payload);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Re-grants flow-control credit for DATA frame on-wire payload (RFC 7540 §6.9). Batched at
    ///     <see cref="Http2Helper.ReceiveCreditBatchThreshold" /> (half of the 768 KiB stream window),
    ///     matching <see cref="Http2Helper" /> so credit is not drip-fed under the write lock per frame.
    /// </summary>
    private Task GrantReceiveCreditAsync(int streamId, int bytes, bool forceFlush,
        CancellationToken cancellationToken)
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

                if (length > 0)
                {
                    if (!await intake.EnsureAsync(length, cancellationToken).ConfigureAwait(false))
                    {
                        Fail(new IOException("The origin h2 connection was closed mid-frame."));
                        return;
                    }
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
                                                var writeVt = pendingData.BodyPipe.WriteAsync(bodyData, cancellationToken);
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

                            await GrantReceiveCreditAsync(streamId, length, forceFlush: false,
                                cancellationToken).ConfigureAwait(false);

                            if ((flags & Http2FrameFlag.EndStream) != 0)
                            {
                                await GrantReceiveCreditAsync(streamId, 0, forceFlush: true,
                                    cancellationToken).ConfigureAwait(false);
                                CompleteStream(streamId);
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
    private static byte[] StripDataFraming(byte[] payload, Http2FrameFlag flags)
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
        var collected = new HeaderCollection();
        ByteString status = default;

        // Avoid GetString() on every field name — ReadLoop must get back to Fill quickly so sibling
        // multiplexed streams' HeadersReceived can fire (c=32 dump: 8 ReadLoops, 32 waiters).
        var listener = new HeaderCollectorListener((name, value) =>
        {
            if (name.Length > 0 && name.Span[0] == (byte)':')
            {
                if (name.Equals(StaticTable.KnownHeaderStatus)) status = value;
                return;
            }

            collected.AddHeader(new HttpHeader(name, value));
        });

        try
        {
            decoder ??= new Decoder(8192, 4096);
            decoder.Decode(compressed, listener);
            decoder.EndHeaderBlock();
        }
        catch (Exception ex)
        {
            Fail(new ProxyHttpException("Failed to decode HTTP/2 headers from the origin.", ex, null));
            return;
        }

        if (!streams.TryGetValue(streamId, out var pending)) return;

        if (status.Length > 0)
        {
            var statusCode = TryParseAsciiStatusCode(status.Span, out var parsed) ? parsed : 502;

            if (statusCode is >= 100 and <= 199)
            {
                // Queue this interim response for relay when a caller registered on1xx (InterimChannel
                // present). Passthrough lite drops 1xx — probe/no-intercept paths do not forward them.
                pending.InterimChannel?.Writer.TryWrite((statusCode, collected));
                return;
            }

            if (pending.Response == null)
            {
                var response = new Response { StatusCode = statusCode, StatusDescription = string.Empty, HttpVersion = HttpHeader.Version11 };
                foreach (var header in collected) response.Headers.AddHeader(header);
                pending.Response = response;
                // Signal that no more interim responses will arrive; unblocks SendAsync's interim drain loop.
                pending.InterimChannel?.Writer.TryComplete();
                // Unblock OpenTunnelAsync / passthrough lite waiting on the final response headers.
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
                pending.TrailingHeaders ??= new HeaderCollection();
                foreach (var header in collected) pending.TrailingHeaders.AddHeader(header);
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
        pending.BodyPipe.CompleteWriter();
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
        pending.BodyPipe.CompleteWriter(ex);
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
            _ = writer.DisposeAsync();
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
        if (writeLock.Wait(0))
            return default;

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
        internal readonly BoundedBodyPipe BodyPipe;
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
        ///     Completed when the final (non-1xx) response HEADERS arrive. Used by
        ///     <see cref="OpenTunnelAsync" /> and by <see cref="SendAsync" /> when <c>on1xx</c> is null
        ///     (no InterimChannel drain).
        /// </summary>
        internal readonly TaskCompletionSource<bool> HeadersReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Inbound DATA payloads for an RFC 8441 tunnel. Null for ordinary request/response streams,
        ///     which use <see cref="BodyPipe" /> instead (and enforce <c>MaxBufferedBodyBytes</c>).
        /// </summary>
        internal readonly Channel<byte[]>? TunnelDataChannel;

        internal Response? Response;
        internal HeaderCollection? TrailingHeaders;

        internal PendingStream(long maxBodyBytes)
            : this(maxBodyBytes, createInterimChannel: true)
        {
        }

        /// <param name="maxBodyBytes">Max buffered body bytes for <see cref="BodyPipe" />.</param>
        /// <param name="createInterimChannel">
        ///     When <see langword="true"/>, allocate the 1xx relay channel. Tests and interception paths
        ///     that expect 1xx use this; <see cref="SendAsync" /> passes <see langword="false"/> when
        ///     <c>on1xx</c> is null.
        /// </param>
        internal PendingStream(long maxBodyBytes, bool createInterimChannel)
        {
            IsTunnel = false;
            BodyPipe = new BoundedBodyPipe(maxBodyBytes);
            if (createInterimChannel)
            {
                InterimChannel = Channel.CreateUnbounded<(int, HeaderCollection)>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            }
        }

        private PendingStream(bool isTunnel)
        {
            IsTunnel = isTunnel;
            // Tunnel streams never buffer a finite HTTP body; BodyPipe is unused but kept non-null
            // so FailPending can CompleteWriter unconditionally.
            BodyPipe = new BoundedBodyPipe(0);
            TunnelDataChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        internal static PendingStream CreateTunnel() => new(true);

        public void Dispose()
        {
            BodyPipe.Dispose();
            // Release any reader blocking on WaitToReadAsync if Dispose is called without a prior Complete.
            InterimChannel?.Writer.TryComplete();
            TunnelDataChannel?.Writer.TryComplete();
            HeadersReceived.TrySetCanceled();
        }
    }

    private sealed class HeaderCollectorListener : IHeaderListener
    {
        private readonly Action<ByteString, ByteString> addHeader;

        internal HeaderCollectorListener(Action<ByteString, ByteString> addHeader)
        {
            this.addHeader = addHeader;
        }

        public void AddHeader(ByteString name, ByteString value, bool sensitive)
        {
            addHeader(name, value);
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
