using System;
using System.Buffers.Binary;

namespace Titanium.Web.Proxy.Masque;

/// <summary>
///     RFC 9297 capsule parser. A declared length never allocates: oversize capsules are discarded
///     as the bytes arrive. One instance is reused for the life of a tunnel.
/// </summary>
internal sealed class CapsuleReader
{
    public const int DefaultMaxCapsuleBytes = 2048;
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

    public int Dropped { get; private set; }

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

    private enum Phase
    {
        Type = 0,
        Length = 1,
        Value = 2,
        Skip = 3,
    }
}

internal delegate void DatagramHandler(ReadOnlySpan<byte> payload);
