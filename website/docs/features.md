# Features

What Titanium offers across the four products. Each item links to the how-to guide — this page is a catalog, not a tutorial.

**Start here:** [Getting started](/docs/getting-started) · [Download](/download) · [Editions & licenses](/docs/editions) · [Capability coverage](/docs/capabilities)

## Inspector

Desktop HTTP(S) debugger on Windows, macOS, and Linux. [Inspector guide](/docs/inspector)

| Capability | What it does |
|------------|--------------|
| Decrypt HTTPS | MITM decrypt on machines you control; install a local root CA |
| System proxy | One toggle for OS proxy on Windows, macOS, and Linux |
| Trust that works | Firefox / NSS, Linux Chromium Snap/Flatpak, Windows Store apps; export CA for phones |
| Automatic tunnel | When a server blocks the proxy (TLS fingerprinting or other reasons), we detect it and automatically tunnel that traffic |
| Session grid | Method, status, host, URL, protocol, duration, TTFB, size, process; show or hide columns (plus Started, Scheme, Content-Type) from the header menu |
| Inspect panes | Request and response headers and bodies (Pretty and Hex options), WebSocket frames, SSE, Protobuf wire dump |
| Rewrite toolkit | AutoResponder, Map Local, Map Remote, breakpoints, Composer |
| GraphQL rules | Match AutoResponder / Map Remote / Breakpoints by `operationName` |
| HAR and share | Import/export HAR; copy as curl or fetch; Diff two sessions |
| Search | `is:ws`, `is:grpc`, `process:`, `status:2xx`, `-method:CONNECT` (or typed `hide:tunnel`), and a leading `-` to hide one host or process (`-host:cursor.sh`, shown as a chip); **Method** unchecks a verb to hide it; **Advanced…** edits host and process hides with more room; **Clear filters** resets it |
| Network throttle | Slow 3G / Fast 3G / LTE. One latency delay per response. Uploads and downloads share separate budgets. Downloads always trickle in paced slices (no toggle). On HTTP/1 and HTTP/3 origins bodies under 64 KB and uploads wait, then arrive as a block. On HTTP/2 every buffered, known-length body is shaped; responses with no declared length only get the latency. |
| Streaming-safe capture | Large known-length bodies are not buffered so downloads keep flowing |
| Scripts | Line directives (`set-header`, `set-status`, `abort`) — not JavaScript or C# |

Free for personal, research, education, government, and charity use ([PolyForm Noncommercial](/docs/editions#license)). Commercial use needs a separate agreement.

## CLI

Standalone reverse / edge proxy. MIT. [CLI guide](/docs/cli) · [Configuration](/docs/configuration)

| Capability | What it does |
|------------|--------------|
| YAML config | Schema 7.1 listeners, routes, clusters, transforms, static files |
| Load balancing | RoundRobin, Random, LeastRequests, LeastTime; sticky cookie/header |
| Upstream mTLS | Per-cluster client certificate, CA bundle, SNI override, and hostname policy (`clusters[].httpClient`) |
| ACME | Optional Let's Encrypt certificates |
| Protocols | HTTP/2 by default; HTTP/3 when QUIC is available; protocol bridging |
| Listeners | Explicit, transparent, SOCKS, QUIC, UDP relay (sessions per client endpoint, cluster load balancing, caps, metrics) |
| OS service | Windows SCM / systemd / launchd via `titanium service` |
| Config dialects | `.yaml` / `.json`, compact `.twp`, nginx-like `.conf` |
| Live reload | `titanium reload` / SIGHUP / `run --watch` — routes and clusters without dropping listeners |
| Self-update | `titanium update` with SHA256 verification |
| Access logs | Opt-in NDJSON (`server.accessLog`) |
| Response compression | Opt-in brotli/gzip for HTTP/1.1 and HTTP/2-to-HTTP/1.1 (`server.responseCompression`): buffered (default) or `mode: streaming`, which compresses while relaying with a flush per upstream chunk, bounded memory and no size cap |
| Request decompression | Opt-in gzip/brotli request bodies decompressed before the origin with a size cap and a zip-bomb ratio limit (`server.requestDecompression`) |
| gRPC-Web | Opt-in per route (`grpcWeb: true`): browser gRPC-Web to a native gRPC HTTP/2 origin, binary unary and server-streaming ([guide](/docs/configuration#grpc-web-route-grpcweb)) |
| Clusters | Passive health, optional active probes, retry, concurrency, priority, backup, hash, and opaque session affinity |
| Routing | Order plus specificity, segment prefix, regex, redirects, direct responses, error pages, traffic split, and mirroring |
| Listeners | SNI certificate selection, optional client certificates, PROXY protocol, TCP passthrough, and drain |
| TCP passthrough with SNI | A passthrough listener reads the ClientHello and picks a cluster by server name (exact, then longest `*.suffix`, then a default cluster or `forwardHost`) without decrypting. Balancing, connect failover, half-close, an idle timeout, and an optional PROXY protocol v2 header to the upstream |
| Policies | Optional per-route header, body-size, CORS, rate, connection, and address checks. An empty policy list leaves terminate-lite eligible |
| Rate limit policy | `rateLimit` counts per client, forwarded client, header, cookie, or route, with a fixed window or token bucket, a bounded key table, and `Retry-After` (plus optional `RateLimit-*`) on a 429. Default `scope: node` is in-process; `scope: fleet` (Plus + Redis) shares a leased token bucket across instances ([Configuration](/docs/configuration#fleet-rate-limit)). Terminate-lite eligible |
| External authorization | `extAuth` policy asks an HTTP service to allow or deny each request. Deny status and chosen headers are relayed, redirects are not followed, `failOpen` and a short positive cache are optional. Leaves terminate-lite |
| Per-route .NET plugins | `plugins` loads an `IProxyMiddleware` DLL into its own collectible load context; `routes[].middleware` runs it for that route only. Per-plugin `failMode` (open or closed) and `timeoutMs`, `twp.plugin.duration` and `twp.plugin.errors` metrics, unloaded on reload. Script engines are a separate row. A config in which a route names a plugin leaves terminate-lite ([guide](/docs/plugins)) |
| Lua and WebAssembly scripting | `kind: lua` (Lua 5.4) and `kind: wasm` (proxy-wasm 0.1.0 to 0.2.1 on Wasmtime) plugins, named in `routes[].middleware`. Sandboxed, with memory, instruction or fuel, and CPU-time limits inside the engine, `failMode` and `timeoutMs`, allow-listed HTTP callouts (`calloutHosts`), shared state, and metrics. Natives ship per RID; `titanium scripting-deps status` reports whether they load ([guide](/docs/scripting)) |
| OpenID Connect login | `oidc` policy (Plus) signs people in with the authorization-code flow and PKCE, keeps an AES-256-GCM encrypted session cookie, renews it quietly, and passes chosen claims as headers. Scripts get `401` instead of a redirect. Leaves terminate-lite |
| Admin listener | `server.admin` serves `/healthz`, `/readyz`, `/metrics`, and `/version` on a port of its own, and `titanium healthcheck` probes it ([reference](/docs/configuration#admin-listener-server-admin)) |
| Metrics and traces | Opt-in Prometheus text (`server.metrics`) and OTLP traces and metrics with W3C trace context (`server.telemetry`) ([reference](/docs/configuration#traces-and-metrics-export-server-telemetry)) |
| Kubernetes | Hardened container image, Helm chart, probes, graceful rollout, and ConfigMap and Secret reload ([guide](/docs/kubernetes)) |
| Gateway API and Ingress | `titanium-gateway` turns `Gateway`, `HTTPRoute`, and `Ingress` resources into a data-plane config ([Gateway API](/docs/gateway-api), [Ingress](/docs/ingress)) |

## Plus

Optional ops sidecar for the CLI (and Inspector panels). [Plus guide](/docs/plus)

| Capability | What it does |
|------------|--------------|
| Dashboard | HTML admin on the control-plane port; local users, optional OIDC SSO, break-glass secrets, reload, and managed-config drift |
| Observability | Prometheus-style metrics |
| Auth | CIDR allow-list, JWT (JWKS), API key, Basic, the `oidc` login policy, and dashboard OIDC SSO |
| WAF | Signature-based request screening with detect mode and anomaly scoring (built-in rule pack, custom YAML rules, per-route `waf` policy), plus an operator deny-list; see [Plus](/docs/plus#web-application-firewall-waf) |
| Control plane | Loopback HTTP API; snapshot get/put; cache purge |
| Managed fleet | Agent long-polls [titanium-manager](/docs/manager) (`manager.url`); staged rollouts and last-known-good |
| Resilience | Health probes, circuit ejection, bounded retries |
| Discovery | File, DNS; Consul / Kubernetes best-effort |
| Rate limit | Per-IP window (memory or Redis); fleet `rateLimit` leases over Redis |
| Cache | In-memory shared response cache (an RFC 9111 subset): never stores `Set-Cookie`, `private`, or `no-store` responses, keys on `Vary`, revalidates, and collapses concurrent misses ([reference](/docs/configuration#response-cache)) |
| gRPC-JSON | REST/JSON ↔ gRPC transcoding ([guide](/docs/grpc-json-transcoding)) |

Same Noncommercial license as Inspector — see [Editions](/docs/editions).

## Library

Embed the same engine in .NET 10. MIT. [Library guide](/docs/library) · [API](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}

| Capability | What it does |
|------------|--------------|
| Intercept | Inspect, modify, redirect, or block HTTP(S) |
| Endpoints | Explicit, transparent, SOCKS4/5 |
| Protocols | HTTP/2 default; HTTP/3 when the host enables it and QUIC is available; stream bodies across versions |
| Upstream | HTTP/HTTPS/SOCKS proxies; system proxy detection |
| Auth | Proxy auth, mTLS, Kerberos, NTLM |
| System proxy + CA | Win / macOS / Linux helpers matching Inspector trust |
| Synthetic responses | Html / Json / File / Stream / Redirect without an origin |
| Fast path | Skip interception work when your handlers do not need the body |
| Metrics and tracing | Core `Meter` and `ActivitySource` named `Titanium.Web.Proxy` (`AddMeter("Titanium.Web.Proxy")` / `AddSource("Titanium.Web.Proxy")`). Request counters stay quiet until a listener is attached. Opt-in Prometheus text via `server.metrics` |

Routes, clusters, policies, and the other reverse-proxy rows under [CLI](#cli) come from the same engine; the CLI sets them from `twp.yaml`.

Deeper API notes: [GitHub wiki](https://github.com/justcoding121/titanium-web-proxy/wiki).

## Also see

- [Capability coverage](/docs/capabilities) — what is supported, partial, and not supported, by config key
- [Kubernetes](/docs/kubernetes) · [Gateway API](/docs/gateway-api) · [Ingress](/docs/ingress)
- [Performance](/docs/performance) — throughput charts and methodology
- [Protocol support](/docs/protocol-support) — HTTP/1 · HTTP/2 · HTTP/3 matrix
- [HTTP/3](/docs/http3) — QUIC default and packaging
- [Security](/docs/security) — MITM and secret handling
