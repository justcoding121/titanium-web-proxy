using System;
using System.Text;

namespace Titanium.Web.Proxy;

/// <summary>
///     A single decoded WebSocket frame, as produced by <see cref="WebSocketDecoder.Decode" />.
/// </summary>
/// <remarks>
///     <see cref="Data" /> is only valid for as long as the buffer it was decoded from is unchanged - it
///     is a zero-copy slice of either the byte array passed into <see cref="WebSocketDecoder.Decode" /> or
///     the decoder's own internal reassembly buffer (used to hold onto a frame that arrived split across
///     multiple calls), and both of those are reused/overwritten by later reads and later calls to
///     <see cref="WebSocketDecoder.Decode" /> on the same decoder instance. Consume <see cref="Data" /> (or
///     call <see cref="GetText()" />) while still enumerating the same <c>Decode(...)</c> call that
///     produced this frame; copy it out (e.g. via <c>Data.ToArray()</c>) before retaining a
///     <see cref="WebSocketFrame" /> for later use, otherwise its content can silently change or become
///     garbage once more data flows through the decoder.
/// </remarks>
public class WebSocketFrame
{
    public bool IsFinal { get; internal set; }

    public WebsocketOpCode OpCode { get; internal set; }

    /// <summary>
    ///     The unmasked frame payload. See the class remarks - this is a zero-copy view into a buffer that
    ///     gets reused, so it must be consumed (or copied, e.g. via <c>Data.ToArray()</c>) before further
    ///     data is read through the same <see cref="WebSocketDecoder" /> or before the caller-supplied
    ///     buffer this frame was decoded from is reused.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; internal set; }

    public string GetText()
    {
        return GetText(Encoding.UTF8);
    }

    public string GetText(Encoding encoding)
    {
#if NET6_0_OR_GREATER
        return encoding.GetString(Data.Span);
#else
        return encoding.GetString(Data.ToArray());
#endif
    }
}