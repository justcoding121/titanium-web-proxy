namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Outcome of an internal buffer fill that distinguishes cancel from EOF without throwing.
/// </summary>
internal enum BufferFillResult
{
    /// <summary>At least one byte was read into the buffer.</summary>
    GotData = 0,

    /// <summary>Peer closed or the stream is already closed (EOF).</summary>
    EndOfStream = 1,

    /// <summary>The wait was cancelled; the stream is not poisoned.</summary>
    Cancelled = 2
}
