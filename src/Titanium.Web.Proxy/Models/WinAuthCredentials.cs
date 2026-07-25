namespace Titanium.Web.Proxy.Models;

/// <summary>
///     Windows authentication credentials for NTLM/Negotiate/Kerberos (issue #461).
///     Prefer supplying these through <see cref="ProxyServer.WinAuthCredentialsProvider" />
///     rather than storing plaintext on session event args.
/// </summary>
public sealed class WinAuthCredentials
{
    /// <summary>
    ///     Optional domain (or machine name). Empty uses the local/default domain.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    ///     User name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Password.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
