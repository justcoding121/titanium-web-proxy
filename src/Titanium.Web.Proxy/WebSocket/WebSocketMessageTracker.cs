namespace Titanium.Web.Proxy;

/// <summary>
///     Tracks fragmented WebSocket message state across frames per RFC 6455 §5.4.
///     Maintains the current message's RSV1 flag (used by permessage-deflate to
///     indicate compression) and validates that fragment opcodes are consistent.
///
///     Usage: one instance per relay direction. Call <see cref="OnFrame"/> for each
///     decoded frame; the return value indicates whether the message is now complete.
/// </summary>
internal sealed class WebSocketMessageTracker
{
    private bool inFragmentedMessage;
    private bool messageRsv1; // RSV1 of the first frame (opener) of the current message

    /// <summary>
    ///     Processes one WebSocket frame and updates message state.
    /// </summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="isCompressed">
    ///     Set to <see langword="true"/> if RSV1 was set on the opening frame
    ///     (indicates permessage-deflate compression on this message).
    /// </param>
    /// <param name="isProtocolError">
    ///     Set to <see langword="true"/> when a non-continuation data frame arrives while a fragmented
    ///     message is still open (RFC 6455 §5.4) - a fatal condition distinct from "this frame does not
    ///     yet complete the message", which the caller must not confuse with the normal in-progress case
    ///     since both would otherwise report the same <see langword="false"/> return value.
    /// </param>
    /// <returns><see langword="true"/> if this frame completes the current message.</returns>
    internal bool OnFrame(WebSocketFrame frame, out bool isCompressed, out bool isProtocolError)
    {
        isProtocolError = false;

        var op = (int)frame.OpCode;
        var isContinuation = op == 0x0;
        var isControl = op == 0x8 || op == 0x9 || op == 0xA;

        if (isControl)
        {
            // Control frames may be injected between fragments — they don't affect message state.
            isCompressed = false;
            return frame.IsFinal;
        }

        if (!inFragmentedMessage)
        {
            // Opening frame of a new message.
            // Note: RSV1 is not preserved in WebSocketFrame.Data after decode (the decoder unmasks).
            // For now, we track whether the frame was the opener and assume RSV1=false
            // (permessage-deflate is not yet negotiated since we strip the extension in Phase 1.4).
            // Frame metadata currently does not expose RSV1, so compressed-message tracking starts clear.
            messageRsv1 = false;
            inFragmentedMessage = !frame.IsFinal;
            isCompressed = messageRsv1;
            return frame.IsFinal;
        }
        else
        {
            // Continuation frame.
            if (!isContinuation)
            {
                // Protocol error: non-continuation data frame during a fragmented message.
                isCompressed = false;
                isProtocolError = true;
                return false; // caller must close the connection, not treat this as "still in progress"
            }

            if (frame.IsFinal)
            {
                inFragmentedMessage = false;
                isCompressed = messageRsv1;
                return true;
            }

            isCompressed = false;
            return false;
        }
    }

    /// <summary>Resets tracker state (e.g. after a protocol error).</summary>
    internal void Reset() => inFragmentedMessage = false;
}
