using System;
using System.ComponentModel;
using System.Linq;
using System.Security.Authentication;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Detects TLS ALPN negotiation failures so they are not mistaken for TLS-version problems
///     that warrant a protocol downgrade retry.
/// </summary>
internal static class AlpnNegotiation
{
    /// <summary>SEC_E_NO_APPLICATION_PROTOCOL — no common ALPN between client and server.</summary>
    internal const int SecENoApplicationProtocol = unchecked((int)0x80090367);

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="error" /> (or any inner exception)
    ///     indicates ALPN application-protocol negotiation failed.
    /// </summary>
    internal static bool IsAlpnNegotiationFailure(Exception? error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (MatchesAlpnFailure(e))
                return true;
        }

        return false;
    }

    private static bool MatchesAlpnFailure(Exception e)
    {
        if (e is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(IsAlpnNegotiationFailure);

        if (e is Win32Exception win32 && win32.NativeErrorCode == SecENoApplicationProtocol)
            return true;

        // Some runtimes surface the Win32 code only on HResult.
        if (e.HResult == SecENoApplicationProtocol)
            return true;

        return e.Message.Contains("No common application protocol", StringComparison.OrdinalIgnoreCase)
               || e.Message.Contains("Application protocol negotiation failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True when an <see cref="AuthenticationException" /> should attempt a TLS-version downgrade
    ///     retry (legacy gate). ALPN mismatches must not enter that path.
    /// </summary>
    internal static bool ShouldAttemptTlsVersionDowngrade(AuthenticationException ex) =>
        !IsAlpnNegotiationFailure(ex);
}
