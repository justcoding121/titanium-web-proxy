using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Parses the <c>Alt-Svc</c> HTTP response header and extracts HTTP/3 alternative-service
///     endpoints advertised by origin servers.
///     <para>
///         Spec: RFC 7838 §3 (Alt-Svc header field syntax).
///         Example header value: <c>h3=":443"; ma=86400, h3-29=":443"; ma=86400</c>
///     </para>
/// </summary>
internal static class AltSvcParser
{
    /// <summary>
    ///     Represents an HTTP/3 alternative service endpoint parsed from an <c>Alt-Svc</c> header.
    /// </summary>
    internal readonly record struct Http3AltSvc(
        /// <summary>
        ///     The port advertised by the origin. <see cref="int.MinValue" /> when the token had an
        ///     empty host in the alt-authority (e.g. <c>h3=":443"</c> with the same host implied), in
        ///     which case the consumer should substitute the current connection's port.
        ///     Note: the RFC allows a different host in the authority; we intentionally ignore different
        ///     hosts for security (same-origin constraint on proxy-level protocol upgrade).
        /// </summary>
        int Port,
        /// <summary>
        ///     Max-age in seconds as advertised. Zero or negative values should be treated as
        ///     "do not cache". Defaults to 86400 (24 h) when the <c>ma</c> parameter is absent.
        /// </summary>
        int MaxAgeSeconds);

    private const int DefaultMaxAge = 86400;

    /// <summary>
    ///     Parses <paramref name="altSvcHeaderValue" /> and returns every HTTP/3 alternative service found.
    ///     ALPN tokens <c>h3</c> and <c>h3-NN</c> (draft versions) are both accepted.
    ///     Tokens for other protocols (h2, etc.) and <c>clear</c> are silently ignored.
    /// </summary>
    internal static IReadOnlyList<Http3AltSvc> Parse(string? altSvcHeaderValue)
    {
        if (string.IsNullOrEmpty(altSvcHeaderValue) || altSvcHeaderValue == "clear")
            return Array.Empty<Http3AltSvc>();

        var results = new List<Http3AltSvc>();
        var span = altSvcHeaderValue.AsSpan();

        // The header value is a comma-separated list of alt-value tokens.
        while (span.Length > 0)
        {
            // Split on commas not inside quotes (Alt-Svc doesn't use quoted commas in practice, but be safe)
            var commaIdx = IndexOfCommaOutsideQuotes(span);
            var token = (commaIdx >= 0 ? span[..commaIdx] : span).Trim();
            if (commaIdx >= 0) span = span[(commaIdx + 1)..];
            else span = ReadOnlySpan<char>.Empty;

            if (TryParseToken(token, out var svc))
                results.Add(svc);
        }

        return results;
    }

    private static bool TryParseToken(ReadOnlySpan<char> token, out Http3AltSvc result)
    {
        result = default;

        // Format: alpnId="alt-authority" *( ";" parameter )
        var eqIdx = token.IndexOf('=');
        if (eqIdx < 0) return false;

        var alpnId = token[..eqIdx].Trim();
        if (!IsHttp3AlpnId(alpnId)) return false;

        var rest = token[(eqIdx + 1)..].Trim();

        // Read alt-authority (a quoted string or bare token)
        if (!TryReadQuotedOrBare(ref rest, out var altAuthority)) return false;

        // Parse the port from the authority.  Alt-Svc typically uses "host:port" or ":port".
        var port = ParsePort(altAuthority);
        if (port < 0) return false; // invalid port or different host

        // Read semicolon-delimited parameters for ma=
        var maxAge = DefaultMaxAge;
        while (rest.Length > 0)
        {
            rest = SkipWhitespaceAndSemicolon(rest);
            if (rest.Length == 0) break;

            var semiIdx = rest.IndexOf(';');
            var param = (semiIdx >= 0 ? rest[..semiIdx] : rest).Trim();
            if (semiIdx >= 0) rest = rest[(semiIdx + 1)..];
            else rest = ReadOnlySpan<char>.Empty;

            var paramEq = param.IndexOf('=');
            if (paramEq < 0) continue;
            var pName = param[..paramEq].Trim();
            var pValue = param[(paramEq + 1)..].Trim();
            if (pName.Equals("ma", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(pValue, out var ma))
                maxAge = ma;
        }

        result = new Http3AltSvc(port, maxAge);
        return true;
    }

    private static bool IsHttp3AlpnId(ReadOnlySpan<char> id)
    {
        if (id.Equals("h3", StringComparison.OrdinalIgnoreCase)) return true;
        // Accept draft identifiers: h3-29, h3-32, etc.
        if (id.StartsWith("h3-", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    ///     Parses the port from an alt-authority string of the form "[host]:port" or ":port".
    ///     Returns <see cref="int.MinValue" /> when the authority has an empty host (same host as
    ///     current connection), a positive integer for the parsed port, or -1 on error.
    /// </summary>
    private static int ParsePort(string authority)
    {
        if (string.IsNullOrEmpty(authority)) return -1;

        var colonIdx = authority.LastIndexOf(':');
        if (colonIdx < 0) return -1;

        var host = authority[..colonIdx];
        var portStr = authority[(colonIdx + 1)..];

        if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535) return -1;

        // Non-empty host means a different origin — ignore for security reasons.
        if (!string.IsNullOrEmpty(host) && host != "\"\"") return -1;

        // Empty host ⇒ same-host alt-svc; port may differ.
        return port;
    }

    private static bool TryReadQuotedOrBare(ref ReadOnlySpan<char> span, out string value)
    {
        value = string.Empty;
        if (span.Length == 0) return false;

        if (span[0] == '"')
        {
            var closeIdx = span[1..].IndexOf('"');
            if (closeIdx < 0) return false;
            value = span[1..(closeIdx + 1)].ToString();
            span = span[(closeIdx + 2)..].Trim();
            return true;
        }

        // Bare token — read until whitespace or semicolon
        var end = 0;
        while (end < span.Length && span[end] != ';' && span[end] != ',' && !char.IsWhiteSpace(span[end]))
            end++;
        value = span[..end].ToString();
        span = span[end..].Trim();
        return end > 0;
    }

    private static ReadOnlySpan<char> SkipWhitespaceAndSemicolon(ReadOnlySpan<char> span)
    {
        var i = 0;
        while (i < span.Length && (span[i] == ' ' || span[i] == '\t' || span[i] == ';'))
            i++;
        return span[i..];
    }

    private static int IndexOfCommaOutsideQuotes(ReadOnlySpan<char> span)
    {
        var inQuotes = false;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == '"') inQuotes = !inQuotes;
            else if (span[i] == ',' && !inQuotes) return i;
        }
        return -1;
    }
}
