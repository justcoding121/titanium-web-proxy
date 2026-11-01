using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

/// <summary>
///     Builds and parses HTTP/1.1 WebSocket upgrade requests/responses for RFC 8441 / RFC 9220 bridges.
/// </summary>
internal static class WebSocketUpgradeRequest
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "upgrade", "keep-alive", "proxy-connection", "te", "transfer-encoding",
        "sec-websocket-key", "sec-websocket-version", "host"
    };

    /// <summary>
    ///     Builds a client WebSocket upgrade request toward an HTTP/1.1 origin.
    /// </summary>
    public static byte[] BuildRequest(
        string pathAndQuery,
        string hostHeader,
        IEnumerable<HttpHeader> clientHeaders,
        out string key)
    {
        if (HttpHeaderHygiene.ContainsForbiddenDelimiter(pathAndQuery))
            throw new InvalidOperationException("Request path contains CR, LF, or NUL.");
        if (HttpHeaderHygiene.ContainsForbiddenDelimiter(hostHeader))
            throw new InvalidOperationException("Authority contains CR, LF, or NUL.");

        key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var sb = new StringBuilder();
        sb.Append($"GET {pathAndQuery} HTTP/1.1\r\n");
        sb.Append($"Host: {hostHeader}\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append($"Sec-WebSocket-Key: {key}\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");

        foreach (var header in clientHeaders)
        {
            var lname = header.Name.ToLowerInvariant();
            if (lname.StartsWith(':') || HopByHop.Contains(lname))
                continue;
            if (lname.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase))
                continue;
            HttpHeaderHygiene.ThrowIfForbidden(header.Name, header.Value);
            sb.Append($"{header.Name}: {header.Value}\r\n");
        }

        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    ///     Parses the status line and headers from the start of an HTTP/1.1 response.
    ///     Returns false when <paramref name="head"/> does not yet contain the header terminator.
    /// </summary>
    public static bool TryParseResponse(
        ReadOnlySpan<byte> head,
        string key,
        out int status,
        out List<HttpHeader> headers,
        out int consumed)
    {
        status = 0;
        headers = new List<HttpHeader>();
        consumed = 0;

        var text = Encoding.ASCII.GetString(head);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            return false;

        consumed = headerEnd + 4;
        var headerSection = text[..headerEnd];
        using var reader = new StringReader(headerSection);
        var statusLine = reader.ReadLine();
        if (statusLine is null || !TryParseStatusLine(statusLine, out status))
            return false;

        string? line;
        while ((line = reader.ReadLine()) is { Length: > 0 })
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers.Add(new HttpHeader(
                line[..colon].Trim(),
                line[(colon + 1)..].Trim()));
        }

        return true;
    }

    /// <summary>Validates a 101 Switching Protocols response for the given client key.</summary>
    public static bool IsValid101(int status, IReadOnlyList<HttpHeader> headers, string key)
    {
        if (status != 101) return false;

        static string? Find(IReadOnlyList<HttpHeader> list, string name)
        {
            foreach (var h in list)
                if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                    return h.Value;
            return null;
        }

        var upgrade = Find(headers, "Upgrade");
        var connection = Find(headers, "Connection");
        var expectedAccept = WebSocketHandshake.ComputeAccept(key);
        var actualAccept = Find(headers, "Sec-WebSocket-Accept");
        if (!string.Equals(actualAccept, expectedAccept, StringComparison.Ordinal) ||
            !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase) ||
            connection == null ||
            !connection.Split(',').Any(token =>
                string.Equals(token.Trim(), "upgrade", StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private static bool TryParseStatusLine(string line, out int statusCode)
    {
        statusCode = 0;
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
               string.Equals(parts[0], "HTTP/1.1", StringComparison.OrdinalIgnoreCase) &&
               parts[1].Length == 3 &&
               int.TryParse(parts[1], out statusCode) &&
               statusCode is >= 100 and <= 999;
    }
}
