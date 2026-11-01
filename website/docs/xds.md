# xDS (Envoy ADS client)

> CLI-only, lazily loaded. Configure an `xds:` section to pull listeners, routes, clusters, endpoints, and secrets from an Envoy-compatible control plane over **ADS**.

Titanium ships **no xDS server**. This is a dataplane client that translates a supported Envoy v3 subset into native `twp.yaml` shape and applies it through the same validator and atomic reload path as file config.

## Configuration

```yaml
schemaVersion: "7.1"
xds:
  server: "127.0.0.1:18000"
  node:
    id: "gateway-1"
    cluster: "edge"
    locality:
      zone: "us-east-1a"
  failOpen: true
  initialFetchTimeoutSeconds: 15
server:
  reloadListeners: true   # forced on when xds: is set
listeners: []             # replaced after first accepted xDS snapshot
routes: []
clusters: []
```

| Key | Meaning |
|-----|---------|
| `xds.server` | ADS gRPC endpoint (`host:port` or `http://…`) |
| `xds.tls.*` | Optional mTLS to the control plane (CA + client cert) |
| `xds.node.*` | Envoy `Node` identity sent on discovery requests |
| `xds.failOpen` | When true, start from file config if the plane is down at boot |
| `server.reloadListeners` | Reconcile listeners on reload (`AddEndPoint` / `RemoveEndPoint`); default **false** for file-only |

## ADS behavior

- One bidirectional **StreamAggregatedResources** session.
- State-of-the-world responses per type; **ACK** (new version + nonce) or **NACK** (`error_detail`, prior version kept).
- Apply order: **CDS → EDS → SDS → RDS → LDS** with a short settle window before apply.
- Unsupported fields → **NACK** with resource name and field path; last good config keeps serving.

## Supported fields (v1)

See `src/Titanium.Xds/FEASIBILITY.md` in the repository for the authoritative table. Highlights:

- **Clusters:** `STATIC`, `STRICT_DNS`, `EDS`; `ROUND_ROBIN`, `LEAST_REQUEST`, `RANDOM`; connect timeout.
- **Endpoints:** IP, port, weight, priority, zone.
- **Routes:** prefix/path, `route.cluster`, `direct_response`.
- **Secrets:** TLS cert/key and validation context PEM written to the state directory.
- **Listeners:** one filter chain; HTTP connection manager (RDS name) or `tcp_proxy`; `tls_inspector`, `proxy_protocol`.

## Listener reconciliation

When `server.reloadListeners` is true (required for xDS), reload:

1. Plans add/remove/replace by listener key `(type, host, port)`.
2. Tears down removals and replaces first (so the same host:port can rebind), with a short drain.
3. Binds adds and replaces next.
4. Rolls back newly added endpoints if a later bind fails.

With `reloadListeners: false`, file reload **warns** and leaves listeners unchanged if the listener set would change.

## Migration from titanium-manager

Manager pushes **YAML revisions** over long-poll; xDS pushes **protobuf resources** over ADS. You can run file bootstrap + `failOpen: true`, or replace bootstrap listeners/routes/clusters once the first snapshot is accepted.
