> For operators running the `titanium` CLI and .NET library users who scrape or export the core proxy meter.

The proxy publishes one `System.Diagnostics.Metrics.Meter` named `Titanium.Web.Proxy` and one `ActivitySource` of the same name. The CLI can serve a useful subset of that meter as Prometheus text, with no third-party dependency. Everything is off by default.

## Turn it on

| Setting | Effect |
|---------|--------|
| `server.metrics.enabled: true` | The CLI attaches a meter listener at start and serves `GET /metrics` on `server.metrics.host` (default `127.0.0.1`) and `server.metrics.port` (default `9464`). |
| `server.admin.enabled: true` | A meter listener is attached the first time `/metrics` is requested, not at start. |
| neither | No listener exists. Every recording call in the request path stays a single `Enabled` check, so the cost is the same as before this feature. |

```yaml
server:
  metrics:
    enabled: true
    host: 127.0.0.1
    port: 9464
```

The endpoint is plain HTTP and unauthenticated. Keep it on loopback or a private interface. A config reload does not rebind it; restart the CLI to change `server.metrics`.

## What is exposed

Only these instrument families are enabled on the listener: `twp.proxy.*` (requests, duration, active, failures, bytes, upstream and status retries, mirror, destination ejections), `twp.connections.*`, `twp.pool.*`, and `twp.certificates.*` (the certificate cache). Other instruments, such as `twp.body.*` and `twp.http2.*`, are never enabled by the exposition.

Mapping rules:

- Counters become `_total` counters. Up-down counters and gauges become gauges.
- Histograms become cumulative `_bucket{le}` series plus `_sum` and `_count`.
- Dots become underscores, and the unit becomes a suffix: `s` is `_seconds`, `By` is `_bytes`. Annotation units such as `{request}` add nothing.
- `twp.proxy.request.duration` uses fixed bounds of 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 and 30 seconds (14 buckets plus `+Inf`).

## Label cardinality

Only tag keys the meter already emits become labels: `route`, `cluster`, `status_class`, `protocol`, `error`, `direction`, `reason`, and `outcome`. Any other tag is dropped, so a client IP, a request path, or a `Host` value can never become a label.

At most 200 distinct `route` values and 200 distinct `cluster` values are kept. Later values are counted under `other`. Each instrument also holds at most 4096 series.

## Catalog

Generated from the instruments the core meter declares. A unit test fails when this table drifts, and `UPDATE_TWP_METRICS_DOC=1` rewrites it.

<!-- metric-catalog:start -->
| Instrument | Kind | Unit | Prometheus series | Description |
|------------|------|------|-------------------|-------------|
| `twp.auth.rounds` | Counter | `{round}` | not exposed | Authentication challenge rounds completed, tagged by scheme. |
| `twp.body.buffered_bytes` | Histogram | `By` | not exposed | Cumulative bytes buffered for a single whole-body read, tagged by direction. |
| `twp.body.decompressed_bytes` | Histogram | `By` | not exposed | Cumulative decompressed bytes produced draining a Content-Encoding chain, tagged by direction. |
| `twp.cache.bytes` | ObservableGauge | `By` | `twp_cache_bytes` (gauge) | Bytes charged to the in-memory response cache (bodies, headers and keys), summed across live caches. |
| `twp.cache.entries` | ObservableGauge | `{entry}` | `twp_cache_entries` (gauge) | Entries held by the in-memory response cache, summed across live caches. |
| `twp.cache.requests` | Counter | `{request}` | `twp_cache_requests_total` (counter) | Response cache outcomes by result: hit, miss, stale, bypass, revalidated. |
| `twp.certificates.cached` | ObservableGauge | `{certificate}` | `twp_certificates_cached` (gauge) | Leaf certificates currently held in the in-memory certificate cache, summed across every live CertificateManager in this process. Operators can use this to confirm ResourceLimits.MaxCertificateCacheEntries is holding the cache bounded rather than inferring it indirectly from process working set. |
| `twp.connections.active` | UpDownCounter | `{connection}` | `twp_connections_active` (gauge) | Client connections currently admitted and being handled. |
| `twp.connections.rejected` | Counter | `{connection}` | `twp_connections_rejected_total` (counter) | Client connections rejected before a handler task started, by reason. |
| `twp.extauth.requests` | Counter | `{request}` | not exposed | External authorization decisions by result: allow, deny, error_open, error_closed, cache_hit. |
| `twp.http2.capability_lookups` | Counter | `{lookup}` | not exposed | HTTP/2 origin capability cache lookups, tagged by hit or miss. |
| `twp.http2.capability_mismatches` | Counter | `{mismatch}` | not exposed | Stale HTTP/2 capability cache positives detected after client ALPN was already committed. |
| `twp.http2.frame_writer.high_water_crossings` | Counter | `{crossing}` | not exposed | Times an HTTP/2 frame-writer pending depth crossed a watermark threshold. |
| `twp.http2.frame_writer.pending_frames` | Histogram | `{frame}` | not exposed | Frames queued on an HTTP/2 frame writer when a sample is recorded (enqueue high-water crossings). |
| `twp.http2.probe_duration_ms` | Histogram | `ms` | not exposed | Live HTTP/2 origin capability probe duration in milliseconds. |
| `twp.logger.drops` | Counter | `{entry}` | not exposed | Log entries dropped because a channel was saturated, tagged by channel. |
| `twp.parser.errors` | Counter | `{error}` | not exposed | Rejected malformed input, tagged by parser (framing, chunk, header, websocket, http2, http3, qpack). |
| `twp.passthrough.bytes` | Counter | `By` | not exposed | Bytes relayed by TCP passthrough, by direction: in (client to upstream) or out (upstream to client). |
| `twp.passthrough.connections` | Counter | `{connection}` | not exposed | Finished TCP passthrough connections by route (a configured server name pattern, default, or forward) and result. |
| `twp.policy.breaches` | Counter | `{breach}` | not exposed | Resource-bound policy family breaches, tagged by family, the mode that was active, and whether the breach was enforced. |
| `twp.pool.outcomes` | Counter | `{connection}` | `twp_pool_outcomes_total` (counter) | Upstream connection pool outcomes: reuse, retry after a dead pooled connection, or protocol downgrade. |
| `twp.proxy.bytes` | Counter | `By` | `twp_proxy_bytes_total` (counter) | Bytes observed on a proxied request. |
| `twp.proxy.destination.ejections` | Counter | `{ejection}` | `twp_proxy_destination_ejections_total` (counter) | Destinations marked unhealthy. |
| `twp.proxy.mirror.drops` | Counter | `{drop}` | `twp_proxy_mirror_drops_total` (counter) | Mirrored requests dropped because the queue was full. |
| `twp.proxy.mirror.failed` | Counter | `{request}` | `twp_proxy_mirror_failed_total` (counter) | Mirrored requests that failed before a response was discarded. |
| `twp.proxy.mirror.queued` | Counter | `{request}` | `twp_proxy_mirror_queued_total` (counter) | Mirrored requests accepted onto the queue. |
| `twp.proxy.mirror.sent` | Counter | `{request}` | `twp_proxy_mirror_sent_total` (counter) | Mirrored requests written to the mirror destination. |
| `twp.proxy.mirror.skipped` | Counter | `{request}` | `twp_proxy_mirror_skipped_total` (counter) | Mirrored requests skipped because the body was missing, streaming, or over the cap. |
| `twp.proxy.request.duration` | Histogram | `s` | `twp_proxy_request_duration_seconds` (histogram: `_bucket`, `_sum`, `_count`) | Proxy request duration. |
| `twp.proxy.requests` | Counter | `{request}` | `twp_proxy_requests_total` (counter) | Completed proxy requests. |
| `twp.proxy.requests.active` | UpDownCounter | `{request}` | `twp_proxy_requests_active` (gauge) | Requests in flight. |
| `twp.proxy.requests.failed` | Counter | `{request}` | `twp_proxy_requests_failed_total` (counter) | Failed proxy requests. |
| `twp.proxy.status.retries` | Counter | `{retry}` | `twp_proxy_status_retries_total` (counter) | Gateway retries, by reason: status or timeout. |
| `twp.proxy.status.retries_refused` | Counter | `{retry}` | `twp_proxy_status_retries_refused_total` (counter) | Gateway retries not attempted, by reason: budget. |
| `twp.proxy.upstream.retries` | Counter | `{retry}` | `twp_proxy_upstream_retries_total` (counter) | Upstream retry attempts. |
| `twp.streams.active` | UpDownCounter | `{stream}` | not exposed | HTTP/2 or HTTP/3 streams currently open, by protocol. |
| `twp.streams.rejected` | Counter | `{stream}` | not exposed | HTTP/2 or HTTP/3 streams rejected or reset for an abuse-budget reason. |
| `twp.svcb.duration_ms` | Histogram | `ms` | not exposed | HTTPS/SVCB discovery duration in milliseconds, tagged by result. |
| `twp.svcb.queued` | Counter | `{lookup}` | not exposed | HTTPS/SVCB background discovery lookups queued after an Auto-mode cache miss. |
| `twp.timeouts` | Counter | `{timeout}` | not exposed | Deadlines that fired, tagged by ProxyTimeoutKind. |
| `twp.udp.bytes` | Counter | `By` | not exposed | Payload bytes relayed by UDP listeners, by direction: in (client to upstream) or out (upstream to client). |
| `twp.udp.datagrams` | Counter | `{datagram}` | not exposed | Datagrams relayed by UDP listeners, by direction: in (client to upstream) or out (upstream to client). |
| `twp.udp.dropped` | Counter | `{datagram}` | not exposed | Datagrams a UDP listener did not relay, by reason: oversized, empty, malformed, rate_limited, session_cap, client_cap, draining, no_destination, resolve_failed, or upstream_error. |
| `twp.udp.sessions.active` | UpDownCounter | `{session}` | not exposed | UDP relay sessions currently open (one per client endpoint), summed across UDP listeners. |
<!-- metric-catalog:end -->

## Plus series

When Plus is active, the destination-state and destination-latency series (`titanium_destination_state`, `titanium_destination_latency_seconds`) and the Plus scrape counter are appended to the same `/metrics` output.

## OpenTelemetry

The Prometheus text above is a convenience for hosts that scrape. The meter itself is the primary interface: a .NET host that already uses OpenTelemetry can call `AddMeter("Titanium.Web.Proxy")` and `AddSource("Titanium.Web.Proxy")` and export through its own pipeline, with every instrument and no label allow-list. Instrument and tag names are the same in both paths; only the Prometheus suffix rules above differ.
