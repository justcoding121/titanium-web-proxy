# Configuration (`twp.yaml`)

Native schema version **7.0**. Root document maps to the configuration models in `Titanium.Web.Proxy.Configuration`.

## Top-level shape

```yaml
schemaVersion: "7.0"
listeners: []
routes: []
clusters: []
staticFiles: null
plus: null
certificates: null
logging: null
```

## Listeners

| Field | Type | Notes |
|-------|------|-------|
| `host` | string | Default `0.0.0.0` |
| `port` | int | Default `8000` |
| `decryptSsl` | bool | HTTPS MITM / TLS terminate |
| `forwardHost` / `forwardPort` | string / int | Classic single-origin reverse (no route table) |
| `enableHttp2` | bool? | `false` forces H1; null inherits proxy default (H2 on) |
| `enableHttp3` | bool | Opt-in HTTP/3 on transparent endpoints |

## Routes & clusters

```json
{
  "schemaVersion": "7.0",
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

Use real paths and emails in your environment. Do not commit private keys.

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

Common `plus.options` keys (string values): discovery, security, WAF, Redis rate-limit state, resilience probes, and cache. See [Plus](/docs/plus).

## Validate

```shell
titanium test -c twp.yaml
```
