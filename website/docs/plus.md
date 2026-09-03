# Plus

Optional ops plugin for the CLI (and Inspector panels). Licensed under [PolyForm Noncommercial](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt) — **not for commercial use** without a separate agreement.

## Install

Plus is distributed as a sidecar DLL next to the CLI. There is **no** public Plus download button on this site.

```shell
titanium update --plus
titanium version --check --plus
# Prerelease / beta channel:
titanium update --plus --channel beta
titanium version --check --plus --channel beta
```

`titanium version --check --plus` reports local → remote and exit code `2` when a newer Plus is available or Plus is missing. `titanium update --plus` installs or upgrades only when needed; if Plus is already current it prints that and skips the download.

## Enable

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

Use a strong secret in production. Dev-only default secrets require an explicit environment opt-in on loopback.

**`ProxyServer` knobs** (profiles, timeouts, TLS, limits, upstream, …) are configured under `server:` in twp.yaml — not `plus.options`. See [Configuration](/docs/configuration).

## What you get

| Area | Capability |
|------|------------|
| Control plane | Loopback HTTP API with shared-secret header; snapshot get/put; cache purge |
| Dashboard | HTML admin on an ephemeral port (or explicit `controlPlane.dashboardPort`) |
| Observability | Prometheus-style metrics for destination state / latency |
| Operations | Drain / healthy / maintenance destination states |
| Discovery | File watch, DNS poll; Consul / Kubernetes best-effort |
| Security | CIDR allow-list, JWT/OIDC (JWKS) |
| WAF | Thin deny-list (paths, methods, headers, body size) — not a full WAF suite |
| State | Fixed-window per-IP rate limit (`state.mode=memory` or `state.redis`) |
| Resilience | Active HTTP/TCP health probes |
| Cache | In-memory HTTP response cache (`cache.enable`) |

## See also

- [CLI](/docs/cli)
- [Configuration](/docs/configuration)
- [Editions](/docs/editions)
