using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Diagnostics;

/// <summary>
///     The single <see cref="Meter" />/<see cref="ActivitySource" /> pair every part of the proxy
///     reports through, per the plan's rollout section: "Expose OpenTelemetry-compatible <c>Meter</c>
///     and <c>ActivitySource</c> instruments for active/rejected connections and streams, typed
///     limit/timeout outcomes, buffered/decompressed bytes, pool reuse/retry/downgrade, parser
///     errors, authentication rounds and logger drops."
///     <para>
///         Both are process-wide statics, matching how every other OpenTelemetry-instrumented .NET
///         library (<c>System.Net.Http</c>, ASP.NET Core, ...) exposes its meter: a host application
///         wires them into an exporter once, by name, via <c>AddMeter("Titanium.Web.Proxy")</c> /
///         <c>AddSource("Titanium.Web.Proxy")</c>, rather than the proxy taking a dependency on any
///         particular OpenTelemetry SDK package. No instrument here carries a per-request identifier,
///         URL, host or credential as a tag; only bounded-cardinality labels (family/mode/kind/reason
///         names, each a small fixed enum-shaped set) are attached, so wiring an exporter can never
///         turn request traffic into unbounded time-series cardinality.
///     </para>
///     <para>
///         Every recording method here is a static, allocation-light call safe to invoke
///         unconditionally from a hot path: <see cref="Counter{T}.Add(T)" /> and friends already no-op
///         cheaply when no listener is attached, so call sites do not need to guard on whether metrics
///         are "enabled".
///     </para>
/// </summary>
internal static class ProxyMetrics
{
    internal const string MeterName = "Titanium.Web.Proxy";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Exposed for host applications that want distributed-tracing spans, not just counters.</summary>
    public static readonly ActivitySource ActivitySource = new(MeterName, "1.0.0");

    private static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>("twp.connections.active", "{connection}",
            "Client connections currently admitted and being handled.");

    private static readonly Counter<long> RejectedConnections =
        Meter.CreateCounter<long>("twp.connections.rejected", "{connection}",
            "Client connections rejected before a handler task started, by reason.");

    private static readonly UpDownCounter<long> ActiveStreams =
        Meter.CreateUpDownCounter<long>("twp.streams.active", "{stream}",
            "HTTP/2 or HTTP/3 streams currently open, by protocol.");

    private static readonly Counter<long> RejectedStreams =
        Meter.CreateCounter<long>("twp.streams.rejected", "{stream}",
            "HTTP/2 or HTTP/3 streams rejected or reset for an abuse-budget reason.");

    private static readonly Counter<long> PolicyBreaches =
        Meter.CreateCounter<long>("twp.policy.breaches", "{breach}",
            "Resource-bound policy family breaches, tagged by family, the mode that was active, and whether the breach was enforced.");

    private static readonly Counter<long> TimeoutOutcomes =
        Meter.CreateCounter<long>("twp.timeouts", "{timeout}",
            "Deadlines that fired, tagged by ProxyTimeoutKind.");

    private static readonly Histogram<long> BufferedBodyBytes =
        Meter.CreateHistogram<long>("twp.body.buffered_bytes", "By",
            "Cumulative bytes buffered for a single whole-body read, tagged by direction.");

    private static readonly Histogram<long> DecompressedBodyBytes =
        Meter.CreateHistogram<long>("twp.body.decompressed_bytes", "By",
            "Cumulative decompressed bytes produced draining a Content-Encoding chain, tagged by direction.");

    private static readonly Counter<long> PoolOutcomes =
        Meter.CreateCounter<long>("twp.pool.outcomes", "{connection}",
            "Upstream connection pool outcomes: reuse, retry after a dead pooled connection, or protocol downgrade.");

    private static readonly Counter<long> ParserErrors =
        Meter.CreateCounter<long>("twp.parser.errors", "{error}",
            "Rejected malformed input, tagged by parser (framing, chunk, header, websocket, http2, http3, qpack).");

    private static readonly Counter<long> AuthRounds =
        Meter.CreateCounter<long>("twp.auth.rounds", "{round}",
            "Authentication challenge rounds completed, tagged by scheme.");

    private static readonly Counter<long> LoggerDrops =
        Meter.CreateCounter<long>("twp.logger.drops", "{entry}",
            "Log entries dropped because a channel was saturated, tagged by channel.");

    public static void ConnectionAdmitted() => ActiveConnections.Add(1);

    public static void ConnectionReleased() => ActiveConnections.Add(-1);

    public static void ConnectionRejected(string reason) =>
        RejectedConnections.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public static void StreamOpened(string protocol) =>
        ActiveStreams.Add(1, new KeyValuePair<string, object?>("protocol", protocol));

    public static void StreamClosed(string protocol) =>
        ActiveStreams.Add(-1, new KeyValuePair<string, object?>("protocol", protocol));

    public static void StreamRejected(string protocol, string reason) =>
        RejectedStreams.Add(1,
            new KeyValuePair<string, object?>("protocol", protocol),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>
    ///     Records a breach of <paramref name="family" />'s configured limit. <paramref name="mode" />
    ///     is the mode that was active when the breach was detected, so an <see cref="PolicyMode.Observe" />
    ///     breach (recorded but not acted on) and an <see cref="PolicyMode.Enforce" /> breach (recorded
    ///     and acted on) are distinguishable in the exported series without needing a separate
    ///     "would have breached under stricter settings" instrument.
    /// </summary>
    public static void PolicyBreach(PolicyFamily family, PolicyMode mode) =>
        PolicyBreaches.Add(1,
            new KeyValuePair<string, object?>("family", family.ToString()),
            new KeyValuePair<string, object?>("mode", mode.ToString()));

    public static void TimeoutFired(string kind) =>
        TimeoutOutcomes.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public static void BodyBuffered(long bytes, string direction) =>
        BufferedBodyBytes.Record(bytes, new KeyValuePair<string, object?>("direction", direction));

    public static void BodyDecompressed(long bytes, string direction) =>
        DecompressedBodyBytes.Record(bytes, new KeyValuePair<string, object?>("direction", direction));

    public static void PoolReused() =>
        PoolOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", "reuse"));

    public static void PoolRetried() =>
        PoolOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));

    public static void PoolDowngraded() =>
        PoolOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", "downgrade"));

    public static void ParserError(string parser) =>
        ParserErrors.Add(1, new KeyValuePair<string, object?>("parser", parser));

    public static void AuthRoundCompleted(string scheme) =>
        AuthRounds.Add(1, new KeyValuePair<string, object?>("scheme", scheme));

    public static void LoggerEntryDropped(string channel) =>
        LoggerDrops.Add(1, new KeyValuePair<string, object?>("channel", channel));
}
