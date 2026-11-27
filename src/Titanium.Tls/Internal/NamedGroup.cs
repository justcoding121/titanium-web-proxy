namespace Titanium.Tls;

/// <summary>TLS 1.3 supported-group code points this stack knows.</summary>
internal enum NamedGroup : ushort
{
    Secp256r1 = 0x0017,
    Secp384r1 = 0x0018,
    X25519 = 0x001D,
    X25519MlKem768 = 0x11EC,
}
