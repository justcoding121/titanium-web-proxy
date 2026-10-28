> For operators who run the `titanium` CLI on Kubernetes. The full guide, with every Helm value, is on the [website](https://titaniumproxy.com/docs/kubernetes).

The CLI ships as a hardened container image and a Helm chart. The image is `ghcr.io/justcoding121/titanium-web-proxy` for `linux/amd64` and `linux/arm64`; it runs as UID `1654` on a chiseled base with no shell, and its only writable path is `/tmp`. The chart is in `deploy/helm/titanium`.

## What you get

| Piece | Behavior |
|-------|----------|
| Admin listener | `server.admin` serves `/healthz`, `/readyz`, `/metrics`, and `/version` on a port that no proxy listener shares. The chart binds it to `0.0.0.0:9901` and requires a bearer token for `/metrics` and `/version`. |
| Probes | Startup and liveness use `/healthz`; readiness uses `/readyz`. |
| Graceful rollout | On SIGTERM `/readyz` turns `503` at once, Titanium keeps serving for `admin.preStopDelaySeconds`, then drains in-flight requests and exits `0`. Keep `terminationGracePeriodSeconds` longer than the delay plus your longest request. |
| Config reload | The pod runs with `--watch`. A ConfigMap or Secret update swaps the `..data` symlink; Titanium sees it and reloads once. A failed reload keeps the running config. |
| Secrets | Settings take `*Env` and `*File` references, such as `authTokenFile`, instead of literals. |

## Install

```shell
helm install titanium deploy/helm/titanium -n proxy --create-namespace -f my-values.yaml
```

`config` in the values is the whole `twp.yaml`. The chart owns `server.admin`; set those fields through the `admin.*` values. Mount the config and any Secret files as **directories**, never with `subPath`: a single-file mount does not see the `..data` swap.

## Optional resources

PodDisruptionBudget (on), HorizontalPodAutoscaler, ServiceMonitor (needs the Prometheus Operator CRDs), a sample NetworkPolicy, and Titanium Plus wiring are switched on through values. `helm lint --strict` and kubeconform run on every pull request, and a kind smoke run covers probes, ConfigMap reload, and a rolling restart.

## Limits

The image carries no `libmsquic`, so HTTP/3 listeners are off; use the release zip or a custom base for QUIC. Listener, certificate, and Plus plugin changes need a restart, and `rolloutOnConfigChange: true` rolls the pods when the ConfigMap changes.

See [Metrics](Metrics) for the Prometheus text and [Security considerations](Security-Considerations) for hardening.
