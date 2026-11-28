using System;

namespace Titanium.Http3;

/// <summary>HTTP/3 frame layout (RFC 9114 §7.1): type, length, then payload. This type does not write to a stream.</summary>
public static class Http3FrameCodec
{
    /// <summary>Default per-frame payload cap when a caller passes no positive limit.</summary>
    public const long DefaultMaxPayloadBytes = 4 * 1024 * 1024;

    /// <summary>First rent size for a payload larger than this. The buffer grows as bytes arrive.</summary>
    public const int ProgressiveRentChunkBytes = 256 * 1024;

    /// <summary>Slice size for a DATA payload that is streamed instead of rented whole.</summary>
    public const int RelayUnitBytes = 16 * 1024;

    /// <summary>
    /// Payload byte cap for one frame. HEADERS use <paramref name="fieldSectionLimit"/>.
    /// Other frames use <paramref name="dataFrameLimit"/>.
    /// </summary>
    /// <param name="frameType">The HTTP/3 frame type.</param>
    /// <param name="fieldSectionLimit">Cap for a HEADERS payload.</param>
    /// <param name="dataFrameLimit">Cap for every other payload.</param>
    /// <returns>The cap that applies to this frame.</returns>
    public static long PayloadLimitForFrameType(ulong frameType, long fieldSectionLimit, long dataFrameLimit) =>
        frameType == Http3FrameType.Headers ? fieldSectionLimit : dataFrameLimit;

    /// <summary>
    /// True when a DATA payload should be copied in slices instead of rented as one buffer.
    /// </summary>
    /// <param name="frameType">The HTTP/3 frame type.</param>
    /// <param name="length">The declared payload length.</param>
    /// <param name="frameCap">The whole-frame cap. Zero uses <see cref="DefaultMaxPayloadBytes"/>.</param>
    /// <param name="captureWholeFrame">When true, the payload is kept whole.</param>
    /// <returns>Whether the payload should be streamed in slices.</returns>
    public static bool StreamsPastFrameCap(ulong frameType, long length, long frameCap, bool captureWholeFrame)
        => !captureWholeFrame
           && frameType == Http3FrameType.Data
           && length > (frameCap > 0 ? frameCap : DefaultMaxPayloadBytes);

    /// <summary>Writes the frame type and payload length.</summary>
    /// <param name="destination">The buffer that receives the header. It must hold both variable-length integers.</param>
    /// <param name="frameType">The HTTP/3 frame type.</param>
    /// <param name="payloadLength">The payload length.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteHeader(Span<byte> destination, ulong frameType, ulong payloadLength)
    {
        var typeLen = Http3VarInt.Write(destination, frameType);
        var lengthLen = Http3VarInt.Write(destination[typeLen..], payloadLength);
        return typeLen + lengthLen;
    }

    /// <summary>Writes one complete frame.</summary>
    /// <param name="destination">The buffer that receives the frame. It must hold the header and the payload.</param>
    /// <param name="frameType">The HTTP/3 frame type.</param>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(Span<byte> destination, ulong frameType, ReadOnlySpan<byte> payload)
    {
        var headerLen = WriteHeader(destination, frameType, (ulong)payload.Length);
        if (!payload.IsEmpty)
        {
            payload.CopyTo(destination[headerLen..]);
        }

        return headerLen + payload.Length;
    }
}
