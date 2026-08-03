#pragma warning disable CA1416
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Manages the lifecycle of one accepted inbound QUIC connection acting as an HTTP/3 server.
///     Responsibilities:
///     <list type="bullet">
///       <item><description>Open the outbound control stream and send a SETTINGS frame.</description></item>
///       <item><description>Accept and route inbound unidirectional streams (client control, QPACK encoder/decoder).</description></item>
///       <item><description>Accept and dispatch inbound bidirectional streams (request streams) to <see cref="Http3RequestStream" />.</description></item>
///       <item><description>Send GOAWAY on graceful shutdown.</description></item>
///       <item><description>Track in-flight streams and ensure exactly-once AfterResponse on each.</description></item>
///     </list>
/// </summary>
internal sealed class Http3Connection
{
    private readonly QuicConnection _connection;
    private readonly TransparentQuicProxyEndPoint _endPoint;
    private readonly BeforeQuicAuthenticateEventArgs _authArgs;
    private readonly ProxyServer _server;
    private readonly ILogger _logger;
    private readonly Func<SessionEventArgs, Task> _onBeforeRequest;
    private readonly Func<SessionEventArgs, Task> _onBeforeResponse;
    private readonly Func<SessionEventArgs, Task> _onAfterResponse;

    /// <summary>
    ///     Connection-scoped cancellation, linked from the caller-owned shutdown token but also
    ///     cancellable by this connection itself (e.g. from <see cref="AbortConnectionAsync" /> when a
    ///     connection-level protocol violation is detected). Every background task this connection
    ///     spawns - request streams, the control-stream reader, the QPACK encoder-stream reader, the
    ///     QPACK decoder-stream ack writer - observes this token, so cancelling it once in
    ///     <see cref="RunCoreAsync" />'s <c>finally</c> unblocks all of them before <see cref="_qpackContext" />
    ///     or any session state is disposed. Never dispose session state while an unjoined callback
    ///     might still hold a reference to it - the same requirement the hardening plan's "Bounded
    ///     callbacks" section states for every protocol, generalized here from the HTTP/3 defect that
    ///     motivated it.
    /// </summary>
    private readonly CancellationTokenSource _connectionCts;

    /// <summary>
    ///     Every background task spawned for this connection (per-request-stream handlers,
    ///     unidirectional-stream handlers, the QPACK decoder-stream ack writer), so
    ///     <see cref="JoinBackgroundTasksAsync" /> can wait for all of them to actually finish - not
    ///     just observe that their owning token was cancelled - before shared per-connection state is
    ///     torn down.
    /// </summary>
    private readonly ConcurrentBag<Task> _backgroundTasks = new();

    /// <summary>
    ///     Active request streams keyed by QUIC stream ID (long). Values are the per-stream state objects
    ///     used to coordinate finalizations and track in-flight work.
    /// </summary>
    private readonly ConcurrentDictionary<long, Http3StreamState> _activeStreams = new();

    /// <summary>
    ///     The largest client-initiated bidirectional stream ID received from the client, used to compute
    ///     the GOAWAY push ID parameter.
    /// </summary>
    private long _highestStreamIdSeen = -1;

    /// <summary>
    ///     The outbound control stream opened by this proxy (server direction → client).
    ///     Kept open for the duration of the connection.
    /// </summary>
    private QuicStream? _serverControlStream;

    /// <summary>
    ///     The client's inbound control stream (client direction → proxy). We read SETTINGS and
    ///     potential GOAWAY frames from it.
    /// </summary>
    private Http3Settings _clientSettings = new();

    /// <summary>
    ///     Per-connection QPACK dynamic table context. Non-null only when
    ///     <see cref="ProxyServer.EnableQpackDynamicTable" /> is true.
    /// </summary>
    private QpackContext? _qpackContext;

    /// <summary>
    ///     Shared client-connection adapter for every multiplexed request stream on this QUIC
    ///     connection so <c>ClientConnectionId</c> is stable across sessions.
    /// </summary>
    private QuicClientConnection? _clientConnection;

    private Http3Connection(
        QuicConnection connection,
        TransparentQuicProxyEndPoint endPoint,
        BeforeQuicAuthenticateEventArgs authArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken shutdownToken,
        Func<SessionEventArgs, Task> onBeforeRequest,
        Func<SessionEventArgs, Task> onBeforeResponse,
        Func<SessionEventArgs, Task> onAfterResponse)
    {
        _connection = connection;
        _endPoint = endPoint;
        _authArgs = authArgs;
        _server = server;
        _logger = logger;
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        _onBeforeRequest = onBeforeRequest;
        _onBeforeResponse = onBeforeResponse;
        _onAfterResponse = onAfterResponse;
    }

    /// <summary>
    ///     Entry point: runs the entire lifecycle of one HTTP/3 client connection until the connection is
    ///     closed or <paramref name="shutdownToken" /> is cancelled.
    /// </summary>
    public static async Task RunAsync(
        QuicConnection connection,
        TransparentQuicProxyEndPoint endPoint,
        BeforeQuicAuthenticateEventArgs authArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken shutdownToken,
        Func<SessionEventArgs, Task> onBeforeRequest,
        Func<SessionEventArgs, Task> onBeforeResponse,
        Func<SessionEventArgs, Task> onAfterResponse)
    {
        var conn = new Http3Connection(connection, endPoint, authArgs, server, logger, shutdownToken,
            onBeforeRequest, onBeforeResponse, onAfterResponse);
        await conn.RunCoreAsync();
    }

    private async Task RunCoreAsync()
    {
        _server.UpdateHttp3ClientConnectionCount(true);
        try
        {
            _clientConnection = new QuicClientConnection(
                _server,
                (IPEndPoint)_connection.LocalEndPoint,
                (IPEndPoint)_connection.RemoteEndPoint);

            // Instantiate QPACK context when dynamic table is enabled.
            if (_server.EnableQpackDynamicTable)
                _qpackContext = new QpackContext(4096);

            // Open our outbound control stream and send SETTINGS.
            _serverControlStream = await _connection.OpenOutboundStreamAsync(
                QuicStreamType.Unidirectional, _connectionCts.Token);
            await SendServerSettingsAsync(_serverControlStream, _qpackContext, _connectionCts.Token);

            // When dynamic table is enabled, open outbound QPACK encoder and decoder streams and
            // start their background loops.
            if (_qpackContext != null)
            {
                var qpackEncoderStream = await _connection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional, _connectionCts.Token);
                // Write stream type byte 0x02 (QPACK encoder stream).
                await qpackEncoderStream.WriteAsync(new byte[] { (byte)Http3StreamType.QpackEncoder }, _connectionCts.Token);

                var qpackDecoderStream = await _connection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional, _connectionCts.Token);
                // Write stream type byte 0x03 (QPACK decoder stream).
                await qpackDecoderStream.WriteAsync(new byte[] { (byte)Http3StreamType.QpackDecoder }, _connectionCts.Token);

                // Start the ack writer background loop, tracked so it is joined before teardown.
                _backgroundTasks.Add(QpackDecoderStreamWriter.RunAsync(qpackDecoderStream, _qpackContext, _connectionCts.Token));
            }

            // Run request-accept loop concurrently with unidirectional stream setup.
            var acceptTask = AcceptStreamsAsync(_connectionCts.Token);
            await acceptTask;
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (QuicException qex) when (
            qex.QuicError == QuicError.ConnectionAborted ||
            qex.QuicError == QuicError.ConnectionIdle)
        {
            _logger.LogDebug("HTTP/3 client connection closed: {QuicError}", qex.QuicError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP/3 connection error from {Remote}", _connection.RemoteEndPoint);
        }
        finally
        {
            // Cancel first so every task tracked in _backgroundTasks (and every per-stream operation
            // linked to _connectionCts.Token) unblocks promptly, then actually wait for them to finish
            // - not just observe the token as cancelled - before anything they might still be using
            // (QpackContext, session state) is disposed below.
            await _connectionCts.CancelAsync();
            await JoinBackgroundTasksAsync();

            await FinalizeAllStreamsAsync();
            await SendGoAwayAsync();

            if (_qpackContext != null)
                await _qpackContext.DisposeAsync();

            _clientConnection?.Dispose();
            _clientConnection = null;

            _connectionCts.Dispose();
            _server.UpdateHttp3ClientConnectionCount(false);
        }
    }

    /// <summary>
    ///     Tears down the whole QUIC connection with the given HTTP/3 connection-level error code
    ///     (RFC 9114 §8.1). Used instead of letting a per-stream/per-task catch block quietly log a
    ///     <see cref="Http3ConnectionException" /> and move on: the violations that produce this
    ///     exception (a missing/duplicate/misplaced control-stream SETTINGS frame, a closed critical
    ///     stream, a QPACK decompression failure) corrupt state the whole connection depends on - the
    ///     shared QPACK dynamic table, or the control-stream state machine itself - so no other stream
    ///     on this connection can be trusted to continue either.
    /// </summary>
    private async Task AbortConnectionAsync(Http3ErrorCode errorCode, Exception ex)
    {
        ProxyMetrics.ParserError("http3");
        _logger.LogWarning(ex, "HTTP/3 connection-level error, closing connection: {ErrorCode}", errorCode);
        try
        {
            await _connection.CloseAsync((long)errorCode, CancellationToken.None);
        }
        catch (Exception closeEx)
        {
            _logger.LogDebug(closeEx, "Error while closing HTTP/3 connection after {ErrorCode}", errorCode);
        }
    }

    /// <summary>
    ///     Bound on how long <see cref="JoinBackgroundTasksAsync" /> waits for every background task to
    ///     actually finish after <see cref="_connectionCts" /> is cancelled. Every task here is expected
    ///     to observe that cancellation and unwind promptly; this exists only so one task that somehow
    ///     ignores cancellation (e.g. blocked in a non-cancellable call) cannot hang connection teardown
    ///     forever - matching the logger sink's own "leak the handle and report it rather than block
    ///     indefinitely" drain policy.
    /// </summary>
    private static readonly TimeSpan BackgroundTaskDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Waits for every task tracked in <see cref="_backgroundTasks" /> to finish, up to
    ///     <see cref="BackgroundTaskDrainTimeout" />. Called only after <see cref="_connectionCts" /> has
    ///     been cancelled, so every task is expected to unblock and complete promptly rather than hang.
    ///     Faults are logged but not rethrown: each task already reports its own failure at its own call
    ///     site (<see cref="AbortConnectionAsync" /> or the per-stream/per-task catch blocks), and by the
    ///     time this connection is tearing down, there is nothing further to do with a task's fault
    ///     except make sure it has actually stopped running before shared state is disposed.
    /// </summary>
    private async Task JoinBackgroundTasksAsync()
    {
        if (_backgroundTasks.IsEmpty) return;
        try
        {
            var allDone = Task.WhenAll(_backgroundTasks);
            // Drain timeout must not share the connection CTS (already cancelled at this point).
            var completed = await Task.WhenAny(allDone,
                Task.Delay(BackgroundTaskDrainTimeout, CancellationToken.None));
            if (completed != allDone)
            {
                _logger.LogWarning(
                    "One or more HTTP/3 background tasks did not finish within {Timeout} of connection " +
                    "teardown being cancelled; proceeding without waiting further.", BackgroundTaskDrainTimeout);
                return;
            }

            await allDone;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "One or more HTTP/3 background tasks faulted during connection teardown.");
        }
    }

    /// <summary>
    ///     Opens the server-side outbound control stream and sends the SETTINGS frame.
    /// </summary>
    private static async Task SendServerSettingsAsync(QuicStream controlStream, QpackContext? qpackContext, CancellationToken ct)
    {
        // Write the stream type byte (0x00 = control stream).
        var streamTypeBuf = new byte[1];
        streamTypeBuf[0] = (byte)Http3StreamType.Control;
        await controlStream.WriteAsync(streamTypeBuf.AsMemory(), ct);

        // Build SETTINGS payload.
        var settings = new Http3Settings();

        if (qpackContext != null)
        {
            // Advertise QPACK dynamic table capability (RFC 9204 §3.1).
            settings.SetQpackMaxTableCapacity(4096);
            settings.SetQpackBlockedStreams(0); // we don't block
        }

        var payload = settings.Serialize();
        await Http3Frame.WriteAsync(controlStream, Http3FrameType.Settings, payload, ct);
        await controlStream.FlushAsync(ct);
    }

    private async Task AcceptStreamsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QuicStream stream;
            try
            {
                stream = await _connection.AcceptInboundStreamAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (QuicException qex) when (qex.QuicError == QuicError.ConnectionAborted ||
                                            qex.QuicError == QuicError.ConnectionIdle)
            {
                return;
            }

            // Route by stream type. Tracked in _backgroundTasks (rather than fire-and-forget) so
            // JoinBackgroundTasksAsync can wait for every one of them to finish before this
            // connection's shared state (QpackContext, session state) is torn down.
            if (stream.Type == QuicStreamType.Bidirectional)
            {
                _backgroundTasks.Add(HandleRequestStreamAsync(stream, ct));
            }
            else
            {
                _backgroundTasks.Add(HandleUnidirectionalStreamAsync(stream, ct));
            }
        }
    }

    private async Task HandleUnidirectionalStreamAsync(QuicStream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                // Read the unidirectional stream type byte.
                var streamType = await Http3VarInt.ReadAsync(stream, ct);
                if (streamType is null) return;

                switch (streamType.Value)
                {
                    case Http3StreamType.Control:
                        await ProcessClientControlStreamAsync(stream, ct);
                        break;
                    case Http3StreamType.QpackEncoder:
                        if (_qpackContext != null)
                            await QpackEncoderStreamReader.ProcessAsync(stream, _qpackContext, ct);
                        else
                            await DrainStreamAsync(stream, ct);
                        break;
                    case Http3StreamType.QpackDecoder:
                        // Decoder stream from client: Section Acks for our outbound HEADERS blocks.
                        // Currently we don't use acks to unpin InFlightMinAbsoluteIndex entries here
                        // (that is done in AfterResponse), so drain to prevent flow-control stall.
                        await DrainStreamAsync(stream, ct);
                        break;
                    default:
                        // Unknown stream type — drain and discard per RFC 9114 §6.2.
                        await DrainStreamAsync(stream, ct);
                        break;
                }
            }
            catch (Http3ConnectionException ex)
            {
                // A connection-level violation on any unidirectional stream (missing/duplicate
                // SETTINGS or a closed critical control stream, a truncated QPACK encoder-stream
                // instruction) invalidates state the whole connection depends on - tear it all down
                // rather than quietly returning and letting the `await using` above dispose just
                // this one stream as if nothing happened.
                await AbortConnectionAsync(ex.ErrorCode, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Error on HTTP/3 unidirectional stream {StreamId}", stream.Id);
            }
        }
    }

    private async Task ProcessClientControlStreamAsync(QuicStream stream, CancellationToken ct)
    {
        var receivedSettings = false;
        var isFirstFrame = true;
        while (!ct.IsCancellationRequested)
        {
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 16 * 1024, ct);
            if (frame is null)
                // RFC 9114 §6.2.1: "If either control stream is closed at any point, this MUST be
                // treated as a connection error of type H3_CLOSED_CRITICAL_STREAM." The client
                // closing its own control stream - even after a previously valid SETTINGS - is
                // itself the violation; there is no legitimate reason for this stream to ever end.
                throw new Http3ConnectionException(Http3ErrorCode.ClosedCriticalStream,
                    "The client's control stream was closed.");

            if (isFirstFrame)
            {
                isFirstFrame = false;
                // RFC 9114 §6.2.1: "the first frame on the control stream MUST be a SETTINGS
                // frame". Accepting MAX_PUSH_ID/CANCEL_PUSH (or anything else) before SETTINGS
                // would let a peer configure connection-wide behavior we have not yet negotiated.
                if (frame.Type != Http3FrameType.Settings)
                    throw new Http3ConnectionException(Http3ErrorCode.MissingSettings,
                        $"The first frame on the client's control stream must be SETTINGS; got type 0x{frame.Type:X}.");
            }

            switch (frame.Type)
            {
                case Http3FrameType.Settings:
                    if (receivedSettings)
                        throw new Http3ConnectionException(Http3ErrorCode.FrameUnexpected,
                            "Received duplicate SETTINGS on control stream.");
                    _clientSettings = Http3Settings.Parse(frame.Payload.Span);
                    receivedSettings = true;
                    // If the client sets QPACK_MAX_TABLE_CAPACITY = 0, disable outbound dynamic table
                    // encoding so we never reference entries the peer will not maintain.
                    if (_qpackContext != null && _clientSettings.QpackMaxTableCapacity == 0)
                        _qpackContext.DisableOutboundTable();
                    else if (_qpackContext != null && _clientSettings.QpackMaxTableCapacity > 0)
                        _qpackContext.MaxTableCapacityFromPeer = _clientSettings.QpackMaxTableCapacity;
                    break;
                case Http3FrameType.GoAway:
                    // Client is initiating graceful shutdown — stop processing new requests.
                    return;
                case Http3FrameType.CancelPush:
                case Http3FrameType.MaxPushId:
                    // Accepted but we don't implement push — ignore.
                    break;
                default:
                    // DATA, HEADERS etc. are not allowed on the control stream (RFC 9114 §7.2.1).
                    if (frame.Type == Http3FrameType.Data || frame.Type == Http3FrameType.Headers)
                        throw new Http3ConnectionException(Http3ErrorCode.FrameUnexpected,
                            $"Frame type 0x{frame.Type:X} not permitted on control stream.");
                    // Unknown frame types MUST be ignored (RFC 9114 §9).
                    break;
            }
        }
    }

    private async Task HandleRequestStreamAsync(QuicStream stream, CancellationToken ct)
    {
        var streamId = stream.Id;
        Interlocked.Exchange(ref _highestStreamIdSeen, Math.Max(_highestStreamIdSeen, streamId));

        Http3StreamState? streamState = null;

        try
        {
            await Http3RequestStream.HandleAsync(
                stream, _connection, _endPoint, _authArgs, _server, _logger, ct,
                onSessionCreated: (args, state) =>
                {
                    streamState = state;
                    _activeStreams[streamId] = state;
                },
                _onBeforeRequest, _onBeforeResponse, _onAfterResponse,
                qpackContext: _qpackContext,
                clientConnection: _clientConnection!);
        }
        catch (Http3ConnectionException ex)
        {
            // Propagated by Http3RequestStream.HandleAsync (e.g. a QPACK decompression failure):
            // this corrupted state - typically the shared inbound QPACK dynamic table - that every
            // other stream on the connection depends on, so the whole connection must be torn down,
            // not just this one stream.
            await AbortConnectionAsync(ex.ErrorCode, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "HTTP/3 request stream {StreamId} error", streamId);
        }
        finally
        {
            if (streamState != null)
                _activeStreams.TryRemove(streamId, out _);
        }
    }

    private static async Task DrainStreamAsync(QuicStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buf, ct);
            if (read == 0) return;
        }
    }

    private async Task FinalizeAllStreamsAsync()
    {
        foreach (var (_, state) in _activeStreams)
        {
            if (Interlocked.CompareExchange(ref state.FinalizedFlag, 1, 0) == 0)
            {
                try
                {
                    await _onAfterResponse(state.SessionArgs);
                    state.SessionArgs.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in AfterResponse during HTTP/3 connection teardown");
                }
            }
            await state.Cancellation.CancelAsync();
            state.Cancellation.Dispose();
        }
        _activeStreams.Clear();
    }

    private async Task SendGoAwayAsync()
    {
        var controlStream = _serverControlStream;
        if (controlStream is null) return;
        try
        {
            // GOAWAY payload: the stream ID of the last stream we are willing to process.
            // Use the highest stream ID seen + 4 to leave room for in-flight retries.
            var lastStreamId = Math.Max(0, _highestStreamIdSeen);
            var payload = new byte[8];
            var len = Http3VarInt.Write(payload, (ulong)lastStreamId);
            await Http3Frame.WriteAsync(controlStream, Http3FrameType.GoAway, payload.AsMemory(0, len), CancellationToken.None);
            controlStream.CompleteWrites();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error sending HTTP/3 GOAWAY");
        }
        finally
        {
            await controlStream.DisposeAsync();
        }
    }
}
#pragma warning restore CA1416
