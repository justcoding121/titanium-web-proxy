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
    private X509Certificate2 certificate = null!;
    private DateTime notAfterUtc;

    public required X509Certificate2 Certificate
    {
        get => certificate;
        init
        {
            certificate = value;
            // Cached once so the handshake selector never reads certificate properties.
            notAfterUtc = value.NotAfter.Kind == DateTimeKind.Utc ? value.NotAfter : value.NotAfter.ToUniversalTime();
        }
    }

    /// <summary>UTC end of validity of <see cref="Certificate"/>, cached when the certificate is assigned.</summary>
    public DateTime NotAfterUtc => notAfterUtc;

    /// <summary>Builds a binding for the given hosts and certificate.</summary>
    public static SniCertificateBinding Create(IReadOnlyList<string> hosts, X509Certificate2 certificate) =>
        new() { Hosts = hosts, Certificate = certificate };

    /// <summary>True when <see cref="NotAfterUtc"/> is still in the future at <paramref name="utcNow"/>.</summary>
    public bool IsValidAt(DateTime utcNow) => NotAfterUtc > utcNow;
}
