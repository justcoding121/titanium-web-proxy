#pragma warning disable CA1416
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
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
    private readonly CancellationToken _shutdownToken;
    private readonly Func<SessionEventArgs, Task> _onBeforeRequest;
    private readonly Func<SessionEventArgs, Task> _onBeforeResponse;
    private readonly Func<SessionEventArgs, Task> _onAfterResponse;

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
        _shutdownToken = shutdownToken;
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
            // Instantiate QPACK context when dynamic table is enabled.
            if (_server.EnableQpackDynamicTable)
                _qpackContext = new QpackContext(4096);

            // Open our outbound control stream and send SETTINGS.
            _serverControlStream = await _connection.OpenOutboundStreamAsync(
                QuicStreamType.Unidirectional, _shutdownToken);
            await SendServerSettingsAsync(_serverControlStream, _qpackContext, _shutdownToken);

            // When dynamic table is enabled, open outbound QPACK encoder and decoder streams and
            // start their background loops.
            if (_qpackContext != null)
            {
                var qpackEncoderStream = await _connection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional, _shutdownToken);
                // Write stream type byte 0x02 (QPACK encoder stream).
                await qpackEncoderStream.WriteAsync(new byte[] { (byte)Http3StreamType.QpackEncoder }, _shutdownToken);

                var qpackDecoderStream = await _connection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional, _shutdownToken);
                // Write stream type byte 0x03 (QPACK decoder stream).
                await qpackDecoderStream.WriteAsync(new byte[] { (byte)Http3StreamType.QpackDecoder }, _shutdownToken);

                // Start the ack writer background loop.
                _ = QpackDecoderStreamWriter.RunAsync(qpackDecoderStream, _qpackContext, _shutdownToken);
            }

            // Run request-accept loop concurrently with unidirectional stream setup.
            var acceptTask = AcceptStreamsAsync(_shutdownToken);
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
            await FinalizeAllStreamsAsync();
            await SendGoAwayAsync();

            if (_qpackContext != null)
                await _qpackContext.DisposeAsync();

            _server.UpdateHttp3ClientConnectionCount(false);
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

            // Route by stream type.
            if (stream.Type == QuicStreamType.Bidirectional)
            {
                _ = HandleRequestStreamAsync(stream, ct);
            }
            else
            {
                _ = HandleUnidirectionalStreamAsync(stream, ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Error on HTTP/3 unidirectional stream {StreamId}", stream.Id);
            }
        }
    }

    private async Task ProcessClientControlStreamAsync(QuicStream stream, CancellationToken ct)
    {
        var receivedSettings = false;
        while (!ct.IsCancellationRequested)
        {
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 16 * 1024, ct);
            if (frame is null) return;

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
                qpackContext: _qpackContext);
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
            state.Cancellation.Cancel();
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
