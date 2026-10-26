#pragma warning disable CA1416
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Connection-level race between a cold QUIC handshake and the origin's TCP/HTTP/2 connection
///     (RFC 8305 style "connection attempt delay").
///     <para>
///         The browser behind the proxy only ever sees a TCP CONNECT, so it cannot race HTTP/3 against
///         HTTP/2 the way Chrome does when it browses directly. On a network that silently drops UDP/443
///         the proxy would otherwise wait out the whole QUIC handshake before falling back to TCP. Here
///         QUIC gets a short head start, then the TCP leg starts and whichever connection is ready first
///         carries the request.
///     </para>
///     <para>
///         Only <em>connection establishment</em> is raced. The request is written to exactly one
///         connection, so nothing is ever sent twice. A QUIC handshake that loses keeps running in the
///         background: success leaves a warm connection in the pool for later requests, failure marks the
///         origin unreachable.
///     </para>
/// </summary>
internal static partial class Http3OriginBridge
{
    /// <summary>
    ///     Head start QUIC gets before the TCP leg is started. Short enough that a blackholed UDP path
    ///     costs little, long enough that a healthy QUIC origin normally wins without opening a TCP
    ///     connection at all.
    /// </summary>
    internal static TimeSpan TcpRaceDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     A race only makes sense when (a) HTTP/3 was chosen automatically rather than forced, (b) the TCP
    ///     leg would be HTTP/2, whose pool lets us pre-establish a connection without sending anything,
    ///     and (c) there is no live QUIC connection yet, so this request would pay a handshake.
    /// </summary>
    private static bool ShouldRaceTcp(
        SessionEventArgs sessionArgs, ProxyServer server, bool isForcedH3, string poolKey)
    {
        if (isForcedH3 || TcpRaceDelay <= TimeSpan.Zero) return false;
        if (!PrefersHttp2Origin(sessionArgs)) return false;
        return !server.QuicConnectionPool.HasLiveConnection(poolKey);
    }

    /// <summary>
    ///     Acquires a QUIC connection, racing a TCP/HTTP/2 connection against it when the handshake is slow.
    ///     Returns the QUIC connection (with a stream registered, as <c>GetOrCreateAsync</c> does), or
    ///     <c>TcpWon = true</c> when the origin's TCP connection became ready first. A QUIC failure that
    ///     happens before TCP is ready is rethrown so the caller's existing fallback handling applies.
    /// </summary>
    private static async Task<(QuicServerConnection? Quic, bool TcpWon)> AcquireQuicRacingTcpAsync( // NOSONAR S107 -- Origin identity and proxy flags stay explicit.
        SessionEventArgs sessionArgs, ProxyServer server, ILogger logger,
        string connectHost, string sniHost, int port, IPEndPoint? upStreamEndPoint,
        IExternalProxy? upstreamProxy, string poolKey, CancellationToken cancellationToken)
    {
        var hostAndPort = $"{sniHost}:{sessionArgs.HttpClient.Request.GetOriginHostPort(port).Port}";

        // Deliberately not tied to the request's token: when TCP wins this handshake keeps going so a
        // healthy origin ends up warm in the pool (and a dead one is learned).
        var quicTask = server.QuicConnectionPool.GetOrCreateAsync(
            connectHost, port, upStreamEndPoint, upstreamProxy,
            (sender, certificate, chain, errors) =>
                server.ValidateServerCertificate(sender, sessionArgs, certificate, chain, errors),
            CancellationToken.None,
            sniHost: sniHost,
            failFastHandshake: true,
            capabilityKey: hostAndPort,
            precomputedCacheKey: poolKey).AsTask();

        using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var headStart = Task.Delay(TcpRaceDelay, cancellationToken);
            if (await Task.WhenAny(quicTask, headStart) != quicTask)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(
                        "QUIC handshake to {HostAndPort} not ready after {DelayMs}ms; racing TCP/HTTP/2.",
                        hostAndPort, (int)TcpRaceDelay.TotalMilliseconds);

                var tcpTask = PrewarmHttp2OriginAsync(sessionArgs, server, logger, tcpCts.Token);
                var winner = await Task.WhenAny(quicTask, tcpTask);
                if (winner == tcpTask && tcpTask.Result)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("TCP/HTTP/2 won the race for {HostAndPort}; QUIC continues in background.",
                            hostAndPort);
                    ObserveAbandonedQuicHandshake(quicTask, server, hostAndPort);
                    return (null, true);
                }
                // QUIC finished first, or the TCP leg failed: keep waiting on QUIC alone.
            }

            // A QUIC failure propagates from here with the TCP leg left running: the caller's TCP
            // fallback then finds the connection that leg is already establishing.
            var quic = await quicTask.WaitAsync(cancellationToken);

            // QUIC won: the speculative TCP connection is not needed for this request.
            await tcpCts.CancelAsync();
            return (quic, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The request went away mid-race; do not leak the handshake or its stream.
            ObserveAbandonedQuicHandshake(quicTask, server, hostAndPort);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // The pool declined to dial because the origin was marked unreachable while this call queued
            // (not a cancelled request). Surface it as a plain failure so the caller's TCP fallback runs.
            throw new InvalidOperationException("QUIC unavailable: origin marked unreachable.", ex);
        }
    }

    /// <summary>
    ///     Settles a QUIC handshake nobody is waiting for any more. Success hands the stream straight back
    ///     so the connection stays pooled and warm; a genuine failure marks the origin unreachable.
    /// </summary>
    internal static void ObserveAbandonedQuicHandshake(
        Task<QuicServerConnection> quicTask, ProxyServer server, string hostAndPort)
    {
        _ = quicTask.ContinueWith(static (t, state) =>
        {
            var (srv, key) = ((ProxyServer, string))state!;
            if (t.IsCompletedSuccessfully)
            {
                _ = ReleaseQuietlyAsync(t.Result);
                return;
            }

            if (t.IsCanceled)
                return;

            // Draining / disposed is shutdown, not a verdict on the origin (same rule as warm-up).
            if (t.Exception?.GetBaseException() is OperationCanceledException or ObjectDisposedException
                or InvalidOperationException)
                return;

            srv.NoteHttp3OriginUnreachable(key);
        }, (server, hostAndPort), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task ReleaseQuietlyAsync(QuicServerConnection connection)
    {
        try
        {
            await QuicConnectionPool.ReleaseAsync(connection);
        }
        catch (Exception)
        {
            // The handshake already finished; releasing the stream is best effort.
        }
    }

    /// <summary>
    ///     Establishes (or finds) the pooled origin HTTP/2 connection without sending anything, so the
    ///     request can use it immediately if it wins the race. Never throws; <c>false</c> means the TCP leg
    ///     could not be made ready.
    /// </summary>
    private static async Task<bool> PrewarmHttp2OriginAsync(
        SessionEventArgs sessionArgs, ProxyServer server, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var request = sessionArgs.HttpClient.Request;
            var (connectHost, connectPort) = ResolveTransparentForwardTarget(sessionArgs);
            var (host, port) = ResolveH2OriginAuthority(request);
            var poolKey = Http2OriginConnectionPool.BuildPoolKey(server, sessionArgs, host, port, connectHost,
                connectPort);
            _ = await LeaseHttp2OriginAsync(server, logger, sessionArgs,
                new Http2OriginTarget(host, port, connectHost, connectPort, poolKey), cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug) && ex is not OperationCanceledException)
                logger.LogDebug(ex, "TCP/HTTP/2 race leg failed; waiting for QUIC alone.");
            return false;
        }
    }
}
#pragma warning restore CA1416
