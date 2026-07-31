using System;
using System.Buffers;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     QUIC / HTTP/3 variable-length integer encoder/decoder (RFC 9000 §16).
///     <para>
///         Format: the two most significant bits of the first byte encode the length:
///         <c>00</c> = 1 byte (6-bit value), <c>01</c> = 2 bytes (14-bit value),
///         <c>10</c> = 4 bytes (30-bit value), <c>11</c> = 8 bytes (62-bit value).
///     </para>
/// </summary>
#pragma warning disable CA1416
internal static class Http3VarInt
{
    public const ulong Max1ByteValue = (1UL << 6) - 1;   // 63
    public const ulong Max2ByteValue = (1UL << 14) - 1;  // 16,383
    public const ulong Max4ByteValue = (1UL << 30) - 1;  // 1,073,741,823
    public const ulong Max8ByteValue = (1UL << 62) - 1;  // 4,611,686,018,427,387,903

    /// <summary>
    ///     Returns the number of bytes required to encode <paramref name="value" />.
    /// </summary>
    public static int GetByteCount(ulong value)
    {
        if (value <= Max1ByteValue) return 1;
        if (value <= Max2ByteValue) return 2;
        if (value <= Max4ByteValue) return 4;
        if (value <= Max8ByteValue) return 8;
        throw new ArgumentOutOfRangeException(nameof(value), value, "Value exceeds 62-bit QUIC VarInt maximum.");
    }

    /// <summary>Writes a variable-length integer into a span. Returns the number of bytes written.</summary>
    public static int Write(Span<byte> destination, ulong value)
    {
        if (value <= Max1ByteValue)
        {
            destination[0] = (byte)value; // prefix 00
            return 1;
        }
        if (value <= Max2ByteValue)
        {
            destination[0] = (byte)(0x40 | (value >> 8));
            destination[1] = (byte)value;
            return 2;
        }
        if (value <= Max4ByteValue)
        {
            destination[0] = (byte)(0x80 | (value >> 24));
            destination[1] = (byte)(value >> 16);
            destination[2] = (byte)(value >> 8);
            destination[3] = (byte)value;
            return 4;
        }
        if (value <= Max8ByteValue)
        {
            destination[0] = (byte)(0xC0 | (value >> 56));
            destination[1] = (byte)(value >> 48);
            destination[2] = (byte)(value >> 40);
            destination[3] = (byte)(value >> 32);
            destination[4] = (byte)(value >> 24);
            destination[5] = (byte)(value >> 16);
            destination[6] = (byte)(value >> 8);
            destination[7] = (byte)value;
            return 8;
        }
        throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    ///     Attempts to read a variable-length integer from <paramref name="source" />.
    ///     Returns <see langword="false" /> if there are not enough bytes.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int bytesConsumed)
    {
        if (source.IsEmpty)
        {
            value = 0;
            bytesConsumed = 0;
            return false;
        }

        var prefix = (source[0] & 0xC0) >> 6;
        switch (prefix)
        {
            case 0:
                value = (ulong)(source[0] & 0x3F);
                bytesConsumed = 1;
                return true;
            case 1:
                if (source.Length < 2) break;
                value = ((ulong)(source[0] & 0x3F) << 8) | source[1];
                bytesConsumed = 2;
                return true;
            case 2:
                if (source.Length < 4) break;
                value = ((ulong)(source[0] & 0x3F) << 24)
                      | ((ulong)source[1] << 16)
                      | ((ulong)source[2] << 8)
                      | source[3];
                bytesConsumed = 4;
                return true;
            case 3:
                if (source.Length < 8) break;
                value = ((ulong)(source[0] & 0x3F) << 56)
                      | ((ulong)source[1] << 48)
                      | ((ulong)source[2] << 40)
                      | ((ulong)source[3] << 32)
                      | ((ulong)source[4] << 24)
                      | ((ulong)source[5] << 16)
                      | ((ulong)source[6] << 8)
                      | source[7];
                bytesConsumed = 8;
                return true;
        }

        value = 0;
        bytesConsumed = 0;
        return false;
    }

    /// <summary>
    ///     Reads a variable-length integer from a <see cref="QuicStream" />.
    ///     Returns <see langword="null" /> when the stream ends before a complete integer arrives.
    /// </summary>
    public static async ValueTask<ulong?> ReadAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var oneByte = new byte[1];
        if (!await ReadExactAsync(stream, oneByte, cancellationToken)) return null;

        var prefix = (oneByte[0] & 0xC0) >> 6;
        int remaining = prefix switch
        {
            0 => 0,
            1 => 1,
            2 => 3,
            _ => 7
        };

        var buf = new byte[1 + remaining];
        buf[0] = oneByte[0];
        if (remaining > 0 && !await ReadExactAsync(stream, buf.AsMemory(1, remaining), cancellationToken))
            return null;

        TryRead(buf, out var value, out _);
        return value;
    }

    private static async ValueTask<bool> ReadExactAsync(QuicStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
#pragma warning restore CA1416
