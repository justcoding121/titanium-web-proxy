# xDS (Envoy ADS client)

> CLI-only, lazily loaded. Configure an `xds:` section to pull listeners, routes, clusters, endpoints, and secrets from an Envoy-compatible control plane over **ADS**.

Titanium ships **no xDS server**. This is a dataplane client that translates a supported Envoy v3 subset into native `twp.yaml` shape and applies it through the same validator and atomic reload path as file config.

## Configuration

```yaml twp
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
2. Binds new listeners first.
3. Replaces changed listeners (the old endpoint comes down, then the new one binds on the same host:port).
4. Removes listeners that are gone, last.
5. If any bind fails, everything done in that pass is undone: new endpoints are removed and replaced listeners are rebound from their previous config, so a failed apply never leaves fewer listeners than before.

Clusters and routes are applied before listeners (CDS, EDS, RDS, then LDS), so a new listener never receives traffic for a route that is not installed yet.

Safety rules for xDS snapshots:

- Applies run under the same lock as file reloads and dashboard edits, so they never interleave. A snapshot that fails validation or apply is logged and the running config stays in force.
- A snapshot with **no listeners** while listeners are running is refused for the listener step ("no listener data yet", for example an LDS response that has not arrived). Routes and clusters still apply. To remove every listener, change the file config and reload.
- Process-level `server.*` settings (including `hotRestart`) come from the file config; the snapshot only carries xDS resources.
- Secrets (SDS) are translated and written to the state directory, but are not yet wired to listener certificates.

With `reloadListeners: false`, file reload **warns** and leaves listeners unchanged if the listener set would change.

## Migration from titanium-manager

Manager pushes **YAML revisions** over long-poll; xDS pushes **protobuf resources** over ADS. You can run file bootstrap + `failOpen: true`, or replace bootstrap listeners/routes/clusters once the first snapshot is accepted.
