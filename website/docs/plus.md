# Plus

Optional ops plugin for the CLI (and Inspector panels). For operators who want a dashboard, metrics, auth, and a thin web application firewall (WAF) without writing custom code.

**Next:** install with `titanium update --plus`, enable in YAML, open the dashboard.

## What you get

| Outcome | Capability |
|---------|------------|
| Dashboard | HTML admin on an ephemeral port (or explicit `controlPlane.dashboardPort`) |
| Observability | Prometheus-style metrics for destination state / latency |
| Security | CIDR allow-list, JWT/OIDC (JWKS), API key / Basic auth |
| Web application firewall (WAF) | Thin deny-list (paths, methods, headers, body size) — not a full WAF suite |
| Control plane | Loopback HTTP API with shared-secret header; snapshot get/put; cache purge |
| Operations | Drain / healthy / maintenance destination states |
| Discovery | File watch, DNS poll; Consul blocking queries; Kubernetes EndpointSlice list-then-watch with backoff |
| State | Fixed-window per-IP rate limit (`state.mode=memory` or `state.redis`) |
| Resilience | Active HTTP/TCP health probes, circuit outlier ejection, bounded idempotent connection retries |
| CORS | Opt-in preflight + `Access-Control-*` response headers |
| Cache | In-memory HTTP response cache (`cache.enable`) |
| gRPC-JSON transcoding | REST/JSON ↔ gRPC (unary + multi-frame streaming; optional gzip) via FileDescriptorSet + `google.api.http` ([guide](/docs/grpc-json-transcoding)) |

File-based route/cluster reload (`titanium reload` / SIGHUP / `run --watch`) does **not** require Plus — use the control plane when you want programmatic snapshot updates or discovery watches.

## Install

Plus is distributed as a sidecar next to the CLI. There is **no** public Plus download button on this site.

```shell
titanium update --plus
titanium version --check --plus
# Prerelease / beta channel:
titanium update --plus --channel beta
titanium version --check --plus --channel beta
```

`titanium version --check --plus` reports local → remote and exit code `2` when a newer Plus is available or Plus is missing. `titanium update --plus` installs or upgrades only when needed; if Plus is already current it prints that and skips the download.

## Enable / disable

Day-to-day control is config — keep Plus installed and toggle features:

```yaml
plus:
  enabled: true
  controlPlane:
    host: "127.0.0.1"
    port: 9080
    sharedSecret: "<shared-secret>"
  options:
    cache.enable: "true"
```

Set `plus.enabled: false` (or remove the `plus:` block) to stop using Plus without deleting it from disk.

Use a strong secret in production. Dev-only default secrets require an explicit environment opt-in on loopback.

**Engine knobs** (profiles, timeouts, TLS, limits, upstream, …) are configured under `server:` in twp.yaml — not `plus.options`. See [Configuration](/docs/configuration).

## Remove

To delete Plus from disk:

```shell
titanium update --remove-plus
```

This removes Plus beside the CLI (and `.bak` / `.new`). It does **not** stop a running proxy, OS service, or the in-process Plus control plane / dashboard — stop `titanium run` or `titanium service stop`, then start again so Plus unloads. It does not edit your config; disable `plus.enabled` separately if it is still set. Re-install later with `titanium update --plus`.

## `plus.options` keys (reference)

String values under `plus.options` (examples):

| Key | Role |
|-----|------|
| `discovery.mode` | `file`, `dns`, `consul`, or `k8s` |
| `discovery.file` | Path for `mode=file` |
| `discovery.dnsName` / `discovery.dnsPort` | DNS discovery target |
| `discovery.consulUrl` | Consul service endpoint, for example `http://consul:8500/v1/health/service/web` |
| `discovery.consulWaitSeconds` / `discovery.consulPassing` / `discovery.consulToken` | Blocking-query wait (default `30`), passing-only filter (default `true`), ACL token |
| `discovery.k8sUrl` | API server URL. Optional in a pod: `KUBERNETES_SERVICE_HOST` is used. A URL with a path (or without a selector) is used as the exact EndpointSlice collection to watch; a single Endpoints object there is polled every `discovery.intervalMs` |
| `discovery.k8sService` / `discovery.k8sLabelSelector` / `discovery.k8sNamespace` / `discovery.k8sPortName` | Which slices to watch (a selector is required unless `k8sUrl` is used as the exact URL: it has a path or no selector is set); namespace defaults to the pod namespace; named port, default first TCP port |
| `discovery.k8sServiceAccountDir` / `discovery.k8sTokenRefreshSeconds` / `discovery.k8sWatchTimeoutSeconds` | Token and CA directory (default `/var/run/secrets/kubernetes.io/serviceaccount`), token re-read interval (default `60`), watch window (default `300`) |
| `discovery.backoffMinMs` / `discovery.backoffMaxMs` | Retry backoff bounds for `consul` and `k8s` (defaults `1000` and `30000`) |
| `discovery.intervalMs` / `discovery.clusterId` | Poll interval for `dns` and for sources that cannot block or watch; cluster id |
| `security.allowCidrs` | Comma-separated client CIDR allow-list |
| `security.jwtAuthority` / `security.jwtAudience` / `security.jwksUrl` | JWT/OIDC validation |
| `security.apiKeys` | Comma-separated API keys (`X-Api-Key` by default) |
| `security.apiKeyHeader` | Alternate API key header name |
| `security.basicUsers` | Comma-separated `user:password` pairs for HTTP Basic |
| `cors.enabled` | Enable CORS helper (`true`) |
| `cors.allowOrigin` / `cors.allowMethods` / `cors.allowHeaders` / `cors.allowCredentials` / `cors.maxAgeSeconds` | CORS knobs |
| `waf.enabled` | Enable thin deny-list WAF |
| `waf.denyPaths` / `waf.denyMethods` / `waf.denyHeader` | Deny rules |
| `waf.maxBodyBytes` / `waf.rulesFile` | Body cap and optional rules file |
| `state.mode` | `memory` or use Redis via `state.redis` |
| `state.redis` / `state.rateLimitPerMinute` | Redis connection and rate limit |
| `resilience.activeHealth` | Enable active probes |
| `resilience.intervalMs` / `resilience.unhealthyThreshold` / `resilience.path` / `resilience.protocol` / `resilience.timeoutMs` | Probe knobs |
| `resilience.circuit.enabled` | Outlier ejection on consecutive 5xx → Unhealthy |
| `resilience.circuit.failureThreshold` / `resilience.circuit.cooldownMs` | Circuit knobs |
| `resilience.retry.idempotentAttempts` | Raise connection retries for safe methods (1–5) |
| `cache.enable` | In-memory response cache |
| `grpc.transcode.enabled` | Enable gRPC-JSON transcoding |
| `grpc.transcode.descriptorSet` | Path to FileDescriptorSet (`.pb`) |
| `grpc.transcode.services` | Comma-separated fully-qualified service names |
| `grpc.transcode.convertGrpcStatus` | Map non-OK `grpc-status` to HTTP + JSON body (default true) |
| `grpc.transcode.ignoreUnknownQueryParameters` | Ignore unknown query keys (default true) |
| `grpc.transcode.preserveProtoFieldNames` | Use proto field names in JSON (default false) |
| `grpc.transcode.alwaysPrintPrimitiveFields` | Always emit primitive defaults in JSON (default false) |
| `grpc.transcode.compression` | `true` or `gzip` to gzip framed payloads |

## Discovery: Consul and Kubernetes

Both remote modes keep serving the **last known good** destinations. A failed call, an unparseable body, or an empty result is never applied; the next successful non-empty answer replaces the set. Failures retry with exponential backoff and jitter (`discovery.backoffMinMs` to `discovery.backoffMaxMs`) and the delay resets after the next success.

**Consul** uses blocking queries: the first read is plain, then each call sends `?index=<X-Consul-Index>&wait=30s` and returns when the service changes or the wait elapses. An index that goes backwards restarts from a plain read. With `discovery.consulPassing` (default `true`) only instances whose checks all pass are used. A server that sends no index is polled every `discovery.intervalMs`.

```yaml
plus:
  options:
    discovery.mode: "consul"
    discovery.consulUrl: "http://consul:8500/v1/health/service/web"
    discovery.clusterId: "web"
```

**Kubernetes** lists EndpointSlices, then watches from the list `resourceVersion` with bookmarks enabled. Ready endpoints from every matching slice form the destination set. A `410 Gone` (or a watch `ERROR` event with code 410) drops the version and relists. In a pod the API server, namespace, service-account token, and CA come from `KUBERNETES_SERVICE_HOST` and `/var/run/secrets/kubernetes.io/serviceaccount`. The projected token is re-read on a timer and immediately after a 401 or 403, so rotation needs no restart. The service account needs `get`, `list`, and `watch` on `endpointslices` in `discovery.k8s.io`.

```yaml
plus:
  options:
    discovery.mode: "k8s"
    discovery.k8sService: "web"
    discovery.k8sNamespace: "prod"
    discovery.k8sPortName: "http"
    discovery.clusterId: "web"
```

## See also

- [CLI](/docs/cli)
- [Configuration](/docs/configuration)
- [Editions](/docs/editions)
- [gRPC-JSON transcoding](/docs/grpc-json-transcoding)
