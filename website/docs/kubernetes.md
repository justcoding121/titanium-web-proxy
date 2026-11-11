# Kubernetes

Run the `titanium` CLI as a Deployment: a hardened container image, probes on a separate admin port, a graceful drain on rollout, and ConfigMap and Secret updates that reload without a restart.

**Next:** install the [Helm chart](#helm-chart) → edit `config` in your values → `helm upgrade`. To drive the proxy from `Gateway` and `HTTPRoute` resources instead, see [Gateway API](/docs/gateway-api).

## Container image

```text
ghcr.io/justcoding121/titanium-web-proxy:<version>      # linux/amd64 and linux/arm64
```

| Property | Value |
|----------|-------|
| Base | A chiseled .NET runtime-deps image: no shell, no package manager |
| User | Non-root, UID/GID `1654` |
| Entrypoint | `titanium run --config /etc/titanium/twp.yaml`, with the default argument `--watch` |
| Signal | `STOPSIGNAL SIGTERM` |
| Health | `HEALTHCHECK` runs `titanium healthcheck --config /etc/titanium/twp.yaml` |
| Writable paths | `/tmp` only. Run with `--read-only` and a tmpfs or `emptyDir` on `/tmp` |
| Supply chain | Release images carry an SBOM and provenance attestation and are signed with keyless [cosign](https://github.com/sigstore/cosign) |

```shell
docker run --rm -p 8080:8080 --read-only --tmpfs /tmp \
  -v "$PWD/conf:/etc/titanium:ro" \
  ghcr.io/justcoding121/titanium-web-proxy:latest
```

Mount the config as a **directory** at `/etc/titanium`, not as a single file. Kubernetes updates a ConfigMap by swapping a `..data` symlink inside the directory, and a single-file (`subPath`) mount never sees the swap.

Verify a release image:

```shell
cosign verify ghcr.io/justcoding121/titanium-web-proxy:<version> \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github.com/justcoding121/titanium-web-proxy/\.github/workflows/release\.yml@'
```

The image does not carry `libmsquic`, so HTTP/3 and QUIC listeners are off. Use the release zip or a custom base image when you need them.

Release images **do** bundle Lua and Wasmtime natives for `linux/amd64` and `linux/arm64` (glibc). Script plugins work in the default chart Deployment. Alpine / musl nodes need the `linux-musl-*` CLI zip or a custom image — see [Scripting](/docs/scripting) and `titanium scripting-deps status`.

## Admin endpoints

Turn on `server.admin` for probes and metrics on a port that no proxy listener shares. Full reference: [Admin listener](/docs/configuration#admin-listener-server-admin).

| Path | Auth | Use |
|------|------|-----|
| `/healthz` | none | Liveness: `200` while the process runs |
| `/readyz` | none | Readiness: `200` once the config is loaded and the listeners are bound; `503` while starting and while draining |
| `/metrics` | bearer token | Prometheus text |
| `/version` | bearer token | Version, commit, runtime, edition, config hash |

Binding the admin port to anything but loopback requires a token (`authTokenEnv` or `authTokenFile`). The chart does this for you.

## Helm chart

Install from the repo tree, from the Helm `.tgz` on the [GitHub Release](https://github.com/justcoding121/titanium-web-proxy/releases), or from OCI:

```shell
helm install titanium deploy/helm/titanium -n proxy --create-namespace -f my-values.yaml
# or: helm install titanium oci://ghcr.io/justcoding121/charts/titanium --version <chart-version>
```

Gateway API and Manager charts follow the same paths (`titanium-gateway`, `titanium-manager`).

An empty image tag is the chart `appVersion`. Chart `1.0.2` ships `appVersion: 8.0.2`, so a beta install sets the tag or the pods pull `:8.0.2`:

| Chart | Tag values |
|-------|------------|
| `titanium` | `image.tag` |
| `titanium-gateway` | `controller.image.tag` and `dataPlane.image.tag`. `image.tag` on this chart is ignored. |
| `titanium-manager` | `image.tag` |

Minimal `my-values.yaml`:

```yaml
replicaCount: 3
config:
  schemaVersion: "7.1"
  listeners:
    - host: "0.0.0.0"
      port: 8080
  clusters:
    - id: web
      destinations:
        - id: app
          address: app.default.svc.cluster.local
          port: 80
  routes:
    - id: all
      clusterId: web
      match:
        path: /
```

`config` is the whole `twp.yaml`. The chart renders it into a ConfigMap mounted at `/etc/titanium`. It owns `server.admin` (port, host, token reference, pre-stop delay) so that the probes and the config always agree; set those through `admin.*` values.

| Resource | Notes |
|----------|-------|
| Deployment | Startup, liveness (`/healthz`), and readiness (`/readyz`) probes on the admin port; `terminationGracePeriodSeconds`; rolling update with `maxUnavailable: 0` |
| Pod security | `runAsNonRoot`, read-only root filesystem, all capabilities dropped, `RuntimeDefault` seccomp, no service-account token |
| Service | One port per entry in `ports`. A second ClusterIP Service for the admin port appears with `admin.service.enabled` or `serviceMonitor.enabled` |
| ConfigMap | `twp.yaml` rendered from `config` |
| Secret | The admin token. Generated once and kept across upgrades, or set `admin.token.existingSecret` |
| PodDisruptionBudget | On by default with `maxUnavailable: 1` |
| HorizontalPodAutoscaler | `autoscaling.enabled`, CPU and optional memory |
| ServiceMonitor | `serviceMonitor.enabled`: scrapes `/metrics` on the admin port with the admin token (needs the Prometheus Operator CRDs) |
| NetworkPolicy | `networkPolicy.enabled`: a sample that limits the admin port to the sources you list |
| Plus | `plus.enabled` with `plus.controlPlane.sharedSecret.existingSecret` (passed by environment variable) and either `plus.controlPlane.tls.existingSecret` (a `kubernetes.io/tls` Secret, mounted at `/etc/titanium-plus-tls`) or `plus.controlPlane.allowInsecureNonLoopback=true`. The default image does not contain Plus; see [Plus on Kubernetes](#plus-on-kubernetes). |

### Plus on Kubernetes

`ghcr.io/justcoding121/titanium-web-proxy` is the MIT CLI. It does not include `Titanium.Plus.dll` or the xDS and GeoIP satellites. The runtime image has no shell and no package manager, so `titanium update --plus` cannot download a bundle into a running pod.

Build an image that already contains the Plus bundle, then set `image.repository` and `image.tag` (or `image.digest`) on the [proxy chart](#helm-chart) or `dataPlane.image` on the gateway chart. Take `Titanium.Plus-<version>.zip` from the GitHub Release (the manifest field `products.plus.bundle`), unpack it, and copy the files beside the CLI. The process runs as UID `1654`.

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS plus
ARG PLUS_ZIP=Titanium.Plus-8.0.2.zip
COPY ${PLUS_ZIP} /tmp/plus.zip
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends unzip \
    && rm -rf /var/lib/apt/lists/* \
    && unzip /tmp/plus.zip -d /plus \
    && test -f /plus/Titanium.Plus.dll \
    && test -f /plus/Titanium.Plus.files

FROM ghcr.io/justcoding121/titanium-web-proxy:8.0.2
COPY --from=plus --chown=1654:1654 /plus/ /app/
```

`plus.enabled` still needs the control-plane Secret from the table above. The same custom image is required for a gateway data plane that loads Plus features (cache, active health, xDS, GeoIP).

### Graceful rollout

When Kubernetes stops a pod it sends SIGTERM. Titanium then:

1. Flips `/readyz` to `503` straight away, so the endpoint controller removes the pod.
2. Waits `admin.preStopDelaySeconds` (default `5`) while still serving, so the removal reaches kube-proxy and ingress controllers.
3. Drains: stops accepting, lets in-flight requests finish, and exits `0`.

Keep `terminationGracePeriodSeconds` above `preStopDelaySeconds` plus your longest request. The chart refuses to render a grace period that is not longer than the delay. No `preStop` hook is needed.

## Config and Secret reload

The pod runs with `--watch`. Titanium watches the directory that holds the config, so a ConfigMap update (an atomic `..data` swap) reloads routes, clusters, and `server:` knobs in place, with one reload per swap. Files read through `*File` settings, such as a mounted admin token or a certificate, are watched the same way. A reload that fails validation keeps the running config and logs the reason. See [Graceful reload](/docs/configuration#graceful-reload) for what reloads live.

Kubelet refreshes ConfigMap and Secret volumes on its sync period, so expect up to about a minute and a half after `helm upgrade`. Listener, certificate, and Plus plugin changes still need a restart: set `rolloutOnConfigChange: true` to roll the pods when the ConfigMap changes.

## Secrets

Use the secret reference forms instead of literals in `config`:

```yaml
extraVolumes:
  - name: tokens
    secret:
      secretName: titanium-tokens
extraVolumeMounts:
  - name: tokens
    mountPath: /etc/titanium-secret
    readOnly: true
plus:
  enabled: true
  controlPlane:
    sharedSecret:
      existingSecret: titanium-control-plane
  options:
    security.apiKeysFile: /etc/titanium-secret/api-keys
```

Mount Secrets as directories (no `subPath`) so a rotation is seen. See [Secret references](/docs/configuration#secret-references).

## Checks in CI

| Check | What it covers |
|-------|----------------|
| `kubernetes-ci` | Builds the image for both architectures without pushing, runs it read-only, checks probes and the SIGTERM exit, then runs `helm lint --strict` and kubeconform on the default and full values |
| `kubernetes-smoke` | Nightly, on demand, or with the `k8s-smoke` pull request label: installs the chart into a kind cluster and checks probes, token auth, traffic, an in-place ConfigMap reload, and a rolling restart with no failed requests |

See also: [CLI](/docs/cli), [Configuration](/docs/configuration), [Metrics](https://github.com/justcoding121/titanium-web-proxy/wiki/Metrics).
