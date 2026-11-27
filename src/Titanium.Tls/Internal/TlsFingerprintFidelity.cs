using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>How closely a ClientHello can follow a captured browser when a primitive is missing.</summary>
internal enum TlsFingerprintFidelity
{
    Full,
    ReducedNoPostQuantum,
}

internal static class TlsFingerprint
{
    internal static TlsFingerprintFidelity Current =>
        MLKem.IsSupported ? TlsFingerprintFidelity.Full : TlsFingerprintFidelity.ReducedNoPostQuantum;
}
