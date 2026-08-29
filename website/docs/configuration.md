# Configuration (`twp.yaml`)

Native schema version **7.1**. Root document maps to the configuration models in `Titanium.Web.Proxy.Configuration`.

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

Engine knobs live under `server:` (this document). Plus feature options stay under `plus:` / `plus.options` — Plus does **not** configure `ProxyServer`.

## Listeners

| Field | Type | Notes |
|-------|------|-------|
| `host` | string | Default `0.0.0.0` |
| `port` | int | Default `8000` |
| `decryptSsl` | bool | HTTPS MITM / TLS terminate |
| `type` | string? | `explicit`, `transparent`, `socks`, or `quic` (null uses ForwardHost heuristics) |
| `forwardHost` / `forwardPort` | string / int | Classic single-origin reverse (no route table) |
| `enableHttp2` | bool? | `false` forces H1 globally; null inherits `server` / proxy default |
| `enableHttp3` | bool? | Per-listener; any `false` disables global H3 |
| `maxCachedConnections` | int? | Per-endpoint pool depth |
| `maxConcurrentClients` | int? | Per-endpoint admission cap |
| `enableHttpInterception` | bool? | Override server intercept gate |
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

Match fields typically include host, path (`Exact` / `Prefix` / `Template`), method, headers, and query. Cluster algorithms include RoundRobin, Random, LeastRequests, and LeastTime; destinations support weight and sticky cookie/header.

## Server (`ProxyServer` knobs)

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
  enableRequestTimingCapture: false
  enableHttpInterception: false
  originHttpVersionPolicy: PreserveClientVersion  # or NormalizeToHttp11
  viaHeaderPseudonym: "titanium-web-proxy"
  blockPrivateNetworkDestinations: false
  checkCertificateRevocation: NoCheck
  dnsServerEndPoint: "8.8.8.8:53"
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
  auth:
    proxyAuthenticationRealm: TitaniumProxy
    proxyAuthenticationSchemes: []
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
```

Listener-level `enableHttp2: false` still forces HTTP/2 off after `server.enableHttp2`. Listener-level `enableHttp3: false` (or `server.enableHttp3: false`) disables HTTP/3.

### Code-only callbacks

These cannot be set from YAML; wire them in library / embedder code:

- `ProxyBasicAuthenticateFunc`, `ProxySchemeAuthenticateFunc`
- `WinAuthCredentialsProvider`
- `GetCustomUpStreamProxyFunc`, `CustomUpStreamProxyFailureFunc`
- `ShouldInterceptHttp`
- `BufferPool`, `Logging.LoggerFactory`, custom `CertificateStorage`

## Static files

```yaml
staticFiles:
  root: "./www"
  enableGzip: true
  enableBrotli: false
```

## Certificates / ACME

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

Common `plus.options` keys (string values): discovery, security, WAF, rate-limit state (`state.mode=memory` or `state.redis`), resilience probes, and cache. See [Plus](/docs/plus). Engine settings belong in `server:`, not `plus.options`.

## Validate

```shell
titanium test -c twp.yaml
```
