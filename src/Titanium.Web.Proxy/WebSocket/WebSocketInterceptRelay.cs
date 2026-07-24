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
            serverWriteLock, cancellationTokenSource.Token,
            onRead: (b, o, c) => { /* observational reads are implied by frames; wire bytes fire on write */ },
            onWrite: (b, o, c) => session.OnDataSent(b, o, c));

        var serverToClient = RelayDirectionAsync(serverStream, clientStream, bufferPool,
            WebSocketFrameDirection.ServerToClient, maskWhenWriting: false, session,
            clientWriteLock, cancellationTokenSource.Token,
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
        SemaphoreSlim writeLock, CancellationToken cancellationToken,
        Action<byte[], int, int> onRead, Action<byte[], int, int> onWrite)
    {
        var decoder = new WebSocketDecoder(bufferPool);
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
}
