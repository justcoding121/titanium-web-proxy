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
        var safe = string.Equals(method, "GET", StringComparison.Ordinal)
                   || string.Equals(method, "HEAD", StringComparison.Ordinal)
                   || string.Equals(method, "OPTIONS", StringComparison.Ordinal);
        return safe && !IsLongLivedStream(request);
    }

    /// <summary>
    ///     An event stream or long poll legitimately says nothing for longer than the stall timeout, and
    ///     replaying it would open a second subscription and wrongly mark a healthy origin unreachable.
    /// </summary>
    private static bool IsLongLivedStream(Request request)
    {
        var accept = request.Headers.GetFirstHeader("Accept")?.Value;
        return accept is not null && accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reads one response frame. With <paramref name="stallGuard" /> the first
    ///     <see cref="Http3ResponseStallTimeout" /> of silence is treated as a dead path, unless
    ///     <paramref name="connection" /> delivered a response on another stream in that time: then the path is
    ///     alive, only this origin is slow, and the read keeps waiting. A stall sets <c>Stalled</c>;
    ///     cancellation by the caller still propagates as is.
    /// </summary>
    private static async ValueTask<(Http3Frame? Frame, bool Stalled)> ReadResponseFrameAsync(
        QuicStream stream, long maxPayloadBytes, bool stallGuard, QuicServerConnection? connection,
        CancellationToken cancellationToken)
    {
        if (!stallGuard)
            return (await Http3Frame.ReadAsync(stream, maxPayloadBytes, cancellationToken), false);

        // The read itself is never cancelled by the timer: cancelling a QuicStream read aborts the stream, and
        // a slow-but-alive origin must still be able to answer on it.
        var read = Http3Frame.ReadAsync(stream, maxPayloadBytes, cancellationToken).AsTask();
        var timeout = Http3ResponseStallTimeout;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var winner = await Task.WhenAny(read, Task.Delay(timeout, timer.Token));
        if (winner == read)
        {
            await timer.CancelAsync();
            return (await read, false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (connection is not null && connection.HasDeliveredRecently((long)timeout.TotalMilliseconds))
            return (await read, false);

        ObserveAbandonedRead(read);
        return (null, true);
    }

    /// <summary>Settles a read nobody waits for any more: hands a late frame's buffer back and observes the abort.</summary>
    private static void ObserveAbandonedRead(Task<Http3Frame?> read)
    {
        _ = read.ContinueWith(static t =>
        {
            if (t.IsCompletedSuccessfully)
                t.Result?.ReturnPayload();
            else
                _ = t.Exception; // the stream was aborted under it; nothing to report
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Marks an origin unreachable and logs the window it got. Synchronous so the async callers keep no extra local.</summary>
    private static void MarkUnreachableAndLog(ProxyServer server, ILogger logger, string hostAndPort)
    {
        var brokenFor = server.NoteHttp3OriginUnreachable(hostAndPort);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug(
                "Marked {HostAndPort} HTTP/3 unreachable for {BrokenSeconds}s; staying on TCP.",
                hostAndPort, (int)brokenFor.TotalSeconds);
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
        var brokenFor = server.NoteHttp3OriginUnreachable(hostAndPort);

        if (quicConn != null)
            await server.QuicConnectionPool.InvalidateAsync(quicConn);

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug(
                "HTTP/3 origin {HostAndPort} sent nothing for {StallSeconds}s; replaying {Method} over TCP and "
                + "staying on TCP for {BrokenSeconds}s.",
                hostAndPort, (int)Http3ResponseStallTimeout.TotalSeconds, sessionArgs.HttpClient.Request.Method,
                (int)brokenFor.TotalSeconds);

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
