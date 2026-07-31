using System;

namespace Titanium.Web.Proxy;

/// <summary>
///     Thrown by <see cref="WebSocketDecoder" /> when a frame's declared payload length violates
///     RFC 6455 section 5.2 (the reserved high bit of a 64-bit extended length is set, or the declared
///     length exceeds <see cref="int.MaxValue" /> and can never be buffered as a single in-memory frame),
///     or when it exceeds the caller-configured per-frame payload limit (RFC 6455 section 7.4.1,
///     close code 1009).
///     <para>
///         Raised the moment the declared length is known - before any of that frame's payload bytes
///         are copied into the decoder's reassembly buffer - so an attacker who declares an oversized
///         length and then trickles bytes in slowly cannot force unbounded buffer growth while the
///         decoder waits for a frame that will never legitimately complete.
///     </para>
/// </summary>
public sealed class WebSocketProtocolException : Exception
{
    /// <summary>
    ///     The RFC 6455 section 7.4 status code that a conformant close must report for this violation.
    /// </summary>
    public ushort CloseCode { get; }

    public WebSocketProtocolException(string message, ushort closeCode) : base(message)
    {
        CloseCode = closeCode;
    }
}
