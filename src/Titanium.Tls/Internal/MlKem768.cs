using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

internal interface IMlKem768
{
    void GenerateKeyPair(Span<byte> encapsulationKey, Span<byte> decapsulationKey);

    void GenerateKeyPair(Span<byte> encapsulationKey, Span<byte> decapsulationKey, ReadOnlySpan<byte> seedD, ReadOnlySpan<byte> seedZ);

    bool TryEncapsulate(ReadOnlySpan<byte> encapsulationKey, Span<byte> ciphertext, Span<byte> sharedSecret);

    bool TryEncapsulate(ReadOnlySpan<byte> encapsulationKey, ReadOnlySpan<byte> message, Span<byte> ciphertext, Span<byte> sharedSecret);

    bool TryDecapsulate(ReadOnlySpan<byte> decapsulationKey, ReadOnlySpan<byte> ciphertext, Span<byte> sharedSecret);
}

internal static class MlKem768
{
    internal static IMlKem768 Create(bool forceManaged = false)
    {
        if (!forceManaged && MLKem.IsSupported)
        {
            return BclMlKem768.Instance;
        }

        return ManagedMlKem768.Instance;
    }
}

internal sealed class BclMlKem768 : IMlKem768
{
    internal static readonly BclMlKem768 Instance = new();

    public void GenerateKeyPair(Span<byte> encapsulationKey, Span<byte> decapsulationKey)
    {
        using MLKem kem = MLKem.GenerateKey(MLKemAlgorithm.MLKem768);
        kem.ExportEncapsulationKey(encapsulationKey);
        kem.ExportDecapsulationKey(decapsulationKey);
    }

    public void GenerateKeyPair(Span<byte> encapsulationKey, Span<byte> decapsulationKey, ReadOnlySpan<byte> seedD, ReadOnlySpan<byte> seedZ)
    {
        _ = encapsulationKey;
        _ = decapsulationKey;
        _ = seedD;
        _ = seedZ;
        throw new NotSupportedException("The platform ML-KEM key generator does not accept external seeds.");
    }

    public bool TryEncapsulate(ReadOnlySpan<byte> encapsulationKey, Span<byte> ciphertext, Span<byte> sharedSecret)
    {
        try
        {
            using MLKem kem = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, encapsulationKey);
            kem.Encapsulate(ciphertext, sharedSecret);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public bool TryEncapsulate(ReadOnlySpan<byte> encapsulationKey, ReadOnlySpan<byte> message, Span<byte> ciphertext, Span<byte> sharedSecret)
    {
        _ = encapsulationKey;
        _ = message;
        _ = ciphertext;
        _ = sharedSecret;
        throw new NotSupportedException("The platform ML-KEM encapsulation does not accept an external message.");
    }

    public bool TryDecapsulate(ReadOnlySpan<byte> decapsulationKey, ReadOnlySpan<byte> ciphertext, Span<byte> sharedSecret)
    {
        try
        {
            using MLKem kem = MLKem.ImportDecapsulationKey(MLKemAlgorithm.MLKem768, decapsulationKey);
            kem.Decapsulate(ciphertext, sharedSecret);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
