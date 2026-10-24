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
}
