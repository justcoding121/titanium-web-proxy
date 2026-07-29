using System;

namespace Titanium.Web.Proxy.Options;

/// <summary>
///     Immutable, validated snapshot of every deadline the proxy enforces across a request's
///     lifetime, expressed consistently as <see cref="TimeSpan" /> rather than the mixture of
///     "seconds as <see langword="int" />" properties this replaces
///     (<c>ConnectionTimeOutSeconds</c>, <c>ConnectTimeOutSeconds</c>, ...). A deadline that can be
///     legitimately unbounded is nullable, with <see langword="null" /> meaning "no deadline" as an
///     explicit state rather than <see cref="TimeSpan.Zero" /> or a magic sentinel duration.
///     <para>
///         This type only holds values; it does not decide which deadline fired first when several
///         are composed for one request. That is the responsibility of the per-request deadline
///         registry described in the plan's "Deadline composition" section, introduced alongside
///         the header-parsing deadlines in a later item so it can be exercised by a real caller
///         instead of landing as unused scaffolding.
///     </para>
/// </summary>
public sealed class ProxyTimeoutOptions
{
    private ProxyTimeoutOptions()
    {
    }

    /// <summary>Deadline for a client to finish sending the request line and headers.</summary>
    public TimeSpan ClientHeaderTimeout { get; private init; }

    /// <summary>Deadline for establishing the upstream TCP (and TLS, where applicable) connection.</summary>
    public TimeSpan ConnectTimeout { get; private init; }

    /// <summary>Deadline for the origin to finish sending the response status line and headers.</summary>
    public TimeSpan ResponseHeaderTimeout { get; private init; }

    /// <summary>Deadline for a single read to make forward progress once a connection is established.</summary>
    public TimeSpan IdleReadTimeout { get; private init; }

    /// <summary>Deadline for a single write to make forward progress once a connection is established.</summary>
    public TimeSpan IdleWriteTimeout { get; private init; }

    /// <summary>
    ///     Deadline after which a user callback (<c>BeforeRequest</c>, <c>BeforeResponse</c>, etc.) that
    ///     has not returned is abandoned and reported as orphaned. <see langword="null" /> means
    ///     callbacks are never timed out - not recommended, since .NET cannot forcibly stop an
    ///     uncooperative callback and this is the only backstop against one hanging a connection
    ///     indefinitely.
    /// </summary>
    public TimeSpan? CallbackTimeout { get; private init; }

    /// <summary>Deadline for draining a connection's in-flight work during an orderly shutdown.</summary>
    public TimeSpan DrainTimeout { get; private init; }

    /// <summary>
    ///     Deadline for a single HTTP/2 or HTTP/3 stream to complete once opened.
    ///     <see langword="null" /> disables the per-stream deadline.
    /// </summary>
    public TimeSpan? StreamTimeout { get; private init; }

    /// <summary>
    ///     Deadline for an entire request/response exchange, end to end.
    ///     <see langword="null" /> disables the total-request deadline.
    /// </summary>
    public TimeSpan? TotalRequestTimeout { get; private init; }

    /// <summary>
    ///     Today's shipped values, carried forward per the plan's rollout section: 60-second
    ///     connection timeout, 20-second connect timeout. Deadlines newly introduced by the
    ///     hardening plan (client-header, response-header, callback, drain, stream, total-request)
    ///     are set to conservative values pending benchmark-driven tuning.
    /// </summary>
    public static ProxyTimeoutOptions Default { get; } = Create(
        clientHeaderTimeout: TimeSpan.FromSeconds(30),
        connectTimeout: TimeSpan.FromSeconds(20),
        responseHeaderTimeout: TimeSpan.FromSeconds(60),
        idleReadTimeout: TimeSpan.FromSeconds(60),
        idleWriteTimeout: TimeSpan.FromSeconds(60),
        callbackTimeout: TimeSpan.FromSeconds(30),
        drainTimeout: TimeSpan.FromSeconds(3),
        streamTimeout: TimeSpan.FromSeconds(60),
        totalRequestTimeout: null);

    /// <summary>
    ///     Validates and constructs a <see cref="ProxyTimeoutOptions" /> snapshot. Every supplied
    ///     duration - including a nullable one, when present - must be strictly positive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A supplied duration is zero or negative.</exception>
    public static ProxyTimeoutOptions Create(
        TimeSpan clientHeaderTimeout,
        TimeSpan connectTimeout,
        TimeSpan responseHeaderTimeout,
        TimeSpan idleReadTimeout,
        TimeSpan idleWriteTimeout,
        TimeSpan? callbackTimeout,
        TimeSpan drainTimeout,
        TimeSpan? streamTimeout,
        TimeSpan? totalRequestTimeout)
    {
        RequirePositive(clientHeaderTimeout, nameof(clientHeaderTimeout));
        RequirePositive(connectTimeout, nameof(connectTimeout));
        RequirePositive(responseHeaderTimeout, nameof(responseHeaderTimeout));
        RequirePositive(idleReadTimeout, nameof(idleReadTimeout));
        RequirePositive(idleWriteTimeout, nameof(idleWriteTimeout));
        RequirePositiveIfPresent(callbackTimeout, nameof(callbackTimeout));
        RequirePositive(drainTimeout, nameof(drainTimeout));
        RequirePositiveIfPresent(streamTimeout, nameof(streamTimeout));
        RequirePositiveIfPresent(totalRequestTimeout, nameof(totalRequestTimeout));

        return new ProxyTimeoutOptions
        {
            ClientHeaderTimeout = clientHeaderTimeout,
            ConnectTimeout = connectTimeout,
            ResponseHeaderTimeout = responseHeaderTimeout,
            IdleReadTimeout = idleReadTimeout,
            IdleWriteTimeout = idleWriteTimeout,
            CallbackTimeout = callbackTimeout,
            DrainTimeout = drainTimeout,
            StreamTimeout = streamTimeout,
            TotalRequestTimeout = totalRequestTimeout
        };
    }

    private static void RequirePositive(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Timeouts must be strictly positive. To disable a deadline that supports it, use null rather than TimeSpan.Zero or a negative duration.");
    }

    private static void RequirePositiveIfPresent(TimeSpan? value, string paramName)
    {
        if (value.HasValue) RequirePositive(value.Value, paramName);
    }
}
