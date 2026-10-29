> For operators who run Titanium behind Kubernetes Gateway API resources. The full guide, with every option, is on the [website](https://titaniumproxy.com/docs/gateway-api).

`titanium-gateway` is a leader-elected controller that turns `GatewayClass`, `Gateway`, `HTTPRoute`, and `ReferenceGrant` resources (Gateway API **standard channel v1.2.1**, HTTP core) into the `twp.yaml` the Titanium data plane runs, and writes status back. The chart is `deploy/helm/titanium-gateway`; the controller image is `ghcr.io/justcoding121/titanium-gateway` for `linux/amd64` and `linux/arm64`.

```shell
helm install titanium-gateway ./deploy/helm/titanium-gateway --namespace gateway-system --create-namespace
```

## How it works

| Step | Behavior |
|------|----------|
| Watch | Informers for `GatewayClass`, `Gateway`, `HTTPRoute`, `ReferenceGrant`, `Namespace`, `Service`, `EndpointSlice`, and `kubernetes.io/tls` Secrets. |
| Render | One pure function turns the cluster state into a config and status. A coalesced loop runs it at most once every 500 ms. |
| Write | Only the `Lease` leader writes the data-plane ConfigMap, the converted-certificate Secret, and status. A follower takes over within 15 s. |
| Reload | Routes, clusters, and certificates reload in place. A change of the listener set rolls the data-plane Deployment. |
| Confirm | `Programmed` is `True` only when every ready data-plane pod reports the rendered config's hash on `/version`. |

## Supported

- **Gateway:** `HTTP` and `HTTPS` (Terminate) listeners, `hostname`, `allowedRoutes`, `certificateRefs` (a cross-namespace Secret needs a `ReferenceGrant`), `attachedRoutes`, addresses from the data-plane Service.
- **HTTPRoute:** path `Exact`, `PathPrefix`, `RegularExpression`; header `Exact`; query parameter `Exact` and `RegularExpression`; `method`; Gateway API precedence.
- **Backends:** `Service` backends with `weight`, from **ready** `EndpointSlice` endpoints only.
- **Filters:** `RequestHeaderModifier`, `ResponseHeaderModifier`, `URLRewrite`, `RequestRedirect`, `RequestMirror`; `timeouts.request` and `timeouts.backendRequest`.

## Not supported, and differences

- `GRPCRoute`, `TCPRoute`, `UDPRoute`, `TLSRoute`, `BackendTLSPolicy`, TLS `Passthrough`, and `Gateway.spec.addresses` are not handled.
- A rule that uses an unsupported value (header regular expression, `ReplacePrefixMatch` redirect, a redirect status other than 301 and 302, a second mirror) answers `500`, and the reason is in the route status.
- Routes are global across listeners: the data plane does not scope a route to a listener port. Gateways of one class share one data plane, and their listeners on one port merge. A request no route matches answers `404`.
- `RequestHeaderModifier` `add` replaces a header the client already sent, where the specification appends.
- A rule that mixes usable and unusable backends answers `503` for the unusable share, where the specification says `500`.
- Header values match case-insensitively. `ResponseHeaderModifier` add acts as set.
- Gateway ports `80` and `443` are bound as `8080` and `8443` because the data plane is non-root (`dataPlane.ports`).

## Conformance

`tools/gateway-conformance/run.sh` runs the upstream Gateway API conformance suite (v1.2.1, `GATEWAY-HTTP`) against the controller in a `kind` cluster; `.github/workflows/gateway-conformance.yml` runs it on Linux and uploads the report. It needs Linux, Docker, `kind`, `kubectl`, and Helm, and does not run on a Windows host (use WSL 2). Only implemented features are claimed. The last run passed 31 of 33 core tests; the failures are a data-plane header-append gap and a data-plane TLS handshake failure on `HTTPS` listeners, not controller logic. The first runs report only; setting the repository variable `GATEWAY_CONFORMANCE_ENFORCE` to `true` makes a failing suite fail the workflow.

## Ingress

`Ingress` resources are not handled by this controller. An Ingress controller is the next planned step (WI-21).

## Security

RBAC is least privilege: reads on the watched kinds, status writes, and `update` and `patch` by name on the data-plane ConfigMap, certificate Secret, Deployment, and Lease, plus `patch` on pods in the release namespace so a config change reaches the pods in seconds. Reading TLS Secrets is cluster-wide by default; set `controller.secretNamespaces` to narrow it. Private keys stay in the controller's memory. Both images run as UID `1654`, read-only, with no capabilities.
