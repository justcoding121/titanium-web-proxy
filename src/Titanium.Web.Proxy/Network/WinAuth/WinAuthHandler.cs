using System;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy.Network.WinAuth;

using static Common;

/// <summary>
///     A handler for NTLM/Kerberos windows authentication challenge from server
///     NTLM process details below
///     https://blogs.msdn.microsoft.com/chiranth/2013/09/20/ntlm-want-to-know-how-it-works/
/// </summary>
internal static class WinAuthHandler
{
    /// <summary>
    ///     Get the initial client token for server.
    ///     When <paramref name="credentials" /> is null, uses the process identity; otherwise SSPI
    ///     acquires a handle for the supplied credentials (Windows only).
    /// </summary>
    /// <param name="serverHostname"></param>
    /// <param name="authScheme"></param>
    /// <param name="data"></param>
    /// <param name="credentials">Optional alternate credentials from <c>WinAuthCredentialsProvider</c>.</param>
    /// <returns></returns>
    internal static string GetInitialAuthToken(string serverHostname, string authScheme, InternalDataStore data,
        WinAuthCredentials? credentials = null)
    {
        var tokenBytes = WinAuthEndPoint.AcquireInitialSecurityToken(serverHostname, authScheme, data,
            IscReqConfidentiality | IscReqReplayDetect | IscReqSequenceDetect | IscReqConnection, credentials);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the initial authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    /// <summary>
    ///     Get the final token given the server challenge token
    /// </summary>
    /// <param name="serverHostname"></param>
    /// <param name="serverToken"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static string GetFinalAuthToken(string serverHostname, string serverToken, InternalDataStore data)
    {
        var tokenBytes =
            WinAuthEndPoint.AcquireFinalSecurityToken(serverHostname, Convert.FromBase64String(serverToken),
                data, IscReqConfidentiality | IscReqReplayDetect | IscReqSequenceDetect | IscReqConnection);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the final authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    /// <summary>
    ///     Get the initial authentication token for an upstream proxy using the current process identity.
    /// </summary>
    internal static string GetInitialProxyAuthToken(string proxyHostname, string authScheme, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireInitialSecurityToken(proxyHostname, authScheme, data, 0);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the initial proxy authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    /// <summary>
    ///     Get the response token for an upstream proxy challenge.
    /// </summary>
    internal static string GetFinalProxyAuthToken(string proxyHostname, string serverToken, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireFinalSecurityToken(proxyHostname,
            Convert.FromBase64String(serverToken), data, 0);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the final proxy authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }
}