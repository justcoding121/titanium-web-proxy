#if NET6_0_OR_GREATER
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
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
///         Known simplification: response bodies are fully buffered in memory before being handed back to the
///         caller (mirroring the h2-to-HTTP/1.1 bridge's request-body buffering), rather than streamed
///         incrementally to the HTTP/1.1 client as DATA frames arrive.
///     </para>
/// </summary>
internal sealed class Http2OriginConnection
{
    /// <summary>Every HTTP/2 endpoint must accept frames up to this size (RFC 7540 §4.2), so it is always safe to send.</summary>
    private const int SafeMaxFrameSize = 16384;

    private const int DefaultConcurrencyCap = 100;

    private readonly TcpServerConnection connection;
    private readonly Stream stream;
    private readonly ExceptionHandler? exceptionFunc;
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

    private Http2OriginConnection(TcpServerConnection connection, ExceptionHandler? exceptionFunc)
    {
        this.connection = connection;
        stream = connection.Stream;
        this.exceptionFunc = exceptionFunc;
    }

    /// <summary>True while this connection may still be leased for a new request.</summary>
    internal bool IsUsable => !faulted && !goingAway && !connection.IsClosed;

    /// <summary>
    ///     Establishes a new origin h2 connection over an already TLS/ALPN=h2-negotiated <see cref="TcpServerConnection" />:
    ///     writes the client connection preface and this proxy's own SETTINGS (advertising
    ///     <c>SETTINGS_ENABLE_PUSH=0</c>, since this bridge never generates or forwards server push), starts the
    ///     background frame-reading loop, and waits for the origin's own initial SETTINGS frame so
    ///     <see cref="SendAsync" /> always has a real <c>MAX_CONCURRENT_STREAMS</c>/frame-size budget to honor.
    /// </summary>
    internal static async Task<Http2OriginConnection> CreateAsync(TcpServerConnection connection,
        ExceptionHandler? exceptionFunc, CancellationToken cancellationToken)
    {
                var instance = new Http2OriginConnection(connection, exceptionFunc);

                var preface = Http2Helper.ConnectionPreface;
                await instance.stream.WriteAsync(preface, 0, preface.Length, cancellationToken);
                await instance.SendInitialSettingsAsync(cancellationToken);

                instance.readLoopTask = Task.Run(() => instance.ReadLoopAsync(instance.connectionCts.Token));

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
    internal async Task<Http2OriginExchange> SendAsync(Request request, CancellationToken cancellationToken)
    {
        if (!IsUsable) throw new Http2OriginGoAwayException("The origin h2 connection is no longer usable.");

        await initialSettingsReceived.Task.WaitAsync(cancellationToken);

        var gate = concurrencyGate ?? throw new InvalidOperationException("Origin settings were never processed.");
        await gate.WaitAsync(cancellationToken);

        var streamId = Interlocked.Add(ref lastStreamId, 2);
        var pending = new PendingStream();

        if (goingAway && streamId > goAwayLastStreamId)
        {
            gate.Release();
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
            sendFlow.RemoveStream(streamId);
            gate.Release();
            throw;
        }

        try
        {
            await using var registration = cancellationToken.Register(
                () => pending.Completion.TrySetCanceled(cancellationToken));
            return await pending.Completion.Task;
        }
        finally
        {
            streams.TryRemove(streamId, out _);
            sendFlow.RemoveStream(streamId);
            gate.Release();
        }
    }

    private async Task SendInitialSettingsAsync(CancellationToken cancellationToken)
    {
        var frameHeader = new Http2FrameHeader
        {
            StreamId = 0, Type = Http2FrameType.Settings, Flags = 0, Length = 6
        };
        var frameHeaderBuffer = new byte[9];
        frameHeader.CopyToBuffer(frameHeaderBuffer);

        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)Http2SettingsId.EnablePush);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2, 4), 0);

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
                        if ((flags & Http2FrameFlag.Ack) == 0)
                        {
                            ApplySettings(payload);
                            await SendSettingsAckAsync(cancellationToken);
                            if (!initialSettingsReceived.Task.IsCompleted)
                            {
                                var cap = originSettings.MaxConcurrentStreams == int.MaxValue
                                    ? DefaultConcurrencyCap
                                    : Math.Max(1, Math.Min(originSettings.MaxConcurrentStreams, DefaultConcurrencyCap * 4));
                                concurrencyGate = new SemaphoreSlim(cap, cap);
                                initialSettingsReceived.TrySetResult(true);
                            }
                        }

                        break;

                    case Http2FrameType.WindowUpdate:
                        if (payload.Length == 4)
                        {
                            var increment = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7fffffff);
                            sendFlow.OnWindowUpdate(streamId, increment);
                        }

                        break;

                    case Http2FrameType.Headers:
                    {
                        headerBlockStreamId = streamId;
                        headerBlockBuffer.SetLength(0);
                        headerBlockEndStream = (flags & Http2FrameFlag.EndStream) != 0;
                        var data = StripHeadersFraming(payload, flags);
                        headerBlockBuffer.Write(data, 0, data.Length);
                        if ((flags & Http2FrameFlag.EndHeaders) != 0)
                        {
                            ProcessHeaderBlock(streamId, headerBlockBuffer.ToArray(), headerBlockEndStream);
                            headerBlockStreamId = null;
                        }

                        break;
                    }

                    case Http2FrameType.Continuation:
                        if (headerBlockStreamId == streamId)
                        {
                            headerBlockBuffer.Write(payload, 0, payload.Length);
                            if ((flags & Http2FrameFlag.EndHeaders) != 0)
                            {
                                ProcessHeaderBlock(streamId, headerBlockBuffer.ToArray(), headerBlockEndStream);
                                headerBlockStreamId = null;
                            }
                        }

                        break;

                    case Http2FrameType.Data:
                    {
                        var data = StripDataFraming(payload, flags);
                        if (streams.TryGetValue(streamId, out var pending)) pending.Body.Write(data, 0, data.Length);

                        await GrantReceiveCreditAsync(streamId, length, cancellationToken);

                        if ((flags & Http2FrameFlag.EndStream) != 0) CompleteStream(streamId);

                        break;
                    }

                    case Http2FrameType.RstStream:
                        FailStream(streamId,
                            new IOException($"HTTP/2 stream {streamId} was reset by the origin (RST_STREAM)."));
                        break;

                    case Http2FrameType.GoAway:
                        if (payload.Length >= 8)
                        {
                            var lastId = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)) & 0x7fffffff);
                            goAwayLastStreamId = lastId;
                            goingAway = true;
                            foreach (var kvp in streams)
                                if (kvp.Key > lastId)
                                    kvp.Value.Completion.TrySetException(new Http2OriginGoAwayException(
                                        $"The origin sent GOAWAY before stream {kvp.Key} was processed; it is safe to retry."));
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
                originSettings.HeaderTableSize = value;
            else if (identifier == (int)Http2SettingsId.MaxFrameSize)
                originSettings.MaxFrameSize = value;
            else if (identifier == (int)Http2SettingsId.InitialWindowSize)
                sendFlow.OnInitialWindowSizeChanged(value);
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

            // Informational (1xx) responses are not relayed to the HTTP/1.1 client by this bridge; wait for
            // the final response HEADERS block that must still follow on the same stream.
            if (statusCode is >= 100 and <= 199) return;

            if (pending.Response == null)
            {
                var response = new Response { StatusCode = statusCode, StatusDescription = string.Empty, HttpVersion = HttpHeader.Version11 };
                foreach (var header in collected) response.Headers.AddHeader(header);
                pending.Response = response;
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
        if (!streams.TryGetValue(streamId, out var pending)) return;

        var response = pending.Response ??
                        new Response { StatusCode = 502, StatusDescription = string.Empty, HttpVersion = HttpHeader.Version11 };
        var exchange = new Http2OriginExchange(response, pending.Body.ToArray(), pending.TrailingHeaders);
        pending.Completion.TrySetResult(exchange);
    }

    private void FailStream(int streamId, Exception ex)
    {
        if (streams.TryGetValue(streamId, out var pending)) pending.Completion.TrySetException(ex);
    }

    /// <param name="ex">The failure to fault every in-flight/future stream with.</param>
    /// <param name="report">
    ///     Whether this is a genuine, previously-unobserved origin failure that should be surfaced via
    ///     <see cref="exceptionFunc" /> (I/O errors, protocol violations, HPACK decode failures encountered by the
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
            exceptionFunc?.Invoke(
                new ProxyHttpException("The HTTP/1.1-to-HTTP/2 origin bridge connection failed.", ex, null));

        foreach (var kvp in streams) kvp.Value.Completion.TrySetException(ex);
        initialSettingsReceived.TrySetException(ex);
    }

    /// <summary>
    ///     Closes this origin connection. Any streams still awaiting a response are failed. This is a normal,
    ///     expected part of the bridge's connection lifecycle (e.g. the HTTP/1.1 client connection ended, the
    ///     bridge is replacing this connection after a GOAWAY, or the user asked to discard it via
    ///     <c>CloseServerConnection</c>) and must not, by itself, be reported through <see cref="exceptionFunc" />
    ///     - see <see cref="Fail" />.
    /// </summary>
    internal void Dispose()
    {
        Fail(new ObjectDisposedException(nameof(Http2OriginConnection)), false);
        connectionCts.Cancel();
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

    private sealed class PendingStream
    {
        internal readonly TaskCompletionSource<Http2OriginExchange> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly MemoryStream Body = new();
        internal Response? Response;
        internal HeaderCollection? TrailingHeaders;
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
#endif
