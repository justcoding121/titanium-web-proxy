using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

internal sealed class X25519KeyExchange : IKeyExchangeGroup
{
    public NamedGroup Group => NamedGroup.X25519;

    public int PublicKeyLength => 32;

    public int SharedSecretLength => 32;

    public IPrivateKeyShare GenerateKeyShare(IEntropySource entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        return new X25519PrivateShare(entropy);
    }

    private sealed class X25519PrivateShare : IPrivateKeyShare
    {
        private readonly SecretBuffer _scalar = new(32);
        private readonly byte[] _public = new byte[32];
        private bool _disposed;

        internal X25519PrivateShare(IEntropySource entropy)
        {
            entropy.Fill(_scalar.Span);
            X25519.PublicKey(_scalar.Span, _public);
        }

        public void WritePublicKey(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _public.CopyTo(destination);
        }

        public bool TryComputeSharedSecret(ReadOnlySpan<byte> peerShare, Span<byte> secret, out int written)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            written = 0;
            if (peerShare.Length != 32 || secret.Length < 32)
            {
                return false;
            }

            Span<byte> scalar = stackalloc byte[32];
            _scalar.Span.CopyTo(scalar);
            try
            {
                if (!X25519.TryAgree(scalar, peerShare, secret))
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(scalar);
            }

            written = 32;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scalar.Dispose();
            CryptographicOperations.ZeroMemory(_public);
        }
    }
}
