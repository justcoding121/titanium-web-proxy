using System;
using System.Buffers.Binary;

namespace Titanium.Http3;

/// <summary>
/// RFC 9297 capsule parser. A declared length never allocates: oversize capsules are discarded
/// as the bytes arrive. One instance is reused for the life of a tunnel.
/// </summary>
public sealed class CapsuleReader
{
    /// <summary>Largest capsule body this reader accepts by default.</summary>
    public const int DefaultMaxCapsuleBytes = 2048;

    /// <summary>Largest datagram payload this reader delivers by default.</summary>
    public const int DefaultMaxDatagramBytes = 1500;

    private readonly byte[] value;
    private readonly byte[] varint = new byte[8];
    private readonly int maxCapsuleBytes;
    private readonly int maxDatagramBytes;
    private long capsuleType;
    private long skipRemaining;
    private int varintFilled;
    private int varintNeed;
    private int valueFilled;
    private int valueNeed;
    private Phase phase = Phase.Type;

    /// <summary>Creates a reader with the given capsule and datagram limits.</summary>
    /// <param name="maxCapsuleBytes">Largest capsule body to keep. Larger bodies are discarded.</param>
    /// <param name="maxDatagramBytes">Largest datagram payload to deliver.</param>
    public CapsuleReader(int maxCapsuleBytes = DefaultMaxCapsuleBytes, int maxDatagramBytes = DefaultMaxDatagramBytes)
    {
        if (maxCapsuleBytes <= 0 || maxDatagramBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCapsuleBytes));
        }

        this.maxCapsuleBytes = maxCapsuleBytes;
        this.maxDatagramBytes = maxDatagramBytes;
        value = new byte[maxCapsuleBytes];
    }

    /// <summary>How many capsules this reader has discarded.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// Invoked for capsule types other than DATAGRAM (0). The memory is valid only for the duration of the call.
    /// </summary>
    public Action<long, ReadOnlyMemory<byte>>? OnUnknownCapsule { get; set; }

    /// <summary>Parses the next bytes and delivers each complete context-0 datagram.</summary>
    /// <param name="data">The next chunk of capsule bytes.</param>
    /// <param name="onDatagram">Called with each datagram payload. The span is valid only for the call.</param>
    public void Feed(ReadOnlySpan<byte> data, DatagramHandler onDatagram)
    {
        var index = 0;
        while (index < data.Length)
        {
            if (phase == Phase.Skip)
            {
                Skip(data, ref index);
                continue;
            }

            if (phase is Phase.Type or Phase.Length)
            {
                if (!ReadHeader(data, ref index, onDatagram))
                {
                    return;
                }

                continue;
            }

            CopyValue(data, ref index, onDatagram);
        }
    }

    /// <summary>Writes a context-0 DATAGRAM capsule.</summary>
    /// <param name="destination">The buffer that receives the capsule.</param>
    /// <param name="payload">The datagram payload.</param>
    /// <returns>The number of bytes written, or -1 when the buffer is too small.</returns>
    public static int WriteDatagram(Span<byte> destination, ReadOnlySpan<byte> payload)
    {
        var length = 1 + payload.Length;
        var needed = VarintSize(0) + VarintSize(length) + length;
        if (destination.Length < needed)
        {
            return -1;
        }

        var written = WriteVarint(destination, 0);
        written += WriteVarint(destination[written..], length);
        destination[written++] = 0;
        payload.CopyTo(destination[written..]);
        return written + payload.Length;
    }

    /// <summary>Writes a QUIC variable-length integer.</summary>
    /// <param name="destination">The buffer that receives the integer.</param>
    /// <param name="value">The integer to write.</param>
    /// <returns>The number of bytes written, or -1 when the buffer is too small.</returns>
    public static int WriteVarint(Span<byte> destination, long value)
    {
        var size = VarintSize(value);
        if (destination.Length < size)
        {
            return -1;
        }

        switch (size)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value);
                destination[0] |= 0x40;
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value);
                destination[0] |= 0x80;
                break;
            default:
                BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)value);
                destination[0] |= 0xC0;
                break;
        }

        return size;
    }

    private void Skip(ReadOnlySpan<byte> data, ref int index)
    {
        var take = (int)Math.Min(skipRemaining, data.Length - index);
        index += take;
        skipRemaining -= take;
        if (skipRemaining == 0)
        {
            phase = Phase.Type;
        }
    }

    private bool ReadHeader(ReadOnlySpan<byte> data, ref int index, DatagramHandler onDatagram)
    {
        if (!FillVarint(data, ref index))
        {
            return false;
        }

        var decoded = ReadVarint(varint, varintNeed);
        varintFilled = 0;
        if (phase == Phase.Type)
        {
            capsuleType = decoded;
            phase = Phase.Length;
            return true;
        }

        if (decoded > maxCapsuleBytes)
        {
            Dropped++;
            skipRemaining = decoded;
            phase = skipRemaining == 0 ? Phase.Type : Phase.Skip;
            return true;
        }

        valueNeed = (int)decoded;
        valueFilled = 0;
        if (valueNeed == 0)
        {
            Complete(onDatagram);
            phase = Phase.Type;
        }
        else
        {
            phase = Phase.Value;
        }

        return true;
    }

    private void CopyValue(ReadOnlySpan<byte> data, ref int index, DatagramHandler onDatagram)
    {
        var copy = Math.Min(valueNeed - valueFilled, data.Length - index);
        data.Slice(index, copy).CopyTo(value.AsSpan(valueFilled));
        valueFilled += copy;
        index += copy;
        if (valueFilled == valueNeed)
        {
            Complete(onDatagram);
            phase = Phase.Type;
        }
    }

    private bool FillVarint(ReadOnlySpan<byte> data, ref int index)
    {
        if (varintFilled == 0)
        {
            if (index >= data.Length)
            {
                return false;
            }

            varint[0] = data[index++];
            varintNeed = VarintLength(varint[0]);
            varintFilled = 1;
        }

        while (varintFilled < varintNeed && index < data.Length)
        {
            varint[varintFilled++] = data[index++];
        }

        return varintFilled == varintNeed;
    }

    private void Complete(DatagramHandler onDatagram)
    {
        if (capsuleType != 0)
        {
            OnUnknownCapsule?.Invoke(capsuleType, value.AsMemory(0, valueNeed));
            return;
        }

        var body = value.AsSpan(0, valueNeed);
        if (body.IsEmpty || !TryReadVarint(body, out var contextId, out var consumed))
        {
            Dropped++;
            return;
        }

        if (contextId != 0)
        {
            Dropped++;
            return;
        }

        var payload = body[consumed..];
        if (payload.Length > maxDatagramBytes)
        {
            Dropped++;
            return;
        }

        onDatagram(payload);
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (data.IsEmpty)
        {
            return false;
        }

        var need = VarintLength(data[0]);
        if (data.Length < need)
        {
            return false;
        }

        value = ReadVarint(data, need);
        consumed = need;
        return true;
    }

    private static long ReadVarint(ReadOnlySpan<byte> bytes, int length)
    {
        long decoded = bytes[0] & 0x3F;
        for (var i = 1; i < length; i++)
        {
            decoded = (decoded << 8) | bytes[i];
        }

        return decoded;
    }

    private static int VarintLength(byte first) => (first >> 6) switch
    {
        0 => 1,
        1 => 2,
        2 => 4,
        _ => 8,
    };

    private static int VarintSize(long value)
    {
        if (value < 64)
        {
            return 1;
        }

        if (value < 16384)
        {
            return 2;
        }

        if (value < 1_073_741_824)
        {
            return 4;
        }

        return 8;
    }

    private enum Phase
    {
        Type = 0,
        Length = 1,
        Value = 2,
        Skip = 3,
    }
}

/// <summary>Receives one datagram payload. The span is valid only for the call.</summary>
/// <param name="payload">The datagram payload.</param>
public delegate void DatagramHandler(ReadOnlySpan<byte> payload);
