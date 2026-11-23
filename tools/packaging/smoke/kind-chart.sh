#!/usr/bin/env bash
# Fast kind proof of one packaged chart. Installs the .tgz (or the OCI chart
# when SOURCE=release-tag) and checks the same probes as kubernetes-smoke.yml.
# Deep reload and rolling restart stay in the nightly workflow.
# Usage: kind-chart.sh titanium|gateway|manager
set -euo pipefail
which="${1:?titanium|gateway|manager}"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
source_mode="${SOURCE:-run-artifacts}"
version="${RELEASE_VERSION:-}"
chart_version="${CHART_VERSION:-}"
if [[ "$source_mode" == "release-tag" && -z "$version" ]]; then
  tag="${RELEASE_TAG:-}"
  if [[ -z "$tag" ]]; then
    tag="$(gh api "repos/${GITHUB_REPOSITORY}/releases" --jq '[.[] | select(.prerelease==true)][0].tag_name')"
  fi
  version="${tag#v}"
fi
charts_dir="${CHARTS_DIR:-$root/charts}"

if [[ -z "$chart_version" ]]; then
  chart_version="$(awk '/^version:/ { print $2; exit }' "$root/deploy/helm/titanium/Chart.yaml")"
fi

load_local() {
  local name="$1" tag="$2"
  local tar="$root/artifacts/images/image-oci-$name/image.oci.tar"
  if [[ ! -f "$tar" ]]; then
    tar="$(find "$root/artifacts/images" -name "image.oci.tar" -path "*$name*" | head -1)"
  fi
  if [[ -z "${tar:-}" || ! -f "$tar" ]]; then
    echo "ASSERT: missing OCI layout for $name" >&2
    exit 1
  fi
  skopeo copy --override-os linux --override-arch amd64 "oci-archive:$tar" "docker-daemon:${tag}"
}

case "$which" in
  titanium)
    cluster=twp
    ns=twp-smoke
    rel=twp
    ;;
  gateway)
    cluster=twp-gateway
    ns=gateway-system
    rel=titanium-gateway
    ;;
  manager)
    cluster=twp-manager
    ns=mgr-smoke
    rel=twp-mgr
    ;;
esac

install_local() {
  local tgz="$1"
  shift
  helm install "$rel" "$tgz" "$@"
}

install_oci() {
  local name="$1"
  shift
  helm install "$rel" "oci://ghcr.io/justcoding121/charts/$name" --version "$chart_version" "$@"
}

if [[ "$which" == "titanium" ]]; then
  kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  helm uninstall "$rel" -n "$ns" --ignore-not-found >/dev/null 2>&1 || true
  if ! kubectl -n "$ns" get deployment backend >/dev/null 2>&1; then
    kubectl -n "$ns" create deployment backend --image=nginx:1.27-alpine --port=80
    kubectl -n "$ns" expose deployment backend --port=80
  fi
  kubectl -n "$ns" rollout status deployment/backend --timeout=120s
  args=(-n "$ns" -f "$root/deploy/helm/titanium/ci/smoke-values.yaml" --set serviceMonitor.enabled=false --set admin.service.enabled=true --wait --timeout 180s)
  if [[ "$source_mode" == "release-tag" ]]; then
    helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium" --version "$chart_version" "${args[@]}" \
      --set image.repository=ghcr.io/justcoding121/titanium-web-proxy \
      --set image.tag="$version" \
      --set image.pullPolicy=IfNotPresent
  else
    load_local proxy titanium-web-proxy:ci
    kind load docker-image titanium-web-proxy:ci --name "$cluster"
    tgz="$(ls "$charts_dir"/titanium-*.tgz | head -1)"
    helm install "$rel" "$tgz" "${args[@]}"
  fi
  token="$(kubectl -n "$ns" get secret "${rel}-titanium-admin" -o jsonpath='{.data.admin-token}' | base64 -d)"
  kubectl -n "$ns" port-forward "svc/${rel}-titanium-admin" 19901:9901 >/tmp/pf-admin.log 2>&1 &
  pf1=$!
  kubectl -n "$ns" port-forward "svc/${rel}-titanium" 18080:8080 >/tmp/pf-proxy.log 2>&1 &
  pf2=$!
  sleep 3
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:19901/healthz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:19901/readyz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:19901/metrics)" = "401"
  curl -fsS http://127.0.0.1:18080/ | grep -qi nginx
  kill "$pf1" "$pf2" || true
fi

if [[ "$which" == "gateway" ]]; then
  app_ns=gw-smoke
  kubectl create namespace "$app_ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  helm uninstall "$rel" --namespace "$ns" --ignore-not-found >/dev/null 2>&1 || true
  if ! kubectl -n "$app_ns" get deployment backend >/dev/null 2>&1; then
    kubectl -n "$app_ns" create deployment backend --image=nginx:1.27-alpine --port=80
    kubectl -n "$app_ns" expose deployment backend --port=80
  fi
  kubectl -n "$app_ns" rollout status deployment/backend --timeout=120s
  args=(--namespace "$ns" --create-namespace -f "$root/deploy/helm/titanium-gateway/ci/kind-values.yaml" --wait --timeout 8m)
  if [[ "$source_mode" == "release-tag" ]]; then
    helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium-gateway" --version "$chart_version" "${args[@]}" \
      --set controller.image.repository=ghcr.io/justcoding121/titanium-gateway \
      --set controller.image.tag="$version" \
      --set controller.image.pullPolicy=IfNotPresent \
      --set dataPlane.image.repository=ghcr.io/justcoding121/titanium-web-proxy \
      --set dataPlane.image.tag="$version" \
      --set dataPlane.image.pullPolicy=IfNotPresent
  else
    load_local proxy titanium-web-proxy:ci
    load_local gateway titanium-gateway:ci
    kind load docker-image titanium-web-proxy:ci titanium-gateway:ci --name "$cluster"
    tgz="$(ls "$charts_dir"/titanium-gateway-*.tgz | head -1)"
    helm install "$rel" "$tgz" "${args[@]}"
  fi
  kubectl -n "$ns" wait gatewayclass/titanium --for=condition=Accepted --timeout=120s
  cat <<'YAML' | kubectl apply -f -
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: web
  namespace: gw-smoke
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
  name: web
  namespace: gw-smoke
spec:
  parentRefs:
    - name: web
  hostnames:
    - app.test
  rules:
    - backendRefs:
        - name: backend
          port: 80
YAML
  programmed=""
  for _ in $(seq 1 60); do
    programmed="$(kubectl -n gw-smoke get gateway web -o jsonpath='{.status.conditions[?(@.type=="Programmed")].status}' 2>/dev/null || true)"
    [[ "$programmed" == "True" ]] && break
    sleep 5
  done
  test "$programmed" = "True"
  kubectl -n "$ns" rollout status "deployment/${rel}-data" --timeout=180s
  kubectl -n "$ns" port-forward "svc/${rel}-data" 28080:80 >/tmp/pf-gw.log 2>&1 &
  pf=$!
  ok=0
  for _ in $(seq 1 30); do
    if curl -fsS -H 'Host: app.test' http://127.0.0.1:28080/ | grep -qi nginx; then
      ok=1
      break
    fi
    sleep 2
  done
  test "$ok" = "1"
  kill "$pf" || true
  kubectl -n "$ns" port-forward "deployment/${rel}-controller" 28081:8081 >/tmp/pf-gwc.log 2>&1 &
  pf2=$!
  sleep 2
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:28081/healthz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:28081/readyz)" = "200"
  kill "$pf2" || true
fi

if [[ "$which" == "manager" ]]; then
  kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  helm uninstall "$rel" -n "$ns" --ignore-not-found >/dev/null 2>&1 || true
  args=(-n "$ns" -f "$root/deploy/helm/titanium-manager/ci/smoke-values.yaml" --wait --timeout 5m)
  if [[ "$source_mode" == "release-tag" ]]; then
    helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium-manager" --version "$chart_version" "${args[@]}" \
      --set image.repository=ghcr.io/justcoding121/titanium-manager \
      --set image.tag="$version" \
      --set image.pullPolicy=IfNotPresent
  else
    load_local manager titanium-manager:ci
    kind load docker-image titanium-manager:ci --name "$cluster"
    tgz="$(ls "$charts_dir"/titanium-manager-*.tgz | head -1)"
    helm install "$rel" "$tgz" "${args[@]}"
  fi
  token="$(kubectl -n "$ns" get secret "${rel}-titanium-manager-admin" -o jsonpath='{.data.admin-token}' | base64 -d)"
  test -n "$token"
  kubectl -n "$ns" port-forward "svc/${rel}-titanium-manager" 27700:7700 >/tmp/pf-mgr.log 2>&1 &
  pf=$!
  sleep 3
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:27700/v1/healthz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${token}" http://127.0.0.1:27700/v1/fleets)" = "200"
  kill "$pf" || true
fi
echo "kind chart $which ok"
