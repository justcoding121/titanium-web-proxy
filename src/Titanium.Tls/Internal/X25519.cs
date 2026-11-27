using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// RFC 7748 X25519. Field elements are five 51-bit limbs. The ladder does not branch on the scalar.
/// </summary>
internal static class X25519
{
    private const int LimbBits = 51;
    private const ulong LimbMask = (1UL << LimbBits) - 1;
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

        FieldElement x1 = FieldElement.FromBytes(uBytes);
        FieldElement x2 = FieldElement.One;
        FieldElement z2 = default;
        FieldElement x3 = x1;
        FieldElement z3 = FieldElement.One;
        uint swap = 0;
        for (int bit = 254; bit >= 0; bit--)
        {
            uint current = (uint)((clamped[bit >> 3] >> (bit & 7)) & 1);
            swap ^= current;
            FieldElement.ConditionalSwap(ref x2, ref x3, swap);
            FieldElement.ConditionalSwap(ref z2, ref z3, swap);
            swap = current;

            FieldElement a = x2 + z2;
            FieldElement aa = a.Square();
            FieldElement b = x2 - z2;
            FieldElement bb = b.Square();
            FieldElement e = aa - bb;
            FieldElement c = x3 + z3;
            FieldElement d = x3 - z3;
            FieldElement da = d * a;
            FieldElement cb = c * b;
            FieldElement sum = da + cb;
            FieldElement difference = da - cb;
            x3 = sum.Square();
            z3 = x1 * difference.Square();
            x2 = aa * bb;
            z2 = e * (aa + (e * A24));
        }

        FieldElement.ConditionalSwap(ref x2, ref x3, swap);
        FieldElement.ConditionalSwap(ref z2, ref z3, swap);
        FieldElement output = x2 * z2.Invert();
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

    private struct FieldElement
    {
        private ulong _z0;
        private ulong _z1;
        private ulong _z2;
        private ulong _z3;
        private ulong _z4;

        internal static FieldElement One => new() { _z0 = 1 };

        internal static FieldElement FromBytes(ReadOnlySpan<byte> bytes)
        {
            ulong b0 = Load(bytes, 0);
            ulong b1 = Load(bytes, 6);
            ulong b2 = Load(bytes, 12);
            ulong b3 = Load(bytes, 19);
            ulong b4 = Load(bytes, 24);
            FieldElement value = default;
            value._z0 = b0 & LimbMask;
            value._z1 = (b1 >> 3) & LimbMask;
            value._z2 = (b2 >> 6) & LimbMask;
            value._z3 = (b3 >> 1) & LimbMask;
            value._z4 = (b4 >> 12) & LimbMask;
            return value;
        }

        internal void ToBytes(Span<byte> destination)
        {
            FieldElement reduced = CarryFull(this);
            UInt128 q = ((UInt128)reduced._z0 + 19) >> LimbBits;
            q = ((UInt128)reduced._z1 + q) >> LimbBits;
            q = ((UInt128)reduced._z2 + q) >> LimbBits;
            q = ((UInt128)reduced._z3 + q) >> LimbBits;
            q = ((UInt128)reduced._z4 + q) >> LimbBits;
            reduced._z0 += (ulong)(19 * q);
            ulong carryOut = reduced._z0 >> LimbBits;
            reduced._z0 &= LimbMask;
            reduced._z1 += carryOut;
            carryOut = reduced._z1 >> LimbBits;
            reduced._z1 &= LimbMask;
            reduced._z2 += carryOut;
            carryOut = reduced._z2 >> LimbBits;
            reduced._z2 &= LimbMask;
            reduced._z3 += carryOut;
            carryOut = reduced._z3 >> LimbBits;
            reduced._z3 &= LimbMask;
            reduced._z4 = (reduced._z4 + carryOut) & LimbMask;

            Span<byte> packed = stackalloc byte[32];
            Store(packed, 0, reduced._z0 | (reduced._z1 << 51));
            Store(packed, 8, (reduced._z1 >> 13) | (reduced._z2 << 38));
            Store(packed, 16, (reduced._z2 >> 26) | (reduced._z3 << 25));
            Store(packed, 24, (reduced._z3 >> 39) | (reduced._z4 << 12));
            packed.CopyTo(destination);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal FieldElement Square()
        {
            ulong a0 = _z0;
            ulong a1 = _z1;
            ulong a2 = _z2;
            ulong a3 = _z3;
            ulong a4 = _z4;
            ulong z0 = 0, z0h = 0, z1 = 0, z1h = 0, z2 = 0, z2h = 0, z3 = 0, z3h = 0, z4 = 0, z4h = 0;
            Product(ref z0, ref z0h, a0, a0);
            Product(ref z0, ref z0h, a1, a4, 38);
            Product(ref z0, ref z0h, a2, a3, 38);
            Product(ref z1, ref z1h, a0, a1, 2);
            Product(ref z1, ref z1h, a2, a4, 38);
            Product(ref z1, ref z1h, a3, a3, 19);
            Product(ref z2, ref z2h, a0, a2, 2);
            Product(ref z2, ref z2h, a1, a1);
            Product(ref z2, ref z2h, a3, a4, 38);
            Product(ref z3, ref z3h, a0, a3, 2);
            Product(ref z3, ref z3h, a1, a2, 2);
            Product(ref z3, ref z3h, a4, a4, 19);
            Product(ref z4, ref z4h, a0, a4, 2);
            Product(ref z4, ref z4h, a1, a3, 2);
            Product(ref z4, ref z4h, a2, a2);
            return CarryWide(z0, z0h, z1, z1h, z2, z2h, z3, z3h, z4, z4h);
        }

        internal FieldElement Invert()
        {
            // a^(2^255 - 21). The exponent is public. Bits 2 and 4 are clear; the rest below bit 255 are set.
            FieldElement result = One;
            for (int bit = 254; bit >= 0; bit--)
            {
                result = result.Square();
                if (bit is not 2 and not 4)
                {
                    result *= this;
                }
            }

            return result;
        }

        internal static void ConditionalSwap(ref FieldElement left, ref FieldElement right, uint swap)
        {
            ulong mask = 0UL - swap;
            Swap(ref left._z0, ref right._z0, mask);
            Swap(ref left._z1, ref right._z1, mask);
            Swap(ref left._z2, ref right._z2, mask);
            Swap(ref left._z3, ref right._z3, mask);
            Swap(ref left._z4, ref right._z4, mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FieldElement operator +(FieldElement left, FieldElement right)
        {
            FieldElement sum = default;
            sum._z0 = left._z0 + right._z0;
            sum._z1 = left._z1 + right._z1;
            sum._z2 = left._z2 + right._z2;
            sum._z3 = left._z3 + right._z3;
            sum._z4 = left._z4 + right._z4;
            return sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FieldElement operator -(FieldElement left, FieldElement right)
        {
            FieldElement difference = default;
            difference._z0 = (left._z0 + 0xFFFFFFFFFFFDAUL) - right._z0;
            difference._z1 = (left._z1 + 0xFFFFFFFFFFFFEUL) - right._z1;
            difference._z2 = (left._z2 + 0xFFFFFFFFFFFFEUL) - right._z2;
            difference._z3 = (left._z3 + 0xFFFFFFFFFFFFEUL) - right._z3;
            difference._z4 = (left._z4 + 0xFFFFFFFFFFFFEUL) - right._z4;
            return difference;
        }

        public static FieldElement operator *(FieldElement left, ulong small)
        {
            ulong z0 = 0, z0h = 0, z1 = 0, z1h = 0, z2 = 0, z2h = 0, z3 = 0, z3h = 0, z4 = 0, z4h = 0;
            Product(ref z0, ref z0h, left._z0, small);
            Product(ref z1, ref z1h, left._z1, small);
            Product(ref z2, ref z2h, left._z2, small);
            Product(ref z3, ref z3h, left._z3, small);
            Product(ref z4, ref z4h, left._z4, small);
            return CarryWide(z0, z0h, z1, z1h, z2, z2h, z3, z3h, z4, z4h);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FieldElement operator *(FieldElement left, FieldElement right)
        {
            ulong z0 = 0, z0h = 0, z1 = 0, z1h = 0, z2 = 0, z2h = 0, z3 = 0, z3h = 0, z4 = 0, z4h = 0;
            Product(ref z0, ref z0h, left._z0, right._z0);
            Product(ref z1, ref z1h, left._z0, right._z1);
            Product(ref z1, ref z1h, left._z1, right._z0);
            Product(ref z2, ref z2h, left._z0, right._z2);
            Product(ref z2, ref z2h, left._z1, right._z1);
            Product(ref z2, ref z2h, left._z2, right._z0);
            Product(ref z3, ref z3h, left._z0, right._z3);
            Product(ref z3, ref z3h, left._z1, right._z2);
            Product(ref z3, ref z3h, left._z2, right._z1);
            Product(ref z3, ref z3h, left._z3, right._z0);
            Product(ref z4, ref z4h, left._z0, right._z4);
            Product(ref z4, ref z4h, left._z1, right._z3);
            Product(ref z4, ref z4h, left._z2, right._z2);
            Product(ref z4, ref z4h, left._z3, right._z1);
            Product(ref z4, ref z4h, left._z4, right._z0);
            Product(ref z0, ref z0h, left._z1, right._z4, 19);
            Product(ref z0, ref z0h, left._z2, right._z3, 19);
            Product(ref z0, ref z0h, left._z3, right._z2, 19);
            Product(ref z0, ref z0h, left._z4, right._z1, 19);
            Product(ref z1, ref z1h, left._z2, right._z4, 19);
            Product(ref z1, ref z1h, left._z3, right._z3, 19);
            Product(ref z1, ref z1h, left._z4, right._z2, 19);
            Product(ref z2, ref z2h, left._z3, right._z4, 19);
            Product(ref z2, ref z2h, left._z4, right._z3, 19);
            Product(ref z3, ref z3h, left._z4, right._z4, 19);
            return CarryWide(z0, z0h, z1, z1h, z2, z2h, z3, z3h, z4, z4h);
        }

        private static FieldElement CarryFull(FieldElement value)
        {
            return CarryLimbs(value._z0, value._z1, value._z2, value._z3, value._z4);
        }

        private static FieldElement CarryLimbs(ulong z0, ulong z1, ulong z2, ulong z3, ulong z4)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                z1 += z0 >> LimbBits;
                z0 &= LimbMask;
                z2 += z1 >> LimbBits;
                z1 &= LimbMask;
                z3 += z2 >> LimbBits;
                z2 &= LimbMask;
                z4 += z3 >> LimbBits;
                z3 &= LimbMask;
                z0 += (z4 >> LimbBits) * 19;
                z4 &= LimbMask;
            }

            z1 += z0 >> LimbBits;
            z0 &= LimbMask;
            FieldElement result = default;
            result._z0 = z0;
            result._z1 = z1;
            result._z2 = z2;
            result._z3 = z3;
            result._z4 = z4;
            return result;
        }

        private static FieldElement CarryWide(
            ulong z0, ulong z0h, ulong z1, ulong z1h, ulong z2, ulong z2h, ulong z3, ulong z3h, ulong z4, ulong z4h)
        {
            Propagate(ref z0, ref z0h, ref z1, ref z1h);
            Propagate(ref z1, ref z1h, ref z2, ref z2h);
            Propagate(ref z2, ref z2h, ref z3, ref z3h);
            Propagate(ref z3, ref z3h, ref z4, ref z4h);
            Wrap(ref z4, ref z4h, ref z0, ref z0h);
            Propagate(ref z0, ref z0h, ref z1, ref z1h);
            Propagate(ref z1, ref z1h, ref z2, ref z2h);
            Propagate(ref z2, ref z2h, ref z3, ref z3h);
            Propagate(ref z3, ref z3h, ref z4, ref z4h);
            Wrap(ref z4, ref z4h, ref z0, ref z0h);
            Propagate(ref z0, ref z0h, ref z1, ref z1h);

            FieldElement result = default;
            result._z0 = z0;
            result._z1 = z1;
            result._z2 = z2;
            result._z3 = z3;
            result._z4 = z4;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Product(ref ulong lo, ref ulong hi, ulong left, ulong right)
        {
            ulong high = Math.BigMul(left, right, out ulong low);
            ulong sum = lo + low;
            hi += high + (sum < lo ? 1UL : 0UL);
            lo = sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Product(ref ulong lo, ref ulong hi, ulong left, ulong right, ulong scale)
        {
            ulong high = Math.BigMul(left, right, out ulong low);
            ulong scaledHigh = Math.BigMul(low, scale, out ulong scaledLow);
            _ = Math.BigMul(high, scale, out ulong highScaleLow);
            ulong sum = lo + scaledLow;
            ulong carry = sum < lo ? 1UL : 0UL;
            ulong mid = scaledHigh + highScaleLow;
            ulong midCarry = mid < scaledHigh ? 1UL : 0UL;
            ulong hiSum = hi + mid + carry;
            lo = sum;
            hi = hiSum + midCarry;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Propagate(ref ulong lo, ref ulong hi, ref ulong next, ref ulong nextHi)
        {
            ulong carry = (lo >> LimbBits) | (hi << (64 - LimbBits));
            ulong carryHi = hi >> LimbBits;
            lo &= LimbMask;
            hi = 0;
            ulong sum = next + carry;
            nextHi += carryHi + (sum < next ? 1UL : 0UL);
            next = sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Wrap(ref ulong lo, ref ulong hi, ref ulong next, ref ulong nextHi)
        {
            ulong carry = (lo >> LimbBits) | (hi << (64 - LimbBits));
            ulong carryHi = hi >> LimbBits;
            lo &= LimbMask;
            hi = 0;
            ulong high = Math.BigMul(carry, 19, out ulong low);
            _ = Math.BigMul(carryHi, 19, out ulong carryHiLow);
            ulong sum = next + low;
            nextHi += high + carryHiLow + (sum < next ? 1UL : 0UL);
            next = sum;
        }

        private static ulong Load(ReadOnlySpan<byte> bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
        }

        private static void Store(Span<byte> destination, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, 8), value);
        }

        private static void Swap(ref ulong left, ref ulong right, ulong mask)
        {
            ulong difference = (left ^ right) & mask;
            left ^= difference;
            right ^= difference;
        }
    }
}
