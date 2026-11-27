using System;
using System.Numerics;

namespace Titanium.Quic;

/// <summary>
/// One ChaCha20 block (RFC 8439 section 2.3). The counter is a parameter because QUIC header protection takes it from the sample.
/// </summary>
internal static class ChaCha20Block
{
    internal static void Write(ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> nonce, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(key.Length, 32);
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, 12);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, 64);
        Span<uint> state = stackalloc uint[16];
        Span<uint> working = stackalloc uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++)
        {
            state[4 + i] = Word(key, i * 4);
        }

        state[12] = counter;
        state[13] = Word(nonce, 0);
        state[14] = Word(nonce, 4);
        state[15] = Word(nonce, 8);
        state.CopyTo(working);
        for (int i = 0; i < 10; i++)
        {
            Quarter(ref working[0], ref working[4], ref working[8], ref working[12]);
            Quarter(ref working[1], ref working[5], ref working[9], ref working[13]);
            Quarter(ref working[2], ref working[6], ref working[10], ref working[14]);
            Quarter(ref working[3], ref working[7], ref working[11], ref working[15]);
            Quarter(ref working[0], ref working[5], ref working[10], ref working[15]);
            Quarter(ref working[1], ref working[6], ref working[11], ref working[12]);
            Quarter(ref working[2], ref working[7], ref working[8], ref working[13]);
            Quarter(ref working[3], ref working[4], ref working[9], ref working[14]);
        }

        for (int i = 0; i < 16; i++)
        {
            working[i] += state[i];
            int at = i * 4;
            destination[at] = (byte)working[i];
            destination[at + 1] = (byte)(working[i] >> 8);
            destination[at + 2] = (byte)(working[i] >> 16);
            destination[at + 3] = (byte)(working[i] >> 24);
        }

        state.Clear();
        working.Clear();
    }

    private static uint Word(ReadOnlySpan<byte> source, int offset)
    {
        return source[offset]
            | ((uint)source[offset + 1] << 8)
            | ((uint)source[offset + 2] << 16)
            | ((uint)source[offset + 3] << 24);
    }

    private static void Quarter(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b;
        d ^= a;
        d = BitOperations.RotateLeft(d, 16);
        c += d;
        b ^= c;
        b = BitOperations.RotateLeft(b, 12);
        a += b;
        d ^= a;
        d = BitOperations.RotateLeft(d, 8);
        c += d;
        b ^= c;
        b = BitOperations.RotateLeft(b, 7);
    }
}
