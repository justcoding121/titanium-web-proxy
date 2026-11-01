# P9.0 xDS feasibility spike (D15: go)

Decision: **go**. This note fixes the v1 field list and records build proof.

## Proto / build proof

- Trimmed Envoy v3 protos under `protos/envoy/` compile with **Grpc.Tools** 2.67.0 and **Google.Protobuf** 3.31.1.
- Google `protobuf/*` well-known types come from the Grpc.Tools include path (not duplicated in-repo).
- Release build of `Titanium.Xds` succeeds; ADS client uses generated `AggregatedDiscoveryService` stub.

## ADS proof

- `AdsClient` opens a bidirectional `StreamAggregatedResources` stream, sends discovery requests in order **CDS → EDS → SDS → RDS → LDS**, and replies **ACK** (matching version + nonce) or **NACK** (`error_detail`, prior version).
- `tests/Titanium.Xds.Tests/AdsClientIntegrationTests` runs against an in-process **fake ADS** (Grpc.AspNetCore): ACK on a static cluster, NACK on `LOGICAL_DNS`.

## Mapping proof

Translators NACK unsupported fields with `(resource name, field path, message)` and map the v1 subset below onto `TwpConfig` + `TwpConfigValidator`:

| xDS resource | Supported (v1) | NACK examples |
|--------------|----------------|---------------|
| **Cluster** | `STATIC`, `STRICT_DNS`, `EDS`; `ROUND_ROBIN`, `LEAST_REQUEST`, `RANDOM`; `connect_timeout` | `LOGICAL_DNS`, `RING_HASH`, unknown LB |
| **ClusterLoadAssignment** | socket address, port, weight, priority, locality zone | missing address/port |
| **RouteConfiguration** | prefix/path match, `route.cluster`, `direct_response` | redirect, safe_regex, weighted_clusters |
| **Secret** | `tls_certificate`, `validation_context` PEM → state dir `0600` | other secret types |
| **Listener** | one chain; HCM with RDS name; `tcp_proxy` cluster; `tls_inspector`, `proxy_protocol` | multiple chains, unsupported filters |

## Follow-up (not blockers)

- Replace trimmed protos with pinned upstream files + commit hashes in `README.md`.
- Delta xDS, mTLS to control plane, full health/outlier/circuit-breaker mapping.
- Reference control plane interop job (optional CI).
