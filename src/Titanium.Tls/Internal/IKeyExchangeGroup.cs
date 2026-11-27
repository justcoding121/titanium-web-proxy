using System;

namespace Titanium.Tls;

internal interface IKeyExchangeGroup
{
    NamedGroup Group { get; }

    int PublicKeyLength { get; }

    int SharedSecretLength { get; }

    IPrivateKeyShare GenerateKeyShare(IEntropySource entropy);
}

internal interface IPrivateKeyShare : IDisposable
{
    void WritePublicKey(Span<byte> destination);

    bool TryComputeSharedSecret(ReadOnlySpan<byte> peerShare, Span<byte> secret, out int written);
}

internal static class KeyExchangeGroups
{
    internal static IKeyExchangeGroup Create(NamedGroup group)
    {
        return group switch
        {
            NamedGroup.X25519 => new X25519KeyExchange(),
            NamedGroup.Secp256r1 => new NistKeyExchange(group, ECCurveNamed.P256),
            NamedGroup.Secp384r1 => new NistKeyExchange(group, ECCurveNamed.P384),
            _ => throw new ArgumentOutOfRangeException(nameof(group)),
        };
    }
}

internal enum ECCurveNamed
{
    P256,
    P384,
}
