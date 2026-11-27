using System.Buffers;

namespace Titanium.Tls;

/// <summary>
/// A browser ClientHello shape. The engine asks the profile for the hello and checks the
/// peer's reply against what that hello offered.
/// </summary>
public abstract class TlsFingerprintProfile
{
    /// <summary>A short profile name, such as the browser family.</summary>
    public abstract string Name { get; }

    /// <summary>The captured browser version this profile was measured from.</summary>
    public abstract string Version { get; }

    /// <summary>Whether this profile can offer every captured group.</summary>
    public abstract TlsFingerprintFidelity Fidelity { get; }

    /// <summary>Writes one or more TLS records containing a ClientHello.</summary>
    /// <param name="context">Server name, key shares, and any HelloRetryRequest state.</param>
    /// <param name="destination">Receives the record bytes.</param>
    public abstract void BuildClientHello(ClientHelloContext context, IBufferWriter<byte> destination);
}
