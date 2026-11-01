using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;
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
    private const string OutcomeTag = "outcome";
    internal const string MeterName = "Titanium.Web.Proxy";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Exposed for host applications that want distributed-tracing spans, not just counters.</summary>
    public static readonly ActivitySource ActivitySource = new(MeterName, "1.0.0");

    private const string ConnectionUnit = "{connection}";
    private const string DirectionTag = "direction";

    private static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>("twp.connections.active", ConnectionUnit,
            "Client connections currently admitted and being handled.");

    private static readonly Counter<long> RejectedConnections =
        Meter.CreateCounter<long>("twp.connections.rejected", ConnectionUnit,
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
        Meter.CreateCounter<long>("twp.pool.outcomes", ConnectionUnit,
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

    private static readonly Counter<long> SvcbQueued =
        Meter.CreateCounter<long>("twp.svcb.queued", "{lookup}",
            "HTTPS/SVCB background discovery lookups queued after an Auto-mode cache miss.");

    private static readonly Histogram<double> SvcbDurationMs =
        Meter.CreateHistogram<double>("twp.svcb.duration_ms", "ms",
            "HTTPS/SVCB discovery duration in milliseconds, tagged by result.");

    private static readonly Counter<long> Http2CapabilityLookups =
        Meter.CreateCounter<long>("twp.http2.capability_lookups", "{lookup}",
            "HTTP/2 origin capability cache lookups, tagged by hit or miss.");

    private static readonly Histogram<double> Http2ProbeDurationMs =
        Meter.CreateHistogram<double>("twp.http2.probe_duration_ms", "ms",
            "Live HTTP/2 origin capability probe duration in milliseconds.");

    private static readonly Counter<long> Http2CapabilityMismatches =
        Meter.CreateCounter<long>("twp.http2.capability_mismatches", "{mismatch}",
            "Stale HTTP/2 capability cache positives detected after client ALPN was already committed.");

    /// <summary>
    ///     Tracks every live <see cref="CertificateManager" /> by weak reference, so the observable
    ///     gauge below can sum current cache occupancy across however many are alive in this process
    ///     without keeping any of them alive itself (a strong-referencing static registry would be a
    ///     leak in its own right, and there is no single natural place to unregister from given
    ///     <see cref="CertificateManager" /> is disposed on <see cref="ProxyServer" /> teardown, not
    ///     necessarily before process exit).
    /// </summary>
    private static readonly ConditionalWeakTable<CertificateManager, object?> LiveCertificateManagers = new();

    private static readonly ObservableGauge<long> CachedCertificates = // NOSONAR S1144 -- Strong reference keeps the observable instrument registered.
        Meter.CreateObservableGauge("twp.certificates.cached", ObserveCachedCertificateCount, "{certificate}",
            "Leaf certificates currently held in the in-memory certificate cache, summed across every " +
            "live CertificateManager in this process. Operators can use this to confirm " +
            "ResourceLimits.MaxCertificateCacheEntries is holding the cache bounded rather than " +
            "inferring it indirectly from process working set.");

    private static IEnumerable<Measurement<long>> ObserveCachedCertificateCount()
    {
        long total = 0;
        foreach (var pair in LiveCertificateManagers)
            total += pair.Key.CachedCertificateCount;

        yield return new Measurement<long>(total);
    }

    /// <summary>Called once by each <see cref="CertificateManager" /> constructor.</summary>
    internal static void RegisterCertificateManager(CertificateManager manager) =>
        LiveCertificateManagers.AddOrUpdate(manager, null);

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
        BufferedBodyBytes.Record(bytes, new KeyValuePair<string, object?>(DirectionTag, direction));

    public static void BodyDecompressed(long bytes, string direction) =>
        DecompressedBodyBytes.Record(bytes, new KeyValuePair<string, object?>(DirectionTag, direction));

    public static void PoolReused() =>
        PoolOutcomes.Add(1, new KeyValuePair<string, object?>(OutcomeTag, "reuse"));

    public static void PoolRetried() =>
        PoolOutcomes.Add(1, new KeyValuePair<string, object?>(OutcomeTag, "retry"));

    public static void PoolDowngraded() =>
        PoolOutcomes.Add(1, new KeyValuePair<string, object?>(OutcomeTag, "downgrade"));

    public static void ParserError(string parser) =>
        ParserErrors.Add(1, new KeyValuePair<string, object?>("parser", parser));

    public static void AuthRoundCompleted(string scheme) =>
        AuthRounds.Add(1, new KeyValuePair<string, object?>("scheme", scheme));

    public static void LoggerEntryDropped(string channel) =>
        LoggerDrops.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public static void SvcbDiscoveryQueued() => SvcbQueued.Add(1);

    public static void SvcbDiscoveryCompleted(double durationMs, string result) =>
        SvcbDurationMs.Record(durationMs, new KeyValuePair<string, object?>("result", result));

    public static void Http2CapabilityLookup(bool cacheHit) =>
        Http2CapabilityLookups.Add(1,
            new KeyValuePair<string, object?>(OutcomeTag, cacheHit ? "hit" : "miss"));

    public static void Http2ProbeCompleted(double durationMs) =>
        Http2ProbeDurationMs.Record(durationMs);

    public static void Http2CapabilityMismatch() => Http2CapabilityMismatches.Add(1);

    private static readonly Histogram<long> Http2FrameWriterPending =
        Meter.CreateHistogram<long>("twp.http2.frame_writer.pending_frames", "{frame}",
            "Frames queued on an HTTP/2 frame writer when a sample is recorded (enqueue high-water crossings).");

    private static readonly Counter<long> Http2FrameWriterHighWater =
        Meter.CreateCounter<long>("twp.http2.frame_writer.high_water_crossings", "{crossing}",
            "Times an HTTP/2 frame-writer pending depth crossed a watermark threshold.");

    /// <summary>
    ///     Records a frame-writer queue-depth sample when a high-water threshold is crossed.
    ///     Call sites sample sparsely (threshold crossings only) so the hot enqueue path stays cheap.
    /// </summary>
    public static void Http2FrameWriterDepthSample(long pendingFrames, string threshold) =>
        Http2FrameWriterPending.Record(pendingFrames,
            new KeyValuePair<string, object?>("threshold", threshold));

    public static void Http2FrameWriterHighWaterCrossed(string threshold) =>
        Http2FrameWriterHighWater.Add(1,
            new KeyValuePair<string, object?>("threshold", threshold));

    private const string RequestUnit = "{request}";

    private static readonly Counter<long> Requests =
        Meter.CreateCounter<long>("twp.proxy.requests", RequestUnit, "Completed proxy requests.");

    private static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("twp.proxy.request.duration", "s", "Proxy request duration.");

    private static readonly UpDownCounter<long> ActiveRequests =
        Meter.CreateUpDownCounter<long>("twp.proxy.requests.active", RequestUnit, "Requests in flight.");

    private static readonly Counter<long> FailedRequests =
        Meter.CreateCounter<long>("twp.proxy.requests.failed", RequestUnit, "Failed proxy requests.");

    private static readonly Counter<long> ProxyBytes =
        Meter.CreateCounter<long>("twp.proxy.bytes", "By", "Bytes observed on a proxied request.");

    private static readonly Counter<long> UpstreamRetries =
        Meter.CreateCounter<long>("twp.proxy.upstream.retries", "{retry}", "Upstream retry attempts.");

    private static readonly Counter<long> StatusRetries =
        Meter.CreateCounter<long>("twp.proxy.status.retries", "{retry}", "Gateway retries, by reason: status or timeout.");

    private static readonly Counter<long> StatusRetriesRefused =
        Meter.CreateCounter<long>("twp.proxy.status.retries_refused", "{retry}", "Gateway retries not attempted, by reason: budget.");

    private static readonly Counter<long> DestinationEjections =
        Meter.CreateCounter<long>("twp.proxy.destination.ejections", "{ejection}", "Destinations marked unhealthy.");

    private static readonly Counter<long> MirrorDrops =
        Meter.CreateCounter<long>("twp.proxy.mirror.drops", "{drop}", "Mirrored requests dropped because the queue was full.");

    private static readonly Counter<long> MirrorQueued =
        Meter.CreateCounter<long>("twp.proxy.mirror.queued", RequestUnit, "Mirrored requests accepted onto the queue.");

    private static readonly Counter<long> MirrorSent =
        Meter.CreateCounter<long>("twp.proxy.mirror.sent", RequestUnit, "Mirrored requests written to the mirror destination.");

    private static readonly Counter<long> MirrorFailed =
        Meter.CreateCounter<long>("twp.proxy.mirror.failed", RequestUnit, "Mirrored requests that failed before a response was discarded.");

    private static readonly Counter<long> MirrorSkipped =
        Meter.CreateCounter<long>("twp.proxy.mirror.skipped", RequestUnit, "Mirrored requests skipped because the body was missing, streaming, or over the cap.");

    private static readonly Counter<long> ExtAuthRequests =
        Meter.CreateCounter<long>("twp.extauth.requests", RequestUnit,
            "External authorization decisions by result: allow, deny, error_open, error_closed, cache_hit, coalesced, and saturated_* or cooldown_* (open or closed) when the service was not called.");

    public static void RecordExtAuth(string result)
    {
        if (ExtAuthRequests.Enabled)
        {
            ExtAuthRequests.Add(1, new KeyValuePair<string, object?>("result", result));
        }
    }

    private static readonly Histogram<double> PluginDuration =
        Meter.CreateHistogram<double>("twp.plugin.duration", "s",
            "Time one per-route plugin call took, tagged by the plugin name from the configuration.");

    private static readonly Counter<long> PluginErrors =
        Meter.CreateCounter<long>("twp.plugin.errors", "{error}",
            "Per-route plugin calls that failed, tagged by plugin name and kind: exception, timeout, or unloaded.");

    /// <param name="plugin">The configured plugin name (a bounded set; never a request value).</param>
    /// <param name="seconds">Wall time of the call.</param>
    public static void RecordPluginCall(string plugin, double seconds)
    {
        if (PluginDuration.Enabled)
        {
            PluginDuration.Record(seconds, new KeyValuePair<string, object?>("plugin", plugin));
        }
    }

    /// <param name="plugin">The configured plugin name.</param>
    /// <param name="kind"><c>exception</c>, <c>timeout</c> or <c>unloaded</c>.</param>
    public static void RecordPluginError(string plugin, string kind)
    {
        if (PluginErrors.Enabled)
        {
            PluginErrors.Add(1,
                new KeyValuePair<string, object?>("plugin", plugin),
                new KeyValuePair<string, object?>("kind", kind));
        }
    }

    /// <summary>
    ///     Running proxy servers, held weakly like <see cref="LiveCertificateManagers" />. <c>twp.lite.eligible</c> reads each one live,
    ///     so a reload that adds or removes a lite blocker shows on the next collection.
    /// </summary>
    private static readonly ConditionalWeakTable<ProxyServer, object?> LiveProxyServers = new();

    private static readonly ObservableGauge<int> LiteEligible = // NOSONAR S1144 -- Strong reference keeps the observable instrument registered.
        Meter.CreateObservableGauge("twp.lite.eligible", ObserveLiteEligible, "{state}",
            "1 when H1 terminate-lite can serve requests on a running proxy that has a fixed ForwardHost listener, 0 when a " +
            "configured feature (route plugins, extAuth/oidc, grpcWeb, connectionLimit, compression, session-only middleware) " +
            "forces the slower full session path. One measurement per such server; reports nothing otherwise.");

    private static IEnumerable<Measurement<int>> ObserveLiteEligible()
    {
        foreach (var pair in LiveProxyServers)
        {
            if (pair.Key.LiteEligibleValue() is { } value)
            {
                yield return new Measurement<int>(value);
            }
        }
    }

    /// <summary>Called once per <c>ProxyServer.Start()</c>.</summary>
    internal static void RegisterProxyServer(ProxyServer server) =>
        LiveProxyServers.AddOrUpdate(server, null);

    private static readonly Counter<long> CacheRequests =
        Meter.CreateCounter<long>("twp.cache.requests", RequestUnit,
            "Response cache outcomes by result: hit, miss, stale, bypass, revalidated.");

    /// <summary>
    ///     Live response caches, held weakly like <see cref="LiveCertificateManagers" />, so the gauges below sum
    ///     whatever is alive in this process without keeping a cache (and its bodies) reachable.
    /// </summary>
    private static readonly ConditionalWeakTable<Caching.MemoryHttpResponseCache, object?> LiveResponseCaches = new();

    private static readonly ObservableGauge<long> CacheBytes = // NOSONAR S1144 -- Strong reference keeps the observable instrument registered.
        Meter.CreateObservableGauge("twp.cache.bytes", ObserveCacheBytes, "By",
            "Bytes charged to the in-memory response cache (bodies, headers and keys), summed across live caches.");

    private static readonly ObservableGauge<long> CacheEntries = // NOSONAR S1144 -- Strong reference keeps the observable instrument registered.
        Meter.CreateObservableGauge("twp.cache.entries", ObserveCacheEntries, "{entry}",
            "Entries held by the in-memory response cache, summed across live caches.");

    private static IEnumerable<Measurement<long>> ObserveCacheBytes()
    {
        long total = 0;
        foreach (var pair in LiveResponseCaches)
        {
            total += pair.Key.Bytes;
        }

        yield return new Measurement<long>(total);
    }

    private static IEnumerable<Measurement<long>> ObserveCacheEntries()
    {
        long total = 0;
        foreach (var pair in LiveResponseCaches)
        {
            total += pair.Key.Count;
        }

        yield return new Measurement<long>(total);
    }

    /// <summary>Called once by each <see cref="Caching.MemoryHttpResponseCache" /> constructor.</summary>
    internal static void RegisterResponseCache(Caching.MemoryHttpResponseCache cache) =>
        LiveResponseCaches.AddOrUpdate(cache, null);

    /// <param name="result"><c>hit</c>, <c>miss</c>, <c>stale</c>, <c>bypass</c> or <c>revalidated</c>.</param>
    public static void RecordCacheRequest(string result)
    {
        if (CacheRequests.Enabled)
        {
            CacheRequests.Add(1, new KeyValuePair<string, object?>("result", result));
        }
    }

    private static readonly Counter<long> PassthroughConnections =
        Meter.CreateCounter<long>("twp.passthrough.connections", ConnectionUnit,
            "Finished TCP passthrough connections by route (a configured server name pattern, default, or forward) and result.");

    private static readonly Counter<long> PassthroughBytes =
        Meter.CreateCounter<long>("twp.passthrough.bytes", "By",
            "Bytes relayed by TCP passthrough, by direction: in (client to upstream) or out (upstream to client).");

    private static readonly Counter<long> ZoneSelections =
        Meter.CreateCounter<long>("twp.loadbalancer.zone.selections", "{pick}",
            "Load-balancer zone-aware picks: local (stayed in zone) or spill (used all zones).");

    private static int _overloadLevelValue;

    private static readonly Counter<long> OverloadActions =
        Meter.CreateCounter<long>("twp.overload.actions", "{action}",
            "Overload shedding actions taken: disable_keep_alive, reject, or stop_accepting.");

    // Observable: sampled by the exporter at scrape time, so the hot path never touches it.
    private static readonly ObservableGauge<int> OverloadLevelGauge =
        Meter.CreateObservableGauge("twp.overload.level",
            () => System.Threading.Volatile.Read(ref _overloadLevelValue), "{level}",
            "Overload level: 0 normal, 1 elevated, 2 critical.");

    public static void SetOverloadLevel(int level) =>
        System.Threading.Volatile.Write(ref _overloadLevelValue, level);

    public static void RecordOverloadAction(string action)
    {
        if (OverloadActions.Enabled)
        {
            OverloadActions.Add(1, new KeyValuePair<string, object?>("action", action));
        }
    }

    public static void RecordZoneSelection(string scope)
    {
        if (ZoneSelections.Enabled)
        {
            ZoneSelections.Add(1, new KeyValuePair<string, object?>("scope", scope));
        }
    }

    public static void RecordPassthrough(string route, string result, long bytesIn, long bytesOut)
    {
        if (PassthroughConnections.Enabled)
        {
            PassthroughConnections.Add(1,
                new KeyValuePair<string, object?>("route", route),
                new KeyValuePair<string, object?>("result", result));
        }

        if (PassthroughBytes.Enabled)
        {
            if (bytesIn > 0)
            {
                PassthroughBytes.Add(bytesIn, new KeyValuePair<string, object?>(DirectionTag, "in"));
            }

            if (bytesOut > 0)
            {
                PassthroughBytes.Add(bytesOut, new KeyValuePair<string, object?>(DirectionTag, "out"));
            }
        }
    }

    private static readonly UpDownCounter<long> UdpSessionsActive =
        Meter.CreateUpDownCounter<long>("twp.udp.sessions.active", "{session}",
            "UDP relay sessions currently open (one per client endpoint), summed across UDP listeners.");

    private static readonly Counter<long> UdpDatagrams =
        Meter.CreateCounter<long>("twp.udp.datagrams", "{datagram}",
            "Datagrams relayed by UDP listeners, by direction: in (client to upstream) or out (upstream to client).");

    private static readonly Counter<long> UdpBytes =
        Meter.CreateCounter<long>("twp.udp.bytes", "By",
            "Payload bytes relayed by UDP listeners, by direction: in (client to upstream) or out (upstream to client).");

    private static readonly Counter<long> UdpDropped =
        Meter.CreateCounter<long>("twp.udp.dropped", "{datagram}",
            "Datagrams a UDP listener did not relay, by reason: oversized, empty, malformed, rate_limited, session_cap, client_cap, draining, no_destination, resolve_failed, or upstream_error.");

    public static void UdpSessionOpened()
    {
        if (UdpSessionsActive.Enabled)
        {
            UdpSessionsActive.Add(1);
        }
    }

    public static void UdpSessionClosed()
    {
        if (UdpSessionsActive.Enabled)
        {
            UdpSessionsActive.Add(-1);
        }
    }

    /// <param name="direction"><c>in</c> (client to upstream) or <c>out</c> (upstream to client).</param>
    public static void UdpDatagramRelayed(string direction, int bytes)
    {
        if (UdpDatagrams.Enabled)
        {
            UdpDatagrams.Add(1, new KeyValuePair<string, object?>(DirectionTag, direction));
        }

        if (UdpBytes.Enabled)
        {
            UdpBytes.Add(bytes, new KeyValuePair<string, object?>(DirectionTag, direction));
        }
    }

    public static void UdpDatagramDropped(string reason)
    {
        if (UdpDropped.Enabled)
        {
            UdpDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    /// <summary>Live listener check. Do not cache the result.</summary>
    public static bool RequestInstrumentsEnabled =>
        Requests.Enabled || RequestDuration.Enabled || ActiveRequests.Enabled;

    /// <summary>True when any per-request instrument has a listener. Live check, not cached.</summary>
    public static bool RequestMetricsEnabled =>
        RequestInstrumentsEnabled || FailedRequests.Enabled || ProxyBytes.Enabled;

    public static void AddActiveRequest(int delta)
    {
        if (ActiveRequests.Enabled)
        {
            ActiveRequests.Add(delta);
        }
    }

    public static void RecordRetry()
    {
        if (UpstreamRetries.Enabled)
        {
            UpstreamRetries.Add(1);
        }
    }

    /// <param name="reason"><c>status</c> for a retryable origin status, <c>timeout</c> for a per-try deadline.</param>
    public static void RecordStatusRetry(string reason)
    {
        if (StatusRetries.Enabled)
        {
            StatusRetries.Add(1, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    /// <param name="reason"><c>budget</c> when the cluster's retry budget had no slot left.</param>
    public static void RecordStatusRetryRefused(string reason)
    {
        if (StatusRetriesRefused.Enabled)
        {
            StatusRetriesRefused.Add(1, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    public static void RecordEjection()
    {
        if (DestinationEjections.Enabled)
        {
            DestinationEjections.Add(1);
        }
    }

    public static void RecordMirrorDrop()
    {
        if (MirrorDrops.Enabled)
        {
            MirrorDrops.Add(1);
        }
    }

    public static void RecordMirrorQueued()
    {
        if (MirrorQueued.Enabled)
        {
            MirrorQueued.Add(1);
        }
    }

    public static void RecordMirrorSent()
    {
        if (MirrorSent.Enabled)
        {
            MirrorSent.Add(1);
        }
    }

    public static void RecordMirrorFailed()
    {
        if (MirrorFailed.Enabled)
        {
            MirrorFailed.Add(1);
        }
    }

    public static void RecordMirrorSkipped()
    {
        if (MirrorSkipped.Enabled)
        {
            MirrorSkipped.Add(1);
        }
    }

    internal readonly struct RequestSample
    {
        internal required string Route { get; init; }
        internal required string Cluster { get; init; }
        internal required int Status { get; init; }
        internal required string Protocol { get; init; }
        internal required string Error { get; init; }
        internal required double DurationSeconds { get; init; }
        internal required long BytesIn { get; init; }
        internal required long BytesOut { get; init; }
    }

    public static void RecordRequest(RequestSample sample)
    {
        if (!RequestInstrumentsEnabled && !FailedRequests.Enabled && !ProxyBytes.Enabled)
        {
            return;
        }

        var statusClass = StatusClass(sample.Status);
        if (Requests.Enabled)
        {
            Requests.Add(1,
                new KeyValuePair<string, object?>("route", sample.Route),
                new KeyValuePair<string, object?>("cluster", sample.Cluster),
                new KeyValuePair<string, object?>("status_class", statusClass),
                new KeyValuePair<string, object?>("protocol", sample.Protocol));
        }

        if (RequestDuration.Enabled && sample.DurationSeconds > 0)
        {
            RequestDuration.Record(sample.DurationSeconds,
                new KeyValuePair<string, object?>("route", sample.Route),
                new KeyValuePair<string, object?>("status_class", statusClass));
        }

        if (FailedRequests.Enabled && !string.Equals(sample.Error, "None", StringComparison.Ordinal))
        {
            FailedRequests.Add(1, new KeyValuePair<string, object?>("error", sample.Error));
        }

        RecordBytes(sample.BytesIn, sample.BytesOut);
    }

    private static string StatusClass(int status) => status switch
    {
        >= 100 and < 200 => "1xx",
        >= 200 and < 300 => "2xx",
        >= 300 and < 400 => "3xx",
        >= 400 and < 500 => "4xx",
        >= 500 and < 600 => "5xx",
        _ => "none",
    };

    private static void RecordBytes(long bytesIn, long bytesOut)
    {
        if (!ProxyBytes.Enabled)
        {
            return;
        }

        if (bytesIn > 0)
        {
            ProxyBytes.Add(bytesIn, new KeyValuePair<string, object?>(DirectionTag, "in"));
        }

        if (bytesOut > 0)
        {
            ProxyBytes.Add(bytesOut, new KeyValuePair<string, object?>(DirectionTag, "out"));
        }
    }
}
