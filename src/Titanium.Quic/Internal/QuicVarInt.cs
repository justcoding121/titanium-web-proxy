using System;

namespace Titanium.Quic;

/// <summary>RFC 9000 section 16 variable-length integers. Writers use the shortest form.</summary>
internal static class QuicVarInt
{
    internal const ulong MaxValue = (1UL << 62) - 1;

    internal static int Encode(ulong value, Span<byte> destination)
    {
        int length = Length(value);
        if (destination.Length < length)
        {
            return 0;
        }

        switch (length)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 2:
                Write(destination, (ushort)(value | 0x4000));
                break;
            case 4:
                Write(destination, (uint)value | 0x8000_0000);
                break;
            default:
                Write(destination, value | 0xC000_0000_0000_0000);
                break;
        }

        return length;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> source, out ulong value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (source.IsEmpty)
        {
            return false;
        }

        int length = 1 << (source[0] >> 6);
        if (source.Length < length)
        {
            return false;
        }

        value = source[0] & 0x3Fu;
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | source[i];
        }

        consumed = length;
        return true;
    }

    private static int Length(ulong value)
    {
        if (value <= 63)
        {
            return 1;
        }

        if (value <= 16_383)
        {
            return 2;
        }

        if (value <= 1_073_741_823)
        {
            return 4;
        }

        if (value <= MaxValue)
        {
            return 8;
        }

        throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static void Write(Span<byte> destination, ushort value)
    {
        destination[0] = (byte)(value >> 8);
        destination[1] = (byte)value;
    }

    private static void Write(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static void Write(Span<byte> destination, ulong value)
    {
        destination[0] = (byte)(value >> 56);
        destination[1] = (byte)(value >> 48);
        destination[2] = (byte)(value >> 40);
        destination[3] = (byte)(value >> 32);
        destination[4] = (byte)(value >> 24);
        destination[5] = (byte)(value >> 16);
        destination[6] = (byte)(value >> 8);
        destination[7] = (byte)value;
    }
}
