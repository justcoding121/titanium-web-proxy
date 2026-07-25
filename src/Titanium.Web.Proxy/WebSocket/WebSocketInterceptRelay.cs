using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        var clientToServer = RelayDirectionAsync(clientStream, serverStream, bufferPool,
            WebSocketFrameDirection.ClientToServer, maskWhenWriting: true, session,
            serverWriteLock, cancellationTokenSource,
            onRead: (b, o, c) => { /* observational reads are implied by frames; wire bytes fire on write */ },
            onWrite: (b, o, c) => session.OnDataSent(b, o, c));

        var serverToClient = RelayDirectionAsync(serverStream, clientStream, bufferPool,
            WebSocketFrameDirection.ServerToClient, maskWhenWriting: false, session,
            clientWriteLock, cancellationTokenSource,
            onRead: (_, _, _) => { },
            onWrite: (b, o, c) => session.OnDataReceived(b, o, c));

        await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
        cancellationTokenSource.Cancel();
        try
        {
            await Task.WhenAll(clientToServer, serverToClient).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            session.WebSocketServerWriter = null;
            session.WebSocketClientWriter = null;
            clientWriteLock.Dispose();
            serverWriteLock.Dispose();
        }
    }

    private static async Task RelayDirectionAsync(Stream source, Stream destination, IBufferPool bufferPool,
        WebSocketFrameDirection direction, bool maskWhenWriting, SessionEventArgs session,
        SemaphoreSlim writeLock, CancellationTokenSource cancellationTokenSource,
        Action<byte[], int, int> onRead, Action<byte[], int, int> onWrite)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var decoder = new WebSocketDecoder(bufferPool);
        var messageTracker = new WebSocketMessageTracker();
        var buffer = bufferPool.GetBuffer();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) return;

                onRead(buffer, 0, read);

                foreach (var frame in decoder.Decode(buffer, 0, read))
                {
                    if (!ValidateWebSocketFrame(frame, direction,
                            session.Server.MaxWebSocketFramePayloadBytes, out _))
                    {
                        // RFC 6455 §7.2: a protocol error requires closing the connection.
                        // Cancel both relay directions so the connection tears down cleanly.
                        messageTracker.Reset();
                        cancellationTokenSource.Cancel();
                        return;
                    }

                    // Track message boundaries and compression state per RFC 6455 §5.4.
                    // isCompressed will be true only when permessage-deflate is negotiated and RSV1
                    // is set on the opening frame; currently always false (extension stripped in Phase 1.4).
                    messageTracker.OnFrame(frame, out _);

                    var args = new WebSocketFrameInterceptEventArgs(session.Server, session.ClientConnection,
                        session, direction, frame);

                    if (session.HasWebSocketFrameInterceptHandler)
                        await session.InvokeBeforeWebSocketFrame(args).ConfigureAwait(false);

                    if (args.Action == WebSocketFrameInterceptAction.Drop)
                        continue;

                    if (args.Delay > TimeSpan.Zero)
                        await Task.Delay(args.Delay, cancellationToken).ConfigureAwait(false);

                    var wire = WebSocketFrameEncoder.Encode(args.OpCode, args.Data, maskWhenWriting, args.IsFinal);
                    await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await destination.WriteAsync(wire, 0, wire.Length, cancellationToken)
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
    }

    /// <summary>
    ///     Validates a decoded WebSocket frame against RFC 6455 protocol rules.
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
        long maxPayloadBytes,
        out ushort closeCode)
    {
        closeCode = 1002; // Protocol Error (default)

        // RFC 6455 §7.4.1: payload too large for the configured interception limit.
        if (frame.Data.Length > maxPayloadBytes)
        {
            closeCode = 1009; // Message Too Big
            return false;
        }

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
