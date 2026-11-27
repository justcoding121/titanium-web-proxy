using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// RFC 8032 Ed25519 verification only. The check is the cofactorless equation
/// [S]B = R + [k]A, which section 5.1.7 allows in place of the multiply-by-8 form.
/// Public keys of small order are rejected. There is no signing operation.
/// </summary>
internal static class Ed25519Verifier
{
    internal const int PublicKeyLength = 32;
    internal const int SignatureLength = 64;

    private static readonly BigInteger Order = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);
    private static readonly Curve25519Field D;
    private static readonly Curve25519Field SqrtNegativeOne;
    private static readonly Point BasePoint;
    private static readonly byte[] Cofactor = [8];

    static Ed25519Verifier()
    {
        D = Curve25519Field.Zero - (Curve25519Field.FromUInt64(121665) * Curve25519Field.FromUInt64(121666).Invert());
        SqrtNegativeOne = PowerOfTwo();
        Curve25519Field y = Curve25519Field.FromUInt64(4) * Curve25519Field.FromUInt64(5).Invert();
        Span<byte> encoded = stackalloc byte[PublicKeyLength];
        y.ToBytes(encoded);
        if (!TryDecode(encoded, out Point basePoint))
        {
            throw new InvalidOperationException("The Ed25519 base point did not decode.");
        }

        BasePoint = basePoint;
    }

    internal static bool Verify(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature,
        TlsAlgorithmPolicy policy = TlsAlgorithmPolicy.Unrestricted)
    {
        if (policy == TlsAlgorithmPolicy.FipsApprovedOnly)
        {
            return false;
        }

        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength)
        {
            return false;
        }

        if (!TryDecode(signature[..PublicKeyLength], out Point r) || !TryDecode(publicKey, out Point a))
        {
            return false;
        }

        if (IsSmallOrder(a))
        {
            return false;
        }

        ReadOnlySpan<byte> sBytes = signature[PublicKeyLength..];
        BigInteger s = new(sBytes, isUnsigned: true, isBigEndian: false);
        if (s < 0 || s >= Order)
        {
            return false;
        }

        Span<byte> digest = stackalloc byte[64];
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
        {
            hash.AppendData(signature[..PublicKeyLength]);
            hash.AppendData(publicKey);
            hash.AppendData(message);
            hash.GetHashAndReset(digest);
        }

        BigInteger k = new BigInteger(digest, isUnsigned: true, isBigEndian: false) % Order;
        Span<byte> kBytes = stackalloc byte[32];
        kBytes.Clear();
        if (!k.TryWriteBytes(kBytes, out _, isUnsigned: true, isBigEndian: false))
        {
            return false;
        }

        Point left = Multiply(BasePoint, sBytes);
        Point right = r.Add(Multiply(a, kBytes));
        return left.Equals(right);
    }

    internal static bool VerifyScheme(
        SignatureScheme scheme,
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature,
        TlsAlgorithmPolicy policy = TlsAlgorithmPolicy.Unrestricted)
    {
        return scheme == SignatureScheme.Ed25519 && Verify(publicKey, message, signature, policy);
    }

    private static bool IsSmallOrder(Point point)
    {
        return Multiply(point, Cofactor).Equals(Point.Identity);
    }

    private static Point Multiply(Point point, ReadOnlySpan<byte> scalar)
    {
        Point result = Point.Identity;
        Point addend = point;
        for (int i = 0; i < scalar.Length; i++)
        {
            int bits = scalar[i];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((bits & 1) == 1)
                {
                    result = result.Add(addend);
                }

                addend = addend.Double();
                bits >>= 1;
            }
        }

        return result;
    }

    private static bool TryDecode(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;
        Span<byte> yBytes = stackalloc byte[PublicKeyLength];
        encoded.CopyTo(yBytes);
        int sign = yBytes[31] >> 7;
        yBytes[31] &= 0x7F;
        Curve25519Field y = Curve25519Field.FromBytes(yBytes);
        Span<byte> canonical = stackalloc byte[PublicKeyLength];
        y.ToBytes(canonical);
        if (!canonical.SequenceEqual(yBytes))
        {
            return false;
        }

        Curve25519Field y2 = y.SquareReduced();
        Curve25519Field u = y2 - Curve25519Field.One;
        Curve25519Field v = y2.MultiplyReduced(D) + Curve25519Field.One;
        Curve25519Field v3 = v.SquareReduced().MultiplyReduced(v);
        Curve25519Field v7 = v3.SquareReduced().MultiplyReduced(v);
        Curve25519Field x = u.MultiplyReduced(v3).MultiplyReduced(Pow(u.MultiplyReduced(v7)));
        Curve25519Field vx2 = v.MultiplyReduced(x.SquareReduced());
        if (!vx2.Equals(u))
        {
            if (!vx2.Equals(Curve25519Field.Zero - u))
            {
                return false;
            }

            x = x.MultiplyReduced(SqrtNegativeOne);
        }

        Span<byte> xBytes = stackalloc byte[PublicKeyLength];
        x.ToBytes(xBytes);
        bool xIsZero = true;
        for (int i = 0; i < PublicKeyLength; i++)
        {
            if (xBytes[i] != 0)
            {
                xIsZero = false;
                break;
            }
        }

        if (xIsZero && sign == 1)
        {
            return false;
        }

        if ((xBytes[0] & 1) != sign)
        {
            x = Curve25519Field.Zero - x;
        }

        point = Point.Affine(x, y);
        return true;
    }

    /// <summary>(p-5)/8 = 2^252 - 3. Bit 1 is clear; the other bits below 252 are set.</summary>
    private static Curve25519Field Pow(Curve25519Field value)
    {
        Curve25519Field result = Curve25519Field.One;
        for (int bit = 251; bit >= 0; bit--)
        {
            result = result.SquareReduced();
            if (bit != 1)
            {
                result = result.MultiplyReduced(value);
            }
        }

        return result;
    }

    /// <summary>2^((p-1)/4). The exponent is 2^253 - 5, whose bit 2 is clear.</summary>
    private static Curve25519Field PowerOfTwo()
    {
        Curve25519Field result = Curve25519Field.One;
        Curve25519Field value = Curve25519Field.FromUInt64(2);
        for (int bit = 252; bit >= 0; bit--)
        {
            result = result.SquareReduced();
            if (bit != 2)
            {
                result = result.MultiplyReduced(value);
            }
        }

        return result;
    }

    private readonly struct Point
    {
        internal static Point Identity { get; } = new(Curve25519Field.Zero, Curve25519Field.One, Curve25519Field.One, Curve25519Field.Zero);

        private readonly Curve25519Field _x;
        private readonly Curve25519Field _y;
        private readonly Curve25519Field _z;
        private readonly Curve25519Field _t;

        private Point(Curve25519Field x, Curve25519Field y, Curve25519Field z, Curve25519Field t)
        {
            _x = x;
            _y = y;
            _z = z;
            _t = t;
        }

        internal static Point Affine(Curve25519Field x, Curve25519Field y)
        {
            return new Point(x, y, Curve25519Field.One, x.MultiplyReduced(y));
        }

        internal Point Add(Point other)
        {
            Curve25519Field two = Curve25519Field.FromUInt64(2);
            Curve25519Field a = (_y - _x).MultiplyReduced(other._y - other._x);
            Curve25519Field b = (_y + _x).MultiplyReduced(other._y + other._x);
            Curve25519Field c = _t.MultiplyReduced(other._t).MultiplyReduced(D + D);
            Curve25519Field doubledZ = _z.MultiplyReduced(other._z).MultiplyReduced(two);
            Curve25519Field e = b - a;
            Curve25519Field f = doubledZ - c;
            Curve25519Field g = doubledZ + c;
            Curve25519Field h = b + a;
            return new Point(e.MultiplyReduced(f), g.MultiplyReduced(h), f.MultiplyReduced(g), e.MultiplyReduced(h));
        }

        internal Point Double()
        {
            Curve25519Field a = _x.SquareReduced();
            Curve25519Field b = _y.SquareReduced();
            Curve25519Field c = _z.SquareReduced().MultiplyReduced(Curve25519Field.FromUInt64(2));
            Curve25519Field h = a + b;
            Curve25519Field e = h - (_x + _y).SquareReduced();
            Curve25519Field g = a - b;
            Curve25519Field f = c + g;
            return new Point(e.MultiplyReduced(f), g.MultiplyReduced(h), f.MultiplyReduced(g), e.MultiplyReduced(h));
        }

        internal readonly bool Equals(Point other)
        {
            return _x.MultiplyReduced(other._z).Equals(other._x.MultiplyReduced(_z))
                && _y.MultiplyReduced(other._z).Equals(other._y.MultiplyReduced(_z));
        }
    }
}
