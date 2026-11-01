# Gateway API

Run Titanium as a [Kubernetes Gateway API](https://gateway-api.sigs.k8s.io/) implementation. A leader-elected controller, `titanium-gateway`, watches `GatewayClass`, `Gateway`, `HTTPRoute`, and `ReferenceGrant` resources, renders them into the data plane's `twp.yaml`, and reports status back on the resources.

**Next:** [install the chart](#install) → apply a [Gateway and an HTTPRoute](#quick-start) → read [what is not supported](#what-is-not-supported) before you migrate real routes.

> Gateway API support covers the **standard channel** bundle **v1.7.0** (HTTPRoute, GRPCRoute, TLSRoute, TCPRoute, UDPRoute, BackendTLSPolicy, and ReferenceGrant v1). TLS listener **Terminate** mode is not supported until L4 TLS termination ships. The same controller also serves `networking.k8s.io/v1` `Ingress` resources and the `titanium.io/` annotations; see [Ingress](/docs/ingress).

## Install

```shell
helm install titanium-gateway ./deploy/helm/titanium-gateway \
  --namespace gateway-system --create-namespace
```

The chart installs, in one release:

| Piece | What it is |
|-------|------------|
| Gateway API CRDs | The standard channel, bundle `v1.7.0`, unmodified, from the chart's `crds/` directory. Clusters need Kubernetes **1.31+** for TLSRoute v1 CEL validation. Upgrade CRDs with `kubectl apply --server-side -f` on the bundle before `helm upgrade`; Helm does not replace CRDs on upgrade. |
| `GatewayClass` `titanium` | Owned by the controller (`titaniumproxy.com/gateway-controller`) |
| Controller Deployment | `ghcr.io/justcoding121/titanium-gateway`, 2 replicas, one leader at a time |
| Data-plane Deployment and Service | The `titanium` proxy image running `titanium run --config /etc/titanium/twp.yaml --watch`, 2 replicas |
| RBAC | A least-privilege `ClusterRole` for reads and status writes, and a namespaced `Role` for the objects the controller writes |

Helm installs the CRDs on `helm install` and never upgrades them. To move to another Gateway API release, apply that release's standard-channel CRDs with `kubectl apply --server-side`, then upgrade the chart.

The data-plane Service is a `LoadBalancer` by default. Its load balancer address becomes `Gateway.status.addresses`. Set `dataPlane.service.type=ClusterIP` on clusters without a load balancer.

## Quick start

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: web
  namespace: apps
spec:
  gatewayClassName: titanium
  listeners:
    - name: http
      port: 80
      protocol: HTTP
      allowedRoutes:
        namespaces:
          from: Same
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: shop
  namespace: apps
spec:
  parentRefs:
    - name: web
  hostnames:
    - shop.example.com
  rules:
    - matches:
        - path:
            type: PathPrefix
            value: /api
      backendRefs:
        - name: api
          port: 8080
          weight: 9
        - name: api-canary
          port: 8080
          weight: 1
    - backendRefs:
        - name: frontend
          port: 8080
```

```shell
kubectl -n apps get gateway web -o jsonpath='{range .status.conditions[*]}{.type}={.status} ({.reason}){"\n"}{end}'
kubectl -n apps get httproute shop -o jsonpath='{.status.parents[0].conditions[*].type}'
```

`Accepted` means the Gateway is valid. `Programmed` turns `True` only after every ready data-plane pod reports the config the controller rendered (see [How it works](#how-it-works)).

## How it works

1. **Informers** keep an in-memory view of `GatewayClass`, `Gateway`, `HTTPRoute`, `ReferenceGrant`, `Namespace`, `Service`, `EndpointSlice`, the `kubernetes.io/tls` Secrets, and, for [Ingress](/docs/ingress), `IngressClass` and `Ingress`. Nothing is written to disk.
2. **A coalesced reconcile** runs at most once every 500 ms, however many changes arrive. It renders the whole cluster state through a pure function: the same state always gives the same config.
3. **Only the leader writes.** Replicas elect a leader with a `Lease`. The leader writes the data-plane ConfigMap (`twp.yaml`), the Secret with converted certificates, and resource status. A follower keeps its caches warm and takes over within the lease duration (15 s) if the leader stops.
4. **The data plane reloads in place.** The pods mount the ConfigMap and the Secret in one directory and run with `--watch`, so routes, clusters, and certificates reload without a restart. The kubelet refreshes a mounted ConfigMap only on its next periodic sync, up to about a minute later, so after each write the controller annotates the data-plane pods (`gateway.titaniumproxy.com/config-poke`), which makes the kubelet refresh the volume at once. A change reaches the pods in a few seconds.
5. **Listeners roll the pods.** Listeners are bound at start. When the set of listeners changes (a new port, or a port that starts to decrypt TLS), the controller stamps the data-plane pod template and Kubernetes rolls the Deployment with no unavailable pod.
6. **Programmed is confirmed, not assumed.** The controller reads `/readyz` and `/version` from every ready data-plane pod and compares the `config_hash` with the SHA-256 of the config it wrote. Until they match, `Programmed` is `False` with reason `Pending`, and the controller polls again every 5 s.

### Certificates

Listener `certificateRefs` name `kubernetes.io/tls` Secrets. The controller converts each pair of `tls.crt` and `tls.key` into a password-less PFX and publishes it in the generated Secret as `<namespace>.<name>.pfx`, which the config names as an SNI certificate (`certificates.items`). A Secret that does not hold a matching certificate and key is reported as `InvalidCertificateRef`; the data plane never sees it. The controller keeps private keys in memory only.

A `certificateRef` in another namespace needs a `ReferenceGrant`.

## Supported resources and fields

| Resource | Supported |
|----------|-----------|
| `GatewayClass` | `controllerName`. Status `Accepted`. |
| `Gateway` | Listeners with protocol `HTTP` or `HTTPS` (TLS mode `Terminate`), `hostname`, `allowedRoutes` (`namespaces.from` Same, All, or Selector, and `kinds`), `certificateRefs`. Status `Accepted`, `Programmed`, per-listener `attachedRoutes`, `ResolvedRefs`, `Conflicted`, and addresses from the data-plane Service. |
| `HTTPRoute` | `parentRefs` (with `sectionName` and `port`), `hostnames`, and `rules` with the matches, filters, and backends below. Status per parent: `Accepted`, `ResolvedRefs`. |
| `ReferenceGrant` | Cross-namespace `backendRefs`, `RequestMirror` backends, and `certificateRefs` (`v1beta1`). |

**Matches:** path `Exact`, `PathPrefix` (on a path-segment boundary), `RegularExpression`; headers (`Exact`); query parameters (`Exact`, `RegularExpression`); `method`. Several matches in one rule are alternatives, as the API defines. Routes are ordered by the Gateway API precedence rules: exact path, then the longest prefix, then method, then the number of header and query matches, then oldest creation time and name.

**Backends:** `backendRefs` to `Service` with `weight`. Destinations are the **ready** endpoints of the Service's `EndpointSlice`s, so a pod that is not ready receives no traffic. A weight of `0` takes no traffic.

**Filters:** `RequestHeaderModifier`, `ResponseHeaderModifier` (set, add, remove), `URLRewrite` (`hostname`, `ReplaceFullPath`, `ReplacePrefixMatch`), `RequestRedirect` (301 and 302, `scheme`, `hostname`, `port`, `ReplaceFullPath`), `RequestMirror` (`percent` or `fraction`).

**Timeouts:** `timeouts.request` and `timeouts.backendRequest`.

## What is not supported

The controller never guesses. A rule it cannot honor is answered with `500` and the reason is in the route status (`Accepted` `False` with reason `UnsupportedValue`, or `ResolvedRefs` `False`). A rule is never silently dropped.

| Gateway API feature | Behavior |
|---------------------|----------|
| `GRPCRoute`, `TCPRoute`, `UDPRoute`, `TLSRoute`, `BackendTLSPolicy` | Not watched. The standard CRDs for `GRPCRoute` are installed, but no controller acts on them. |
| TLS `Passthrough`, protocols other than `HTTP` and `HTTPS` | Listener `Accepted` `False`, reason `UnsupportedProtocol`. |
| `Gateway.spec.addresses` | `Accepted` `False`, reason `UnsupportedAddress`. The address comes from the data-plane Service. |
| Header matches of type `RegularExpression` | The rule answers `500` (`UnsupportedValue`). |
| `RequestRedirect` with `ReplacePrefixMatch`, or a status other than 301 and 302 | The rule answers `500`. |
| `RequestRedirect` without a `hostname` | The redirect needs a concrete host, so the route must list concrete `hostnames`. |
| More than one `RequestMirror` in a rule | The rule answers `500`; the data plane mirrors to one cluster. |
| A listener on a port not in `dataPlane.ports` | Listener not programmed, reason `PortUnavailable`. |

### Behavior that differs from the specification

These follow from how the data plane works. They are deliberate and listed so nothing surprises you.

- **Gateways of one class share one data plane.** Listeners of different Gateways on the same port merge into one data-plane listener, and routes are not scoped to a Gateway. Two listeners with the same hostname conflict only inside one Gateway. A request no route matches answers `404`; it is never forwarded to the host the client named.
- **`add` in a header modifier joins values with `, `.** A request or response header that already carries a value gets the new one appended (`foo` and `bar` become `foo, bar`), which is the same list as the specification's `foo,bar`. Repeated lines of one header collapse into one. `Set-Cookie` is the exception: each added cookie keeps its own line.
- **Routes are global across listeners.** The data plane matches routes by host, path, header, query, and method, not by listener port. Two Gateway listeners that would serve different routes on different ports serve the same routes.
- **A mix of valid and invalid backends answers `503` for the invalid share**, where the specification says `500`. When no backend of a rule is usable, the rule answers `500`.
- **Header names match case-insensitively**, and so do `Exact` header values. The specification makes values case-sensitive.
- **A listener with a mix of supported and unsupported `allowedRoutes.kinds`** serves the supported ones (`supportedKinds` lists `HTTPRoute`) and reports `ResolvedRefs` `False`, reason `InvalidRouteKinds`.
- **Privileged ports are mapped.** The data plane runs as non-root, so Gateway port `80` is bound as `8080` and `443` as `8443` (`dataPlane.ports`). The Service maps them back.

## Conformance

The repository runs the upstream Gateway API conformance suite (v1.2.1, profile `GATEWAY-HTTP`) against this controller:

- `tools/gateway-conformance/run.sh` builds both images, creates a `kind` cluster, installs MetalLB (so the data-plane Service gets an address), installs the chart with `deploy/helm/titanium-gateway/ci/kind-values.yaml`, runs the suite in a `golang` container on the `kind` network, and writes the report to `artifacts/gateway-conformance-report.yaml`.
- `.github/workflows/gateway-conformance.yml` runs it on Linux for pull requests that touch the controller, the chart, or the images, and weekly. It uploads the report. **The first runs report only**: a failing suite does not fail the workflow until the repository variable `GATEWAY_CONFORMANCE_ENFORCE` is `true`.
- **It needs Linux, Docker, `kind`, `kubectl`, and Helm. It does not run on a Windows host**, because the suite reaches the Gateway address over the container network; use WSL 2 or a Linux machine.
- Only features this controller implements are claimed (`--supported-features`). Not claimed: `HTTPRouteResponseHeaderModification` (response header `add` now appends, but the feature is not claimed until the suite has been re-run against it), redirects without a hostname, the request and backend timeout features (not yet verified against the suite), `GRPCRoute`, `TLSRoute`, and port-based listener features. The report lists what passed, failed, and was skipped.

**Status.** The suite was last run on a `kind` cluster (Linux containers on Docker Desktop) before two data-plane fixes: 31 of the 33 core tests passed, and 2 of the 5 extended tests. The failures had two causes, both in the data plane rather than the controller, and both are now fixed in the data plane:

- A header modifier `add` replaced a header the request already carried instead of appending to it (`HTTPRouteRequestHeaderModifier`, `HTTPRouteRewriteHost`, `HTTPRouteRewritePath`, and `HTTPRouteRequestMirror` each include such a rule).
- A transparent decrypting listener that selects its certificate by SNI failed every TLS handshake (`HTTPRouteHTTPSListener`), so an `HTTPS` Gateway listener was accepted and programmed but did not serve traffic.

The suite has not been re-run since those fixes; run it on Linux to refresh the numbers.

## Configuration

The controller reads flags or the matching `TITANIUM_GATEWAY_*` environment variables (the chart sets them). An unknown option stops the controller at start.

| Option | Default | Meaning |
|--------|---------|---------|
| `--controller-name` | `titaniumproxy.com/gateway-controller` | `GatewayClass.spec.controllerName` to own |
| `--data-plane-service` | required | The data-plane Service. Its ready endpoints are polled. |
| `--data-plane-namespace` | `POD_NAMESPACE` | Namespace of the data plane and its ConfigMap and Secret |
| `--data-plane-deployment` | none | Deployment rolled when the listener set changes |
| `--config-map` / `--tls-secret` | the Service name, and the Service name plus `-tls` | Objects the controller writes |
| `--port-map` | `80=8080,443=8443` | Gateway port to data-plane port |
| `--secret-namespaces` | all | Namespaces whose TLS Secrets are read |
| `--ingress-controller-name` | `titanium.io/ingress-controller` | `IngressClass.spec.controller` to own |
| `--watch-legacy-annotation` | off | Also own Ingresses that carry the `kubernetes.io/ingress.class` annotation. See [Ingress](/docs/ingress#choosing-the-ingresses-this-controller-serves). |
| `--admin-port` | `9901` | Data-plane admin port. `TITANIUM_ADMIN_TOKEN` is the bearer token. |
| `--lease-name`, `--lease-namespace`, `--identity`, `--no-leader-election` | | Leader election |
| `--min-reconcile-ms` | `500` | Smallest gap between reconciles |
| `--health-port` | `8081` | The controller's `/healthz` and `/readyz` |
| `--verbose` | off | Debug logging |

## Security

- **RBAC is least privilege.** Reads cover the watched kinds. Writes are status on `gatewayclasses`, `gateways`, `httproutes`, and `ingresses`, `create` and `patch` on `events` (to report a rejected Ingress rule), plus `update` and `patch` on exactly the data-plane ConfigMap, the certificate Secret, the data-plane Deployment, and the Lease, all by name, in the release namespace. The one write that cannot be limited by name is `patch` on `pods` in the release namespace, used only to set the `config-poke` annotation described in [How it works](#how-it-works); pod names are not known ahead of time.
- **Secret read access is the broad part.** By default the controller can read TLS Secrets in every namespace, because a Gateway may reference any of them. Set `controller.secretNamespaces` to limit both the watch and the RBAC to listed namespaces; the chart then creates a `Role` in each instead of a cluster-wide rule.
- **The admin token** protects `/metrics` and `/version` on the data plane. The chart creates one and shares it with the controller, which needs `/version` to confirm `Programmed`.
- **Both images run as non-root** (UID `1654`) with a read-only root file system and no capabilities.

## See also

- [Ingress](/docs/ingress): `networking.k8s.io` Ingress and the `titanium.io/` annotations.
- [Kubernetes](/docs/kubernetes): the container image and the plain Helm chart for the `titanium` CLI.
- [Configuration](/docs/configuration): the `twp.yaml` the controller renders.
