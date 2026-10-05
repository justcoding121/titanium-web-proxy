using System;
using System.Text;

namespace Titanium.Web.Proxy;

/// <summary>
///     A single decoded WebSocket frame, as produced by <see cref="WebSocketDecoder.Decode" />.
/// </summary>
/// <remarks>
///     <see cref="Data" /> is an owned copy of the unmasked payload produced by
///     <see cref="WebSocketDecoder.Decode" />, safe to retain after that call returns.
/// </remarks>
public class WebSocketFrame
{
    public bool IsFinal { get; internal set; }

    public WebsocketOpCode OpCode { get; internal set; }

    /// <summary>
    ///     The unmasked frame payload (owned copy from <see cref="WebSocketDecoder" />).
    ///     When <see cref="RelayRaw"/> is set, this is a slice of the original wire bytes to forward
    ///     unchanged, not a decoded payload.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; internal set; }

    /// <summary>Forward <see cref="Data"/> as wire bytes. Do not re-encode or hand to frame hooks.</summary>
    internal bool RelayRaw { get; set; }

    /// <summary>First raw slice of an oversize frame. Later slices of the same frame leave this false.</summary>
    internal bool IsOversizeNotice { get; set; }

    /// <summary>Declared payload length of an oversize frame, not including the header or mask key.</summary>
    internal long DeclaredPayloadLength { get; set; }

    public string GetText()
    {
        return GetText(Encoding.UTF8);
    }

    public string GetText(Encoding encoding)
    {
        return encoding.GetString(Data.Span);
    }
}