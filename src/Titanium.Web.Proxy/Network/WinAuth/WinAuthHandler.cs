using System;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.WinAuth.Security;
using static Titanium.Web.Proxy.Network.WinAuth.Security.Common;

namespace Titanium.Web.Proxy.Network.WinAuth;

/// <summary>
///     A handler for NTLM/Kerberos windows authentication challenge from server
///     NTLM process details below
///     https://blogs.msdn.microsoft.com/chiranth/2013/09/20/ntlm-want-to-know-how-it-works/
/// </summary>
internal static class WinAuthHandler
{
    /// <summary>
    ///     Get the initial client token for server
    ///     using credentials of user running the proxy server process
    /// </summary>
    /// <param name="serverHostname"></param>
    /// <param name="authScheme"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static string GetInitialAuthToken(string serverHostname, string authScheme, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireInitialSecurityToken(serverHostname,
            authScheme,
            data,
            IscReqConfidentiality | IscReqReplayDetect | IscReqSequenceDetect | IscReqConnection);
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
            WinAuthEndPoint.AcquireFinalSecurityToken(serverHostname,
                Convert.FromBase64String(serverToken),
                data,
                IscReqConfidentiality | IscReqReplayDetect | IscReqSequenceDetect | IscReqConnection);

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    // NTLM authentication with the proxy server works in a different way and different ISC_REQ_* flags need to be passed
    // Chromium sets ISC_REQ_DELEGATE | ISC_REQ_MUTUAL_AUTH as seen in https://chromium.googlesource.com/chromium/src/net/+/b8c947c21ffb46f616ece1948ba0545e671cf23e/http/http_auth_sspi_win.cc#546
    // cURL uses no flags since commit https://github.com/curl/curl/commit/8ee182288af1bd828613fdcab2e7e8b551e91901 (now moved in lib/vauth/ntlm_sspi.c)
    // .NET since 6.0.4 has chosen ISC_REQ_CONNECTION https://github.com/dotnet/runtime/pull/66305/files
    // CNTLM (the new maintained version) instead passes ISC_REQ_CONFIDENTIALITY | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONNECTION https://github.com/versat/cntlm/blob/d6a47bb5c2489503e3d97e52685b8dc10300da96/sspi.c#L239
    // This is Microsoft documentation for the InitializeSecurityContext function https://learn.microsoft.com/en-us/windows/win32/secauthn/initializesecuritycontext--general
    // The whole thing seems pretty randomic...

    /// <summary>
    ///     Get the initial client token for proxy server
    ///     using credentials of user running the proxy server process
    /// </summary>
    /// <param name="proxyHostname"></param>
    /// <param name="authScheme"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static string GetInitialProxyAuthToken(string proxyHostname, string authScheme, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireInitialSecurityToken(proxyHostname,
            authScheme,
            data,
            0);
        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    /// <summary>
    ///     Get the final token given the proxy server challenge token
    /// </summary>
    /// <param name="proxyHostname"></param>
    /// <param name="serverToken"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static string GetFinalProxyAuthToken(string proxyHostname, string serverToken, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireFinalSecurityToken(proxyHostname,
            Convert.FromBase64String(serverToken),
            data,
            0);

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }
}