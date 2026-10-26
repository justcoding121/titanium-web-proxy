#pragma warning disable CA1416
using System;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Recovery for a QUIC origin that completes the handshake but never answers (rate-limited or lossy UDP,
///     typical on in-flight Wi-Fi). The handshake race cannot help here: the connection is "up", the request is
///     written, and the response simply never arrives until the idle timeout (~16s observed), so one stalled
///     request holds the browser tab.
///     <para>
///         For requests that are safe to send twice (<c>GET</c>/<c>HEAD</c>/<c>OPTIONS</c> without a body), the first
///         response frame gets a deadline. When it passes, the QUIC stream is abandoned, the origin is marked
///         unreachable and the same request is replayed over TCP, as Chrome does for a broken alternative service.
///         Anything else is never replayed (it might already have been processed), but a QUIC idle timeout after
///         the request was sent still marks the origin unreachable so later requests skip QUIC.
///     </para>
/// </summary>
internal static partial class Http3OriginBridge
{
    /// <summary>
    ///     How long an Auto-policy QUIC origin may stay completely silent after a replayable request was written
    ///     before the request is replayed over TCP. A healthy but slow origin can also trip this, in which case the
    ///     only cost is one duplicate safe request and a few minutes on TCP/HTTP/2.
    /// </summary>
    internal static TimeSpan Http3ResponseStallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     True when sending the request a second time (over TCP) after QUIC went silent cannot double-submit
    ///     anything: a safe method with no body at all.
    /// </summary>
    internal static bool IsReplayableOnStall(Request request, bool requestHasNoBody)
    {
        if (!requestHasNoBody || Http3ResponseStallTimeout <= TimeSpan.Zero)
            return false;

        var method = request.Method;
        return string.Equals(method, "GET", StringComparison.Ordinal)
               || string.Equals(method, "HEAD", StringComparison.Ordinal)
               || string.Equals(method, "OPTIONS", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Reads one response frame. With <paramref name="stallGuard" /> the read is bounded by
    ///     <see cref="Http3ResponseStallTimeout" />. A timeout sets <c>Stalled</c>; cancellation by the
    ///     caller still propagates as is.
    /// </summary>
    private static async ValueTask<(Http3Frame? Frame, bool Stalled)> ReadResponseFrameAsync(
        QuicStream stream, long maxPayloadBytes, bool stallGuard, CancellationToken cancellationToken)
    {
        if (!stallGuard)
            return (await Http3Frame.ReadAsync(stream, maxPayloadBytes, cancellationToken), false);

        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(Http3ResponseStallTimeout);
        try
        {
            return (await Http3Frame.ReadAsync(stream, maxPayloadBytes, guard.Token), false);
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            return (null, true);
        }
    }

    /// <summary>
    ///     Gives up on a silent QUIC stream: aborts it, retires the connection (other requests must not reuse a
    ///     path that stopped delivering), marks the origin unreachable and replays the request over TCP.
    /// </summary>
    private static async Task ReplayStalledRequestOverTcpAsync( // NOSONAR S107 -- Origin identity and transport state stay explicit.
        SessionEventArgs sessionArgs, ProxyServer server, ILogger logger,
        QuicStream? originStream, QuicServerConnection? quicConn, string sniHost, int port,
        CancellationToken cancellationToken, Func<Response, CancellationToken, Task>? onInterimResponse)
    {
        if (originStream != null)
        {
            try { originStream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.RequestCancelled); }
            catch { /* already closed */ }

            try { await originStream.DisposeAsync(); }
            catch { /* best effort */ }
        }

        var originPort = sessionArgs.HttpClient.Request.GetOriginHostPort(port).Port;
        var hostAndPort = $"{sniHost}:{originPort}";
        server.NoteHttp3OriginUnreachable(hostAndPort);

        if (quicConn != null)
            await server.QuicConnectionPool.InvalidateAsync(quicConn);

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug(
                "HTTP/3 origin {HostAndPort} sent nothing for {StallSeconds}s; replaying {Method} over TCP and "
                + "staying on TCP for {BrokenSeconds}s.",
                hostAndPort, (int)Http3ResponseStallTimeout.TotalSeconds, sessionArgs.HttpClient.Request.Method,
                (int)Http3OriginCapabilityCache.BrokenTtl.TotalSeconds);

        try
        {
            await FallbackToTcpOriginAsync(sessionArgs, server, logger, cancellationToken, onInterimResponse);
        }
        catch (Exception tcpEx) when (tcpEx is not OperationCanceledException)
        {
            sessionArgs.HttpClient.Response = MakeBadGatewayResponse(
                $"QUIC stalled; TCP fallback failed: {tcpEx.Message}");
        }
    }
}
#pragma warning restore CA1416
