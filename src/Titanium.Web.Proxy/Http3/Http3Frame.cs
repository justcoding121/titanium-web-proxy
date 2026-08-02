using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     HTTP/3 frame as read from or written to a stream (typically a QUIC stream).
///     Format: <c>Type (VarInt) | Length (VarInt) | Payload (Length bytes)</c> (RFC 9114 §7.1).
/// </summary>
internal sealed class Http3Frame
{
    public ulong Type { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }

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

        byte[] payload;
        if (payloadLength == 0)
        {
            payload = Array.Empty<byte>();
        }
        else
        {
            payload = new byte[payloadLength];
            var offset = 0;
            while (offset < payload.Length)
            {
                var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken);
                if (read == 0)
                    throw new Http3ConnectionException(Http3ErrorCode.FrameError,
                        $"Unexpected end of stream reading frame payload (expected {payloadLength}, got {offset}).");
                offset += read;
            }
        }

        return new Http3Frame { Type = frameType.Value, Payload = payload };
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
        var typeBytes = new byte[8];
        var typeLen = Http3VarInt.Write(typeBytes, frameType);

        var lengthBytes = new byte[8];
        var lengthLen = Http3VarInt.Write(lengthBytes, (ulong)payload.Length);

        // Write header (type + length) then payload.
        await stream.WriteAsync(typeBytes.AsMemory(0, typeLen), cancellationToken);
        await stream.WriteAsync(lengthBytes.AsMemory(0, lengthLen), cancellationToken);
        if (!payload.IsEmpty)
            await stream.WriteAsync(payload, cancellationToken);
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
