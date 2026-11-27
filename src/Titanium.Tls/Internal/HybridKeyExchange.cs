using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// X25519MLKEM768. The share is the ML-KEM-768 encapsulation key or ciphertext followed by the X25519
/// public key. Chrome 158 and OpenSSL 3.5.9 both use that order, and the X25519 half is a separate key
/// from a standalone X25519 share.
/// </summary>
internal sealed class HybridKeyExchange : IKeyExchangeGroup
{
    internal const int EncapsulationKeyLength = 1184;
    internal const int CiphertextLength = 1088;
    internal const int KemSecretLength = 32;
    internal const int ClientShareLength = EncapsulationKeyLength + 32;
    internal const int ServerShareLength = CiphertextLength + 32;
    internal const int SharedLength = KemSecretLength + 32;

    private readonly IMlKem768 _kem;

    public HybridKeyExchange(bool forceManaged = false)
    {
        _kem = MlKem768.Create(forceManaged);
    }

    public NamedGroup Group => NamedGroup.X25519MlKem768;

    public int PublicKeyLength => ClientShareLength;

    public int SharedSecretLength => SharedLength;

    public IPrivateKeyShare GenerateKeyShare(IEntropySource entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        return new HybridPrivateShare(_kem, entropy);
    }

    public bool TryEncapsulate(ReadOnlySpan<byte> clientShare, IEntropySource entropy, Span<byte> serverShare, Span<byte> secret, out int serverWritten, out int secretWritten)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        serverWritten = 0;
        secretWritten = 0;
        if (clientShare.Length != ClientShareLength || serverShare.Length < ServerShareLength || secret.Length < SharedLength)
        {
            return false;
        }

        Span<byte> scalar = stackalloc byte[32];
        entropy.Fill(scalar);
        try
        {
            if (!_kem.TryEncapsulate(clientShare[..EncapsulationKeyLength], serverShare[..CiphertextLength], secret[..KemSecretLength]))
            {
                return false;
            }

            X25519.PublicKey(scalar, serverShare.Slice(CiphertextLength, 32));
            if (!X25519.TryAgree(scalar, clientShare.Slice(EncapsulationKeyLength, 32), secret.Slice(KemSecretLength, 32)))
            {
                CryptographicOperations.ZeroMemory(secret[..SharedLength]);
                CryptographicOperations.ZeroMemory(serverShare[..ServerShareLength]);
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }

        serverWritten = ServerShareLength;
        secretWritten = SharedLength;
        return true;
    }

    private sealed class HybridPrivateShare : IPrivateKeyShare
    {
        private readonly IMlKem768 _kem;
        private readonly SecretBuffer _scalar = new(32);
        private readonly byte[] _public = new byte[ClientShareLength];
        private readonly byte[] _decapsulation = new byte[ManagedMlKem768.DecapsulationKeyLength];
        private readonly GCHandle _decapsulationPin;
        private bool _disposed;

        internal HybridPrivateShare(IMlKem768 kem, IEntropySource entropy)
        {
            _kem = kem;
            _decapsulationPin = GCHandle.Alloc(_decapsulation, GCHandleType.Pinned);
            _kem.GenerateKeyPair(_public.AsSpan(0, EncapsulationKeyLength), _decapsulation);
            entropy.Fill(_scalar.Span);
            X25519.PublicKey(_scalar.Span, _public.AsSpan(EncapsulationKeyLength, 32));
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
            if (peerShare.Length != ServerShareLength || secret.Length < SharedLength)
            {
                return false;
            }

            Span<byte> scalar = stackalloc byte[32];
            _scalar.Span.CopyTo(scalar);
            try
            {
                if (!_kem.TryDecapsulate(_decapsulation, peerShare[..CiphertextLength], secret[..KemSecretLength]))
                {
                    return false;
                }
                if (!X25519.TryAgree(scalar, peerShare.Slice(CiphertextLength, 32), secret.Slice(KemSecretLength, 32)))
                {
                    CryptographicOperations.ZeroMemory(secret[..SharedLength]);
                    return false;
                }
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(secret[..SharedLength]);
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(scalar);
            }

            written = SharedLength;
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
            CryptographicOperations.ZeroMemory(_decapsulation);
            CryptographicOperations.ZeroMemory(_public);
            if (_decapsulationPin.IsAllocated)
            {
                _decapsulationPin.Free();
            }
        }
    }
}
