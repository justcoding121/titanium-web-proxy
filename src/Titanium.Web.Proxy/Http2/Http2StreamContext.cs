using System.IO;
using System.Threading;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Per-invocation context handed to the <c>onBeforeRequest</c>/<c>onBeforeResponse</c> delegates passed to
///     <see cref="Http2Helper.SendHttp2" />, in addition to the <c>SessionEventArgs</c> those delegates already
///     received. The normal (protocol-symmetric) h2 relay never needs this - it exists so that a
///     protocol-translation bridge (e.g. the h2-client-to-HTTP/1.1-origin bridge) invoked through the very same
///     delegate can reach the connection-wide HPACK/flow-control/synchronization state
///     (<see cref="ConnectionState" />) and the real client-facing transport (<see cref="ClientStream" />) it
///     needs to answer a stream on its own schedule, independently of the frame-relay loop that invoked it.
/// </summary>
internal sealed class Http2StreamContext
{
    internal Http2StreamContext(int streamId, Http2ConnectionState connectionState, Stream clientStream,
        CancellationToken cancellationToken)
    {
        StreamId = streamId;
        ConnectionState = connectionState;
        ClientStream = clientStream;
        CancellationToken = cancellationToken;
    }

    /// <summary>The HTTP/2 stream id this invocation is processing headers for.</summary>
    internal int StreamId { get; }

    /// <summary>The state shared by both relay directions of the whole HTTP/2 connection this stream belongs to.</summary>
    internal Http2ConnectionState ConnectionState { get; }

    /// <summary>
    ///     The real client-facing transport for this connection (regardless of which relay direction - request
    ///     or response - is invoking the delegate), for writers that need to send frames toward the client
    ///     outside the normal per-frame relay/dispatch path (e.g. <see cref="Http2Helper.EmitSyntheticResponseAsync" />).
    /// </summary>
    internal Stream ClientStream { get; }

    /// <summary>
    ///     The connection-wide cancellation token observed by the frame-relay loops. A bridge implementation
    ///     that needs to react to this *stream* individually being reset should link this with the relevant
    ///     <see cref="Http2StreamState.Cancellation" /> token instead of using this one directly.
    /// </summary>
    internal CancellationToken CancellationToken { get; }
}
