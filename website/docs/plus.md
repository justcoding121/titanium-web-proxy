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
| Discovery | File watch, DNS poll; Consul / Kubernetes best-effort |
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
| `discovery.consulUrl` / `discovery.k8sUrl` | Best-effort HTTP poll endpoints |
| `discovery.intervalMs` / `discovery.clusterId` | Poll interval and cluster id |
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

## See also

- [CLI](/docs/cli)
- [Configuration](/docs/configuration)
- [Editions](/docs/editions)
- [gRPC-JSON transcoding](/docs/grpc-json-transcoding)
