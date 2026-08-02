using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Titanium.Web.Proxy;

/// <summary>
///     Shared WebSocket opening-handshake helpers (RFC 6455).
/// </summary>
internal static class WebSocketHandshake
{
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    ///     Computes <c>Sec-WebSocket-Accept</c> from the client's <c>Sec-WebSocket-Key</c>
    ///     (RFC 6455 §1.3). SHA-1 is mandatory for this value; it is not used as a general digest.
    /// </summary>
    [SuppressMessage("Major Vulnerability", "S4790:Using weak hashing algorithms is security-sensitive",
        Justification = "RFC 6455 §1.3 requires SHA-1 for Sec-WebSocket-Accept; this is not a general-purpose hash.")]
    internal static string ComputeAccept(string secWebSocketKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(secWebSocketKey);
        // SHA-1 is mandated by RFC 6455 §1.3 for this handshake field only.
        return Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey + AcceptGuid))); // NOSONAR
    }
}
