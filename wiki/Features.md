Short catalog of what Titanium offers. Product how-tos live on the **[website Features page](https://titaniumproxy.com/docs/features)** — use that as the canonical list. This wiki page is a pointer for GitHub visitors.

## Highlights

- **Inspector** — native HTTPS debugger on Windows, macOS, and Linux: decrypt, system proxy, AutoResponder / Map Local / Map Remote, breakpoints, Composer, HAR, curl/fetch
- When a server blocks the proxy (TLS fingerprinting or other reasons), we detect it and automatically tunnel that traffic
- **Modern protocols** — HTTP/2 by default, optional HTTP/3, WebSocket, SSE, gRPC/Protobuf, GraphQL `operationName` rules
- **CLI reverse / edge proxy** — YAML routes, load balancing, health checks, retries with budgets, rate limits, external authorization, response compression, gRPC-Web, TCP and UDP relay, ACME, live reload, OS service ([CLI](https://titaniumproxy.com/docs/cli), [Configuration](https://titaniumproxy.com/docs/configuration))
- **Observe it** — Prometheus text, OTLP traces and metrics, an admin listener with probes, and an opt-in access log ([Configuration](https://titaniumproxy.com/docs/configuration#observability))
- **Kubernetes** — hardened container image, Helm chart, Gateway API and Ingress controller ([Kubernetes](Kubernetes), [Gateway API](Gateway-API))
- **Per-route .NET plugins** — load your own `IProxyMiddleware` for chosen routes, with per-plugin timeout and fail-open or fail-closed isolation ([Plugins](https://titaniumproxy.com/docs/plugins))
- **Library** — embed the same engine in .NET ([Library](https://titaniumproxy.com/docs/library)); wiki Home below covers the API in depth
- **Plus** — optional dashboard (local users, OIDC SSO, break-glass), metrics, auth, OpenID Connect login, fleet rate limits, managed fleet agent, response cache, WAF request screening (detect mode, anomaly scoring), gRPC-JSON ([Plus](https://titaniumproxy.com/docs/plus), [Manager](https://titaniumproxy.com/docs/manager))

## By product

| Product | Start here |
|---------|------------|
| Inspector | [Inspector guide](https://titaniumproxy.com/docs/inspector) |
| CLI | [CLI](https://titaniumproxy.com/docs/cli) · [Configuration](https://titaniumproxy.com/docs/configuration) |
| Plus | [Plus](https://titaniumproxy.com/docs/plus) · [Manager](https://titaniumproxy.com/docs/manager) · [gRPC-JSON](https://titaniumproxy.com/docs/grpc-json-transcoding) |
| Library | [Library](https://titaniumproxy.com/docs/library) · [Home](Home) (API) · [Synthetic responses](Synthetic-Responses) · [Streaming](Streaming-Bodies) |

Full tables: **[titaniumproxy.com/docs/features](https://titaniumproxy.com/docs/features)**. What is supported, partial, and not supported, by config key: [Capabilities](Capabilities).
