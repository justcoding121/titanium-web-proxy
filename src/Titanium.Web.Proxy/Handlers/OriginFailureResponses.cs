using System;
using System.Net;
using System.Net.Sockets;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;

namespace Titanium.Web.Proxy;

/// <summary>
///     Shared status mapping for synthetic error responses the bridges send when the origin round trip fails
///     before any response byte reached the client: deadline / socket timeouts are 504, everything else 502.
///     The body keeps the historical "Bad Gateway. {message}" text for 502 so existing clients and tests
///     keep matching.
/// </summary>
internal static class OriginFailureResponses
{
    internal static bool IsTimeout(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is ProxyTimeoutException) return true;
            if (e is SocketException { SocketErrorCode: SocketError.TimedOut }) return true;
        }

        return false;
    }

    internal static HttpStatusCode StatusFor(Exception ex) =>
        IsTimeout(ex) ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway;

    /// <summary>Populate <paramref name="args" /> with the 502/504 response for <paramref name="ex" />.</summary>
    internal static void Apply(SessionEventArgs args, Exception ex)
    {
        var status = StatusFor(ex);
        var label = status == HttpStatusCode.GatewayTimeout ? "Gateway Timeout" : "Bad Gateway";
        args.GenericResponse($"{label}. {ex.Message}", status);
    }
}
