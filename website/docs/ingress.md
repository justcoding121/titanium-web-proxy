# Ingress

> For operators who run Titanium behind Kubernetes `Ingress` resources. `titanium-gateway`, the controller from [Gateway API](/docs/gateway-api), also serves `networking.k8s.io/v1` `Ingress`. Both kinds feed the same data plane: one `twp.yaml`, one set of listeners, one set of clusters.

An `Ingress` becomes routes, clusters, and certificate bindings in the config the data plane already understands. **No annotation adds data-plane behavior.** Each one maps to a feature that exists (a policy, a cluster setting, a transform), and the table below says which. What the data plane cannot do is rejected, never skipped.

## Quick start

```shell
helm install titanium-gateway ./deploy/helm/titanium-gateway --namespace gateway-system --create-namespace
```

The chart creates an `IngressClass` named `titanium` whose controller is `titanium.io/ingress-controller`.

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: shop
  annotations:
    titanium.io/proxy-read-timeout: "30"
    titanium.io/limit-rps: "50"
spec:
  ingressClassName: titanium
  tls:
    - hosts: [shop.example.com]
      secretName: shop-tls
  rules:
    - host: shop.example.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: shop
                port:
                  number: 8080
```

The Ingress's `status.loadBalancer.ingress` shows the address of the data-plane Service (an IP, or a hostname for a load balancer that has one).

## Choosing the Ingresses this controller serves

The controller serves an Ingress when:

1. `spec.ingressClassName` names an `IngressClass` whose `spec.controller` is `titanium.io/ingress-controller` (change it with `--ingress-controller-name`), or
2. it names no class and one owned `IngressClass` carries the annotation `ingressclass.kubernetes.io/is-default-class: "true"`, or
3. only with `--watch-legacy-annotation` (chart value `controller.watchLegacyAnnotation`): it has no `ingressClassName` and its `kubernetes.io/ingress.class` annotation names an owned `IngressClass`, or says `titanium`.

`ingressClassName` always wins over the annotation. Without `--watch-legacy-annotation` the annotation is ignored. An Ingress of another controller is never touched and never reported.

## What is translated

| Ingress field | Result |
|---------------|--------|
| `rules[].host` | Host match. No host matches any host. `*.example.com` is a wildcard host, ranked below an exact host. |
| `rules[].http.paths[]` | One route per path. `Exact` matches that path only. `Prefix` matches on a path-segment boundary (`/foo` matches `/foo` and `/foo/bar`, not `/foobar`). `ImplementationSpecific` is a `Prefix`. |
| `defaultBackend` | The lowest-priority route of the Ingress: it answers what no rule matched. Only one Ingress can own it; the others get an event. |
| `tls[].secretName` | A certificate binding for `hosts` (or every host when `hosts` is empty). The Secret is converted exactly as for a Gateway listener, into the same `<namespace>.<name>.pfx`, and the data plane opens a decrypting listener on the HTTPS port (`8443`). |
| `backend.service` | A cluster of the Service's **ready** endpoints. The port is a number or a name. A Service with no ready endpoint answers `503`. |
| `backend.resource` | Rejected: the rule answers `500` and the Ingress gets a `UnsupportedBackend` event. |

Routes are ordered by host (exact, then wildcard, then any), then path (exact, then the longest prefix), then oldest creation time and name. Gateway API routes and Ingress rules share one ordering, so an Ingress rule and an `HTTPRoute` for the same host and path go to whichever is older, and neither is dropped. Route ids are `ingress/<namespace>/<name>/r<rule>p<path>`, so they never collide with `HTTPRoute` ids.

## Events

A rule the controller cannot honor answers `500` and raises a `Warning` event on the Ingress. An annotation under `titanium.io/` it does not know is a warning too, while the Ingress keeps serving. Events are posted once, not once per reconcile; they come back (as a bump of `count`) every 30 minutes while the problem remains, and again at once if it returns after being fixed.

| Reason | Meaning |
|--------|---------|
| `UnknownAnnotation` | A `titanium.io/` annotation that is not in the table. The Ingress still serves. |
| `InvalidAnnotation` | A known annotation with a value, or a combination, that cannot be honored. Every rule of the Ingress answers `500` until fixed. |
| `UnsupportedBackend` | `backend.resource`. |
| `InvalidBackend`, `BackendNotFound` | A backend with no port, or a Service or port that does not exist. |
| `InvalidPath` | A path that is not absolute, or a `pathType` other than the three above. |
| `InvalidTLS` | A `tls` Secret that is missing, not `kubernetes.io/tls`, unreadable, or on a port that is not mapped. HTTP still serves. |
| `RouteConflict` | Another Ingress, older, owns the same host, path, and path type (or the default backend). |
| `CanaryWithoutPrimary`, `CanaryConflict` | See [Canary](#canary). |
| `PortUnavailable` | The HTTP or HTTPS port is not in `dataPlane.ports`, or a Gateway listener already uses it with the other protocol. |

## Annotations

Annotations use the prefix `titanium.io/`. The values are strings, as Kubernetes requires (`"30"`, `"true"`).

<!-- annotations:start -->
| Annotation | Value | Data-plane feature | Notes |
|------------|-------|--------------------|-------|
| `titanium.io/ssl-redirect` | `false` (default) | none | `false` is accepted. `true` is rejected: the data plane matches routes by host, path, header, query, and method, not by scheme, so a redirect route would also redirect HTTPS requests in a loop. |
| `titanium.io/rewrite-target` | a path starting with `/` | `PathRegexReplace` transform | Replaces the matched prefix (`Prefix`, `ImplementationSpecific`) or the whole path (`Exact`). Capture groups (`$1`) are not supported and are rejected. The query string is kept. |
| `titanium.io/proxy-read-timeout` | seconds, 1-3600 | cluster `httpClient.requestTimeout` | How long the data plane waits for the backend's response headers. Gives the backend its own cluster. |
| `titanium.io/proxy-body-size` | `8m`, `512k`, `1g`, or bytes; `0` for no limit | `maxRequestBodySize` policy | Checked against `Content-Length`; a request with no length (chunked) is not checked. Answers `413`. |
| `titanium.io/backend-protocol` | `HTTP` (default), `HTTPS`, `H2C`, `GRPC` | destination `useHttps` | `HTTPS` connects to the backend with TLS and validates its certificate. `H2C` and `GRPC` connect in cleartext; the data plane speaks HTTP/2 to the backend when the client does (ALPN or prior knowledge), so gRPC clients get end-to-end HTTP/2. Gives the backend its own cluster. |
| `titanium.io/affinity` | `cookie` | cluster `sessionAffinity` (cookie) | Pins a client to one backend pod with an opaque cookie named `titanium-affinity`. Gives the backend its own cluster. |
| `titanium.io/load-balance` | `round_robin` (default), `random`, `least_conn`, `ewma`, `p2c` | cluster `algorithm` | `least_conn` is `LeastRequests`, `ewma` is `LeastTime`, `p2c` is `PowerOfTwoChoices`. Gives the backend its own cluster. |
| `titanium.io/whitelist-source-range` | comma-separated addresses or CIDR prefixes | `ipAllowDeny` policy (`allow`) | Any other client address gets `403`. The address is the socket peer, so behind a proxy list the proxy's range. |
| `titanium.io/limit-rps` | requests per second per client, 1-1000000 | `rateLimit` policy (fixed window of 1 second) | Counted per client address and per Ingress. Excess requests get `429` with `Retry-After`. |
| `titanium.io/cors-allow-origin` | `*` or comma-separated origins such as `https://app.example.com` | `cors` policy | Answers the `OPTIONS` preflight itself with `204` and the `Access-Control-Allow-*` headers. It does not add headers to the actual response. |
| `titanium.io/auth-url` | an absolute `http` or `https` URL without credentials | `extAuth` policy | Each request is checked with a `GET` subrequest first; a `2xx` answer admits it, any other answer is relayed to the client. Fails closed: a timeout or an error answers `503`, never admits. |
| `titanium.io/canary` | `true` or `false` (default) | weighted route clusters | Marks the Ingress as a canary of the Ingress that serves the same host, path, and path type. The canary Ingress contributes only its backend; every other annotation comes from the primary. |
| `titanium.io/canary-weight` | percent, 0-100 (default 0) | route `clusters` weights | Share of requests the canary backend receives. Needs `canary: "true"`. |
| `titanium.io/canary-by-header` | a header name | route header match | `always` in that header sends the request to the canary, `never` to the primary; any other value follows the weight. Needs `canary: "true"`. |
| `titanium.io/upstream-tls-secret` | `name` (same namespace) or `namespace/name` of the Ingress's own namespace | cluster `httpClient` TLS files | A `kubernetes.io/tls` Secret. `ca.crt` becomes the trust anchors for the backend, and `tls.crt` and `tls.key` the client certificate. Needs `backend-protocol: HTTPS`. Gives the backend its own cluster. |
<!-- annotations:end -->

### Canary

An Ingress with `titanium.io/canary: "true"` is the canary of the Ingress that serves the same host, path, and path type. The canary Ingress contributes only its backend. The primary's route gets both backends, weighted `100 - canary-weight` and `canary-weight`. With `canary-by-header: X-Canary`, the header `always` forces the canary and `never` the primary; any other value follows the weight. A canary with no primary, or a second canary for the same route, is reported and does nothing.

## Limitations

These follow from the data plane. They are listed so nothing surprises you.

- **`ssl-redirect: "true"` is rejected.** Routes are matched by host, path, header, query, and method, not by scheme, so a redirect rule would also catch HTTPS requests. Terminate and redirect at the load balancer in front, or use an `HTTPRoute` with `RequestRedirect`.
- **CORS answers the preflight only.** `cors-allow-origin` replies to `OPTIONS` itself; it does not add `Access-Control-*` headers to the actual response.
- **`H2C` and `GRPC` follow the client.** The data plane speaks HTTP/2 to the backend when the client does, so there is no separate setting for it.
- **`proxy-body-size` reads `Content-Length`.** A chunked request with no length is not checked.
- **Annotations of other controllers are ignored**, not translated. Only `titanium.io/` is read.

## Chart values

| Value | Default | Meaning |
|-------|---------|---------|
| `ingressClass.create` | `true` | Create the `IngressClass` |
| `ingressClass.name` | `titanium` | Its name |
| `ingressClass.default` | `false` | Mark it the default class of the cluster |
| `controller.ingressControllerName` | `titanium.io/ingress-controller` | The controller name it owns |
| `controller.watchLegacyAnnotation` | `false` | Also honor `kubernetes.io/ingress.class` |

The chart's `ClusterRole` gains read on `ingresses` and `ingressclasses`, `patch` on `ingresses/status`, and `create` and `patch` on `events`.

## See also

- [Gateway API](/docs/gateway-api): the controller, its install, and its options.
- [Configuration](/docs/configuration): the `twp.yaml` the controller renders.
