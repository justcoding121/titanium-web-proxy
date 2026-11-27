namespace Titanium.Tls;

/// <summary>
/// TLS 1.3 signature schemes this stack verifies. Ed25519 is 0x0807.
/// </summary>
internal enum SignatureScheme : ushort
{
    Ed25519 = 0x0807,
}
