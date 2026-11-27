namespace Titanium.Tls;

/// <summary>Which algorithms a handshake may offer or select.</summary>
public enum TlsAlgorithmPolicy
{
    /// <summary>The stack's ordinary TLS 1.3 set.</summary>
    Default = 0,

    /// <summary>AES-GCM, NIST P-256 and P-384, and the approved ECDSA and RSA-PSS schemes. Ed25519 is rejected.</summary>
    FipsApprovedOnly = 1,
}
