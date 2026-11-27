using System.Security.Authentication;

namespace Titanium.Tls;

/// <summary>The peer selected a TLS version below 1.3.</summary>
public sealed class TlsVersionNotSupportedException : AuthenticationException
{
    /// <summary>Creates the exception.</summary>
    public TlsVersionNotSupportedException()
        : base("The remote party selected a TLS version below 1.3.")
    {
    }
}
