#pragma warning disable CA1416
using System;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Minimal HTTP/3 client session for an outbound origin <see cref="QuicConnection"/>.
///     Opens the required client control stream (SETTINGS) and continuously accepts/drains
///     inbound unidirectional streams so peer SETTINGS/QPACK bytes cannot stall connection-level
///     flow control (which previously delayed response HEADERS by hundreds of milliseconds).
/// </summary>
internal sealed class Http3OriginClientSession : IAsyncDisposable
{
    private readonly QuicConnection _connection;
    private readonly ProxyServer _proxyServer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private QuicStream? _controlStream;
    private int _disposed;

    internal Http3OriginClientSession(QuicConnection connection, ProxyServer proxyServer)
    {
        _connection = connection;
        _proxyServer = proxyServer;
    }

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

        _acceptLoop = Task.Run(() => AcceptUnidirectionalStreamsAsync(_cts.Token), CancellationToken.None);
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
                try { await stream.DisposeAsync(); } catch { /* best effort */ }
                continue;
            }

            _ = DrainInboundUnidirectionalStreamAsync(stream, ct);
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
                // Best-effort drain; connection teardown handles the rest.
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
                _ = Http3Settings.Parse(frame.Payload.Span);
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

        try { await _cts.CancelAsync(); } catch { /* ignore */ }

        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* best effort */ }
        }

        if (_controlStream != null)
        {
            try { await _controlStream.DisposeAsync(); } catch { /* best effort */ }
            _controlStream = null;
        }

        _cts.Dispose();
    }
}
#pragma warning restore CA1416
