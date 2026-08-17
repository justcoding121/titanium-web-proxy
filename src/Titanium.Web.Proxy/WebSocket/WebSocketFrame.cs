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
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; internal set; }

    public string GetText()
    {
        return GetText(Encoding.UTF8);
    }

    public string GetText(Encoding encoding)
    {
        return encoding.GetString(Data.Span);
    }
}