using System;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Classifies origin TLS failures that are safe to learn as "tunnel without decrypt"
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
}
