using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     HTTP/3 frame as read from or written to a stream (typically a QUIC stream).
///     Format: <c>Type (VarInt) | Length (VarInt) | Payload (Length bytes)</c> (RFC 9114 §7.1).
///     Payload buffers are rented from <see cref="ArrayPool{T}"/> when non-empty; call
///     <see cref="ReturnPayload"/> when finished with <see cref="Payload"/> (idempotent).
/// </summary>
internal sealed class Http3Frame
{
    private byte[]? rentedPayload;

    public ulong Type { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    private Http3Frame(ulong type, ReadOnlyMemory<byte> payload, byte[]? rented)
    {
        Type = type;
        Payload = payload;
        rentedPayload = rented;
    }

    /// <summary>
    ///     Returns a rented payload buffer to <see cref="ArrayPool{T}"/>. Safe to call more than once.
    ///     After this, <see cref="Payload"/> must not be read.
    /// </summary>
    public void ReturnPayload()
    {
        var buffer = Interlocked.Exchange(ref rentedPayload, null);
        if (buffer != null)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>
    ///     Reads one HTTP/3 frame from <paramref name="stream" />.
    ///     Returns <see langword="null" /> when the stream is cleanly closed (end-of-data).
    /// </summary>
    /// <exception cref="Http3ConnectionException">On malformed frame (e.g., huge payload).</exception>
    public static async ValueTask<Http3Frame?> ReadAsync(
        Stream stream,
        long maxPayloadBytes,
        CancellationToken cancellationToken)
    {
        var frameType = await Http3VarInt.ReadAsync(stream, cancellationToken);
        if (frameType is null) return null;

        var payloadLength = await Http3VarInt.ReadAsync(stream, cancellationToken)
            ?? throw new Http3ConnectionException(Http3ErrorCode.FrameError, "Unexpected end of stream reading frame length.");

        if (maxPayloadBytes > 0 && (long)payloadLength > maxPayloadBytes)
            throw new Http3ConnectionException(Http3ErrorCode.ExcessiveLoad,
                $"HTTP/3 frame payload {payloadLength} bytes exceeds limit {maxPayloadBytes}.");

        if (payloadLength == 0)
            return new Http3Frame(frameType.Value, ReadOnlyMemory<byte>.Empty, null);

        var length = (int)payloadLength;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(rented.AsMemory(offset, length - offset), cancellationToken);
                if (read == 0)
                    throw new Http3ConnectionException(Http3ErrorCode.FrameError,
                        $"Unexpected end of stream reading frame payload (expected {payloadLength}, got {offset}).");
                offset += read;
            }

            return new Http3Frame(frameType.Value, rented.AsMemory(0, length), rented);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    /// <summary>
    ///     Writes a frame (type + length + payload) to <paramref name="stream" />.
    /// </summary>
    public static async ValueTask WriteAsync(
        Stream stream,
        ulong frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var typeBytes = ArrayPool<byte>.Shared.Rent(8);
        var lengthBytes = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            var typeLen = Http3VarInt.Write(typeBytes, frameType);
            var lengthLen = Http3VarInt.Write(lengthBytes, (ulong)payload.Length);

            // Write header (type + length) then payload.
            await stream.WriteAsync(typeBytes.AsMemory(0, typeLen), cancellationToken);
            await stream.WriteAsync(lengthBytes.AsMemory(0, lengthLen), cancellationToken);
            if (!payload.IsEmpty)
                await stream.WriteAsync(payload, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(typeBytes);
            ArrayPool<byte>.Shared.Return(lengthBytes);
        }
    }

    /// <summary>
    ///     Writes a zero-payload frame (used for GOAWAY and some SETTINGS without parameters).
    /// </summary>
    public static async ValueTask WriteAsync(
        Stream stream,
        ulong frameType,
        CancellationToken cancellationToken)
        => await WriteAsync(stream, frameType, ReadOnlyMemory<byte>.Empty, cancellationToken);
}
