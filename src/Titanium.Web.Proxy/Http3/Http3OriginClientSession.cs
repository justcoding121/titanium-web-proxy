#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

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
    private readonly Http2PendingWork _backgroundTasks = new();
    private Task? _acceptLoop;
    private QuicStream? _controlStream;
    private Http3Settings? _peerSettings;
    private readonly TaskCompletionSource _peerSettingsReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private static readonly TimeSpan ConnectProtocolSettingsWait = TimeSpan.FromSeconds(2);

    // Connection-scoped reuse of identical bodiless reverse QPACK request blocks (probe tiny-GET).
    // Published as one immutable slot so fingerprint and bytes cannot tear under c=64.
    private CachedEncodedRequestHeaders? _cachedEncodedRequestHeaders;

    internal Http3OriginClientSession(QuicConnection connection, ProxyServer proxyServer)
    {
        _connection = connection;
        _proxyServer = proxyServer;
    }

    /// <summary>Peer SETTINGS when received; otherwise <see langword="null"/>.</summary>
    internal Http3Settings? PeerSettings => _peerSettings;

    /// <summary>
    ///     True when the origin advertised <c>SETTINGS_ENABLE_CONNECT_PROTOCOL=1</c> within
    ///     <see cref="ConnectProtocolSettingsWait"/> after the session started.
    /// </summary>
    internal async Task<bool> OriginAdvertisesConnectProtocolAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _peerSettingsReceived.Task.WaitAsync(ConnectProtocolSettingsWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }

        return _peerSettings?.EnableConnectProtocol == true;
    }

    /// <summary>
    ///     Opens an RFC 9220 extended CONNECT tunnel on a fresh bidirectional stream. The request must
    ///     already have <c>Method = CONNECT</c> and <see cref="Request.ExtendedConnectProtocol" /> set.
    ///     Throws <see cref="InvalidOperationException" /> when the origin did not advertise connect
    ///     protocol support.
    /// </summary>
    internal async Task<Http3OriginTunnelResult> OpenExtendedConnectAsync(
        Request request,
        string authorityHost,
        ProxyResourceLimits resourceLimits,
        Action onTunnelDisposed,
        CancellationToken cancellationToken)
    {
        if (!await OriginAdvertisesConnectProtocolAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The origin did not advertise SETTINGS_ENABLE_CONNECT_PROTOCOL=1; " +
                "extended CONNECT cannot be opened on this HTTP/3 connection.");
        }

        var originStream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
        var maxFrame = resourceLimits.MaxHttp3FramePayloadBytes;

        try
        {
            var encodedHeaders = QpackEncoder.EncodeRequest(request, authorityHost);
            await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, encodedHeaders, cancellationToken);
            await originStream.FlushAsync(cancellationToken);

            var response = await ReadExtendedConnectResponseAsync(originStream, maxFrame, cancellationToken);
            if (response.StatusCode is < 200 or >= 300)
            {
                await originStream.DisposeAsync();
                return new Http3OriginTunnelResult(response, null);
            }

            var tunnelStream = new Http3TunnelStream(originStream, maxFrame, onTunnelDisposed);
            return new Http3OriginTunnelResult(response, tunnelStream);
        }
        catch
        {
            try { await originStream.DisposeAsync(); }
            catch
            {
                // best-effort
            }

            throw;
        }
    }

    private static async Task<Response> ReadExtendedConnectResponseAsync(
        QuicStream originStream,
        long maxFramePayloadBytes,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await Http3Frame.ReadAsync(originStream, maxFramePayloadBytes, cancellationToken);
            if (frame is null)
            {
                return new Response
                {
                    HttpVersion = HttpHeader.Version30,
                    StatusCode = 502,
                    StatusDescription = "Bad Gateway"
                };
            }

            try
            {
                if (frame.Type != Http3FrameType.Headers)
                {
                    if (frame.Type == Http3FrameType.Data)
                    {
                        throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                            "DATA frame received before response HEADERS on extended CONNECT.");
                    }

                    continue;
                }

                var decoded = QpackDecoder.Decode(frame.Payload.Span);
                return BuildResponseFromDecodedHeaders(decoded);
            }
            finally
            {
                frame.ReturnPayload();
            }
        }
    }

    private static Response BuildResponseFromDecodedHeaders(List<(string Name, string Value)> headers)
    {
        var response = new Response { HttpVersion = HttpHeader.Version30 };
        foreach (var (name, value) in headers)
        {
            if (name == ":status" && int.TryParse(value, out var statusCode))
                response.StatusCode = statusCode;
            else if (!name.StartsWith(':'))
                response.Headers.AddHeader(new HttpHeader(name, value));
        }

        if (response.StatusCode is < 100 or > 599)
        {
            response.StatusCode = 502;
            response.StatusDescription = "Bad Gateway";
        }

        return response;
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

        // Run on the thread pool without nesting Task.Run(Func<Task>) — assign the task directly.
        _acceptLoop = AcceptUnidirectionalStreamsAsync(_cts.Token);
    }

    private async Task AcceptUnidirectionalStreamsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var stream = await TryAcceptInboundStreamAsync(ct);
            if (stream is null)
                return;

            if (stream.Type == QuicStreamType.Bidirectional)
            {
                await DisposeUnexpectedBidiStreamQuietlyAsync(stream);
                continue;
            }

            // Track drain tasks (do not discard) so DisposeAsync can join them.
            _backgroundTasks.Track(DrainInboundUnidirectionalStreamAsync(stream, ct));
        }
    }

    private async Task<QuicStream?> TryAcceptInboundStreamAsync(CancellationToken ct)
    {
        try
        {
            return await _connection.AcceptInboundStreamAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (QuicException qex) when (qex.QuicError is QuicError.ConnectionAborted
                                       or QuicError.ConnectionIdle
                                       or QuicError.ConnectionTimeout)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogShutdown(ex, "HTTP/3 origin AcceptInboundStreamAsync ended");
            return null;
        }
    }

    /// <summary>
    ///     Peer abort / idle / timeout / our own abort are the normal end of an origin session (every browsing
    ///     pause produces them), so they log at Trace; anything else stays at Debug.
    /// </summary>
    internal static LogLevel ClassifyShutdownLevel(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is OperationCanceledException) return LogLevel.Trace;
            if (e is QuicException q)
            {
                if (q.QuicError is QuicError.ConnectionAborted or QuicError.ConnectionIdle
                    or QuicError.ConnectionTimeout or QuicError.OperationAborted)
                    return LogLevel.Trace;

                // msquic reports a graceful shutdown as InternalError carrying QUIC_STATUS_SUCCESS.
                if (q.QuicError == QuicError.InternalError
                    && q.Message.Contains("QUIC_STATUS_SUCCESS", StringComparison.Ordinal))
                    return LogLevel.Trace;
            }
        }

        return LogLevel.Debug;
    }

    private void LogShutdown(Exception ex, string message)
    {
        var level = ClassifyShutdownLevel(ex);
        if (_proxyServer.Logger.IsEnabled(level))
            _proxyServer.Logger.Log(level, ex, "{Message}", message);
    }

    private async Task DisposeUnexpectedBidiStreamQuietlyAsync(QuicStream stream)
    {
        try { await stream.DisposeAsync(); }
        catch (Exception ex)
        {
            if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                _proxyServer.Logger.LogDebug(ex, "Failed disposing unexpected inbound bidi stream");
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
                    var peerSettings = await ProcessPeerControlStreamAsync(stream, ct);
                    if (peerSettings != null)
                    {
                        _peerSettings = peerSettings;
                        _peerSettingsReceived.TrySetResult();
                    }
                    return;
                }

                // QPACK / push / unknown: drain so connection flow control never stalls.
                await DrainBytesAsync(stream, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogShutdown(ex, "HTTP/3 origin inbound uni-stream drain ended");
            }
        }
    }

    private static async Task DrainBytesAsync(QuicStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buf, ct);
            if (read == 0) return;
        }
    }

    private static async Task<Http3Settings?> ProcessPeerControlStreamAsync(QuicStream stream, CancellationToken ct)
    {
        var receivedSettings = false;
        Http3Settings? settings = null;
        while (!ct.IsCancellationRequested)
        {
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 16 * 1024, ct);
            if (frame is null) return settings;

            try
            {
                if (!receivedSettings)
                {
                    if (frame.Type != Http3FrameType.Settings)
                        throw new Http3ConnectionException(Http3ErrorCode.MissingSettings,
                            "RFC 9114 §6.2.2: first frame on the control stream must be SETTINGS.");
                    settings = Http3Settings.Parse(frame.Payload.Span);
                    receivedSettings = true;
                    continue;
                }

                // Drain remaining control frames. GOAWAY means stop new streams but keep the
                // control stream open (RFC 9114 §6.2.1: closing it is H3_CLOSED_CRITICAL_STREAM).
                if (frame.Type == Http3FrameType.GoAway)
                    continue;
            }
            finally
            {
                frame.ReturnPayload();
            }
        }

        return settings;
    }


    internal byte[]? TryGetCachedEncodedRequestHeaders(
        int fingerprint,
        string method,
        ReadOnlySpan<byte> authority,
        ReadOnlySpan<byte> path)
    {
        var cached = Volatile.Read(ref _cachedEncodedRequestHeaders);
        if (cached is null || cached.Fingerprint != fingerprint)
            return null;
        if (!string.Equals(cached.Method, method, StringComparison.Ordinal))
            return null;
        if (!cached.Authority.AsSpan().SequenceEqual(authority))
            return null;
        if (!cached.Path.AsSpan().SequenceEqual(path))
            return null;
        return cached.Encoded;
    }

    internal void SetCachedEncodedRequestHeaders(
        int fingerprint,
        string method,
        ReadOnlySpan<byte> authority,
        ReadOnlySpan<byte> path,
        byte[] encoded)
    {
        Volatile.Write(ref _cachedEncodedRequestHeaders, new CachedEncodedRequestHeaders(
            fingerprint,
            method,
            authority.ToArray(),
            path.ToArray(),
            encoded));
    }

    private sealed class CachedEncodedRequestHeaders
    {
        internal CachedEncodedRequestHeaders(
            int fingerprint, string method, byte[] authority, byte[] path, byte[] encoded)
        {
            Fingerprint = fingerprint;
            Method = method;
            Authority = authority;
            Path = path;
            Encoded = encoded;
        }

        internal int Fingerprint { get; }
        internal string Method { get; }
        internal byte[] Authority { get; }
        internal byte[] Path { get; }
        internal byte[] Encoded { get; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await CancelQuietlyAsync();
        await JoinAcceptLoopQuietlyAsync();
        await JoinBackgroundTasksQuietlyAsync();
        await DisposeControlStreamQuietlyAsync();

        _cts.Dispose();
    }

    private async Task CancelQuietlyAsync()
    {
        try { await _cts.CancelAsync(); }
        catch (Exception ex)
        {
            if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin client session cancel failed");
        }
    }

    private async Task JoinAcceptLoopQuietlyAsync()
    {
        if (_acceptLoop is null) return;

        try
        {
            // CancellationToken.None: workers were already cancelled via _cts; this timed join
            // must not abort immediately because _cts.Token is already in the cancelled state.
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin accept loop did not finish cleanly");
        }
    }

    private async Task JoinBackgroundTasksQuietlyAsync()
    {
        if (_backgroundTasks.IsEmpty) return;

        try
        {
            await _backgroundTasks.WhenAllAsync().WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin background drains did not finish cleanly");
        }
    }

    private async Task DisposeControlStreamQuietlyAsync()
    {
        if (_controlStream is null) return;

        try { await _controlStream.DisposeAsync(); }
        catch (Exception ex)
        {
            if (_proxyServer.Logger.IsEnabled(LogLevel.Debug))
                _proxyServer.Logger.LogDebug(ex, "HTTP/3 origin control stream dispose failed");
        }

        _controlStream = null;
    }
}
#pragma warning restore CA1416