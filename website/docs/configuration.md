# Configuration (`twp.yaml`)

Native schema version **7.1**. For CLI operators: start with a minimal reverse file, then add routes, certificates, or Plus as needed.

**Next:** copy the minimal reverse below → `titanium test -c twp.yaml` → `titanium run -c twp.yaml`.

## Minimal reverse

```yaml
schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 8000
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

```shell
titanium test -c twp.yaml
titanium run -c twp.yaml
```

`forwardHost` / `forwardPort` is the classic single-origin reverse (no route table). For path-based routing and load balancing, add `routes` and `clusters` below.

## Top-level shape

```yaml
schemaVersion: "7.1"
listeners: []
routes: []
clusters: []
staticFiles: null
plus: null
certificates: null
logging: null
server: null
```

Engine knobs live under `server:` ([reference](#server-reference)). Plus feature options stay under `plus:` / `plus.options` — Plus does **not** configure the engine.

## Listeners

| Field | Type | Notes |
|-------|------|-------|
| `host` | string | Bind address. Default `0.0.0.0` (all IPv4 interfaces). `*` is the same as `0.0.0.0`. Use `127.0.0.1` for localhost only. |
| `port` | int | Default `8000`. `0` is an OS-chosen (ephemeral) port. Inspector toolbar `*` / empty means the same. |
| `decryptSsl` | bool | Terminate TLS / decrypt HTTPS (man-in-the-middle when used for MITM) |
| `type` | string? | `explicit`, `transparent`, `socks`, `quic`, or `masque` (null uses ForwardHost heuristics). `masque` is an opt-in CONNECT-UDP relay; see the [MASQUE wiki page](https://github.com/justcoding121/titanium-web-proxy/wiki/MASQUE) |
| `forwardHost` / `forwardPort` | string / int | Classic single-origin reverse (no route table) |
| `enableHttp2` | bool? | `false` forces H1 globally; null inherits `server` / proxy default |
| `enableHttp3` | bool? | Per-listener; any `false` disables global H3 |
| `maxCachedConnections` | int? | Per-endpoint pool depth |
| `maxConcurrentClients` | int? | Per-endpoint admission cap |
| `genericCertificateName` | string? | Fallback hostname when SNI is absent |
| `maxInboundBidirectionalStreams` / `maxInboundUnidirectionalStreams` | int? | QUIC stream caps |
| `handshakeTimeoutSeconds` / `idleTimeoutSeconds` | int? | QUIC timeouts. On a `passthrough` listener, `idleTimeoutSeconds` closes a connection with no bytes in either direction for that long (`0`, the default, never) |
| `passthrough` | bool | Copy TCP bytes to the upstream without parsing. See [TCP passthrough with SNI](#tcp-passthrough-with-sni) |
| `sniRoutes` | list | Passthrough only. Each entry has `serverName` (exact or `*.example.com`) and `clusterId` |
| `sniDefaultClusterId` | string? | Passthrough only. Cluster for a client with no match, no server name, or no ClientHello |
| `sniReadTimeoutSeconds` | int? | Passthrough only. How long to wait for the ClientHello, 1 to 60. Default `5` |
| `sendProxyProtocol` | string? | Passthrough only. `none` (default) or `v2` |

### TCP passthrough with SNI

A passthrough listener copies bytes between the client and an upstream and never decrypts them. With `sniRoutes`, it first reads the client's TLS ClientHello, takes the server name from it, and sends the connection to a cluster. The ClientHello bytes are forwarded unchanged, so the origin completes the handshake and presents its own certificate.

```yaml
listeners:
  - port: 443
    type: transparent
    passthrough: true
    decryptSsl: false
    sniRoutes:
      - serverName: "api.example.com"
        clusterId: tcp-api
      - serverName: "*.example.com"
        clusterId: tcp-web
    sniDefaultClusterId: tcp-web
    sniReadTimeoutSeconds: 5
    idleTimeoutSeconds: 300
    sendProxyProtocol: v2
```

The clusters are ordinary clusters, defined as in [Routes & clusters](#routes-clusters):

```json
{
  "clusters": [
    {
      "id": "tcp-api",
      "algorithm": "LeastRequests",
      "destinations": [
        { "id": "api1", "address": "10.0.0.11", "port": 443 },
        { "id": "api2", "address": "10.0.0.12", "port": 443 }
      ]
    },
    {
      "id": "tcp-web",
      "destinations": [{ "id": "web1", "address": "10.0.0.21", "port": 443 }]
    }
  ]
}
```

- **Match order.** An exact name wins. Otherwise the longest matching `*.suffix` wins (`*.api.example.com` beats `*.example.com`, and `*.example.com` also matches `a.b.example.com` but not `example.com`). Then `sniDefaultClusterId`, then `forwardHost`. Names compare without regard to case. With none of those, the connection is closed.
- **No server name.** A client that connects by IP address, sends no `server_name`, speaks something other than TLS, or stays silent for `sniReadTimeoutSeconds` (a protocol where the server speaks first) is treated as having no name. Whatever it already sent is forwarded.
- **Balancing.** The cluster's algorithm, weights, priorities, caps, and passive health apply to connections the same way they do to requests. A connect that fails is tried against another destination, up to the smaller of 3 and the number of eligible destinations, and each failure counts toward passive ejection. Open connections count as active requests for `LeastRequests`.
- **Half-close.** When one side sends EOF, the proxy shuts down sending to the other side and lets the remaining direction finish for up to 30 seconds. A client that finishes its request and waits for the reply works.
- **`sendProxyProtocol: v2`.** Writes a PROXY protocol v2 header with the client and listener addresses to the upstream before any client bytes. The upstream must expect it. A cluster can set its own `sendProxyProtocol` (see below), which replaces the listener setting for connections routed to that cluster.
- **Validation.** Every `clusterId` must exist. `serverName` is an exact name or one leading `*.` label. At least one of `sniRoutes`, `sniDefaultClusterId`, or `forwardHost` is required on a passthrough listener, and `sniRoutes` requires `passthrough: true` and `decryptSsl: false`.
- **Metrics.** `twp.passthrough.connections{route,result}` counts finished connections. `route` is the matching `serverName` pattern, `default`, or `forward`. `result` is `ok`, `idle_timeout`, `grace_expired`, `connect_failed`, `no_destination`, `no_route`, `client_closed`, `cancelled`, or `error`. `twp.passthrough.bytes{direction}` counts bytes relayed (`in` is client to upstream, `out` is upstream to client).

Without `sniRoutes` and `sniDefaultClusterId`, a passthrough listener behaves as before: every connection goes to `forwardHost` and nothing is read ahead.

## Observability

Request instruments on the `Titanium.Web.Proxy` meter stay quiet until a listener is attached:

- `twp.proxy.requests` counts completed requests, tagged `route`, `cluster`, `status_class`, `protocol`.
- `twp.proxy.request.duration` records seconds, tagged `route` and `status_class`.
- `twp.proxy.requests.active` is the in-flight gauge.
- `twp.proxy.requests.failed` counts failures, tagged `error`.
- `twp.proxy.bytes` counts bytes, tagged `direction`.

`server.requestId.enabled` and `server.tracing.enabled` are off by default. Turning either on leaves the terminate-lite path.

### Prometheus metrics (`server.metrics`)

Opt-in text exposition of the core meter. Off by default: with no `server.metrics` (or `enabled: false`) no meter listener exists and the request path costs what it did before.

```yaml
server:
  metrics:
    enabled: true
    host: 127.0.0.1   # default
    port: 9464        # default
```

`GET /metrics` serves counters as `_total`, up-down counters as gauges, and the request duration as a histogram with 14 fixed buckets from 1 ms to 30 s. Labels are limited to `route`, `cluster`, `status_class`, `protocol`, `error`, `direction`, `reason`, and `outcome`; at most 200 distinct `route` and 200 distinct `cluster` values are kept before the rest count as `other`. The endpoint is unauthenticated, so keep it on loopback or a private interface. Changing it needs a restart. The full instrument list is in the [Metrics wiki page](https://github.com/justcoding121/titanium-web-proxy/wiki/Metrics). A .NET host that already uses OpenTelemetry can instead call `AddMeter("Titanium.Web.Proxy")`.

## Routes & clusters

```json
{
  "schemaVersion": "7.1",
  "listeners": [
    { "host": "127.0.0.1", "port": 8000, "decryptSsl": false }
  ],
  "routes": [
    {
      "id": "r1",
      "clusterId": "c1",
      "order": 1,
      "match": { "path": "/", "pathKind": "Prefix" }
    }
  ],
  "clusters": [
    {
      "id": "c1",
      "algorithm": "RoundRobin",
      "destinations": [
        { "id": "d1", "address": "127.0.0.1", "port": 8080 }
      ]
    }
  ]
}
```

Match fields typically include host, path (`Exact` / `Prefix` / `Template`), method, headers, and query. A lower `order` wins. Equal `order` is resolved by specificity (exact over segment prefix over prefix, then a longer path, then host, method, and header or query constraints) and then by file order. `segmentPrefix` does not match a longer segment (`/api` does not match `/apiary`). `regex` paths are compiled once. Host lists accept `*.example.com`. Matching uses the path the origin sees: dot segments are removed and unreserved percent-escapes are decoded; other escapes stay encoded. Cluster algorithms include RoundRobin, Random, LeastRequests, and LeastTime; destinations support weight and sticky cookie/header. Destination ids must be unique across clusters. When a route matches and no destination is eligible, the proxy answers 503 with `Retry-After: 1` instead of forwarding to the request host. Passive health, when left unset, treats 502, 503, and 504 as destination failures and treats other statuses, including 500, as success. After `reactivateAfterSeconds` an ejected destination receives one trial; a successful trial restores it. `healthCheck.passive.maxEjectionPercent` stops ejecting once that share of the cluster is already out. `healthCheck.active` probes on a background timer and only restores destinations that probe marked unhealthy. `retry.attempts` fails a connect over to the next destination for idempotent methods; set `retryNonIdempotent` to allow a buffered POST. `maxConcurrentRequests`, `priority`, and `backup` limit and order the pool. A destination or cluster `slowStartSeconds` ramps weight from 10 percent. `queue` waits briefly when every destination is at its cap. Dispose `ClusterManager` on shutdown so active probes stop. These knobs leave the single-destination fast path unchanged while they are unset. `server.forwardedHeaders` is off until an action other than Off is set. A peer outside `trustedProxies` cannot append to an inbound X-Forwarded-* value. Setting any forwarded action leaves terminate-lite. A route `policies` list can require a header, check an API key, cap the request body, answer a CORS preflight, or apply a rate, connection, or address limit before a destination is chosen. JWT and distributed rate limits stay in Titanium Plus. An empty policy list leaves terminate-lite alone. An `extAuth` policy asks an external service whether to proceed. It sends a bodyless subrequest to `url` (`method` defaults to GET) that carries the `sendHeaders` (default Authorization and Cookie) plus `X-Original-Method`, `X-Original-Uri`, `X-Original-Host`, and `X-Forwarded-For`. Any 2xx lets the request through, and the headers named in `copyResponseHeaders` replace the same-named client headers before the origin sees them. Any other status is answered to the client with that status, a body of at most 64 KiB, and only the `denyResponseHeaders` (default Location, Set-Cookie, WWW-Authenticate, Content-Type). Redirects are relayed, never followed. A timeout (`timeoutMilliseconds`, default 2000, 1 to 60000), a connection error, or a 5xx answers 503 unless `failOpen` is true, in which case the request proceeds and `twp.extauth.requests` counts `error_open`. `cacheSeconds` (default 0) keeps a 2xx verdict for the same method, host, path, and `sendHeaders` values, bounded by `cacheMaxEntries` (default 10000, oldest evicted first). A subrequest carrying `X-Titanium-ExtAuth: 1` is refused with 508 so two proxies cannot loop. A route that lists an `extAuth` policy leaves terminate-lite; routes without one are unchanged. Each request that misses the cache waits for one extra round trip to the service, so keep it on the same host or network and use `cacheSeconds` where a short-lived verdict is acceptable. The `twp-cli-extauth-http1` arm of the load probe measures that cost against an allowing service; re-measure on your own hardware before relying on a number. A route `clusters` list splits traffic by weight. `redirect.httpToHttps` upgrades a cleartext redirect. `errorPages` replaces the body of a generated status. `sessionAffinity` pins an opaque cookie. `hashKey` feeds consistent hashing. Listener `passthrough` copies TCP bytes, and `acceptProxyProtocol` reads a PROXY preamble. When any cluster uses `LeastTime`, the CLI automatically enables request timing capture so latency EWMA can drive selection.

`retry` also replays a gateway answer onto another destination before anything reaches the client. `attempts` bounds the replays, `statusCodes` replaces the default 502, 503, and 504 (429 and 500-599 are accepted), and `perTryTimeoutMilliseconds` ends a slow try as a 504 on every try but the last. `backoffMilliseconds` doubles per replay up to `maxBackoffMilliseconds`, with `jitterPercent` added. `honorRetryAfter` takes a delta-seconds `Retry-After` instead, capped the same way. `budgetPercent` limits replays in flight for a cluster to that share of its in-flight requests, never below `budgetMinConcurrent`, and refused replays count in `twp.proxy.status.retries_refused`. HTTP/2 origins also replay a request whose body was fully buffered. See [Protocol support](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support) for the paths that retry.

A `rateLimit` policy counts requests per key and answers 429 with `Retry-After` (whole seconds until the window ends or a token is back) when the limit is spent. `key` picks what is counted: `client` (default, the socket address), `forwardedClient` (the rightmost `X-Forwarded-For` entry that is not in `server.forwardedHeaders.trustedProxies`, used only when the peer itself is trusted, otherwise the socket address), `header:<Name>`, `cookie:<Name>`, or `route`. A header or cookie value is stored as a 16-byte SHA-256 prefix, and a request without it is counted by client address. `algorithm` is `fixedWindow` (default, `limit` requests per `windowSeconds`) or `tokenBucket` (refills `limit` tokens per `windowSeconds` up to `burst`, which defaults to `limit`). `maxKeys` (1000 to 10000000, default 100000) bounds the counters kept per policy: when full, expired counters are dropped, and any further new key shares one overflow counter, so a client that rotates header values cannot grow memory or dodge the limit. `emitHeaders` (default false) adds `RateLimit-Limit`, `RateLimit-Remaining`, and `RateLimit-Reset` to the 429. With the default keys and algorithm, counting is unchanged. Rate limits stay in process memory per proxy instance.

A route `mirror` sends a copy of matching requests to another cluster and discards the answer. `samplePercent` (0-100, default 100) picks the share, `maxBodyBytes` (default 64 KiB, at most 1 MiB) is the largest body copied and larger bodies are skipped, and `maxConcurrency` (1-16, default 2) bounds the sends in flight. Each mirror route has its own queue and workers, so a slow mirror cannot starve another route. A request line or header that contains a control character is not mirrored. `server.forwardedHeaders.trustedProxies` takes addresses and CIDR prefixes for IPv4 and IPv6; an entry that does not parse fails validation, and an IPv4-mapped client address is written as IPv4. Active health probes run concurrently, probe unix-socket destinations by connecting, and bracket IPv6 hosts.

Optional `limits` on a route overrides that session only: `requestTimeoutSeconds`, `idleTimeoutSeconds`, `maxBufferedBodyBytes`, `maxWebSocketFramePayloadBytes`. Omit it and the server limits apply. Full key list: [Limits and escape hatches](/docs/limits).

### Upstream TLS

Optional `clusters[].httpClient` settings control how the proxy authenticates to a cluster's HTTPS destinations. Every field is off by default. A cluster that sets none of them keeps the machine trust store, the origin host as the TLS name, and the shared connection pool.

```json
{
  "schemaVersion": "7.1",
  "clusters": [
    {
      "id": "payments",
      "destinations": [
        { "id": "p1", "address": "10.0.0.5", "port": 8443, "useHttps": true }
      ],
      "httpClient": {
        "clientCertificatePath": "/etc/titanium/payments-client.crt",
        "clientCertificateKeyPath": "/etc/titanium/payments-client.key",
        "caBundlePath": "/etc/titanium/payments-ca.pem",
        "serverName": "payments.internal",
        "verifyHostname": true
      }
    }
  ]
}
```

Keep `schemaVersion` (or another native section) in the JSON file. A bare listeners, routes, and clusters document is read by the reverse-proxy subset, which has no `httpClient`.

| Key | Effect |
|-----|--------|
| `clientCertificatePath`, `clientCertificateKeyPath` | Client certificate for mutual TLS. Give a PEM certificate and its PEM key together. For a PFX or PKCS#12 file, set only `clientCertificatePath`. |
| `clientCertificatePasswordEnv` | Name of the environment variable that holds the PFX password, or the password of an encrypted PEM key. The password never appears in the file. |
| `caBundlePath` | PEM or CRT trust anchors for this cluster. They replace the machine store, so a public CA is not trusted unless the bundle lists it. |
| `serverName` | Sent as TLS SNI and checked against the origin certificate. The HTTP `Host` header is not changed. |
| `verifyHostname` | Default `true`. `false` tolerates only a certificate name mismatch. Chain, expiry, and revocation errors still fail. |
| `acceptAnyServerCertificate` | Skips certificate validation for this cluster on HTTP/1.1, HTTP/2, and HTTP/3. |

The files are read once when the configuration loads or reloads, never per request, and their content is part of the connection pool key, so two clusters with different identities never share a socket. Rotating a file takes effect on the next `titanium reload`. A missing, unreadable, or unparsable file is a validation error at load time; if it breaks later the cluster answers 502 and logs the cause once. It never falls back to the machine store or an anonymous handshake. Active health probes to an HTTPS destination use the same certificate, CA bundle, and server name. A route `mirror` ignores these settings and uses the machine trust store, so a mirror cluster that needs a private CA fails its best-effort copy.

### Route transforms

Optional `transforms` on a route rewrite the upstream request (and can stage response header changes). Empty/absent transforms keep the reverse fast path. Supported kinds:

| Kind | Parameters | Effect |
|------|------------|--------|
| `PathRemovePrefix` | `prefix` | Strip a path prefix |
| `PathPrefix` | `prefix` | Prepend a path prefix |
| `QueryValueSet` | `name`, `value` | Set or replace a query parameter |
| `RequestHeaderSet` | `name`, `value` | Set a request header |
| `RequestHeaderRemove` | `name` | Remove a request header |
| `ResponseHeaderSet` | `name`, `value` | Set a response header after the origin responds |
| `ResponseHeaderRemove` | `name` | Remove a response header after the origin responds |

```json
"transforms": [
  { "kind": "PathPrefix", "parameters": { "prefix": "/gw" } },
  { "kind": "QueryValueSet", "parameters": { "name": "env", "value": "lab" } },
  { "kind": "RequestHeaderSet", "parameters": { "name": "X-Edge", "value": "1" } }
]
```

### gRPC-Web (route `grpcWeb`)

Off by default. A route with `grpcWeb: true` lets a browser gRPC-Web client reach a native gRPC (HTTP/2) origin:

```yaml
routes:
  - id: api
    clusterId: grpc-backend
    grpcWeb: true
```

On a matching `POST` the proxy rewrites `application/grpc-web` or `application/grpc-web+proto` to `application/grpc` and adds `te: trailers`. It answers with the client's own media type, relays each length-prefixed message as the origin sends it, and appends the origin trailers (`grpc-status`, `grpc-message`, and any others) as one final frame whose flag byte is `0x80`. A trailers-only reply (status in the headers, no body) becomes that same final frame. An origin reply that is not `application/grpc` (a 404 page, say) is left alone.

v1 limits:

- Unary and server-streaming calls, binary only. `application/grpc-web-text` is not translated and passes through unchanged. `titanium validate` warns when a route sets `grpcWeb`.
- The origin must speak HTTP/2. The route is served on the HTTP/2-to-HTTP/2 dispatch path. A route with `grpcWeb` is never treated as forward-host equivalent, so the native frame relay is not used for it. The flag cannot be combined with `redirect` or `directResponse`.
- An HTTP/1.1 browser connection reaches an HTTP/2 origin only where the HTTP/1.1-to-HTTP/2 bridge is enabled (the library's `UpstreamHttpProtocol` and `AllowHttpProtocolTranslation`, set in `BeforeSslAuthenticate`). `titanium run` does not set that for you.
- The response is rebuilt as one body for a known-length reply and streamed for a streaming reply. Trailers reach the browser only as that final frame; the trailers are also copied into response headers so a client that reads headers can see `grpc-status`.

**CORS.** Browsers send a preflight `OPTIONS` before a gRPC-Web call. Answer it with a `cors` policy attached to the route, and list the gRPC-Web request headers in `headerName`:

```yaml
policies:
  - name: grpc-web-cors
    kind: cors
    origins: https://app.example.com
    methods: POST, OPTIONS
    headerName: content-type, x-grpc-web, x-user-agent, grpc-timeout
routes:
  - id: api
    clusterId: grpc-backend
    grpcWeb: true
    policies: [grpc-web-cors]
```

The policy answers preflights only. For the real response, expose the status headers with a `ResponseHeaderSet` transform (`Access-Control-Allow-Origin`, and `Access-Control-Expose-Headers: grpc-status, grpc-message`).

## Static files

```yaml
staticFiles:
  root: "./www"
  enableGzip: true
  enableBrotli: false
```

## Certificates / ACME

Automatic Certificate Management Environment (**ACME**) can obtain Let’s Encrypt (or other directory) certificates for public listeners:

```yaml
certificates:
  certificatePath: "./certs/fullchain.pem"
  privateKeyPath: "./certs/privkey.pem"
  acmeEmail: "ops@example.com"
  acmeDomain: "app.example.com"
  acmeDirectory: "https://acme-v02.api.letsencrypt.org/directory"
```

Use real paths and emails in your environment. Do not commit private keys. MITM engine knobs are under `server.certificateManager`; this section is for listener leaf PEM/PFX and ACME.

## Logging

```yaml
logging:
  enabled: true
  minimumLevel: "Error"   # Error shows limit rejections. Use Debug, or titanium run -v, for Observe lines.
  enableConsole: true
  enableConsoleColors: true
  enableFile: false
  filePath: null
  maxFileSizeBytes: null
  maxRolledFiles: null
  queueCapacity: null
```

## Plus

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

Common `plus.options` keys (string values): see the table on [Plus](/docs/plus) (`discovery.*`, `security.*`, `waf.*`, `state.*`, `resilience.*`, `cache.enable`, `grpc.transcode.*`). Enabling `grpc.transcode.enabled` forces the HTTP session interception path — see [gRPC-JSON transcoding](/docs/grpc-json-transcoding). Engine settings belong in `server:`, not `plus.options`.

## Validate

```shell
titanium test -c twp.yaml
```

## Server reference

Null nested objects and null properties leave the library or profile default. Apply order: `profile` first, then overlays.

```yaml
server:
  profile: Balanced   # Balanced | LegacyCompatible | PublicFacing. PublicFacing sets streams to 256, idle 60s, request 120s.
  enableHttp2: true
  enableHttp3: null
  enableRfc8441: false
  enableQpackDynamicTable: false
  enableHttpsSvcbDnsDiscovery: null
  enable100ContinueBehaviour: false
  compatibilityMode100Continue: false
  enableWinAuth: false
  originHttpVersionPolicy: PreserveClientVersion  # or NormalizeToHttp11
  viaHeaderPseudonym: "titanium-web-proxy"
  blockPrivateNetworkDestinations: false
  blockLoopbackDestinations: false
  checkCertificateRevocation: NoCheck
  dnsServerEndPoint: "8.8.8.8:53"
  accessLog:
    path: "logs/access.ndjson"   # omit or null = off (zero cost)
    sampleRate: 1.0              # 0.0–1.0
  timeouts:
    connectionTimeOutSeconds: 60
    connectTimeOutSeconds: 20
    clientHeaderTimeoutSeconds: 0
    responseHeaderTimeoutSeconds: 0
    idleReadTimeoutSeconds: 0
    idleWriteTimeoutSeconds: 0
    requestTimeoutSeconds: 0
    networkFailureRetryAttempts: 1
  pooling:
    enableConnectionPool: true
    enableTcpServerConnectionPrefetch: true
    enableIpv6UnreachableSoftSkip: true
    maxCachedConnections: 128
    maxConcurrentHttp11HttpsOriginCreates: null
    maxConcurrentClientConnections: null
    noDelay: true
    enableTcpKeepAlive: true
    tcpTimeWaitSeconds: 0
    listenerBackLog: 1024
    reuseSocket: true
    threadPoolWorkerThread: null
  limits:
    maxHeaderLineBytes: 65536
    maxHeaderCount: 256
    maxHeaderAggregateBytes: 262144
    maxEncodedBodyBytes: null
    maxDecodedBodyBytes: null
    maxDecompressionRatio: 200
    maxConcurrentClients: null
    maxConcurrentStreamsPerConnection: 1000   # PublicFacing profile uses 256
    maxPeerInitiatedIncompleteStreamResets: 100
    maxOpenHeaderBlockFrames: 128
    maxOpenHeaderBlockDurationSeconds: 10
    connectionPoolingEnabled: true
    maxCachedConnectionsPerHost: 128
    maxOriginHttp2ConnectionsPerAuthority: 1
    maxCertificateCacheEntries: 1024
    maxCertificateDiskCacheEntries: null
    maxBufferedBodyBytes: 4194304
    maxDecodedHeaderListBytes: 65536
    maxWebSocketFramePayloadBytes: 16777216
    maxHttp3FramePayloadBytes: 4194304
    maxDeferredOutboundBytesPerStream: 4210688
    maxTrailerHeaderCount: 100
    maxTrailerHeaderBlockBytes: 16384
    maxHttp2CompressedHeaderBlockBytes: 262144
    maxInterimResponses: 20
    maxAuthChallengeRounds: 3
    maxUpstreamProxyAuthenticationAttempts: 5
    maxWinAuthTokenBytes: 12288
    http2WindowUpdateTimeoutSeconds: 60
  policyModes:
    bodyBudget: Enforce
    decompressionRatio: Enforce
    headerLimits: Enforce
    admissionControl: Enforce
    http2AbuseBudget: Enforce
    webSocketFrameBudget: Enforce
    http1ReplaySafety: Observe   # PublicFacing keeps Enforce unless you set this
    allowAmbiguousFraming: false
  tls:
    supportedSslProtocols: [Tls12, Tls13]
    supportedServerSslProtocols: [None]
    minVersion: null          # Tls12 or Tls13, listeners only (see below)
    cipherSuites: null        # TlsCipherSuite names, Linux and macOS only
    ocspStapling: false       # let the server certificate context fetch an OCSP response
  upstream:
    forwardToUpstreamGateway: false
    upstreamProxyConfigurationScript: null
    httpProxy:
      hostName: "proxy.example"
      port: 8080
      proxyType: Http
      useDefaultCredentials: false
      bypassLocalhost: false
      proxyDnsRequests: false
      nextHop: null
    httpsProxy: null
    upStreamEndPoint: null
    upStreamEndPointIPv4: null
    upStreamEndPointIPv6: null
  decryptSkipHosts: []      # present ⇒ Replace tunnel-only list (omit key to keep Merge factory defaults)
  decryptOnlyHosts: []      # when non-empty, only these hosts are decrypted (Replace with skip/only)
  # enableDecryptFailureBypass, decryptFailureBypassTtlMinutes, decryptFailureBypassMaxEntries,
  # decryptFailureBypassThreshold, and proxyAuthenticationRealm are server keys. See Limits.
  systemProxyBypassHosts: null  # present ⇒ Replace OS bypass (omit ⇒ Merge identity defaults); SSO risk if removed
  proxyLoopback: true       # localhost via proxy when building SystemProxySettings from config
  certificateManager:
    certificateEngine: BouncyCastleFast
    leafCertificateKeyAlgorithm: EcdsaP256
    pfxFilePath: null
    pfxPassword: null
    overwritePfxFile: true
    certificateValidDays: 396
    certificateGraceDays: 2
    certificateCacheTimeOutMinutes: 60
    rootCertificateName: null
    rootCertificateIssuerName: null
    saveFakeCertificates: true
    disableWildCardCertificates: false
  accessLog: null   # opt-in NDJSON access log (see below)
```

Listener-level `enableHttp2: false` still forces HTTP/2 off after `server.enableHttp2`. Listener-level `enableHttp3: false` (or `server.enableHttp3: false`) disables HTTP/3.

HTTP interception is not a YAML knob: the CLI turns it on automatically when the config needs the session path (transforms, static files, ACME, access logs, response compression, or gRPC-JSON). Request timing capture is not a YAML knob either: it is enabled automatically when any cluster uses `LeastTime` (and when access logs are enabled).

### Listener TLS policy (`server.tls`)

These keys shape the handshake the proxy completes with its own clients on terminating listeners (explicit, transparent, and MASQUE). They never change origin-facing handshakes, and each one is off by default.

| Key | Effect |
|-----|--------|
| `minVersion` | `Tls12` or `Tls13` (`1.2`, `1.3`, `TLSv1.2`, and `TLSv1.3` also parse). Trims `supportedSslProtocols` for listeners and never widens it. A client that offers only older versions is refused. A `minVersion` that excludes every entry of `supportedSslProtocols` fails validation. |
| `cipherSuites` | `TlsCipherSuite` names such as `TLS_AES_256_GCM_SHA384`. Linux and macOS only: .NET has no cipher suite policy on Windows, where the list is ignored, a validation warning is printed, and the OS default applies. Leaving out every TLS 1.3 suite turns TLS 1.3 off for listeners. |
| `ocspStapling` | `true` builds server certificate contexts with `offline: false`, so .NET may fetch an OCSP response and staple it. The default builds them offline: no network use and no stapling. Only a certificate whose authority information access extension names an OCSP responder can be stapled, and the certificates the proxy mints per host have none, so this matters for a certificate you supply. Chain building may touch the network the first time a certificate is presented. |

```yaml
server:
  tls:
    minVersion: Tls13
    cipherSuites: [TLS_AES_256_GCM_SHA384, TLS_AES_128_GCM_SHA256]
    ocspStapling: true
```

### PROXY protocol to TCP origins (`clusters[].sendProxyProtocol`)

`none` or `v2`. A cluster that sets it replaces the passthrough listener's `sendProxyProtocol` for connections routed to that cluster: `v2` writes a PROXY protocol v2 header with the client and listener addresses before any client bytes, and `none` suppresses a header the listener would send. Leave it unset to inherit the listener. The setting applies to TCP passthrough only. Pooled HTTP origin connections are shared between clients and never carry a header, so validation warns when a cluster sets `v2` but no passthrough listener routes to it. A TCP active health probe to a `v2` cluster sends a v2 LOCAL header so an origin that insists on the preamble does not reject the probe.

```json
{
  "schemaVersion": "7.1",
  "clusters": [
    {
      "id": "tcp-web",
      "sendProxyProtocol": "v2",
      "destinations": [{ "id": "w1", "address": "10.0.0.7", "port": 8443 }]
    }
  ]
}
```

### Access log (`server.accessLog`)

Opt-in JSON Lines (NDJSON) access log written **after** each response. Bodies are never buffered for this feature.

```yaml
server:
  accessLog:
    path: "./logs/access.ndjson"
    sampleRate: 1.0   # 0.0–1.0; omit for 1.0
```

Each line includes `ts`, `method`, `url`, `host`, `status`, `durationMs`, and `clientIp`. Omit `accessLog` (or leave `path` empty) for zero cost on the hot path.

### Response compression (`server.responseCompression`)

Opt-in, buffered compression of origin responses. Off by default and costs one null check when absent. Enabling it moves traffic onto the session path (the H1 terminate-lite shortcut is skipped).

```yaml
server:
  responseCompression:
    enabled: true
    algorithms: [br, gzip]   # default: both; brotli preferred when the client accepts it
    minBytes: 1024           # default 1024
    maxBytes: 1048576        # default 1 MiB, upper limit 1 MiB
    level: fastest           # fastest | optimal | smallestSize
    mimeTypes:               # default: text/*, application/json, application/javascript, application/xml, image/svg+xml
      - text/*
      - application/json
```

A response is compressed only when **all** of these hold:

- `Accept-Encoding` allows the algorithm (an explicit coding beats `*`; `q=0` forbids it).
- Status is `200` (not `206`).
- `Content-Length` is known and between `minBytes` and `maxBytes`.
- The response has no `Content-Encoding` and no `Cache-Control: no-transform`.
- The request is not `HEAD`, has no `Range`, and is not a WebSocket upgrade.
- The `Content-Type` matches `mimeTypes`. `text/event-stream` and `application/grpc*` are never compressed.

When it compresses, the proxy sets `Content-Encoding`, replaces `Content-Length`, adds `Vary: Accept-Encoding`, and weakens a strong `ETag` (`W/"…"`). A body that would not get smaller is sent unchanged.

A route opts out with `compression: off`:

```yaml
routes:
  - id: downloads
    clusterId: files
    compression: off
```

v1 buffers the body, so it applies to HTTP/1.1 responses and to the HTTP/2-to-HTTP/1.1 bridge only. It is not applied on HTTP/2-to-HTTP/2, HTTP/3, or HTTP/1.1-to-HTTP/2 paths; `titanium validate` warns about that. Streaming compression is not part of v1.

### Graceful reload

Change routes and backends without dropping connections:

```shell
titanium reload -c twp.yaml
```

| What | Live reload? |
|------|----------------|
| Routes, transforms, clusters / destinations, LB algorithm | Yes |
| `server:` timeouts, pooling, protocol flags, decrypt skip hosts | Yes |
| Listeners (bind / port / type) | No — restart |
| Certificates / ACME | No — restart |
| Plus plugins (WAF, JWT, gRPC-JSON, discovery options) | No — restart |
| Static files, access log path | No — restart |

| How | Notes |
|-----|--------|
| `titanium reload -c <config>` | All OS (Windows named event; Unix SIGHUP) |
| Unix **SIGHUP** / systemd `systemctl reload` | Unit includes `ExecReload=/bin/kill -HUP $MAINPID` |
| `titanium run --watch` | Debounced file watch (optional) |
| Plus `PUT /v1/snapshot` | Programmatic route/cluster swap without editing the file |

Invalid reloads keep the previous routes and clusters; the process stays up. Unlike a typical YARP process recycle, Titanium keeps listeners bound and in-flight sessions on the old cluster snapshot until they finish.

### Code-only callbacks

These cannot be set from YAML; wire them in Library / C# code:

- `ProxyBasicAuthenticateFunc`, `ProxySchemeAuthenticateFunc` (and related realm/schemes on `ProxyServer`)
- `WinAuthCredentialsProvider`
- `GetCustomUpStreamProxyFunc`, `CustomUpStreamProxyFailureFunc`
- `ShouldInterceptHttp`
- `BufferPool`, `Logging.LoggerFactory`, custom `CertificateStorage`

## See also

- [Limits and escape hatches](/docs/limits)
- [Why did my request fail](/docs/troubleshooting)
- [CLI](/docs/cli)
