# Features

What Titanium offers across the four products. Each item links to the how-to guide — this page is a catalog, not a tutorial.

**Start here:** [Getting started](/docs/getting-started) · [Download](/download) · [Editions & licenses](/docs/editions)

## Inspector

Desktop HTTP(S) debugger on Windows, macOS, and Linux. [Inspector guide](/docs/inspector)

| Capability | What it does |
|------------|--------------|
| Decrypt HTTPS | MITM decrypt on machines you control; install a local root CA |
| System proxy | One toggle for OS proxy on Windows, macOS, and Linux |
| Trust that works | Firefox / NSS, Linux Chromium Snap/Flatpak, Windows Store apps; export CA for phones |
| Login-safe decrypt | SSO hosts stay on OS bypass; auto-tunnel when MITM fails or the origin returns 403/429 |
| Session grid | Method, status, host, URL, protocol, duration, TTFB, size, process |
| Inspect panes | Headers, Pretty/Raw body, Hex, WebSocket frames, SSE, Protobuf wire dump |
| Rewrite toolkit | AutoResponder, Map Local, Map Remote, breakpoints, Composer |
| GraphQL rules | Match AutoResponder / Map Remote / Breakpoints by `operationName` |
| HAR and share | Import/export HAR; copy as curl or fetch; Diff two sessions |
| Search | `is:ws`, `is:grpc`, `process:`, `status:2xx`, `hide:tunnel`, and more |
| Network throttle | Slow 3G / Fast 3G / LTE shaping on body writes and WebSocket frames |
| Streaming-safe capture | Large known-length bodies are not buffered so downloads keep flowing |
| Scripts | Line directives (`set-header`, `set-status`, `abort`) — not JavaScript or C# |

Free for personal, research, education, government, and charity use ([PolyForm Noncommercial](/docs/editions#license)). Commercial use needs a separate agreement.

## CLI

Standalone reverse / edge proxy. MIT. [CLI guide](/docs/cli) · [Configuration](/docs/configuration)

| Capability | What it does |
|------------|--------------|
| YAML config | Schema 7.1 listeners, routes, clusters, transforms, static files |
| Load balancing | RoundRobin, Random, LeastRequests, LeastTime; sticky cookie/header |
| ACME | Optional Let's Encrypt certificates |
| Protocols | HTTP/2 by default; optional HTTP/3 (QUIC); protocol bridging |
| Listeners | Explicit, transparent, SOCKS, QUIC |
| OS service | Windows SCM / systemd / launchd via `titanium service` |
| Config dialects | `.yaml` / `.json`, compact `.twp`, nginx-like `.conf` |
| Self-update | `titanium update` with SHA256 verification |
| Access logs | Opt-in NDJSON (`server.accessLog`) |

## Plus

Optional ops sidecar for the CLI (and Inspector panels). [Plus guide](/docs/plus)

| Capability | What it does |
|------------|--------------|
| Dashboard | HTML admin on the control-plane port |
| Observability | Prometheus-style metrics |
| Auth | CIDR allow-list, JWT/OIDC, API key, Basic |
| Thin WAF | Deny-list for paths, methods, headers, body size |
| Control plane | Loopback HTTP API; snapshot get/put; cache purge |
| Resilience | Health probes, circuit ejection, bounded retries |
| Discovery | File, DNS; Consul / Kubernetes best-effort |
| Rate limit | Per-IP window (memory or Redis) |
| Cache | In-memory HTTP response cache |
| gRPC-JSON | REST/JSON ↔ gRPC transcoding ([guide](/docs/grpc-json-transcoding)) |

Same Noncommercial license as Inspector — see [Editions](/docs/editions).

## Library

Embed the same engine in .NET 10. MIT. [Library guide](/docs/library) · [API](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}

| Capability | What it does |
|------------|--------------|
| Intercept | Inspect, modify, redirect, or block HTTP(S) |
| Endpoints | Explicit, transparent, SOCKS4/5 |
| Protocols | HTTP/2 default; HTTP/3 opt-in; stream bodies across versions |
| Upstream | HTTP/HTTPS/SOCKS proxies; system proxy detection |
| Auth | Proxy auth, mTLS, Kerberos, NTLM |
| System proxy + CA | Win / macOS / Linux helpers matching Inspector trust |
| Synthetic responses | Html / Json / File / Stream / Redirect without an origin |
| Fast path | Skip interception work when your handlers do not need the body |

Deeper API notes: [GitHub wiki](https://github.com/justcoding121/titanium-web-proxy/wiki).

## Also see

- [Performance](/docs/performance) — measured RPS vs YARP, nginx, HAProxy, Envoy
- [Protocol support](/docs/protocol-support) — HTTP/1 · HTTP/2 · HTTP/3 matrix
- [HTTP/3](/docs/http3) — enabling QUIC and packaging
- [Security](/docs/security) — MITM and secret handling
