using System;
using System.Threading;
using System.Threading.Channels;
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
    public Http2StreamState(int streamId)
    {
        StreamId = streamId;
        SessionArgs = null;
        IsCompressedRelay = true;
        Cancellation = new CancellationTokenSource();
    }

    public Http2StreamState(int streamId, SessionEventArgs sessionArgs)
    {
        StreamId = streamId;
        SessionArgs = sessionArgs;
        IsCompressedRelay = false;
        Cancellation = new CancellationTokenSource();
    }

    public int StreamId { get; private set; }

    public SessionEventArgs? SessionArgs { get; private set; }

    public bool IsCompressedRelay { get; private set; }

    /// <summary>
    ///     Cancelled when this stream is individually reset (RST_STREAM) or the peer GOAWAYs past it, so a
    ///     body/before-handler waiter or synthetic-response task blocked only on this stream can unblock
    ///     without tearing down every other multiplexed stream on the connection.
    /// </summary>
    public CancellationTokenSource Cancellation { get; private set; }

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

    /// <summary>
    ///     Guards <c>AfterResponse</c> + <c>Dispose</c> so they run exactly once for this stream's
    ///     <see cref="SessionArgs" /> regardless of which of the three possible termination paths
    ///     (normal end-stream on both directions, RST_STREAM, or connection teardown with the stream still
    ///     open) observes completion first. 0 = not yet finalized, 1 = finalized. Mutated only via
    ///     <see cref="System.Threading.Interlocked.CompareExchange(ref int, int, int)" />.
    /// </summary>
    public int FinalizedFlag;

    /// <summary>
    ///     RFC 8441: set to <see langword="true"/> when this stream was opened as an extended CONNECT
    ///     request (i.e. <c>:method = CONNECT</c> with a <c>:protocol</c> pseudo-header). The relay
    ///     uses this flag to switch DATA frame handling to the appropriate WebSocket-tunnel path.
    /// </summary>
    public bool IsExtendedConnect { get; set; }

    /// <summary>
    ///     RFC 8441: the value of the <c>:protocol</c> pseudo-header for this extended CONNECT stream
    ///     (e.g. <c>"websocket"</c>). Only meaningful when <see cref="IsExtendedConnect"/> is true.
    /// </summary>
    public string? ExtendedConnectProtocol { get; set; }

    /// <summary>
    ///     RFC 8441: set to <see langword="true"/> once a final 2xx response to this extended CONNECT
    ///     request has been forwarded to the client, establishing the native h2↔h2 tunnel.  DATA frames on
    ///     this stream bypass the HTTP body API once this flag is set; any subsequent HEADERS/CONTINUATION
    ///     frame is a stream-level PROTOCOL_ERROR (RFC 9113 §8.5).
    /// </summary>
    public bool ExtendedConnectEstablished { get; set; }

    /// <summary>
    ///     RFC 8441: channel through which DATA-frame payloads arrive for this extended CONNECT tunnel
    ///     stream. Set by <see cref="ProxyServer.BridgeOnBeforeRequest"/> before the tunnel task starts,
    ///     so that DATA frames arriving immediately after the HEADERS frame are always routed correctly.
    ///     <see cref="Http2Helper"/> writes payloads here when <see cref="IsExtendedConnect"/> is true
    ///     and this channel is non-null, instead of following the normal body-buffering path.
    /// </summary>
    internal Channel<ReadOnlyMemory<byte>>? InboundTunnelChannel { get; set; }

    /// <summary>
    ///     Bounded channel of inbound request DATA payloads for <see cref="IsExternalBridge"/> streams
    ///     when the body was not buffered via <c>GetRequestBody</c>. The bridge pumps these chunks
    ///     live to the origin instead of waiting for a full <c>byte[]</c>.
    /// </summary>
    internal Channel<ReadOnlyMemory<byte>>? InboundRequestBodyChannel { get; set; }

    /// <summary>
    ///     Completes when the queued origin HEADERS write for this stream has finished (or failed).
    ///     Client→origin DATA must await this so frames never overtake HEADERS on the wire.
    /// </summary>
    internal TaskCompletionSource? OriginHeadersFlushed { get; set; }

    /// <summary>
    ///     Set by an external bridge handler (e.g. the H2→H3 bridge) before returning from
    ///     <c>onBeforeRequest</c> to signal that it owns this stream's origin round trip and
    ///     response emission entirely.
    /// </summary>
    internal bool IsExternalBridge { get; set; }

    internal void ResetForCompressedRelay(int streamId)
    {
        StreamId = streamId;
        SessionArgs = null;
        IsCompressedRelay = true;
        ResetMutableFields();
    }

    internal void ResetForSession(int streamId, SessionEventArgs sessionArgs)
    {
        StreamId = streamId;
        SessionArgs = sessionArgs;
        IsCompressedRelay = false;
        ResetMutableFields();
    }

    internal void PrepareForPool()
    {
        SessionArgs = null;
        // Prefer TryReset over dispose+new: compressed-relay streams churn one CTS per request
        // otherwise (Cancel on finalize path, then Return).
        if (!Cancellation.TryReset())
        {
            try { Cancellation.Dispose(); }
            catch { /* ignore */ }
            Cancellation = new CancellationTokenSource();
        }

        ResetMutableFields(preserveCancellation: true);
    }

    private void ResetMutableFields(bool preserveCancellation = false)
    {
        if (!preserveCancellation)
        {
            if (!Cancellation.TryReset())
            {
                try { Cancellation.Dispose(); }
                catch { /* ignore */ }
                Cancellation = new CancellationTokenSource();
            }
        }

        RequestClosed = false;
        ResponseClosed = false;
        SyntheticTask = null;
        FinalizedFlag = 0;
        IsExtendedConnect = false;
        ExtendedConnectProtocol = null;
        ExtendedConnectEstablished = false;
        InboundTunnelChannel = null;
        InboundRequestBodyChannel = null;
        OriginHeadersFlushed = null;
        IsExternalBridge = false;
    }
}
