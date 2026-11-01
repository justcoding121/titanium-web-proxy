# Capability coverage

What the `titanium` CLI and its optional Plus add-on cover, by area. Every row links to the page that explains it. Rows marked `not supported` are listed on purpose, with the reason.

**Status:** `supported` works as described. `partial` works with the limits named in Notes. `not supported` is not offered; Notes gives the reason and what to use instead.

**Config key:** a key in `twp.yaml`, written the way the schema nests it (`routes[]` is a list entry). A key with a prefix such as `plus.options:` is a Titanium Plus option, which is a free-form string key. A row with a command, an API path, or a resource name has no `twp.yaml` key; the cell says what to use. `—` means there is nothing to set.

## Routing

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Match on host, path, method, header, and query, with order and specificity | supported | `routes[].match.hosts`, `routes[].match.path`, `routes[].match.pathKind`, `routes[].order` | Exact, segment prefix, prefix, template, and regex paths. A lower order wins; ties go to the more specific match. | [Configuration](/docs/configuration#routes-clusters) |
| Redirects, direct responses, and custom error pages | supported | `routes[].redirect`, `routes[].directResponse`, `routes[].errorPages` | Answer from the proxy without an origin, or replace the body of a generated status. | [Configuration](/docs/configuration#routes-clusters) |
| Weighted traffic split across clusters | supported | `routes[].clusters` | A route lists clusters with weights; a cookie or header can pin a client to one of them. | [Configuration](/docs/configuration#routes-clusters) |
| Request mirroring | supported | `routes[].mirror` | Sends a sampled copy of matching requests to another cluster and discards the answer. Large bodies are skipped. | [Configuration](/docs/configuration#routes-clusters) |
| Request and response rewrites | supported | `routes[].transforms` | Path prefix, query, and header edits on the way to the origin; response header edits on the way back. | [Route transforms](/docs/configuration#route-transforms) |
| Static files | supported | `staticFiles` | Serves a directory, with optional gzip and brotli. | [Static files](/docs/configuration#static-files) |

## Load balancing

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Balancing algorithms | supported | `clusters[].algorithm` | RoundRobin, Random, LeastRequests, and LeastTime. | [Configuration](/docs/configuration#routes-clusters) |
| Weights, priority, backup destinations, and slow start | supported | `clusters[].destinations[].weight`, `clusters[].destinations[].priority`, `clusters[].destinations[].backup`, `clusters[].slowStartSeconds` | Slow start ramps a new destination from 10 percent of its weight. | [Configuration](/docs/configuration#routes-clusters) |
| Session affinity by cookie, header, or hash | supported | `clusters[].sessionAffinity`, `clusters[].hashKey` | An opaque affinity cookie, a sticky header, or consistent hashing on a chosen key. | [Configuration](/docs/configuration#routes-clusters) |
| Concurrency caps and a wait queue | supported | `clusters[].destinations[].maxConcurrentRequests`, `clusters[].queue` | The queue waits briefly when every destination is at its cap. | [Configuration](/docs/configuration#routes-clusters) |
| Service discovery | partial | `plus.options: discovery.*` | Plus watches a file, polls DNS, and queries Consul or Kubernetes EndpointSlices. Consul and Kubernetes discovery are best-effort. | [Plus](/docs/plus#discovery-consul-and-kubernetes) |

## Health checks

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Passive ejection | supported | `clusters[].healthCheck.passive` | Treats 502, 503, and 504 as failures by default, caps the share ejected, and sends one trial after reactivateAfterSeconds. | [Configuration](/docs/configuration#routes-clusters) |
| Active probes | supported | `clusters[].healthCheck.active` | Probes run on a background timer and only restore destinations that a probe marked unhealthy. | [Configuration](/docs/configuration#routes-clusters) |
| Probe endpoints for orchestrators | supported | `server.admin` | /healthz and /readyz on a port no proxy listener shares. Readiness turns 503 on SIGTERM before the drain starts. | [Admin listener](/docs/configuration#admin-listener-server-admin) |
| Drain and maintenance states | supported | `plus.controlPlane` | An operator drains a destination from the dashboard or the control-plane API (Plus). | [Plus](/docs/plus#sign-in-and-roles) |

## Retries and budgets

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Connect failover to the next destination | supported | `routes[].retry.attempts`, `clusters[].retry.attempts` | Idempotent methods by default. | [Configuration](/docs/configuration#routes-clusters) |
| Gateway-status replay with backoff and jitter | supported | `routes[].retry.statusCodes`, `routes[].retry.backoffMilliseconds`, `routes[].retry.jitterPercent`, `routes[].retry.honorRetryAfter` | Replays a 502, 503, or 504 onto another destination before anything reaches the client. | [Configuration](/docs/configuration#routes-clusters) |
| Retry budget | supported | `routes[].retry.budgetPercent`, `routes[].retry.budgetMinConcurrent` | Limits replays in flight to a share of the cluster's in-flight requests. Refused replays are counted. | [Configuration](/docs/configuration#routes-clusters) |
| Per-try timeout | supported | `routes[].retry.perTryTimeoutMilliseconds` | Ends a slow try as a 504 on every try but the last. | [Configuration](/docs/configuration#routes-clusters) |
| Retry of requests with a body | partial | `routes[].retry.retryNonIdempotent`, `routes[].retry.maxBufferedBodyBytes` | Only a body that was fully buffered is replayed. An HTTP/3 client talking to an HTTP/2 origin is not retried. | [Protocol support](/docs/protocol-support#routing-and-clusters) |

## Timeouts

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Connect, header, idle, and request timeouts | supported | `server.timeouts.connectTimeOutSeconds`, `server.timeouts.clientHeaderTimeoutSeconds`, `server.timeouts.responseHeaderTimeoutSeconds`, `server.timeouts.idleReadTimeoutSeconds`, `server.timeouts.requestTimeoutSeconds` | Zero turns a timeout off. | [Server reference](/docs/configuration#server-reference) |
| Per-route timeout and buffer overrides | supported | `routes[].limits.requestTimeoutSeconds`, `routes[].limits.idleTimeoutSeconds`, `routes[].limits.maxBufferedBodyBytes` | A route overrides the server limits for its own sessions. | [Limits](/docs/limits#reference) |
| Drain on shutdown | supported | `server.admin.preStopDelaySeconds` | Keeps serving for the delay after /readyz flips, then drains in-flight requests. | [Kubernetes](/docs/kubernetes#graceful-rollout) |

## Rate limiting

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Request rate limit per key | supported | `policies[].key`, `policies[].algorithm`, `policies[].limit`, `policies[].windowSeconds`, `policies[].burst` | Fixed window or token bucket, keyed by client, forwarded client, header, cookie, or route. Answers 429 with Retry-After. | [Configuration](/docs/configuration#routes-clusters) |
| Concurrent connection caps | supported | `listeners[].maxConcurrentClients`, `server.limits.maxConcurrentClients` | Admission control per listener or for the whole server. | [Limits](/docs/limits#reference) |
| Rate limit shared across instances | supported | `policies[].scope`, `policies[].fleetFailMode`, `policies[].localShare`, `plus.options: state.mode`, `plus.options: state.redis` | Leased fleet token bucket over Redis; the request path stays sync. Default scope node is unchanged. | [Configuration](/docs/configuration#fleet-rate-limit) |

## Auth methods

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Required header and API key checks | supported | `policies[].headerName`, `policies[].headerValue` | The requireHeader and apiKey policy kinds refuse a request that lacks the header or value. | [Configuration](/docs/configuration#routes-clusters) |
| External authorization service | supported | `policies[].url`, `policies[].failOpen`, `policies[].cacheSeconds` | An HTTP service allows or denies each request. Redirects are not followed, and a short positive cache is optional. | [Configuration](/docs/configuration#routes-clusters) |
| OpenID Connect login | supported | `policies[].issuer`, `policies[].clientId` | Authorization-code flow with PKCE and an encrypted session cookie (Plus). | [Login policy](/docs/configuration#login-route-policy-oidc-plus) |
| JWT, API key, and Basic authentication | supported | `plus.options: security.*` | JWKS-verified JWT, API keys, and Basic users with bcrypt or PBKDF2 hashes (Plus). | [Plus](/docs/plus#plus-options-keys-reference) |
| Address allow and deny lists | supported | `policies[].allow`, `policies[].deny` | IPv4 and IPv6 addresses and CIDR prefixes. | [Configuration](/docs/configuration#routes-clusters) |
| Kerberos and NTLM to upstream proxies | partial | `server.enableWinAuth` | Switch is in the file; credential and proxy-auth callbacks are set in .NET code. | [Code-only callbacks](/docs/configuration#code-only-callbacks) |
| Multi-user dashboard accounts | supported | `plus.controlPlane.dashboardUsersFile` | Per-person operator and viewer accounts from a local users file (PBKDF2 hashes). Shared secrets remain as optional break-glass. | [Plus](/docs/plus#sign-in-and-roles) |
| SSO beyond the OIDC login policy | supported | `plus.options: controlPlane.dashboardOidc.*` | Plus dashboard OpenID Connect (authorization code + PKCE), with group and email allow-lists for operator and viewer roles. | [Plus](/docs/plus#dashboard-sso-openid-connect) |

## mTLS

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Client certificate to the origin | supported | `clusters[].httpClient.clientCertificatePath`, `clusters[].httpClient.clientCertificateKeyPath`, `clusters[].httpClient.clientCertificatePasswordEnv` | PEM pair or PFX per cluster. The password comes from an environment variable, never the file. | [Upstream TLS](/docs/configuration#upstream-tls) |
| Private CA, SNI override, and hostname policy for an origin | supported | `clusters[].httpClient.caBundlePath`, `clusters[].httpClient.serverName`, `clusters[].httpClient.verifyHostname` | The bundle replaces the machine trust store for that cluster. | [Upstream TLS](/docs/configuration#upstream-tls) |
| Client certificates from callers | supported | `listeners[].clientCertificateMode`, `listeners[].clientCertificateCaPath` | Mode is none, allow, or require, checked against the CA file you name. | [Features](/docs/features#library) |

## TLS policy

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Minimum TLS version and OCSP stapling for listeners | supported | `server.tls.minVersion`, `server.tls.ocspStapling` | Applies to the handshake with clients; origin-facing handshakes are not changed. | [Listener TLS policy](/docs/configuration#listener-tls-policy-server-tls) |
| Cipher suite list for listeners | partial | `server.tls.cipherSuites` | Linux and macOS only. On Windows .NET has no cipher suite policy, the list is ignored, and validation prints a warning. | [Listener TLS policy](/docs/configuration#listener-tls-policy-server-tls) |
| Automatic certificates (ACME) | supported | `certificates.acmeDomain`, `certificates.acmeEmail`, `certificates.acmeDirectory` | Obtains certificates from Let's Encrypt or another ACME directory. | [Certificates and ACME](/docs/configuration#certificates-acme) |
| 0-RTT (early data) | not supported | — | System.Net.Quic does not offer early data, so every QUIC connection starts with a full 1-RTT handshake. | [HTTP/3](/docs/http3) |

## Caching

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Shared response cache | supported | `plus.options: cache.enable`, `plus.options: cache.maxBytes` | In-memory RFC 9111 subset with Vary, revalidation, size limits, and request collapsing (Plus). | [Response cache](/docs/configuration#response-cache) |
| Cache purge | supported | `plus.controlPlane` | By exact key or prefix through the control plane at /v1/cache/purge (Plus). | [Response cache](/docs/configuration#response-cache) |

## Compression

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Response compression (gzip, brotli) | partial | `server.responseCompression`, `routes[].compression` | Buffered or streaming. Runs on HTTP/1.1 and the HTTP/2-to-HTTP/1.1 bridge only, not on HTTP/2-to-HTTP/2, HTTP/3, or HTTP/1.1-to-HTTP/2. | [Response compression](/docs/configuration#response-compression-server-responsecompression) |
| Request decompression with size and ratio limits | supported | `server.requestDecompression` | gzip and brotli request bodies on HTTP/1.x, with a decompressed-size cap and a zip-bomb ratio limit. | [Request decompression](/docs/configuration#request-decompression-server-requestdecompression) |

## WAF

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Signature screening with detect mode and anomaly scoring | supported | `policies[].mode`, `policies[].paranoiaLevel`, `policies[].blockThreshold` | Built-in rule pack, per-route override, and an operator deny-list (Plus). | [Request screening](/docs/configuration#request-screening-route-policy-waf-plus) |
| Custom rules | supported | `plus.options: waf.rulesFile` | Your own YAML rules next to the built-in pack. | [Plus](/docs/plus#custom-rules-waf-rulesfile) |
| Geo country allow and deny | supported | `policies[].kind`, `policies[].allow`, `policies[].deny`, `server.geoIpDatabasePath` | Country codes from an operator-supplied MaxMind DB. Unknown countries fail closed on allow lists and pass on deny-only lists. | [Geo route policy](/docs/configuration#geo-route-policy-geo) |

## Observability

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Prometheus metrics | supported | `server.metrics` | Text exposition of the core meter with bounded label sets. | [Prometheus metrics](/docs/configuration#prometheus-metrics-server-metrics) |
| Traces and metrics export over OTLP | supported | `server.telemetry` | W3C trace context through the proxy. The OTEL_* environment variables set the defaults. | [Telemetry](/docs/configuration#traces-and-metrics-export-server-telemetry) |
| Access log | supported | `server.accessLog`, `server.requestId` | Opt-in NDJSON written after each response, with sampling. Bodies are never buffered for it. | [Access log](/docs/configuration#access-log-server-accesslog) |
| Read-only dashboard | supported | `plus.controlPlane.dashboardPort` | Overview, routes and clusters, health, config, certificates, and audit views (Plus). | [Plus](/docs/plus#dashboard) |
| Dashboard route editing | not supported | — | The dashboard shows routes read-only, with Drain and Healthy buttons. Change routes in the file, or push a snapshot through the control plane. | [Plus](/docs/plus#dashboard) |

## Kubernetes

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Helm chart for the proxy | supported | `deploy/helm/titanium` | Config, probes, a disruption budget, autoscaling, ServiceMonitor, and a sample NetworkPolicy. | [Kubernetes](/docs/kubernetes#helm-chart) |
| Hardened container image | supported | `ghcr.io/justcoding121/titanium-web-proxy` | Chiseled base, non-root, read-only root filesystem, linux/amd64 and linux/arm64. It has no libmsquic, so HTTP/3 is off. | [Kubernetes](/docs/kubernetes#container-image) |
| ConfigMap and Secret reload | supported | `titanium run --watch` | Follows the atomic ..data swap and reloads once. A reload that fails validation keeps the running config. | [Kubernetes](/docs/kubernetes#config-and-secret-reload) |
| Gateway API HTTPRoute | partial | `GatewayClass titanium` | HTTP core of the standard channel. A rule the controller cannot honor answers 500 and says why in the route status. | [Gateway API](/docs/gateway-api#supported-resources-and-fields) |
| Ingress | supported | `networking.k8s.io/v1 Ingress` | Served by the same controller, with titanium.io annotations for rewrites and canary. | [Ingress](/docs/ingress) |
| Gateway API TLSRoute, TCPRoute, UDPRoute, and BackendTLSPolicy | not supported | — | The controller does not watch them; only HTTPRoute is served. GRPCRoute is not watched either. | [Gateway API](/docs/gateway-api#what-is-not-supported) |
| Hot binary upgrade | not supported | — | titanium update replaces the executable and the process restarts. Roll pods behind the readiness drain instead. | [Kubernetes](/docs/kubernetes#graceful-rollout) |

## Protocols

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| HTTP/1.1 and HTTP/2 | supported | `server.enableHttp2`, `listeners[].enableHttp2` | HTTP/2 is on by default. | [Protocol support](/docs/protocol-support) |
| HTTP/3 over QUIC | supported | `server.enableHttp3`, `listeners[].enableHttp3` | On when QUIC is available. The container image has no libmsquic. | [HTTP/3](/docs/http3) |
| Bridging between HTTP versions | supported | `server.originHttpVersionPolicy` | HTTP/1.1, HTTP/2, and HTTP/3 clients reach origins that speak another version. Not every bridge covers every feature. | [Protocol support](/docs/protocol-support#protocol-bridges) |
| WebSocket over HTTP/1.1 and HTTP/2 | supported | `server.enableRfc8441` | HTTP/1.1 upgrade always; RFC 8441 extended CONNECT when enabled. | [Protocol support](/docs/protocol-support#connections-and-framing-high-level) |
| gRPC-Web to native gRPC | supported | `routes[].grpcWeb` | Binary unary and server-streaming calls to an HTTP/2 origin. | [gRPC-Web](/docs/configuration#grpc-web-route-grpcweb) |
| gRPC and JSON transcoding | supported | `plus.options: grpc.transcode.enabled` | REST and JSON to gRPC through a descriptor set (Plus). | [gRPC-JSON transcoding](/docs/grpc-json-transcoding) |
| PROXY protocol from a load balancer | supported | `listeners[].acceptProxyProtocol`, `listeners[].trustedProxyProtocolSources` | Reads the preamble only from the sources you trust. | [Configuration](/docs/configuration#routes-clusters) |
| CONNECT-UDP relay (MASQUE) | supported | `listeners[].masque` | Opt-in listener type. It relays datagrams opaquely, with authentication, tunnel caps, and a private-target block. | [Listeners](/docs/configuration#listeners) |
| WebSocket over HTTP/3 | supported | `server.enableRfc8441` | RFC 9220. HTTP/3 clients reach HTTP/1.1, HTTP/2 and HTTP/3 origins. | [Protocol support](/docs/protocol-support) |
| Explicit-proxy h2c Upgrade | not supported | — | Upgrade: h2c is not handled. Cleartext HTTP/2 works with prior knowledge only. | [Protocol support](/docs/protocol-support) |
| Decrypting QUIC inside CONNECT-UDP | not supported | — | The relay forwards each datagram as it came, so the payload stays encrypted end to end. There is no switch to decrypt it. | [Listeners](/docs/configuration#listeners) |

## L4

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| TCP passthrough with SNI routing | supported | `listeners[].passthrough`, `listeners[].sniRoutes`, `listeners[].sniDefaultClusterId` | Reads the ClientHello, picks a cluster by server name, and forwards the bytes unchanged. Balancing, failover, and an idle timeout apply. | [TCP passthrough](/docs/configuration#tcp-passthrough-with-sni) |
| UDP relay | supported | `listeners[].type`, `listeners[].maxSessions`, `listeners[].maxDatagramsPerSecondPerClient` | One upstream per client endpoint, with session and rate caps. | [UDP listeners](/docs/configuration#udp-listeners) |
| PROXY protocol v2 to TCP origins | supported | `listeners[].sendProxyProtocol`, `clusters[].sendProxyProtocol` | A cluster setting replaces the listener setting. TCP passthrough only. | [PROXY protocol](/docs/configuration#proxy-protocol-to-tcp-origins-clusters-sendproxyprotocol) |
| SOCKS and transparent listeners | supported | `listeners[].type` | Explicit, transparent, SOCKS, QUIC, MASQUE, and UDP listener types. | [Listeners](/docs/configuration#listeners) |

## Extensibility

| Capability | Status | Config key | Notes | Docs |
|------------|--------|------------|-------|------|
| Per-route .NET plugins | supported | `plugins[].assembly`, `plugins[].failMode`, `plugins[].timeoutMs`, `routes[].middleware` | Each plugin loads into its own collectible context, behind a timeout and an exception wall. | [Plugins](/docs/plugins) |
| Embedding the engine in .NET | supported | `ProxyServer events` | The same engine with event handlers, synthetic responses, and stream hooks. | [Library](/docs/library) |
| Programmatic route and cluster swap | supported | `PUT /v1/snapshot` | Plus control-plane call. File reload needs no Plus. | [Graceful reload](/docs/configuration#graceful-reload) |
| Managed fleet control plane | supported | `plus.options: manager.url`, `titanium-manager` | titanium-manager publishes YAML revisions; Plus agents long-poll, ack or nack, and keep a last-known-good file. Not an xDS client. | [Manager](/docs/manager) |
| xDS control plane | not supported | — | There is no xDS client. Use titanium-manager (or the file, titanium reload, the Plus snapshot call, or the Kubernetes controllers) instead. | [Manager](/docs/manager) |
| Lua scripting | supported | `plugins[].kind`, `plugins[].file`, `plugins[].settings`, `plugins[].bodyMode`, `routes[].middleware` | Lua 5.4 in sandboxed states, named in a route middleware list. A file runs the request-headers and buffered request-body phases; every phase runs when a .NET host sets the script pipeline. Needs the lua54 native. | [Scripting](/docs/scripting#lua-plugins) |
| Lua sandbox, limits, and failure mode | supported | `plugins[].failMode`, `plugins[].timeoutMs`, `plugins[].limits.memoryMb`, `plugins[].limits.maxInstructions`, `plugins[].limits.epochDeadlineMs`, `plugins[].limits.poolSize` | No io, require, load, or bytecode. Memory, instruction, and CPU-time limits hold inside the engine even when the script catches errors. A failed call answers 503, or passes the request with failMode open. | [Scripting](/docs/scripting#lua-limits) |
| Lua libraries, shared dictionaries, and JWT verification | supported | `plugins[].settings`, `plugins[].limits.sharedDataBytes` | json, base64, hex, url, crypto, linear-time regex, cidr, uuid, timers, metrics, process-wide shared dictionaries with TTL and eviction, and JWKS-based jwt.verify. | [Scripting](/docs/scripting#libraries) |
| Lua HTTP callouts with an allow-list | supported | `plugins[].calloutHosts`, `plugins[].limits.calloutConcurrency`, `plugins[].limits.bufferedBodyBytes` | http.call reaches only the hosts you list. No system proxy, redirects, or cookies. Refusals and failures come back as nil and an error. | [Scripting](/docs/scripting#lua-callouts) |
| Lua L4 stream phases | supported | `listeners[].streamFilters` | on_connect, on_data, and on_close see raw TCP connections. Name the plugin in listeners[].streamFilters on a passthrough listener, or set StreamFilters from a .NET host. | [Scripting](/docs/scripting#l4-phases) |
| Script pipeline and chained scripts | supported | `scriptPipeline` | Every HTTP phase, upstream choice, and several Lua or Wasm hosts in order. Set scriptPipeline in twp.yaml, or ScriptPipeline from a .NET host. Route middleware still runs the request-headers phase alone. | [Scripting](/docs/scripting#several-scripts-on-one-pipeline) |
| WASM proxy-wasm modules | supported | `plugins[].kind`, `plugins[].file`, `plugins[].vmId`, `plugins[].settings`, `plugins[].bodyMode`, `routes[].middleware` | proxy-wasm ABI 0.1.0, 0.2.0, and 0.2.1 on Wasmtime, with the Envoy attribute set and a WASI subset. A module is built and started before it replaces the running one. Needs the wasmtime native. | [Scripting](/docs/scripting#webassembly-plugins) |
| WASM memory, fuel, and deadline limits | supported | `plugins[].failMode`, `plugins[].timeoutMs`, `plugins[].limits.memoryMb`, `plugins[].limits.fuel`, `plugins[].limits.epochDeadlineMs`, `plugins[].limits.poolSize` | A VM that traps is discarded and rebuilt off the request path. Signal-based traps are off, so Wasmtime does not fight the .NET runtime. | [Scripting](/docs/scripting#webassembly-limits) |
| WASM HTTP and gRPC callouts with an allow-list | supported | `plugins[].calloutHosts`, `plugins[].limits.calloutConcurrency`, `plugins[].limits.bufferedBodyBytes` | proxy_http_call, proxy_grpc_call, and gRPC streams reach only the hosts you list. A request paused on a callout resumes when the guest continues it. | [Scripting](/docs/scripting#host-functions) |
| WASM shared data, queues, ticks, and metrics | supported | `plugins[].vmId`, `plugins[].limits.sharedDataBytes`, `plugins[].limits.queueBytes` | Plugins with the same vmId and module share VMs, shared data with CAS, and queues. Guest metrics appear as three fixed instruments, so a guest cannot create new ones. | [Scripting](/docs/scripting#sharing-a-vm-with-vmid) |
| Scripting natives per platform | supported | `titanium scripting-deps status` | Lua and Wasmtime natives are pinned per runtime identifier and checked by SHA-256. A computer without one keeps running; only a route that names that script kind fails to load. win-arm64 has none. | [Scripting](/docs/scripting#per-rid-matrix) |
| OpenResty, Kong PDK, and APISIX Lua APIs | not supported | — | There is no ngx API, Kong PDK, APISIX plugin API, LuaJIT, FFI, cosocket, or require. Lua here is the 5.4 language with the libraries on the Scripting page. | [Scripting](/docs/scripting#parity-checklist) |

This table is written from `website/docs/.capabilities.yml`. A unit test checks that each config key exists in `twp.schema.json`, that each link target exists, and that this page carries every row.
