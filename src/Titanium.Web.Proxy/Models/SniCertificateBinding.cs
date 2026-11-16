using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Models;

/// <summary>How an inbound TLS handshake treats a client certificate.</summary>
public enum ClientCertificateMode
{
    None = 0,
    Allow = 1,
    Require = 2,
}

/// <summary>Certificate offered when the client SNI matches one of <see cref="Hosts"/>.</summary>
public sealed class SniCertificateBinding
{
    public required IReadOnlyList<string> Hosts { get; init; }
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>
    ///     Cached UTC end of validity from <see cref="Certificate"/> at construction so the handshake
    ///     selector never reads certificate properties.
    /// </summary>
    public DateTime NotAfterUtc { get; init; }

    /// <summary>Builds a binding and caches <see cref="NotAfterUtc"/> from the certificate.</summary>
    public static SniCertificateBinding Create(IReadOnlyList<string> hosts, X509Certificate2 certificate) =>
        new()
        {
            Hosts = hosts,
            Certificate = certificate,
            NotAfterUtc = certificate.NotAfter.Kind == DateTimeKind.Utc
                ? certificate.NotAfter
                : certificate.NotAfter.ToUniversalTime(),
        };

    /// <summary>True when <see cref="NotAfterUtc"/> is still in the future at <paramref name="utcNow"/>.</summary>
    public bool IsValidAt(DateTime utcNow) => NotAfterUtc > utcNow;
}
