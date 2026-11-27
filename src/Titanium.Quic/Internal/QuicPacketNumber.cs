using System;
using System.Numerics;

namespace Titanium.Quic;

/// <summary>
/// Packet number truncation from RFC 9000 Appendix A. A value outside the recovery window is rejected.
/// </summary>
internal static class QuicPacketNumber
{
    internal const ulong MaxValue = (1UL << 62) - 1;

    internal static bool TryEncode(ulong full, ulong? largestAcked, Span<byte> destination, out int written)
    {
        written = 0;
        if (full > MaxValue)
        {
            return false;
        }

        ulong unacked;
        if (largestAcked is null)
        {
            unacked = full + 1;
        }
        else if (largestAcked.Value > MaxValue || full <= largestAcked.Value)
        {
            return false;
        }
        else
        {
            unacked = full - largestAcked.Value;
        }

        int bits = BitOperations.Log2(unacked) + 1;
        int bytes = (bits + 7) >> 3;
        if (bytes > 4 || destination.Length < bytes)
        {
            return false;
        }

        for (int i = bytes - 1; i >= 0; i--)
        {
            destination[i] = (byte)full;
            full >>= 8;
        }

        written = bytes;
        return true;
    }

    internal static bool TryDecode(ulong? largest, ReadOnlySpan<byte> truncated, out ulong full)
    {
        full = 0;
        if (truncated.Length is < 1 or > 4)
        {
            return false;
        }

        if (largest is ulong seen && seen >= MaxValue)
        {
            return false;
        }

        ulong expected = largest is null ? 0 : largest.Value + 1;
        int bits = truncated.Length * 8;
        ulong window = 1UL << bits;
        ulong half = window >> 1;
        ulong mask = window - 1;
        ulong candidate = (expected & ~mask) | Read(truncated);
        if (expected >= half && candidate <= expected - half && candidate < (1UL << 62) - window)
        {
            candidate += window;
        }
        else if (candidate > expected + half && candidate >= window)
        {
            candidate -= window;
        }

        bool aboveFloor = expected < half || candidate > expected - half;
        bool belowCeil = candidate <= expected + half;
        if (!aboveFloor || !belowCeil || candidate > MaxValue)
        {
            return false;
        }

        full = candidate;
        return true;
    }

    private static ulong Read(ReadOnlySpan<byte> truncated)
    {
        ulong value = 0;
        for (int i = 0; i < truncated.Length; i++)
        {
            value = (value << 8) | truncated[i];
        }

        return value;
    }
}
