#if NET6_0_OR_GREATER
using System.Threading;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Per-stream state tracked for the lifetime of one HTTP/3 bidirectional request/response stream.
///     HTTP/3 streams are identified by QUIC stream IDs (64-bit integers; client-initiated bidirectional
///     streams use even stream IDs 0, 4, 8, … per QUIC draft RFC 9000 §2.1).
/// </summary>
internal sealed class Http3StreamState
{
    public Http3StreamState(long streamId, SessionEventArgs sessionArgs)
    {
        StreamId = streamId;
        SessionArgs = sessionArgs;
        Cancellation = new CancellationTokenSource();
    }

    /// <summary>QUIC stream ID for this HTTP/3 request/response stream.</summary>
    public long StreamId { get; }

    /// <summary>Per-request event arguments carrying the request/response model and handler callbacks.</summary>
    public SessionEventArgs SessionArgs { get; }

    /// <summary>
    ///     Cancelled when this stream is individually reset (QUIC STOP_SENDING / RESET_STREAM), so a body
    ///     waiter or before-handler task blocked only on this stream can unblock without tearing down every
    ///     other concurrent stream on the QUIC connection.
    /// </summary>
    public CancellationTokenSource Cancellation { get; }

    /// <summary><see langword="true" /> once the request half-stream (client → proxy) has been fully read (END_STREAM).</summary>
    public bool RequestClosed { get; set; }

    /// <summary><see langword="true" /> once the response half-stream (proxy → client) has been fully sent.</summary>
    public bool ResponseClosed { get; set; }

    /// <summary><see langword="true" /> when both halves have closed.</summary>
    public bool IsClosed => RequestClosed && ResponseClosed;

    /// <summary>
    ///     Guards <c>AfterResponse</c> + <c>Dispose</c> so they execute exactly once for this stream's
    ///     <see cref="SessionArgs" /> regardless of which of the three possible termination paths (normal
    ///     end-stream on both directions, stream reset, or connection-level GOAWAY/teardown while the stream
    ///     is still open) observes completion first. 0 = not yet finalized, 1 = finalized. Mutated only via
    ///     <see cref="System.Threading.Interlocked.CompareExchange(ref int, int, int)" />.
    /// </summary>
    public int FinalizedFlag;
}
#endif
