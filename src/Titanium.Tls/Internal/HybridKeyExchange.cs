using System;
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

    public HybridKeyExchange()
    {
        if (!MLKem.IsSupported)
        {
            throw new PlatformNotSupportedException("ML-KEM-768 is not available on this runtime.");
        }
    }

    public NamedGroup Group => NamedGroup.X25519MlKem768;

    public int PublicKeyLength => ClientShareLength;

    public int SharedSecretLength => SharedLength;

    public IPrivateKeyShare GenerateKeyShare(IEntropySource entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        return new HybridPrivateShare(entropy);
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

        MLKem kem;
        try
        {
            kem = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, clientShare[..EncapsulationKeyLength]);
        }
        catch (CryptographicException)
        {
            return false;
        }

        using (kem)
        {
            Span<byte> scalar = stackalloc byte[32];
            entropy.Fill(scalar);
            try
            {
                kem.Encapsulate(serverShare[..CiphertextLength], secret[..KemSecretLength]);
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
        }

        serverWritten = ServerShareLength;
        secretWritten = SharedLength;
        return true;
    }

    private sealed class HybridPrivateShare : IPrivateKeyShare
    {
        private readonly SecretBuffer _scalar = new(32);
        private readonly byte[] _public = new byte[ClientShareLength];
        private MLKem? _kem;

        internal HybridPrivateShare(IEntropySource entropy)
        {
            _kem = MLKem.GenerateKey(MLKemAlgorithm.MLKem768);
            _kem.ExportEncapsulationKey(_public.AsSpan(0, EncapsulationKeyLength));
            entropy.Fill(_scalar.Span);
            X25519.PublicKey(_scalar.Span, _public.AsSpan(EncapsulationKeyLength, 32));
        }

        public void WritePublicKey(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_kem is null, this);
            _public.CopyTo(destination);
        }

        public bool TryComputeSharedSecret(ReadOnlySpan<byte> peerShare, Span<byte> secret, out int written)
        {
            ObjectDisposedException.ThrowIf(_kem is null, this);
            written = 0;
            if (peerShare.Length != ServerShareLength || secret.Length < SharedLength)
            {
                return false;
            }

            Span<byte> scalar = stackalloc byte[32];
            _scalar.Span.CopyTo(scalar);
            try
            {
                _kem.Decapsulate(peerShare[..CiphertextLength], secret[..KemSecretLength]);
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
            MLKem? kem = _kem;
            _kem = null;
            kem?.Dispose();
            _scalar.Dispose();
            CryptographicOperations.ZeroMemory(_public);
        }
    }
}
