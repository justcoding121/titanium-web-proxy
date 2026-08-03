using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy;

/// <summary>
///     Frame-aware WebSocket relay that invokes per-frame interception callbacks while still
///     raising observational <c>DataSent</c>/<c>DataReceived</c> for bytes actually written.
/// </summary>
internal static class WebSocketInterceptRelay
{
    internal static async Task RelayAsync(Stream clientStream, Stream serverStream, IBufferPool bufferPool,
        SessionEventArgs session, CancellationTokenSource cancellationTokenSource)
    {
        var clientWriteLock = new SemaphoreSlim(1, 1);
        var serverWriteLock = new SemaphoreSlim(1, 1);

        session.WebSocketServerWriter = new WebSocketFrameWriter(serverStream, mask: true, serverWriteLock,
            (b, o, c) => session.OnDataSent(b, o, c));
        session.WebSocketClientWriter = new WebSocketFrameWriter(clientStream, mask: false, clientWriteLock,
            (b, o, c) => session.OnDataReceived(b, o, c));

        var maxFramePayloadBytes = session.MaxWebSocketFramePayloadBytes ?? session.Server.MaxWebSocketFramePayloadBytes;

        var clientToServer = RelayDirectionAsync(clientStream, serverStream, bufferPool,
            WebSocketFrameDirection.ClientToServer, maxFramePayloadBytes, session,
            serverWriteLock, cancellationTokenSource,
            onRead: (b, o, c) => { /* observational reads are implied by frames; wire bytes fire on write */ },
            onWrite: (b, o, c) => session.OnDataSent(b, o, c));

        var serverToClient = RelayDirectionAsync(serverStream, clientStream, bufferPool,
            WebSocketFrameDirection.ServerToClient, maxFramePayloadBytes, session,
            clientWriteLock, cancellationTokenSource,
            onRead: (_, _, _) => { },
            onWrite: (b, o, c) => session.OnDataReceived(b, o, c));

        await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
        await cancellationTokenSource.CancelAsync();

        ushort? closeCode = null;
        try
        {
            await Task.WhenAll(clientToServer, serverToClient).ConfigureAwait(false);
            closeCode = clientToServer.Result ?? serverToClient.Result;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // RFC 6455 sections 5.5.1 and 7.1.1: a frame/fragmentation violation is not a best-effort
            // teardown - both legs must receive a Close control frame (masked toward the origin, since
            // the proxy is impersonating the client on that leg) before the underlying connections close,
            // and no further data frames may follow it. Do this before the writers/locks below are torn
            // down, while both streams are still known to be open.
            if (closeCode.HasValue)
                await SendConformantCloseAsync(clientStream, serverStream, clientWriteLock, serverWriteLock,
                    closeCode.Value).ConfigureAwait(false);

            session.WebSocketServerWriter = null;
            session.WebSocketClientWriter = null;
            clientWriteLock.Dispose();
            serverWriteLock.Dispose();
        }
    }

    /// <summary>
    ///     Sends a Close control frame on both legs of the tunnel. Best-effort per leg: a peer that has
    ///     already disconnected must not prevent the other leg's Close from being attempted, and the
    ///     underlying connections are torn down by the caller regardless once this returns.
    /// </summary>
    private static async Task SendConformantCloseAsync(Stream clientStream, Stream serverStream,
        SemaphoreSlim clientWriteLock, SemaphoreSlim serverWriteLock, ushort closeCode)
    {
        var payload = new[] { (byte)(closeCode >> 8), (byte)closeCode };

        await TrySendCloseFrameAsync(clientStream, clientWriteLock, payload, mask: false).ConfigureAwait(false);
        await TrySendCloseFrameAsync(serverStream, serverWriteLock, payload, mask: true).ConfigureAwait(false);

        // Both WriteAsync/FlushAsync calls above only guarantee the bytes left this process's send
        // buffer, not that the peer's TCP stack has ACKed them - the caller tears down the underlying
        // sockets immediately after this method returns, and an instantaneous close can otherwise beat
        // a just-flushed small frame off the wire on some platforms/network stacks. This short grace
        // period costs nothing on the hot path (only reached after a protocol violation) and makes
        // delivery of the Close frame reliable in practice.
        await Task.Delay(100).ConfigureAwait(false);
    }

    private static async Task TrySendCloseFrameAsync(Stream stream, SemaphoreSlim writeLock, byte[] payload,
        bool mask)
    {
        try
        {
            var wire = WebSocketFrameEncoder.Encode(WebsocketOpCode.ConnectionClose, payload, mask);
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(wire.AsMemory()).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Best-effort - the underlying connection is closed unconditionally after this regardless of
            // whether the peer was still reachable to receive the Close frame.
        }
    }

    /// <returns>
    ///     <see langword="null" /> if the direction ended normally (peer disconnect, cancellation or a
    ///     transport-level I/O error); otherwise the RFC 6455 section 7.4 status code to report in a
    ///     conformant Close for the frame/fragmentation violation that ended it.
    /// </returns>
    private static async Task<ushort?> RelayDirectionAsync(Stream source, Stream destination, IBufferPool bufferPool,
        WebSocketFrameDirection direction, long maxFramePayloadBytes, SessionEventArgs session,
        SemaphoreSlim writeLock, CancellationTokenSource cancellationTokenSource,
        Action<byte[], int, int> onRead, Action<byte[], int, int> onWrite)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var decoder = new WebSocketDecoder(bufferPool, maxFramePayloadBytes);
        var messageTracker = new WebSocketMessageTracker();
        var buffer = bufferPool.GetBuffer();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) return null;

                onRead(buffer, 0, read);

                try
                {
                    foreach (var frame in decoder.Decode(buffer, 0, read))
                    {
                        if (!ValidateWebSocketFrame(frame, direction, out var closeCode))
                        {
                            // RFC 6455 §7.2: a protocol error requires closing the connection.
                            messageTracker.Reset();
                            return closeCode;
                        }

                        // Track message boundaries and compression state per RFC 6455 §5.4.
                        // isCompressed will be true only when permessage-deflate is negotiated and RSV1
                        // is set on the opening frame; currently always false (extension stripped in
                        // Phase 1.4).
                        messageTracker.OnFrame(frame, out _, out var isFragmentationError);
                        if (isFragmentationError)
                        {
                            // A non-continuation data frame arrived while a fragmented message was still
                            // open - RFC 6455 §5.4 makes this a protocol error, not a resumable condition.
                            messageTracker.Reset();
                            return 1002;
                        }

                        var args = new WebSocketFrameInterceptEventArgs(session.Server, session.ClientConnection,
                            session, direction, frame);

                        if (session.HasWebSocketFrameInterceptHandler)
                            await session.InvokeBeforeWebSocketFrame(args).ConfigureAwait(false);

                        if (args.Action == WebSocketFrameInterceptAction.Drop)
                            continue;

                        if (args.Delay > TimeSpan.Zero)
                            await Task.Delay(args.Delay, cancellationToken).ConfigureAwait(false);

                        var wire = WebSocketFrameEncoder.Encode(args.OpCode, args.Data,
                            direction == WebSocketFrameDirection.ClientToServer, args.IsFinal);
                        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await destination.WriteAsync(wire.AsMemory(), cancellationToken)
                                .ConfigureAwait(false);
                            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                            onWrite(wire, 0, wire.Length);
                        }
                        finally
                        {
                            writeLock.Release();
                        }
                    }
                }
                catch (WebSocketProtocolException ex)
                {
                    // Raised by the decoder before any of the offending frame's payload was buffered
                    // (declared length violates the reserved-bit rule, exceeds int.MaxValue, or exceeds
                    // the configured per-frame limit) - never forwarded, so nothing to unwind here beyond
                    // reporting the close code the caller should send.
                    ProxyMetrics.ParserError("websocket");
                    messageTracker.Reset();
                    return ex.CloseCode;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }

        return null;
    }

    /// <summary>
    ///     Validates a decoded WebSocket frame against RFC 6455 protocol rules not already enforced by
    ///     <see cref="WebSocketDecoder" /> before buffering (the declared-length checks - reserved high
    ///     bit, structural <see cref="int.MaxValue" /> bound and the configured per-frame payload limit -
    ///     live there instead, so a violating frame is rejected before it is ever materialized).
    ///     Returns <see langword="false" /> (and sets <paramref name="closeCode" />) when the frame
    ///     represents a protocol violation that requires the connection to be closed.
    /// </summary>
    /// <remarks>
    ///     RSV bit validation (RSV1–3 must be zero unless an extension was negotiated) requires access
    ///     to the raw first byte before decoding; <see cref="WebSocketDecoder" /> does not currently
    ///     preserve RSV bits in <see cref="WebSocketFrame" />.  Full RSV enforcement is deferred to a
    ///     future phase that adds permessage-deflate support.
    /// </remarks>
    private static bool ValidateWebSocketFrame(
        WebSocketFrame frame,
        WebSocketFrameDirection direction,
        out ushort closeCode)
    {
        closeCode = 1002; // Protocol Error (default)

        var op = (int)frame.OpCode;

        // RFC 6455 §5.2: opcodes 0x3–0x7 and 0xB–0xF are reserved and must not be used.
        if ((op >= 3 && op <= 7) || (op >= 0xB && op <= 0xF))
        {
            closeCode = 1002;
            return false;
        }

        // RFC 6455 §5.5: control frames (Close, Ping, Pong) must have FIN=1 and payload ≤ 125 bytes.
        var isControl = op == (int)WebsocketOpCode.ConnectionClose
                        || op == (int)WebsocketOpCode.Ping
                        || op == (int)WebsocketOpCode.Pong;
        if (isControl)
        {
            if (!frame.IsFinal)
            {
                closeCode = 1002; // fragmented control frame is a protocol error
                return false;
            }

            if (frame.Data.Length > 125)
            {
                closeCode = 1002; // control frame payload too long
                return false;
            }
        }

        // RFC 6455 §7.4.1: validate Close frame status codes when a payload is present.
        if (op == (int)WebsocketOpCode.ConnectionClose && frame.Data.Length >= 2)
        {
            var statusCode = (ushort)((frame.Data.Span[0] << 8) | frame.Data.Span[1]);
            if (!IsValidCloseCode(statusCode))
            {
                closeCode = 1002;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Returns <see langword="true" /> for Close status codes that are legal to transmit on the wire
    ///     (RFC 6455 §7.4.1 / §7.4.2).
    /// </summary>
    private static bool IsValidCloseCode(ushort code)
    {
        // Explicitly forbidden codes.
        if (code == 1004 || code == 1005 || code == 1006)
            return false;

        // IANA-registered range (1000–2999) — only the defined subset is legal.
        if (code >= 1000 && code <= 1011)
            return true;

        // Private-use ranges.
        if (code >= 3000 && code <= 3999) return true;
        if (code >= 4000 && code <= 4999) return true;

        return false;
    }
}
