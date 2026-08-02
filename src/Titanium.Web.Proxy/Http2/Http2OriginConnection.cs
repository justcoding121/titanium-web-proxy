using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
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
///     HTTP/1.1-client-to-h2-origin translation bridge (<c>Http11ToHttp2BridgeHandler</c>). Leases an odd,
///     strictly increasing stream id (RFC 7540 §5.1.1) for each translated request rather than dequeuing the
///     whole connection the way an HTTP/1.1 pooled connection is used, so several requests - sequential or
///     concurrent - on the same bridged client connection reuse one persistent origin connection instead of
///     opening a new TCP/TLS connection per request.
///     <para>
///         Per the delivery plan, this milestone binds one dedicated instance to one client connection rather
///         than sharing it across independent client connections; cross-client multiplexing is deferred until
///         auth/cancellation/fairness/pool stress tests exist for it.
///     </para>
///     <para>
///         Response bodies are streamed through a <see cref="BoundedBodyPipe" /> to enforce
///         <c>MaxBufferedBodyBytes</c> and propagate RST_STREAM/GOAWAY cancellation promptly; the body is
///         fully materialized into a <c>byte[]</c> before being handed to the caller (full streaming delivery
///         to the HTTP/1.1 client is a future phase).
///     </para>
/// </summary>
internal sealed class Http2OriginConnection
{
    /// <summary>Every HTTP/2 endpoint must accept frames up to this size (RFC 7540 §4.2), so it is always safe to send.</summary>
    private const int SafeMaxFrameSize = 16384;

    /// <summary>Maximum total header block (HEADERS + CONTINUATION fragments) we accept from origin before treating it as a protocol violation.</summary>
    private const int MaxHeaderBlockBytes = 256 * 1024;

    private readonly TcpServerConnection connection;
    private readonly Stream stream;
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

    private SemaphoreSlim? concurrencyGate;
    private Decoder? decoder;
    private int lastStreamId = -1;
    private volatile bool faulted;
    private volatile bool goingAway;
    private int goAwayLastStreamId = int.MaxValue;
    private Task? readLoopTask;

    private Http2OriginConnection(TcpServerConnection connection, ILogger logger, long maxBufferedBodyBytes,
        ProxyResourceLimits resourceLimits)
    {
        this.connection = connection;
        stream = connection.Stream;
        this.logger = logger;
        this.maxBufferedBodyBytes = maxBufferedBodyBytes;
        this.resourceLimits = resourceLimits;
    }

    /// <summary>True while this connection may still be leased for a new request.</summary>
    internal bool IsUsable => !faulted && !goingAway && !connection.IsClosed;

    /// <summary>
    ///     The underlying TCP connection, exposed so callers can attribute
    ///     <see cref="ProxyServer.EnableRequestTimingCapture" /> timing (connection id, reuse, and
    ///     establishment timing) to each request leased from this shared, persistent origin connection.
    /// </summary>
    internal TcpServerConnection ServerConnection => connection;

    /// <summary>
    ///     Establishes a new origin h2 connection over an already TLS/ALPN=h2-negotiated <see cref="TcpServerConnection" />:
    ///     writes the client connection preface, this proxy's own SETTINGS, and an initial
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
                await instance.stream.WriteAsync(preface, 0, preface.Length, cancellationToken);
                // Shared with the H2↔H2 MITM path (SendHttp2ClientConnectionStartupAsync).
                await Http2Helper.SendHttp2ClientConnectionStartupAsync(instance.stream, cancellationToken);

                instance.readLoopTask = instance.ReadLoopAsync(instance.connectionCts.Token);

                try
                {
                    await instance.initialSettingsReceived.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
                catch (Exception ex)
                {
                    instance.Fail(ex);
                    throw;
                }

        return instance;
    }

    /// <summary>
    ///     Translates <paramref name="request" /> (already fully header-prepared by the caller: hop-by-hop
    ///     headers stripped, host/authority resolved) onto a freshly leased stream and returns once the
    ///     origin's complete response (status, headers, fully buffered body, and any trailers) has been
    ///     received. The caller must have already buffered the request body (if any) via
    ///     <c>SessionEventArgs.GetRequestBody</c> so <c>request.IsBodyRead</c> is true.
    /// </summary>
    /// <param name="request">The HTTP request to send.</param>
    /// <param name="on1xx">
    ///     Optional async callback invoked (in order) for each 1xx interim response received before the
    ///     final response headers arrive. When non-null, the caller can relay these interim responses to the
    ///     connected HTTP/1.1 client while the exchange is still in flight. Invoked from the <c>SendAsync</c>
    ///     continuation (not the background read loop), so it is safe to write to the client stream.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole request/response exchange.</param>
    internal async Task<Http2OriginExchange> SendAsync(Request request,
        Func<int, HeaderCollection, CancellationToken, Task>? on1xx,
        CancellationToken cancellationToken)
    {
        if (!IsUsable) throw new Http2OriginGoAwayException("The origin h2 connection is no longer usable.");

        await initialSettingsReceived.Task.WaitAsync(cancellationToken);

        var gate = concurrencyGate ?? throw new InvalidOperationException("Origin settings were never processed.");
        await gate.WaitAsync(cancellationToken);

        var streamId = Interlocked.Add(ref lastStreamId, 2);
        var pending = new PendingStream(maxBufferedBodyBytes);

        if (goingAway && streamId > goAwayLastStreamId)
        {
            gate.Release();
            pending.Dispose();
            throw new Http2OriginGoAwayException(
                $"The origin sent GOAWAY before stream {streamId} could be opened; it was never processed.");
        }

        streams[streamId] = pending;
        sendFlow.RegisterStream(streamId);

        try
        {
            var frameHeader = new Http2FrameHeader { StreamId = streamId };
            var frameHeaderBuffer = new byte[9];
            var dataBuffer = new byte[SafeMaxFrameSize];

            await writeLock.WaitAsync(cancellationToken);
            try
            {
                await Http2Helper.SendBody(originSettings, request, frameHeader, frameHeaderBuffer, dataBuffer,
                    sendFlow, stream, cancellationToken);

                if (request.HasTrailingHeaders && request.TrailingHeaders.Any())
                    await Http2Helper.SendTrailer(originSettings, frameHeader, frameHeaderBuffer, streamId,
                        request.TrailingHeaders, true, stream);
            }
            finally
            {
                writeLock.Release();
            }
        }
        catch
        {
            streams.TryRemove(streamId, out _);
            pending.Dispose();
            sendFlow.RemoveStream(streamId);
            gate.Release();
            throw;
        }

        try
        {
            // Register cancellation: complete the pipe writer so CopyToAsync below unblocks and throws.
            await using var registration = cancellationToken.Register(() =>
                pending.BodyPipe.CompleteWriter(new OperationCanceledException(cancellationToken)));

            // Relay 1xx interim responses (e.g. 103 Early Hints) to the HTTP/1.1 client before reading
            // the body. ReadLoopAsync writes each interim into pending.InterimChannel and completes the
            // writer as soon as final response headers arrive, so this loop exits cleanly before
            // CopyToAsync below even begins to drain body DATA frames.
            if (on1xx != null)
                await foreach (var interim in pending.InterimChannel.Reader.ReadAllAsync(cancellationToken))
                    await on1xx(interim.StatusCode, interim.Headers, cancellationToken);

            // Concurrently drain the pipe while ReadLoopAsync writes DATA frames into it.
            // CopyToAsync returns when the writer is completed (END_STREAM) or throws when
            // the writer is completed with an exception (RST_STREAM, GOAWAY, cancellation, limit).
            using var bodyMs = new MemoryStream();
            await pending.BodyPipe.CopyToAsync(bodyMs, cancellationToken);

            var response = pending.Response ??
                           new Response
                           {
                               StatusCode = 502, StatusDescription = string.Empty,
                               HttpVersion = HttpHeader.Version11
                           };
            return new Http2OriginExchange(response, bodyMs.ToArray(), pending.TrailingHeaders);
        }
        finally
        {
            streams.TryRemove(streamId, out _);
            pending.Dispose();
            sendFlow.RemoveStream(streamId);
            gate.Release();
        }
    }

    private async Task SendSettingsAckAsync(CancellationToken cancellationToken)
    {
        var frameHeader = new Http2FrameHeader
        {
            StreamId = 0, Type = Http2FrameType.Settings, Flags = Http2FrameFlag.Ack, Length = 0
        };
        var frameHeaderBuffer = new byte[9];
        frameHeader.CopyToBuffer(frameHeaderBuffer);

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task SendPingAckAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var frameHeader = new Http2FrameHeader
        {
            StreamId = 0, Type = Http2FrameType.Ping, Flags = Http2FrameFlag.Ack, Length = payload.Length
        };
        var frameHeaderBuffer = new byte[9];
        frameHeader.CopyToBuffer(frameHeaderBuffer);

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length, cancellationToken);
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Re-grants flow-control credit for one DATA frame's on-wire payload (RFC 7540 §6.9), see the identical strategy in <c>Http2Helper</c>.</summary>
    private async Task GrantReceiveCreditAsync(int streamId, int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return;

        var streamStillTracked = streams.ContainsKey(streamId);
        var frameHeader = new Http2FrameHeader();
        var frameHeaderBuffer = new byte[9];

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await Http2Helper.SendWindowUpdateAsync(frameHeader, frameHeaderBuffer, 0, bytes, stream);
            if (streamStillTracked)
                await Http2Helper.SendWindowUpdateAsync(frameHeader, frameHeaderBuffer, streamId, bytes, stream);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var frameHeaderBuffer = new byte[9];
        var isFirstFrame = true;

        int? headerBlockStreamId = null;
        var headerBlockBuffer = new MemoryStream();
        var headerBlockEndStream = false;

        try
        {
                    while (true)
                    {
                        var read = await ForceReadAsync(frameHeaderBuffer, 0, 9, cancellationToken);
                        if (read != 9)
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
                // so any frame larger than that is a protocol violation. Reject before allocating.
                if (length > SafeMaxFrameSize)
                {
                    Fail(new IOException(
                        $"HTTP/2 protocol error: origin sent a {length}-byte frame payload, exceeding the {SafeMaxFrameSize}-byte limit this proxy advertised."));
                    return;
                }

                var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                if (length > 0)
                {
                    read = await ForceReadAsync(payload, 0, length, cancellationToken);
                    if (read != length)
                    {
                        Fail(new IOException("The origin h2 connection was closed mid-frame."));
                        return;
                    }
                }

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
                            if (payload.Length != 0)
                            {
                                Fail(new IOException("HTTP/2 protocol error: SETTINGS ACK must have empty payload."));
                                return;
                            }

                            break;
                        }

                        if (payload.Length % 6 != 0)
                        {
                            Fail(new IOException(
                                $"HTTP/2 protocol error: SETTINGS payload must be a multiple of 6 bytes, got {payload.Length}."));
                            return;
                        }

                        ApplySettings(payload);
                        if (faulted) return;
                        await SendSettingsAckAsync(cancellationToken);
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
                            concurrencyGate = new SemaphoreSlim(cap, cap);
                            initialSettingsReceived.TrySetResult(true);
                        }

                        break;

                    case Http2FrameType.WindowUpdate:
                        if (payload.Length != 4)
                        {
                            Fail(new IOException("HTTP/2 protocol error: WINDOW_UPDATE frame must be exactly 4 bytes."));
                            return;
                        }

                        {
                            var increment = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7fffffff);
                            if (increment == 0)
                            {
                                // RFC 7540 §6.9.1: a zero-increment WINDOW_UPDATE is a connection error PROTOCOL_ERROR.
                                Fail(new IOException("HTTP/2 protocol error: WINDOW_UPDATE increment must not be zero."));
                                return;
                            }

                            sendFlow.OnWindowUpdate(streamId, increment);
                        }

                        break;

                    case Http2FrameType.Headers:
                    {
                        headerBlockStreamId = streamId;
                        headerBlockBuffer.SetLength(0);
                        headerBlockEndStream = (flags & Http2FrameFlag.EndStream) != 0;
                        var data = StripHeadersFraming(payload, flags);
                        if (data.Length > MaxHeaderBlockBytes)
                        {
                            Fail(new IOException(
                                $"HTTP/2 protocol error: origin header block exceeded {MaxHeaderBlockBytes} bytes."));
                            return;
                        }

                        headerBlockBuffer.Write(data, 0, data.Length);
                        if ((flags & Http2FrameFlag.EndHeaders) != 0)
                        {
                            ProcessHeaderBlock(streamId, headerBlockBuffer.ToArray(), headerBlockEndStream);
                            headerBlockStreamId = null;
                        }

                        break;
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

                        if (headerBlockBuffer.Length + payload.Length > MaxHeaderBlockBytes)
                        {
                            Fail(new IOException(
                                $"HTTP/2 protocol error: origin CONTINUATION block exceeded {MaxHeaderBlockBytes} bytes."));
                            return;
                        }

                        headerBlockBuffer.Write(payload, 0, payload.Length);
                        if ((flags & Http2FrameFlag.EndHeaders) != 0)
                        {
                            ProcessHeaderBlock(streamId, headerBlockBuffer.ToArray(), headerBlockEndStream);
                            headerBlockStreamId = null;
                        }

                        break;

                    case Http2FrameType.Data:
                    {
                        var data = StripDataFraming(payload, flags);
                        if (data.Length > 0 && streams.TryGetValue(streamId, out var pendingData))
                        {
                            try
                            {
                                await pendingData.BodyPipe.WriteAsync(data.AsMemory(), cancellationToken);
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

                        await GrantReceiveCreditAsync(streamId, length, cancellationToken);

                        if ((flags & Http2FrameFlag.EndStream) != 0) CompleteStream(streamId);

                        break;
                    }

                    case Http2FrameType.RstStream:
                        FailStream(streamId,
                            new IOException($"HTTP/2 stream {streamId} was reset by the origin (RST_STREAM)."));
                        break;

                    case Http2FrameType.GoAway:
                        if (streamId != 0)
                        {
                            Fail(new IOException("HTTP/2 protocol error: GOAWAY frame must have stream ID 0."));
                            return;
                        }

                        if (payload.Length >= 8)
                        {
                            var lastId = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)) & 0x7fffffff);
                            goAwayLastStreamId = lastId;
                            goingAway = true;
                            foreach (var kvp in streams)
                            {
                                if (kvp.Key > lastId)
                                {
                                    var goAwayEx = new Http2OriginGoAwayException(
                                        $"The origin sent GOAWAY before stream {kvp.Key} was processed; it is safe to retry.");
                                    kvp.Value.BodyPipe.CompleteWriter(goAwayEx);
                                    // Also unblock any SendAsync that is awaiting the interim channel,
                                    // since no response frames (including 1xx) will ever arrive for this stream.
                                    kvp.Value.InterimChannel.Writer.TryComplete(goAwayEx);
                                }
                            }
                        }

                        break;

                    case Http2FrameType.Ping:
                        if ((flags & Http2FrameFlag.Ack) == 0) await SendPingAckAsync(payload, cancellationToken);
                        break;

                    case Http2FrameType.PushPromise:
                        // This bridge always advertises SETTINGS_ENABLE_PUSH=0 - a PUSH_PROMISE anyway is a
                        // direct protocol violation (RFC 7540 §6.6); tear the connection down rather than
                        // decode a header block that would desync every subsequent HPACK block.
                        Fail(new IOException("HTTP/2 protocol error: unexpected PUSH_PROMISE from the origin."));
                        return;

                    default:
                        // PRIORITY and any unknown/reserved frame types are simply ignored.
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void ApplySettings(byte[] payload)
    {
        for (var i = 0; i + 6 <= payload.Length; i += 6)
        {
            var identifier = (payload[i] << 8) | payload[i + 1];
            var value = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i + 2, 4));

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
        }
    }

    /// <summary>Strips the optional PADDED (1 length byte + trailing padding) and PRIORITY (5 bytes) framing from a HEADERS frame payload.</summary>
    private static byte[] StripHeadersFraming(byte[] payload, Http2FrameFlag flags)
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

        if (offset >= end) return Array.Empty<byte>();

        return payload.AsSpan(offset, end - offset).ToArray();
    }

    /// <summary>Strips the optional PADDED (1 length byte + trailing padding) framing from a DATA frame payload.</summary>
    private static byte[] StripDataFraming(byte[] payload, Http2FrameFlag flags)
    {
        if ((flags & Http2FrameFlag.Padded) == 0 || payload.Length == 0) return payload;

        var padLength = payload[0];
        var end = Math.Max(1, payload.Length - padLength);
        return payload.AsSpan(1, end - 1).ToArray();
    }

    private void ProcessHeaderBlock(int streamId, byte[] compressed, bool endStream)
    {
        var collected = new HeaderCollection();
        ByteString status = default;

        var listener = new HeaderCollectorListener((name, value) =>
        {
            if (name.Length > 0 && name.Span[0] == (byte)':')
            {
                if (name.GetString() == ":status") status = value;
                return;
            }

            collected.AddHeader(new HttpHeader(name, value));
        });

        try
        {
            decoder ??= new Decoder(8192, 4096);
            decoder.Decode(new BinaryReader(new MemoryStream(compressed)), listener);
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
            var statusCode = int.TryParse(status.GetString(), out var parsed) ? parsed : 502;

            if (statusCode is >= 100 and <= 199)
            {
                // Queue this interim response for relay to the HTTP/1.1 client via SendAsync's on1xx callback.
                // The channel writer is completed when the final response headers arrive (below), so SendAsync's
                // ReadAllAsync loop exits naturally and proceeds to drain the body pipe.
                pending.InterimChannel.Writer.TryWrite((statusCode, collected));
                return;
            }

            if (pending.Response == null)
            {
                var response = new Response { StatusCode = statusCode, StatusDescription = string.Empty, HttpVersion = HttpHeader.Version11 };
                foreach (var header in collected) response.Headers.AddHeader(header);
                pending.Response = response;
                // Signal that no more interim responses will arrive; unblocks SendAsync's interim drain loop.
                pending.InterimChannel.Writer.TryComplete();
            }
        }
        else
        {
            // A HEADERS block without a ":status" pseudo-header, following the main response headers, is a
            // trailer block (RFC 7540 §8.1.2.1 / RFC 7230 §4.1.2).
            pending.TrailingHeaders ??= new HeaderCollection();
            foreach (var header in collected) pending.TrailingHeaders.AddHeader(header);
        }

        if (endStream) CompleteStream(streamId);
    }

    private void CompleteStream(int streamId)
    {
        // Use TryRemove so subsequent DATA frames for this stream-id are ignored in the read loop.
        if (!streams.TryRemove(streamId, out var pending)) return;
        pending.BodyPipe.CompleteWriter();
    }

    private void FailStream(int streamId, Exception ex)
    {
        // Use TryRemove so subsequent DATA frames for this stream are ignored in the read loop.
        if (streams.TryRemove(streamId, out var pending))
        {
            pending.BodyPipe.CompleteWriter(ex);
            // Unblock any SendAsync that is awaiting interim responses (e.g. RST_STREAM while draining 1xx).
            pending.InterimChannel.Writer.TryComplete(ex);
        }
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
            ProxyDiagnostics.ReportUnexpected(logger, "The HTTP/1.1-to-HTTP/2 origin bridge connection failed.", 
                new ProxyHttpException("The HTTP/1.1-to-HTTP/2 origin bridge connection failed.", ex, null));

        foreach (var kvp in streams)
        {
            kvp.Value.BodyPipe.CompleteWriter(ex);
            // Unblock any SendAsync that is awaiting interim responses; no more frames will ever arrive.
            kvp.Value.InterimChannel.Writer.TryComplete(ex);
        }

        initialSettingsReceived.TrySetException(ex);
    }

    /// <summary>
    ///     Closes this origin connection. Any streams still awaiting a response are failed. This is a normal,
    ///     expected part of the bridge's connection lifecycle (e.g. the HTTP/1.1 client connection ended, the
    ///     bridge is replacing this connection after a GOAWAY, or the user asked to discard it via
    ///     <c>CloseServerConnection</c>) and must not, by itself, be reported through the logging gateway
    ///     - see <see cref="Fail" />.
    /// </summary>
    internal void Dispose()
    {
        Fail(new ObjectDisposedException(nameof(Http2OriginConnection)), false);
        connectionCts.Cancel();
        connectionCts.Dispose();
        writeLock.Dispose();
        concurrencyGate?.Dispose();
        connection.Dispose();
    }

    private async Task<int> ForceReadAsync(byte[] buffer, int offset, int bytesToRead, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (bytesToRead > 0)
        {
            var read = await stream.ReadAsync(buffer, offset, bytesToRead, cancellationToken);
            if (read == 0) break;

            totalRead += read;
            bytesToRead -= read;
            offset += read;
        }

        return totalRead;
    }

    private sealed class PendingStream : IDisposable
    {
        internal readonly BoundedBodyPipe BodyPipe;

        /// <summary>
        ///     Queue of 1xx interim responses written by <see cref="ProcessHeaderBlock" /> as they arrive from
        ///     the origin, and drained by <see cref="SendAsync" />'s <c>on1xx</c> callback relay loop.
        ///     The writer is completed (without exception) when the final response headers are processed,
        ///     or completed with an exception when the stream or connection fails.
        /// </summary>
        internal readonly Channel<(int StatusCode, HeaderCollection Headers)> InterimChannel =
            Channel.CreateUnbounded<(int, HeaderCollection)>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        internal Response? Response;
        internal HeaderCollection? TrailingHeaders;

        internal PendingStream(long maxBodyBytes = 0) => BodyPipe = new BoundedBodyPipe(maxBodyBytes);

        public void Dispose()
        {
            BodyPipe.Dispose();
            // Release any reader blocking on WaitToReadAsync if Dispose is called without a prior Complete.
            InterimChannel.Writer.TryComplete();
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
