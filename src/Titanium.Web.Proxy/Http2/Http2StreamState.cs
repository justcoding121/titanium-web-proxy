#if NET6_0_OR_GREATER
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Per-stream state tracked for the lifetime of one HTTP/2 stream (a single request/response pair
///     multiplexed on the connection), keyed by stream id in <see cref="Http2ConnectionState.Streams" />.
///     A stream is only removed from that registry - and only then may its id be reused by neither side,
///     per RFC 7540, though this proxy does not police reuse itself - once both halves are closed or it is
///     reset (RST_STREAM) or the connection goes away, not merely on the first END_STREAM seen (request and
///     response close independently).
/// </summary>
internal sealed class Http2StreamState
{
    public Http2StreamState(int streamId, SessionEventArgs sessionArgs)
    {
        StreamId = streamId;
        SessionArgs = sessionArgs;
        Cancellation = new CancellationTokenSource();
    }

    public int StreamId { get; }

    public SessionEventArgs SessionArgs { get; }

    /// <summary>
    ///     Cancelled when this stream is individually reset (RST_STREAM) or the peer GOAWAYs past it, so a
    ///     body/before-handler waiter or synthetic-response task blocked only on this stream can unblock
    ///     without tearing down every other multiplexed stream on the connection.
    /// </summary>
    public CancellationTokenSource Cancellation { get; }

    /// <summary>True once the request side (client -> proxy -> server) has seen END_STREAM or been reset.</summary>
    public bool RequestClosed { get; set; }

    /// <summary>True once the response side (server -> proxy -> client) has seen END_STREAM or been reset.</summary>
    public bool ResponseClosed { get; set; }

    /// <summary>
    ///     Set once a synthetic (proxy-generated) response has been dispatched for this stream so its
    ///     background task (tracked in <see cref="Http2ConnectionState.PendingSynthetics" />) can be found
    ///     and observed/cancelled on RST_STREAM/GOAWAY without scanning the whole bag.
    /// </summary>
    public Task? SyntheticTask { get; set; }

    public bool IsClosed => RequestClosed && ResponseClosed;
}
#endif
