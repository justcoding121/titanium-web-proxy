using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>How closely a ClientHello can follow a captured browser when a primitive is missing.</summary>
public enum TlsFingerprintFidelity
{
    /// <summary>The profile can offer the post-quantum hybrid group.</summary>
    Full,

    /// <summary>The profile omits the post-quantum hybrid group.</summary>
    ReducedNoPostQuantum,
}

internal static class TlsFingerprint
{
    internal static TlsFingerprintFidelity Current =>
        MLKem.IsSupported ? TlsFingerprintFidelity.Full : TlsFingerprintFidelity.ReducedNoPostQuantum;
}
