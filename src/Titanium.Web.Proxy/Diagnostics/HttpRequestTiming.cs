using System;

namespace Titanium.Web.Proxy.Diagnostics;

/// <summary>
///     Captures the timing of a single HTTP request/response exchange handled by the proxy. Only populated
///     (non-null on <see cref="EventArguments.SessionEventArgsBase.Timing" />) when
///     <see cref="ProxyServer.EnableRequestTimingCapture" /> is enabled; otherwise no instance is ever
///     allocated and this class has zero impact on request handling.
///     <para>
///         All timestamps are UTC wall-clock instants captured with <see cref="DateTime.UtcNow" />, in the
///         order the proxy reaches each stage. The derived <c>Duration</c>/<c>TimeToFirstByte</c> properties
///         are simple differences between two such instants and are <see langword="null" /> until both of
///         their endpoints have been recorded - a session that never reaches a given stage (e.g. one
///         answered synthetically during <c>BeforeRequest</c>, before any upstream connection is ever
///         attempted, or one that fails before a response is received) simply leaves the later timestamps
///         <see langword="null" />; nothing throws.
///     </para>
///     <para>
///         A request that is retried (a new upstream connection after <c>RetryableServerConnectionException</c>,
///         or a re-request after a 401/407 challenge) overwrites the connection/send/receive timestamps with
///         those of the latest attempt - <see cref="AttemptCount" /> tracks how many attempts were made in
///         total. <see cref="TotalDuration" /> and <see cref="ClientRequestReadDuration" /> are unaffected by
///         retries since they only depend on <see cref="SessionCreatedAt" />.
///     </para>
/// </summary>
public sealed class HttpRequestTiming
{
    internal HttpRequestTiming(DateTime sessionCreatedAt)
    {
        SessionCreatedAt = sessionCreatedAt;
    }

    /// <summary>
    ///     When the proxy created this session, immediately after accepting the request line from the
    ///     client (before its headers are read).
    /// </summary>
    public DateTime SessionCreatedAt { get; }

    /// <summary>
    ///     When the client's request headers were fully read, immediately before <c>BeforeRequest</c> is
    ///     invoked. <see langword="null" /> if the client disconnected while sending headers.
    /// </summary>
    public DateTime? RequestHeadersReceivedAt { get; internal set; }

    /// <summary>
    ///     When an upstream connection became ready to use for the most recent attempt - either freshly
    ///     established or retrieved from the connection pool. See <see cref="UpstreamConnectionReused" />.
    /// </summary>
    public DateTime? ConnectionReadyAt { get; internal set; }

    /// <summary>
    ///     When the request (headers and, if any, body) finished being written to the upstream connection.
    /// </summary>
    public DateTime? RequestSentAt { get; internal set; }

    /// <summary>
    ///     When the response status line and headers were fully read from the upstream connection (i.e.
    ///     time-to-first-byte of the final, non-interim response).
    /// </summary>
    public DateTime? ResponseHeadersReceivedAt { get; internal set; }

    /// <summary>
    ///     When this session finished completely - after the response (headers and body, if any) was
    ///     delivered to the client and the <c>AfterResponse</c> event handler, if any, has returned. Also
    ///     marked for sessions that end via an unhandled exception or an early return (e.g. a denied/failed
    ///     request), so it always reflects when the session actually stopped, not just the success path.
    /// </summary>
    public DateTime? CompletedAt { get; internal set; }

    /// <summary>
    ///     <see langword="true" /> once <see cref="CompletedAt" /> has been recorded.
    /// </summary>
    public bool IsComplete { get; internal set; }

    /// <summary>
    ///     Number of upstream-connection attempts made for this session so far. Starts at 0 and is
    ///     incremented every time a connection becomes ready (see <see cref="ConnectionReadyAt" />) -
    ///     normally 1, higher only when a <c>RetryableServerConnectionException</c> forced a fresh
    ///     connection, or a 401/407 challenge triggered a re-request.
    /// </summary>
    public int AttemptCount { get; internal set; }

    /// <summary>
    ///     The <c>Id</c> of the upstream connection used for the most recent attempt, or <see langword="null" />
    ///     if no connection has been acquired yet (e.g. the request was answered synthetically). Use together
    ///     with <see cref="EventArguments.SessionEventArgsBase.UpstreamConnectionTiming" /> to inspect that
    ///     connection's own DNS/TCP/TLS establishment timing.
    /// </summary>
    public long? UpstreamConnectionId { get; internal set; }

    /// <summary>
    ///     <see langword="true" /> if the upstream connection for the most recent attempt was reused from the
    ///     connection pool rather than freshly established.
    /// </summary>
    public bool UpstreamConnectionReused { get; internal set; }

    /// <summary>
    ///     How long it took to read the request headers from the client, starting from when the request
    ///     line first arrived.
    /// </summary>
    public TimeSpan? ClientRequestReadDuration => RequestHeadersReceivedAt - SessionCreatedAt;

    /// <summary>
    ///     How long the proxy spent (running any <c>BeforeRequest</c> handler and) acquiring an upstream
    ///     connection for the most recent attempt, whether that meant establishing a fresh one or retrieving
    ///     one from the pool.
    /// </summary>
    public TimeSpan? ConnectionWaitDuration => ConnectionReadyAt - (RequestHeadersReceivedAt ?? SessionCreatedAt);

    /// <summary>
    ///     How long it took to write the request (headers and body, if any) to the upstream connection.
    /// </summary>
    public TimeSpan? RequestSendDuration => RequestSentAt - ConnectionReadyAt;

    /// <summary>
    ///     Time-to-first-byte: how long the upstream server took to return response headers after the
    ///     request was fully sent.
    /// </summary>
    public TimeSpan? TimeToFirstByte => ResponseHeadersReceivedAt - RequestSentAt;

    /// <summary>
    ///     How long it took to deliver the response to the client after its headers were received from
    ///     upstream - this covers any <c>BeforeResponse</c> handler, writing the response headers/body
    ///     (which, for a streamed body, overlaps with still receiving that same body from the upstream
    ///     server - the two are not tracked separately), and any <c>AfterResponse</c> handler.
    /// </summary>
    public TimeSpan? ResponseDeliveryDuration => CompletedAt - ResponseHeadersReceivedAt;

    /// <summary>
    ///     Total wall-clock duration of this session so far: from session creation through to
    ///     <see cref="CompletedAt" /> once known, or through to now if the session is still in flight.
    /// </summary>
    public TimeSpan TotalDuration => (CompletedAt ?? DateTime.UtcNow) - SessionCreatedAt;

    internal void MarkRequestHeadersReceived()
    {
        RequestHeadersReceivedAt = DateTime.UtcNow;
    }

    internal void MarkConnectionReady(long? upstreamConnectionId, bool reused)
    {
        ConnectionReadyAt = DateTime.UtcNow;
        AttemptCount++;
        UpstreamConnectionId = upstreamConnectionId;
        UpstreamConnectionReused = reused;
    }

    internal void MarkRequestSent()
    {
        RequestSentAt = DateTime.UtcNow;
    }

    internal void MarkResponseHeadersReceived()
    {
        ResponseHeadersReceivedAt = DateTime.UtcNow;
    }

    internal void MarkComplete()
    {
        // A retried/re-requested session may legitimately reach this more than once only through the
        // outermost finalization path (each retry loops back into the request-handling methods, but only
        // the very last attempt's finally block runs OnAfterResponse) - guard anyway so an unexpected
        // extra call (e.g. from an unusual exception path) never rewinds an already-final timestamp.
        if (IsComplete) return;

        CompletedAt = DateTime.UtcNow;
        IsComplete = true;
    }
}
