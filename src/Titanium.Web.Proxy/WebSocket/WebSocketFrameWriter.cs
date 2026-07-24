using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy;

/// <summary>
///     Injects WebSocket frames onto one side of an intercepted tunnel.
/// </summary>
public sealed class WebSocketFrameWriter
{
    private readonly Stream stream;
    private readonly bool mask;
    private readonly SemaphoreSlim writeLock;
    private readonly Action<byte[], int, int>? onBytesWritten;

    internal WebSocketFrameWriter(Stream stream, bool mask, SemaphoreSlim writeLock,
        Action<byte[], int, int>? onBytesWritten)
    {
        this.stream = stream;
        this.mask = mask;
        this.writeLock = writeLock;
        this.onBytesWritten = onBytesWritten;
    }

    /// <summary>
    ///     Writes a frame with the given opcode and payload.
    /// </summary>
    public async Task WriteAsync(WebsocketOpCode opCode, ReadOnlyMemory<byte> payload,
        bool isFinal = true, CancellationToken cancellationToken = default)
    {
        var bytes = WebSocketFrameEncoder.Encode(opCode, payload.Span, mask, isFinal);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            onBytesWritten?.Invoke(bytes, 0, bytes.Length);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    ///     Writes a UTF-8 text frame.
    /// </summary>
    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
        return WriteAsync(WebsocketOpCode.Text, payload, true, cancellationToken);
    }
}
