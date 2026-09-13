using System;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>Formats RFC 7230 Host header values (bracketed IPv6 + optional port).</summary>
internal static class HttpHostHeader
{
    /// <summary>
    ///     Builds <c>host</c> or <c>host:port</c>, bracketing IPv6 literals. Omits the port when it
    ///     equals <paramref name="omitPortWhen"/> (HTTP/80 by default).
    /// </summary>
    public static string Format(string host, int port, int omitPortWhen = 80)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);

        var hostPart = host;
        if (host[0] != '[' && host.Contains(':') &&
            IPAddress.TryParse(host, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            hostPart = $"[{host}]";
        }

        if (port <= 0 || port == omitPortWhen)
            return hostPart;

        return $"{hostPart}:{port}";
    }
}
