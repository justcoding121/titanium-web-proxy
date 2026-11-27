namespace Titanium.Tls;

/// <summary>
/// Which algorithms a handshake may offer. FIPS-approved-only mode drops Ed25519.
/// </summary>
internal enum TlsAlgorithmPolicy
{
    Unrestricted = 0,
    FipsApprovedOnly = 1,
}
