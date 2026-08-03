using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Single RFC 3986 (§3.2)-aware parser for splitting a "host[:port]" authority string - as seen in
///     a CONNECT request-target, an HTTP/2 <c>:authority</c> pseudo-header, or an upstream-proxy
///     address - into its host and port parts.
///     <para>
///         Before this existed, several call sites each did their own ad-hoc
///         <c>authority.LastIndexOf(':')</c> split. That is only correct for a bracketed IPv6 literal
///         by accident (the closing <c>]</c> happens to precede the port-separating colon), and is
///         actively wrong for an <em>unbracketed</em> IPv6 literal: <c>"::1:8080"</c> is inherently
///         ambiguous between host <c>::1</c> port <c>8080</c> and a literal host <c>::1:8080</c> with an
///         implicit default port, which is exactly why RFC 3986 mandates brackets around an IPv6
///         literal in an authority - to remove that ambiguity rather than requiring a guess.
///     </para>
/// </summary>
internal static class AuthorityParser
{
    /// <summary>
    ///     Attempts to split <paramref name="authority" /> into a host and port. The returned
    ///     <paramref name="host" /> never includes IP-literal brackets, matching what every consumer
    ///     (DNS resolution, <see cref="IPAddress.Parse(string)" />, certificate SAN generation) needs.
    /// </summary>
    /// <param name="authority">The "host" or "host:port" string to split.</param>
    /// <param name="defaultPort">Used when <paramref name="authority" /> has no port component.</param>
    /// <param name="host">The parsed host, without brackets even if the input had an IP-literal form.</param>
    /// <param name="port">The parsed or defaulted port.</param>
    /// <returns>
    ///     <see langword="false" /> if <paramref name="authority" /> is empty, has an unterminated or
    ///     empty <c>[...]</c> IP-literal, contains a syntactically invalid IPv6 literal, has an
    ///     unbracketed literal with more than one colon (ambiguous - must be rejected, not guessed),
    ///     or has a port that is not a strictly-numeric value in the 1-65535 range.
    /// </returns>
    public static bool TryParse(string? authority, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;

        if (string.IsNullOrEmpty(authority)) return false;

        if (authority[0] == '[')
        {
            var closeIdx = authority.IndexOf(']');
            if (closeIdx < 0) return false; // unterminated IP-literal

            var literal = authority.Substring(1, closeIdx - 1);
            if (literal.Length == 0) return false;

            // IPvFuture ("v" ...) literals are not supported; only IPv6 is relevant to this proxy.
            if (!IsIpv6Literal(literal)) return false;

            host = literal;

            var rest = authority.Substring(closeIdx + 1);
            if (rest.Length == 0) return true; // no port -> defaultPort

            if (rest[0] != ':') return false; // trailing garbage after the closing bracket
            return TryParsePort(rest.Substring(1), out port);
        }

        var firstColon = authority.IndexOf(':');
        if (firstColon < 0)
        {
            host = authority;
            return true;
        }

        // A second colon here means an unbracketed literal with 2+ colons: this is either a malformed
        // IPv6 literal missing its required brackets, or a genuinely ambiguous "which colon is the
        // port separator" string. Per RFC 3986 §3.2.2, reject rather than guess.
        if (authority.IndexOf(':', firstColon + 1) >= 0) return false;

        var candidateHost = authority.Substring(0, firstColon);
        if (candidateHost.Length == 0) return false;

        host = candidateHost;
        return TryParsePort(authority.Substring(firstColon + 1), out port);
    }

    private static bool IsIpv6Literal(string literal)
    {
        return IPAddress.TryParse(literal, out var address) &&
               address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    /// <summary>
    ///     Same as <see cref="TryParse" /> but throws a descriptive <see cref="FormatException" /> on
    ///     failure instead of returning <see langword="false" />, for call sites where a malformed
    ///     authority must fail the request rather than silently substitute a guessed value.
    /// </summary>
    public static (string Host, int Port) Parse(string? authority, int defaultPort)
    {
        if (TryParse(authority, defaultPort, out var host, out var port)) return (host, port);

        throw new FormatException(
            $"'{authority}' is not a valid RFC 3986 authority. Unbracketed IPv6 literals must be " +
            "bracketed (e.g. '[::1]:8080'), and the port, if present, must be numeric in the 1-65535 range.");
    }

    private static bool TryParsePort(string value, out int port)
    {
        port = 0;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed is < 1 or > 65535) return false;

        port = parsed;
        return true;
    }
}
