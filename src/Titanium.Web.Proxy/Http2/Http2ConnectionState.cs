using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;

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
    public Http2ConnectionState(long connectionId, CancellationTokenSource cancellationTokenSource,
        int maxPooledStreamStates = 256)
    {
        ConnectionId = connectionId;
        CancellationTokenSource = cancellationTokenSource;
        StreamStatePool = new Http2StreamStatePool(maxPooledStreamStates);
    }

    public long ConnectionId { get; }

    public Http2StreamStatePool StreamStatePool { get; }

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

    /// <summary>
    ///     Optional dedicated writers (P0.3). When set, <see cref="EnqueueWriteRented"/> prefers these
    ///     over the ContinueWith + SemaphoreSlim chain.
    /// </summary>
    public Http2FrameWriter? ClientFrameWriter { get; set; }

    public Http2FrameWriter? ServerFrameWriter { get; set; }

    /// <summary>
    ///     Optional multi-origin pool for gate-off same-protocol H2↔H2. When set, compressed-relay
    ///     streams are distributed across origin legs with stream-id remapping.
    /// </summary>
    public Http2OriginRelayPool? OriginRelayPool { get; set; }

    /// <summary>Completed once the server's connection SETTINGS frame has been relayed to the client.</summary>
    public TaskCompletionSource<bool> ServerSettingsRelayed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Background tasks for synthetic (proxy-generated) responses, tracked so they can be observed for
    ///     failure and awaited before the owning relay direction completes.
    /// </summary>
    public ConcurrentBag<Task> PendingSynthetics { get; } = new();

    /// <summary>
    ///     Background tasks running each stream's <c>AfterResponse</c> + <c>Dispose</c> finalization (see
    ///     <see cref="Http2StreamState.FinalizedFlag" />), tracked so a slow user <c>AfterResponse</c>
    ///     handler for one stream never stalls frame processing for every other multiplexed stream, while
    ///     still being awaited (in <c>Http2Helper.SendHttp2</c>) before the whole relay call returns.
    /// </summary>
    public ConcurrentBag<Task> PendingFinalizations { get; } = new();

    /// <summary>
    ///     The highest client-initiated stream id admitted so far on this connection, used to enforce RFC
    ///     7540 §5.1.1: client-initiated stream ids must be odd and strictly increasing. 0 (no stream
    ///     admitted yet) is even, so the first real stream id (1) always passes the "strictly increasing"
    ///     check.
    /// </summary>
    public int LastClientStreamId;

    /// <summary>
    ///     Per-stream multipart observers for h2 multipart/form-data boundary-aware streaming.
    ///     Only populated for streams that have a <see cref="SessionEventArgs.MultipartRequestPartSent"/>
    ///     subscriber and a multipart/form-data Content-Type.
    /// </summary>
    internal ConcurrentDictionary<int, MultipartStreamObserver> MultipartObservers { get; } = new();

    /// <summary>Cancels both relay directions; shared with the caller so either can trigger connection-wide teardown.</summary>
    public CancellationTokenSource CancellationTokenSource { get; }

    /// <summary>
    ///     Set to <see langword="true"/> once the proxy has actually written SETTINGS_ENABLE_CONNECT_PROTOCOL=1
    ///     to the client (either relayed from the server or injected).  Extended CONNECT requests received
    ///     before this flag is set are rejected with RST_STREAM(PROTOCOL_ERROR) per RFC 8441 §3.
    /// </summary>
    public volatile bool DownstreamAdvertisedEnableConnect;

    /// <summary>
    ///     0 until the Chrome-sized connection WINDOW_UPDATE has been written to the origin on the H2↔H2
    ///     MITM path (after the browser's first non-ACK SETTINGS is relayed). CompareExchange-guarded so
    ///     only one client→server SETTINGS frame triggers it.
    /// </summary>
    public int InitialOriginWindowUpdateSent;

    /// <summary>
    ///     0 until the 1 MiB connection WINDOW_UPDATE has been written toward the client
    ///     (enlarging the client's connection send window above RFC 65535).
    /// </summary>
    public int InitialClientWindowUpdateSent;

    /// <summary>
    ///     Serializes origin-bound HEADERS writes so HPACK encode stays ordered, while the client
    ///     frame-read loop can continue admitting the next stream without awaiting socket I/O
    ///     (encode on the read loop, queue the write, continue without awaiting peer I/O invariant).
    /// </summary>
    private readonly object serverWriteChainLock = new();
    private Task serverWriteChain = Task.CompletedTask;

    /// <summary>
    ///     Same as the server write chain, but for frames written toward the client (response HEADERS/DATA).
    /// </summary>
    private readonly object clientWriteChainLock = new();
    private Task clientWriteChain = Task.CompletedTask;

    private static Task EnqueueWrite(object gate, ref Task chain, Func<Task> write)
    {
        lock (gate)
        {
            chain = chain.ContinueWith(
                    async antecedent =>
                    {
                        if (antecedent.IsFaulted)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                                .Capture(antecedent.Exception!.InnerException ?? antecedent.Exception).Throw();
                        if (antecedent.IsCanceled)
                            throw new TaskCanceledException(antecedent);

                        await write().ConfigureAwait(false);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
            return chain;
        }
    }

    /// <summary>
    ///     Enqueues an origin-bound write after prior enqueued writes. Returns the chain task (do not
    ///     await it on the frame-read loop — await <see cref="ServerWriteChain"/> at connection teardown).
    /// </summary>
    public Task EnqueueServerWrite(Func<Task> write) =>
        EnqueueWrite(serverWriteChainLock, ref serverWriteChain, write);

    /// <summary>Enqueues a client-bound write (response path).</summary>
    public Task EnqueueClientWrite(Func<Task> write) =>
        EnqueueWrite(clientWriteChainLock, ref clientWriteChain, write);

    /// <summary>
    ///     Enqueues a write of an already-rented buffer (ownership transfers; returned to the pool after write).
    /// </summary>
    public void EnqueueWriteRented(bool towardServer, SemaphoreSlim writeLock, Stream output, byte[] rented,
        int length)
    {
        var dedicated = towardServer ? ServerFrameWriter : ClientFrameWriter;
        if (dedicated != null)
        {
            dedicated.EnqueueRented(rented, length);
            return;
        }

        Func<Task> body = async () =>
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await output.WriteAsync(rented.AsMemory(0, length), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
                ArrayPool<byte>.Shared.Return(rented);
            }
        };

        if (towardServer)
            EnqueueServerWrite(body);
        else
            EnqueueClientWrite(body);
    }

    /// <summary>
    ///     Enqueues a write of an already-rented buffer toward the origin.
    /// </summary>
    public void EnqueueServerWriteRented(SemaphoreSlim writeLock, Stream output, byte[] rented, int length) =>
        EnqueueWriteRented(towardServer: true, writeLock, output, rented, length);

    /// <summary>
    ///     Copies <paramref name="frameBytes"/> and enqueues a write under <paramref name="writeLock"/>.
    /// </summary>
    public void EnqueueServerWriteCopy(SemaphoreSlim writeLock, Stream output, ReadOnlySpan<byte> frameBytes)
    {
        var length = frameBytes.Length;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        frameBytes.CopyTo(rented);
        EnqueueServerWriteRented(writeLock, output, rented, length);
    }

    /// <summary>Current tail of the origin write queue; await at connection teardown.</summary>
    public Task ServerWriteChain
    {
        get
        {
            lock (serverWriteChainLock) return serverWriteChain;
        }
    }

    /// <summary>Current tail of the client write queue; await at connection teardown.</summary>
    public Task ClientWriteChain
    {
        get
        {
            lock (clientWriteChainLock) return clientWriteChain;
        }
    }

    /// <summary>Set once a GOAWAY has been received from the client; no new client-initiated streams above the recorded id should be admitted.</summary>
    public volatile bool ClientGoingAway;

    /// <summary>Highest client-initiated stream id the client itself said it would still process, once <see cref="ClientGoingAway" />.</summary>
    public int ClientLastStreamId = int.MaxValue;

    /// <summary>Set once a GOAWAY has been received from the server; no new streams should be opened toward it.</summary>
    public volatile bool ServerGoingAway;

    /// <summary>Highest stream id the server itself said it would still process, once <see cref="ServerGoingAway" />.</summary>
    public int ServerLastStreamId = int.MaxValue;

    /// <summary>
    ///     Count of RST_STREAM frames received directly from the client for a stream that had not yet
    ///     completed normally (still tracked in <see cref="Streams" /> at the moment the reset
    ///     arrived) - the abuse signal for a Rapid Reset (CVE-2023-44487) style attack, where a client
    ///     opens and immediately cancels streams to make the proxy perform unbounded per-stream setup
    ///     work for zero completed responses. Proxy-initiated resets are never counted: only the
    ///     client->server relay task's own handling of an RST_STREAM frame it read directly from the
    ///     client increments this.
    /// </summary>
    public int ClientIncompleteStreamResetCount;

    /// <summary>
    ///     Set once <see cref="ClientIncompleteStreamResetCount" /> exceeds the configured budget.
    ///     Streams already admitted (id &lt;= <see cref="ClientResetBudgetLastStreamId" />) are still
    ///     allowed to drain normally; any new client-initiated stream above that id is refused - RFC
    ///     9113 §6.8's graceful-shutdown semantics, rather than tearing the whole connection down and
    ///     discarding in-flight work immediately.
    /// </summary>
    public volatile bool ClientResetBudgetExceeded;

    /// <summary>The last-stream-id sent in the GOAWAY triggered by <see cref="ClientResetBudgetExceeded" />.</summary>
    public int ClientResetBudgetLastStreamId = int.MaxValue;

    /// <summary>
    ///     Registers a newly observed stream (first HEADERS frame) in both the stream registry and both
    ///     flow-control send windows.
    /// </summary>
    public Http2StreamState RegisterStream(int streamId, SessionEventArgs sessionArgs)
    {
        var state = StreamStatePool.RentSession(streamId, sessionArgs);
        if (!Streams.TryAdd(streamId, state))
        {
            StreamStatePool.Return(state);
            return Streams[streamId];
        }

        ClientSendFlow.RegisterStream(streamId);
        ServerSendFlow.RegisterStream(streamId);
        return state;
    }

    /// <summary>
    ///     Registers a passthrough stream that relays compressed HEADERS/DATA without a
    ///     <see cref="SessionEventArgs" /> (gate-off same-protocol H2↔H2).
    /// </summary>
    public Http2StreamState RegisterCompressedRelayStream(int streamId)
    {
        var state = StreamStatePool.RentCompressed(streamId);
        if (!Streams.TryAdd(streamId, state))
        {
            StreamStatePool.Return(state);
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
        MultipartObservers.TryRemove(streamId, out _);
        ClientSendFlow.RemoveStream(streamId);
        ServerSendFlow.RemoveStream(streamId);
    }

    /// <summary>Returns a stream state shell to the connection pool after finalization.</summary>
    public void ReturnStreamState(Http2StreamState state) => StreamStatePool.Return(state);
}
