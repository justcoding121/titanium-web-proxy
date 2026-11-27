using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// RFC 7748 X25519. Field elements are five 51-bit limbs. The ladder does not branch on the scalar.
/// </summary>
internal static class X25519
{
    private const ulong A24 = 121665;
    private static ReadOnlySpan<byte> BasePoint => [9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    internal static void PublicKey(ReadOnlySpan<byte> scalar, Span<byte> publicKey)
    {
        ScalarMult(scalar, BasePoint, publicKey);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uCoordinate, Span<byte> result)
    {
        if (scalar.Length != 32 || uCoordinate.Length != 32 || result.Length < 32)
        {
            throw new ArgumentException("X25519 inputs and the output are 32 bytes.");
        }

        Span<byte> clamped = stackalloc byte[32];
        scalar.CopyTo(clamped);
        clamped[0] &= 248;
        clamped[31] &= 127;
        clamped[31] |= 64;

        Span<byte> uBytes = stackalloc byte[32];
        uCoordinate.CopyTo(uBytes);
        uBytes[31] &= 127;

        Curve25519Field x1 = Curve25519Field.FromBytes(uBytes);
        Curve25519Field x2 = Curve25519Field.One;
        Curve25519Field z2 = default;
        Curve25519Field x3 = x1;
        Curve25519Field z3 = Curve25519Field.One;
        uint swap = 0;
        for (int bit = 254; bit >= 0; bit--)
        {
            uint current = (uint)((clamped[bit >> 3] >> (bit & 7)) & 1);
            swap ^= current;
            Curve25519Field.ConditionalSwap(ref x2, ref x3, swap);
            Curve25519Field.ConditionalSwap(ref z2, ref z3, swap);
            swap = current;

            Curve25519Field a = x2 + z2;
            Curve25519Field aa = a.Square();
            Curve25519Field b = x2 - z2;
            Curve25519Field bb = b.Square();
            Curve25519Field e = aa - bb;
            Curve25519Field c = x3 + z3;
            Curve25519Field d = x3 - z3;
            Curve25519Field da = d * a;
            Curve25519Field cb = c * b;
            Curve25519Field sum = da + cb;
            Curve25519Field difference = da - cb;
            x3 = sum.Square();
            z3 = x1 * difference.Square();
            x2 = aa * bb;
            z2 = e * (aa + (e * A24));
        }

        Curve25519Field.ConditionalSwap(ref x2, ref x3, swap);
        Curve25519Field.ConditionalSwap(ref z2, ref z3, swap);
        Curve25519Field output = x2 * z2.Invert();
        output.ToBytes(result);
        CryptographicOperations.ZeroMemory(clamped);
    }

    internal static bool TryAgree(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> peerPublic, Span<byte> shared)
    {
        ScalarMult(scalar, peerPublic, shared);
        byte accumulated = 0;
        for (int i = 0; i < 32; i++)
        {
            accumulated |= shared[i];
        }

        if (ConstantTimeEqualsZero(accumulated) != 0)
        {
            CryptographicOperations.ZeroMemory(shared[..32]);
            return false;
        }

        return true;
    }

    private static uint ConstantTimeEqualsZero(byte value)
    {
        uint widened = value;
        return (~(widened | (0u - widened))) >> 31;
    }
}

