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
| `host` | string | Default `0.0.0.0` |
| `port` | int | Default `8000` |
| `decryptSsl` | bool | Terminate TLS / decrypt HTTPS (man-in-the-middle when used for MITM) |
| `type` | string? | `explicit`, `transparent`, `socks`, or `quic` (null uses ForwardHost heuristics) |
| `forwardHost` / `forwardPort` | string / int | Classic single-origin reverse (no route table) |
| `enableHttp2` | bool? | `false` forces H1 globally; null inherits `server` / proxy default |
| `enableHttp3` | bool? | Per-listener; any `false` disables global H3 |
| `maxCachedConnections` | int? | Per-endpoint pool depth |
| `maxConcurrentClients` | int? | Per-endpoint admission cap |
| `genericCertificateName` | string? | Fallback hostname when SNI is absent |
| `maxInboundBidirectionalStreams` / `maxInboundUnidirectionalStreams` | int? | QUIC stream caps |
| `handshakeTimeoutSeconds` / `idleTimeoutSeconds` | int? | QUIC timeouts |

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

Match fields typically include host, path (`Exact` / `Prefix` / `Template`), method, headers, and query. Cluster algorithms include RoundRobin, Random, LeastRequests, and LeastTime; destinations support weight and sticky cookie/header. When any cluster uses `LeastTime`, the CLI automatically enables request timing capture so latency EWMA can drive selection.

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
  minimumLevel: "Error"
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
  profile: PublicFacing   # Balanced | LegacyCompatible | PublicFacing
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
    maxConcurrentStreamsPerConnection: 256
    maxPeerInitiatedIncompleteStreamResets: 100
    maxOpenHeaderBlockFrames: 128
    maxOpenHeaderBlockDurationSeconds: 10
    connectionPoolingEnabled: true
    maxCachedConnectionsPerHost: 128
    maxOriginHttp2ConnectionsPerAuthority: 8
    maxCertificateCacheEntries: 1024
    maxCertificateDiskCacheEntries: null
    maxBufferedBodyBytes: 4194304
    maxDecodedHeaderListBytes: 65536
    maxWebSocketFramePayloadBytes: 16777216
  policyModes:
    bodyBudget: Enforce
    decompressionRatio: Enforce
    headerLimits: Enforce
    admissionControl: Enforce
    http2AbuseBudget: Enforce
    allowAmbiguousFraming: false
  tls:
    supportedSslProtocols: [Tls12, Tls13]
    supportedServerSslProtocols: [None]
  upstream:
    forwardToUpstreamGateway: false
    upstreamProxyConfigurationScript: null
    httpProxy:
      hostName: "proxy.example"
      port: 8080
      proxyType: Http
    httpsProxy: null
    upStreamEndPoint: null
    upStreamEndPointIPv4: null
    upStreamEndPointIPv6: null
  decryptSkipHosts: []      # present ⇒ Replace tunnel-only list (omit key to keep Merge factory defaults)
  decryptOnlyHosts: []      # when non-empty, only these hosts are decrypted (Replace with skip/only)
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

HTTP interception is not a YAML knob: the CLI turns it on automatically when the config needs the session path (transforms, static files, ACME, access logs, or gRPC-JSON). Request timing capture is not a YAML knob either: it is enabled automatically when any cluster uses `LeastTime` (and when access logs are enabled).

### Access log (`server.accessLog`)

Opt-in JSON Lines (NDJSON) access log written **after** each response. Bodies are never buffered for this feature.

```yaml
server:
  accessLog:
    path: "./logs/access.ndjson"
    sampleRate: 1.0   # 0.0–1.0; omit for 1.0
```

Each line includes `ts`, `method`, `url`, `host`, `status`, `durationMs`, and `clientIp`. Omit `accessLog` (or leave `path` empty) for zero cost on the hot path.

### Graceful reload

On Unix, send **SIGHUP** to a running `titanium run` process to reload routes and clusters from the same config file without stopping listeners or aborting in-flight requests. Windows service hosts should use a process restart or the Plus control-plane snapshot API instead.

### Code-only callbacks

These cannot be set from YAML; wire them in Library / embedder code:

- `ProxyBasicAuthenticateFunc`, `ProxySchemeAuthenticateFunc` (and related realm/schemes on `ProxyServer`)
- `WinAuthCredentialsProvider`
- `GetCustomUpStreamProxyFunc`, `CustomUpStreamProxyFailureFunc`
- `ShouldInterceptHttp`
- `BufferPool`, `Logging.LoggerFactory`, custom `CertificateStorage`
