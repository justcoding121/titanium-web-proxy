using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private IReadOnlyList<TlsCipherSuite>? listenerCipherSuites;
    private CipherSuitesPolicy? listenerCipherSuitesPolicy;
    private bool listenerCipherSuitesRejected;

    /// <summary>
    ///     Lowest TLS version a client-facing handshake accepts (<see cref="SslProtocols.Tls12" /> or
    ///     <see cref="SslProtocols.Tls13" />). It trims <see cref="SupportedSslProtocols" /> for listeners
    ///     (explicit, transparent, and MASQUE) and never widens it; outbound handshakes are unchanged.
    ///     Default <see cref="SslProtocols.None" /> leaves <see cref="SupportedSslProtocols" /> as the only limit.
    ///     When the minimum excludes every supported protocol, listeners accept the minimum and above.
    /// </summary>
    public SslProtocols ListenerMinimumSslProtocol { get; set; }

    /// <summary>
    ///     Cipher suites a client-facing handshake may negotiate. Null (the default) keeps the OS defaults.
    ///     .NET has no cipher suite policy on Windows, where this setting is ignored and a warning is logged on
    ///     first use. Excluding every TLS 1.3 suite disables TLS 1.3 for listeners.
    /// </summary>
    public IReadOnlyList<TlsCipherSuite>? ListenerCipherSuites
    {
        get => listenerCipherSuites;
        set
        {
            listenerCipherSuites = value;
            listenerCipherSuitesPolicy = null;
            listenerCipherSuitesRejected = false;
        }
    }

    /// <summary>
    ///     Lets listener certificates be stapled with an OCSP response. Forwards to
    ///     <see cref="Certificates.CertificateManager.OcspStapling" />. Default <see langword="false" />.
    /// </summary>
    public bool OcspStapling
    {
        get => CertificateManager.OcspStapling;
        set => CertificateManager.OcspStapling = value;
    }

    /// <summary>The protocols a client-facing handshake enables.</summary>
    internal SslProtocols ListenerSslProtocols =>
        ApplyMinimumProtocol(SupportedSslProtocols, ListenerMinimumSslProtocol);

    /// <summary>
    ///     Keeps the protocols at or above <paramref name="minimum" />. <see cref="SslProtocols.None" /> means
    ///     "OS default" to <see cref="SslStream" />, so an empty result becomes the minimum and every newer
    ///     modern protocol instead of silently widening to the OS default.
    /// </summary>
    internal static SslProtocols ApplyMinimumProtocol(SslProtocols supported, SslProtocols minimum)
    {
        if (minimum == SslProtocols.None)
        {
            return supported;
        }

        // Each SslProtocols value is a pair of bits; everything below the lowest bit of the minimum goes.
        var lowest = (int)minimum & -(int)minimum;
        var kept = supported & (SslProtocols)~(lowest - 1);
        if (kept != SslProtocols.None)
        {
            return kept;
        }

        return minimum == SslProtocols.Tls13 ? SslProtocols.Tls13 : SslProtocols.Tls12 | SslProtocols.Tls13;
    }

    /// <summary>
    ///     Applies the listener TLS policy (protocols and, where the OS supports it, cipher suites) to the
    ///     options of a client-facing handshake.
    /// </summary>
    internal void ApplyListenerTlsPolicy(SslServerAuthenticationOptions options)
    {
        options.EnabledSslProtocols = ListenerSslProtocols;
        var policy = GetListenerCipherSuitesPolicy();
        if (policy is not null)
        {
            options.CipherSuitesPolicy = policy;
        }
    }

    private CipherSuitesPolicy? GetListenerCipherSuitesPolicy()
    {
        var suites = listenerCipherSuites;
        if (suites is not { Count: > 0 } || listenerCipherSuitesRejected)
        {
            return null;
        }

        var cached = listenerCipherSuitesPolicy;
        if (cached is not null)
        {
            return cached;
        }

        if (OperatingSystem.IsWindows())
        {
            listenerCipherSuitesRejected = true;
            logger.LogWarning(
                "ListenerCipherSuites is ignored on Windows: .NET has no cipher suite policy there.");
            return null;
        }

        try
        {
            return listenerCipherSuitesPolicy = new CipherSuitesPolicy(suites);
        }
        catch (PlatformNotSupportedException ex)
        {
            listenerCipherSuitesRejected = true;
            logger.LogWarning(ex, "ListenerCipherSuites is not supported on this platform and is ignored.");
            return null;
        }
    }
}
