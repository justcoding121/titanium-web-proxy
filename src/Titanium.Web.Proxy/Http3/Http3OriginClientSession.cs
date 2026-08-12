#pragma warning disable CA1416
using System;
using System.Collections.Concurrent;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Outbound HTTP/3 client control session for an origin <see cref="QuicConnection"/>.
///     Opens the required client control stream (SETTINGS) and continuously accepts/drains
///     inbound unidirectional streams (peer control + QPACK) so connection-level flow control
///     stays healthy. Request/response framing remains in <see cref="Http3OriginBridge"/>.
/// </summary>
internal sealed class Http3OriginClientSession : IAsyncDisposable
{
    private readonly QuicConnection _connection;
    private readonly ProxyServer _proxyServer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _backgroundTasks = [];
    private Task? _acceptLoop;
    private QuicStream? _controlStream;
    private Http3Settings? _peerSettings;
    private int _disposed;

    internal Http3OriginClientSession(QuicConnection connection, ProxyServer proxyServer)
    {
        _connection = connection;
        _proxyServer = proxyServer;
    }

    /// <summary>Peer SETTINGS when received; otherwise <see langword="null"/>.</summary>
    internal Http3Settings? PeerSettings => _peerSettings;

    /// <summary>
    ///     Sends client SETTINGS, then starts the inbound uni-stream drain loop.
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var ct = linked.Token;

        _controlStream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
        // Stream type 0x00 = control (RFC 9114 §6.2.1).
        await _controlStream.WriteAsync(new byte[] { (byte)Http3StreamType.Control }, ct);

        // Empty SETTINGS is valid; we advertise no QPACK dynamic table (static-only default).
        var settings = new Http3Settings();
        await Http3Frame.WriteAsync(_controlStream, Http3FrameType.Settings, settings.Serialize(), ct);
        await _controlStream.FlushAsync(ct);

        // Run on the thread pool without nesting Task.Run(Func<Task>) — assign the task directly.
        _acceptLoop = AcceptUnidirectionalStreamsAsync(_cts.Token);
    }

    private async Task AcceptUnidirectionalStreamsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QuicStream stream;
            try
            {
                stream = await _connection.AcceptInboundStreamAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (QuicException qex) when (qex.QuicError is QuicError.ConnectionAborted
                                           or QuicError.ConnectionIdle
                                           or QuicError.ConnectionTimeout)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                    _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin AcceptInboundStreamAsync ended");
                return;
            }

            // Origin connections never accept inbound bidi request streams (MaxInboundBidirectionalStreams=0).
            if (stream.Type == QuicStreamType.Bidirectional)
            {
                try { await stream.DisposeAsync(); }
                catch (Exception ex)
                {
                    if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                        _proxyServer.Logger.LogDebug(ex, "Failed disposing unexpected inbound bidi stream");
                }
                continue;
            }

            // Track drain tasks (do not discard) so DisposeAsync can join them.
            _backgroundTasks.Add(DrainInboundUnidirectionalStreamAsync(stream, ct));
        }
    }

    private async Task DrainInboundUnidirectionalStreamAsync(QuicStream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                var streamType = await Http3VarInt.ReadAsync(stream, ct);
                if (streamType is null) return;

                if (streamType.Value == Http3StreamType.Control)
                {
                    await ProcessPeerControlStreamAsync(stream, ct);
                    return;
                }

                // QPACK / push / unknown: drain so connection flow control never stalls.
                var buf = new byte[4096];
                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buf, ct);
                    if (read == 0) return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                    _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin inbound uni-stream drain ended");
            }
        }
    }

    private async Task ProcessPeerControlStreamAsync(QuicStream stream, CancellationToken ct)
    {
        var receivedSettings = false;
        while (!ct.IsCancellationRequested)
        {
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 16 * 1024, ct);
            if (frame is null) return;

            if (!receivedSettings)
            {
                if (frame.Type != Http3FrameType.Settings)
                    return; // peer violation; leave connection for request paths to fail/retry
                _peerSettings = Http3Settings.Parse(frame.Payload.Span);
                receivedSettings = true;
                continue;
            }

            // Drain remaining control frames (GOAWAY, etc.).
            if (frame.Type == Http3FrameType.GoAway)
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { await _cts.CancelAsync(); }
        catch (Exception ex)
        {
            if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin client session cancel failed");
        }

        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex)
            {
                if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                    _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin accept loop did not finish cleanly");
            }
        }

        if (!_backgroundTasks.IsEmpty)
        {
            try { await Task.WhenAll(_backgroundTasks).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex)
            {
                if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                    _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin background drains did not finish cleanly");
            }
        }

        if (_controlStream != null)
        {
            try { await _controlStream.DisposeAsync(); }
            catch (Exception ex)
            {
                if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                    _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin control stream dispose failed");
            }
            _controlStream = null;
        }

        _cts.Dispose();
    }
}
#pragma warning restore CA1416
