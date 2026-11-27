using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

/// <summary>Options the BCL authentication types do not carry.</summary>
public sealed class TlsStreamOptions
{
    /// <summary>The ClientHello profile. The engine hello does not use it yet; <see cref="TlsStream.FingerprintFidelity"/> still reports it.</summary>
    public TlsFingerprintProfile? FingerprintProfile { get; set; }

    /// <summary>When true, a transport end without <c>close_notify</c> throws. The default matches <c>SslStream</c> and also throws.</summary>
    public bool StrictCloseNotify { get; set; }

    /// <summary>Optional NSS key log. Leave null except while debugging.</summary>
    public ITlsKeyLogSink? KeyLog { get; set; }

    internal ITlsSessionCache? SessionCache { get; set; }

    internal IEntropySource? Entropy { get; set; }

    internal X509Certificate2Collection? TrustAnchors { get; set; }

    internal NamedGroup[]? Groups { get; set; }

    internal ushort[]? CipherSuites { get; set; }

    internal ITicketProtector? Tickets { get; set; }

    internal bool RequestClientCertificate { get; set; }

    internal X509Certificate2Collection? ClientTrustAnchors { get; set; }
}

/// <summary>Selects server authentication options after the ClientHello arrives.</summary>
/// <param name="stream">The stream that received the hello.</param>
/// <param name="hello">The server name and the protocols the stack offered.</param>
/// <param name="state">The state passed to <see cref="TlsStream.AuthenticateAsServerAsync(TlsServerOptionsSelectionCallback, object?, System.Threading.CancellationToken)"/>.</param>
/// <param name="cancellationToken">Cancels the selection.</param>
/// <returns>The options used for the rest of the handshake.</returns>
public delegate System.Threading.Tasks.ValueTask<System.Net.Security.SslServerAuthenticationOptions> TlsServerOptionsSelectionCallback(
    TlsStream stream,
    System.Net.Security.SslClientHelloInfo hello,
    object? state,
    System.Threading.CancellationToken cancellationToken);
