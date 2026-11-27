using System.Security.Authentication;

namespace Titanium.Tls;

/// <summary>A fatal TLS alert ended the connection.</summary>
public sealed class TlsAlertException : AuthenticationException
{
    /// <summary>Creates the exception for an alert this stack sent or received.</summary>
    /// <param name="description">The alert description.</param>
    /// <param name="sent">True when this stack sent the alert.</param>
    public TlsAlertException(byte description, bool sent)
        : base(sent
            ? "Authentication failed because the local side sent TLS alert " + description + "."
            : "Authentication failed because the remote party sent TLS alert " + description + ".")
    {
        Description = description;
        WasSent = sent;
    }

    /// <summary>The alert description.</summary>
    public byte Description { get; }

    /// <summary>True when this stack sent the alert.</summary>
    public bool WasSent { get; }
}
