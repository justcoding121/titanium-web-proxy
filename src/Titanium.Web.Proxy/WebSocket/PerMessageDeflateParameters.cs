using System;

namespace Titanium.Web.Proxy;

/// <summary>
///     Parameters for the permessage-deflate WebSocket extension (RFC 7692).
///     Parsed from the <c>Sec-WebSocket-Extensions</c> header when permessage-deflate
///     is enabled (Phase 6). Each side of the connection has its own set of parameters.
/// </summary>
internal sealed class PerMessageDeflateParameters
{
    /// <summary>
    ///     Client-to-server context takeover. When <see langword="false"/>,
    ///     the compressor must reset its LZ77 context between messages.
    ///     Default: true (context is maintained).
    /// </summary>
    internal bool ClientContextTakeover { get; set; } = true;

    /// <summary>
    ///     Server-to-client context takeover. When <see langword="false"/>,
    ///     the decompressor must reset its context between messages.
    ///     Default: true (context is maintained).
    /// </summary>
    internal bool ServerContextTakeover { get; set; } = true;

    /// <summary>
    ///     Client maximum window bits for deflate (8-15). Lower values reduce
    ///     memory usage. Default: 15.
    /// </summary>
    internal int ClientMaxWindowBits { get; set; } = 15;

    /// <summary>
    ///     Server maximum window bits for deflate (8-15). Default: 15.
    /// </summary>
    internal int ServerMaxWindowBits { get; set; } = 15;

    internal static readonly char[] anyOf = new[] { ';', ' ', '\r', '\n' };

    /// <summary>
    ///     Tries to parse permessage-deflate parameters from the
    ///     <c>Sec-WebSocket-Extensions</c> header value.
    /// </summary>
    internal static PerMessageDeflateParameters? TryParse(string? extensionHeader)
    {
        if (extensionHeader == null) return null;

        var lower = extensionHeader.ToLowerInvariant();
        if (!lower.Contains("permessage-deflate")) return null;

        var p = new PerMessageDeflateParameters();

        if (lower.Contains("client_no_context_takeover")) p.ClientContextTakeover = false;
        if (lower.Contains("server_no_context_takeover")) p.ServerContextTakeover = false;

        var clientBits = ParseWindowBits(lower, "client_max_window_bits");
        if (clientBits.HasValue) p.ClientMaxWindowBits = clientBits.Value;

        var serverBits = ParseWindowBits(lower, "server_max_window_bits");
        if (serverBits.HasValue) p.ServerMaxWindowBits = serverBits.Value;

        return p;
    }

    private static int? ParseWindowBits(string header, string param)
    {
        var idx = header.IndexOf(param, StringComparison.Ordinal);
        if (idx < 0) return null;

        var rest = header.Substring(idx + param.Length).TrimStart();
        if (!rest.StartsWith("=")) return null;

        var value = rest.Substring(1).TrimStart();
        var end = value.IndexOfAny(anyOf);
        var numStr = end < 0 ? value : value.Substring(0, end);

        return int.TryParse(numStr.Trim(), out var bits) && bits >= 8 && bits <= 15 ? bits : null;
    }
}
