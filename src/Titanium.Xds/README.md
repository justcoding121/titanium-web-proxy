# Titanium.Xds

Lazy-loaded CLI assembly for Envoy **ADS** (Aggregated Discovery Service) xDS v3.

## Vendored protos (Apache-2.0)

Minimal `.proto` subsets under `protos/envoy/` are derived from the Envoy [data-plane-api](https://github.com/envoyproxy/data-plane-api) tree. Google well-known types ship with `Grpc.Tools` (not vendored here).

| Area | Upstream path (reference) | Notes |
|------|---------------------------|--------|
| ADS / discovery | `envoy/service/discovery/v3/` | `@ commit **v1.32.2** (`envoyproxy/envoy` tag, 2024-10) |
| Cluster (CDS) | `envoy/config/cluster/v3/cluster.proto` | trimmed fields |
| Endpoint (EDS) | `envoy/config/endpoint/v3/endpoint.proto` | trimmed fields |
| Route (RDS) | `envoy/config/route/v3/route.proto` | trimmed fields |
| Listener (LDS) | `envoy/config/listener/v3/listener.proto` | trimmed fields |
| Secret (SDS) | `envoy/extensions/transport_sockets/tls/v3/secret.proto` | trimmed fields |
| HCM / TCP proxy filters | `envoy/extensions/filters/network/...` | trimmed typed_config targets |

License: Apache-2.0 (same as upstream Envoy APIs).

## Deviations

- Field numbers and messages are **trimmed** for the supported subset; this is not a full data-plane-api vendoring yet.
- Wire interop with every Envoy control plane is the goal; today tests use a **fake ADS** server with the same trimmed messages.
- See `FEASIBILITY.md` for the supported-field contract and follow-up to import full upstream files via `Grpc.Tools`.

## Build size (informational)

After `dotnet build -c Release`, `Titanium.Xds.dll` plus `Google.Protobuf` and `Grpc.Net.Client` satellites are copied beside `titanium` only when the xDS project is built; they are loaded at runtime only when `xds:` is configured.
