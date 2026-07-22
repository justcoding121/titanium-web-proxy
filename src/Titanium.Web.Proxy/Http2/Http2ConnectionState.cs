#if NET6_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     All state shared by *both* relay directions (client->server and server->client) of one HTTP/2
///     connection pair, i.e. everything that cannot correctly live as a local variable of a single
///     <c>Http2Helper.CopyHttp2FrameAsync</c> call because the two directions read frames that affect each
///     other's bookkeeping (a WINDOW_UPDATE read from the client affects how much the *other* direction may
///     send to the client; a stream is only really closed once *both* directions have seen its end). Owns
///     one instance per connection, created once in <c>Http2Helper.SendHttp2</c> and passed to both
///     <c>CopyHttp2FrameAsync</c> tasks.
/// </summary>
internal sealed class Http2ConnectionState
{
    public Http2ConnectionState(Guid connectionId, CancellationTokenSource cancellationTokenSource)
    {
        ConnectionId = connectionId;
        CancellationTokenSource = cancellationTokenSource;
    }

    public Guid ConnectionId { get; }

    /// <summary>What the client has told the proxy about itself (client is the "remote" for the server->client leg).</summary>
    public Http2Settings ClientSettings { get; } = new();

    /// <summary>What the server has told the proxy about itself (server is the "remote" for the client->server leg).</summary>
    public Http2Settings ServerSettings { get; } = new();

    /// <summary>
    ///     Governs writes toward the client (used by the server->client relay task; fed by WINDOW_UPDATE/
    ///     SETTINGS_INITIAL_WINDOW_SIZE frames read from the client on the client->server relay task).
    /// </summary>
    public Http2FlowController ClientSendFlow { get; } = new();

    /// <summary>
    ///     Governs writes toward the server (used by the client->server relay task; fed by WINDOW_UPDATE/
    ///     SETTINGS_INITIAL_WINDOW_SIZE frames read from the server on the server->client relay task).
    /// </summary>
    public Http2FlowController ServerSendFlow { get; } = new();

    /// <summary>All currently open (or draining) streams, keyed by the stream id used identically on both legs.</summary>
    public ConcurrentDictionary<int, Http2StreamState> Streams { get; } = new();

    /// <summary>
    ///     Writes toward the client can originate from the server->client relay as well as a synthetic
    ///     response emitted from the client->server relay; serialize them so frames never interleave.
    /// </summary>
    public SemaphoreSlim ClientWriteLock { get; } = new(1, 1);

    /// <summary>
    ///     Writes toward the server can originate from the client->server relay's main dispatch/relay path
    ///     as well as the server->client relay's own-leg control-frame replies (WINDOW_UPDATE receive-credit
    ///     grants, RST_STREAM, PING ACK, GOAWAY); serialize them so frames never interleave, mirroring
    ///     <see cref="ClientWriteLock" />.
    /// </summary>
    public SemaphoreSlim ServerWriteLock { get; } = new(1, 1);

    /// <summary>Completed once the server's connection SETTINGS frame has been relayed to the client.</summary>
    public TaskCompletionSource<bool> ServerSettingsRelayed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Background tasks for synthetic (proxy-generated) responses, tracked so they can be observed for
    ///     failure and awaited before the owning relay direction completes.
    /// </summary>
    public ConcurrentBag<Task> PendingSynthetics { get; } = new();

    /// <summary>Cancels both relay directions; shared with the caller so either can trigger connection-wide teardown.</summary>
    public CancellationTokenSource CancellationTokenSource { get; }

    /// <summary>Set once a GOAWAY has been received from the client; no new client-initiated streams above the recorded id should be admitted.</summary>
    public volatile bool ClientGoingAway;

    /// <summary>Highest client-initiated stream id the client itself said it would still process, once <see cref="ClientGoingAway" />.</summary>
    public int ClientLastStreamId = int.MaxValue;

    /// <summary>Set once a GOAWAY has been received from the server; no new streams should be opened toward it.</summary>
    public volatile bool ServerGoingAway;

    /// <summary>Highest stream id the server itself said it would still process, once <see cref="ServerGoingAway" />.</summary>
    public int ServerLastStreamId = int.MaxValue;

    /// <summary>
    ///     Registers a newly observed stream (first HEADERS frame) in both the stream registry and both
    ///     flow-control send windows.
    /// </summary>
    public Http2StreamState RegisterStream(int streamId, SessionEventArgs sessionArgs)
    {
        var state = new Http2StreamState(streamId, sessionArgs);
        if (!Streams.TryAdd(streamId, state))
        {
            return Streams[streamId];
        }

        ClientSendFlow.RegisterStream(streamId);
        ServerSendFlow.RegisterStream(streamId);
        return state;
    }

    /// <summary>Removes a stream from the registry and both flow-control send windows once fully closed/reset.</summary>
    public void RemoveStream(int streamId)
    {
        Streams.TryRemove(streamId, out _);
        ClientSendFlow.RemoveStream(streamId);
        ServerSendFlow.RemoveStream(streamId);
    }
}
#endif
