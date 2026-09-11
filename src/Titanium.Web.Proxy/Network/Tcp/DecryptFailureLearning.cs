using System;
using System.Net.Sockets;
using System.Security.Authentication;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Classifies failures that are safe to learn as "tunnel without decrypt"
///     (bot / fingerprint style). ALPN mismatches and plain TCP/DNS failures are excluded.
/// </summary>
internal static class DecryptFailureLearning
{
    internal static bool IsLearnableOriginTlsFailure(Exception? error)
    {
        if (error == null || AlpnNegotiation.IsAlpnNegotiationFailure(error))
            return false;

        // Prefer not to learn pure connectivity failures.
        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (e is SocketException)
                return false;
        }

        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     MITM HTTPS 403/429 from the origin (not synthetic Ok/Respond/GenericResponse).
    ///     Uses the shared strike threshold — one authZ 403 must not tunnel the whole host.
    /// </summary>
    internal static bool IsLearnableHttpBlock(SessionEventArgs args)
    {
        if (!args.IsHttps || args.Exception is not null)
            return false;

        if (!args.HttpClient.HasResponse)
            return false;

        var response = args.HttpClient.Response;
        if (response.IsSynthetic)
            return false;

        return response.StatusCode is 403 or 429;
    }

    internal static string? ResolveSessionHost(SessionEventArgs args)
    {
        var host = args.HttpClient.Request.RequestUri?.Host;
        if (string.IsNullOrEmpty(host))
            host = args.HttpClient.ConnectRequest?.RequestUri?.Host;
        return host;
    }
}
