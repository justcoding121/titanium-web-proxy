using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// NIST ECDHE. The platform generates the scalar because <see cref="ECDiffieHellman"/> has no hook for
/// <see cref="IEntropySource"/>. X25519 is the group that consumes the entropy source.
/// </summary>
internal sealed class NistKeyExchange : IKeyExchangeGroup
{
    private readonly ECCurveNamed _curve;

    internal NistKeyExchange(NamedGroup group, ECCurveNamed curve)
    {
        Group = group;
        _curve = curve;
    }

    public NamedGroup Group { get; }

    public int PublicKeyLength => FieldSize * 2 + 1;

    public int SharedSecretLength => FieldSize;

    private int FieldSize => _curve == ECCurveNamed.P256 ? 32 : 48;

    public IPrivateKeyShare GenerateKeyShare(IEntropySource entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        return new NistPrivateShare(Curve(), FieldSize);
    }

    internal IPrivateKeyShare Import(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uncompressedPublic)
    {
        return new NistPrivateShare(Curve(), FieldSize, scalar, uncompressedPublic);
    }

    private ECCurve Curve()
    {
        return _curve == ECCurveNamed.P256 ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384;
    }

    private sealed class NistPrivateShare : IPrivateKeyShare
    {
        private readonly ECCurve _curve;
        private readonly int _fieldSize;
        private readonly byte[] _public;
        private ECDiffieHellman? _key;

        internal NistPrivateShare(ECCurve curve, int fieldSize)
        {
            _curve = curve;
            _fieldSize = fieldSize;
            _key = ECDiffieHellman.Create(curve);
            ECParameters parameters = _key.ExportParameters(includePrivateParameters: false);
            _public = new byte[(fieldSize * 2) + 1];
            _public[0] = 0x04;
            CopyCoordinate(parameters.Q.X, _public.AsSpan(1, fieldSize));
            CopyCoordinate(parameters.Q.Y, _public.AsSpan(1 + fieldSize, fieldSize));
        }

        internal NistPrivateShare(ECCurve curve, int fieldSize, ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uncompressedPublic)
        {
            if (uncompressedPublic.Length != (fieldSize * 2) + 1 || uncompressedPublic[0] != 0x04 || scalar.Length != fieldSize)
            {
                throw new ArgumentException("The NIST private key and public point have the wrong length.");
            }

            _curve = curve;
            _fieldSize = fieldSize;
            _public = uncompressedPublic.ToArray();
            ECParameters parameters = new()
            {
                Curve = curve,
                D = scalar.ToArray(),
                Q = new ECPoint
                {
                    X = uncompressedPublic.Slice(1, fieldSize).ToArray(),
                    Y = uncompressedPublic.Slice(1 + fieldSize, fieldSize).ToArray(),
                },
            };
            _key = ECDiffieHellman.Create(parameters);
        }

        public void WritePublicKey(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            _public.CopyTo(destination);
        }

        public bool TryComputeSharedSecret(ReadOnlySpan<byte> peerShare, Span<byte> secret, out int written)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            written = 0;
            if (peerShare.Length != _public.Length || peerShare[0] != 0x04 || secret.Length < _fieldSize)
            {
                return false;
            }

            byte[] x = peerShare.Slice(1, _fieldSize).ToArray();
            byte[] y = peerShare.Slice(1 + _fieldSize, _fieldSize).ToArray();
            try
            {
                ECParameters peer = new()
                {
                    Curve = _curve,
                    Q = new ECPoint { X = x, Y = y },
                };
                using ECDiffieHellman peerKey = ECDiffieHellman.Create(peer);
                byte[] agreed = _key.DeriveRawSecretAgreement(peerKey.PublicKey);
                try
                {
                    if (agreed.Length > _fieldSize)
                    {
                        return false;
                    }

                    secret[.._fieldSize].Clear();
                    agreed.CopyTo(secret[(_fieldSize - agreed.Length)..]);
                    written = _fieldSize;
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(agreed);
                }
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException or PlatformNotSupportedException)
            {
                secret[.._fieldSize].Clear();
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(x);
                CryptographicOperations.ZeroMemory(y);
            }
        }

        public void Dispose()
        {
            ECDiffieHellman? key = _key;
            _key = null;
            key?.Dispose();
            CryptographicOperations.ZeroMemory(_public);
        }

        private static void CopyCoordinate(byte[]? source, Span<byte> destination)
        {
            destination.Clear();
            if (source is null || source.Length == 0)
            {
                return;
            }

            source.AsSpan(^Math.Min(source.Length, destination.Length)).CopyTo(destination[^Math.Min(source.Length, destination.Length)..]);
        }
    }
}
