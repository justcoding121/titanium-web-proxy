# Shared helpers for kind-chart.sh and kind-chart-lifecycle.sh (source only).
_kind_chart_smoke_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
_kind_chart_root="$(cd "$_kind_chart_smoke_dir/../../.." && pwd)"

kind_chart_resolve_versions() {
  local source_mode="${SOURCE:-run-artifacts}"
  export RELEASE_VERSION="${RELEASE_VERSION:-}"
  export CHART_VERSION="${CHART_VERSION:-}"
  local tag="${RELEASE_TAG:-}"
  if [[ "$source_mode" == "release-tag" && -z "$RELEASE_VERSION" ]]; then
    if [[ -z "$tag" ]]; then
      tag="$(gh api "repos/${GITHUB_REPOSITORY}/releases" --jq '[.[] | select(.prerelease==true)][0].tag_name')"
    fi
    RELEASE_VERSION="${tag#v}"
  fi
  if [[ -z "$CHART_VERSION" ]]; then
    if [[ "$source_mode" == "release-tag" ]]; then
      local ref="${tag:-}"
      if [[ -z "$ref" && -n "$RELEASE_VERSION" ]]; then
        ref="v${RELEASE_VERSION}"
      fi
      if [[ -n "$ref" ]]; then
        CHART_VERSION="$(git -C "$_kind_chart_root" show "${ref}:deploy/helm/titanium/Chart.yaml" 2>/dev/null \
          | awk '/^version:/ { print $2; exit }' || true)"
      fi
      if [[ -z "$CHART_VERSION" && -n "$RELEASE_VERSION" ]]; then
        CHART_VERSION="$(python3 - "$RELEASE_VERSION" <<'PY' 2>/dev/null || true
import sys
v = sys.argv[1].split("-", 1)[0]
parts = v.split(".")
if len(parts) >= 3 and parts[0].isdigit():
    print(f"1.{parts[1]}.{parts[2]}")
PY
)"
      fi
    fi
    if [[ -z "$CHART_VERSION" ]]; then
      CHART_VERSION="$(awk '/^version:/ { print $2; exit }' "$_kind_chart_root/deploy/helm/titanium/Chart.yaml")"
    fi
  fi
}

kind_chart_set_context() {
  local which="$1"
  case "$which" in
    titanium)
      cluster=twp
      ns=twp-smoke
      rel=twp
      chart_oci=titanium
      tgz_glob=titanium-*.tgz
      ;;
    gateway)
      cluster=twp-gateway
      ns=gateway-system
      rel=titanium-gateway
      chart_oci=titanium-gateway
      tgz_glob=titanium-gateway-*.tgz
      app_ns=gw-smoke
      ;;
    manager)
      cluster=twp-manager
      ns=mgr-smoke
      rel=twp-mgr
      chart_oci=titanium-manager
      tgz_glob=titanium-manager-*.tgz
      ;;
    *)
      echo "ASSERT: unknown chart $which" >&2
      return 1
      ;;
  esac
}

kind_chart_product_to_chart_semver() {
  python3 - "$1" <<'PY'
import sys
v = sys.argv[1].split("-", 1)[0]
parts = v.split(".")
if len(parts) >= 3 and parts[0].isdigit():
    print(f"1.{parts[1]}.{parts[2]}")
PY
}

kind_chart_load_local() {
  local name="$1" tag="$2"
  local tar="$_kind_chart_root/artifacts/images/image-oci-$name/image.oci.tar"
  if [[ ! -f "$tar" ]]; then
    tar="$(find "$_kind_chart_root/artifacts/images" -name "image.oci.tar" -path "*$name*" | head -1)"
  fi
  if [[ -z "${tar:-}" || ! -f "$tar" ]]; then
    echo "ASSERT: missing OCI layout for $name" >&2
    return 1
  fi
  skopeo copy --override-os linux --override-arch amd64 "oci-archive:$tar" "docker-daemon:${tag}"
}

kind_chart_preload_published() {
  local ref="$1"
  docker pull "$ref"
  kind load docker-image "$ref" --name "$cluster"
}

kind_chart_dump_failed_install() {
  echo "helm install failed; cluster snapshot:" >&2
  kubectl get pods -A -o wide >&2 || true
  kubectl get svc -A >&2 || true
  kubectl get events -A --sort-by=.lastTimestamp >&2 | tail -n 40 || true
}

kind_chart_assert_titanium_probes() {
  local token
  token="$(kubectl -n "$ns" get secret "${rel}-titanium-admin" -o jsonpath='{.data.admin-token}' | base64 -d)"
  kubectl -n "$ns" port-forward "svc/${rel}-titanium-admin" 19901:9901 >/tmp/pf-admin.log 2>&1 &
  local pf1=$!
  kubectl -n "$ns" port-forward "svc/${rel}-titanium" 18080:8080 >/tmp/pf-proxy.log 2>&1 &
  local pf2=$!
  sleep 3
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:19901/healthz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:19901/readyz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:19901/metrics)" = "401"
  curl -fsS http://127.0.0.1:18080/ | grep -qi nginx
  kill "$pf1" "$pf2" 2>/dev/null || true
}

kind_chart_assert_gateway_probes() {
  local app_ns="${app_ns:-gw-smoke}"
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
  local programmed=""
  for _ in $(seq 1 60); do
    programmed="$(kubectl -n "$app_ns" get gateway web -o jsonpath='{.status.conditions[?(@.type=="Programmed")].status}' 2>/dev/null || true)"
    [[ "$programmed" == "True" ]] && break
    sleep 5
  done
  test "$programmed" = "True"
  kubectl -n "$ns" rollout status "deployment/${rel}-data" --timeout=180s
  kubectl -n "$ns" port-forward "svc/${rel}-data" 28080:80 >/tmp/pf-gw.log 2>&1 &
  local pf=$!
  local ok=0
  for _ in $(seq 1 30); do
    if curl -fsS -H 'Host: app.test' http://127.0.0.1:28080/ | grep -qi nginx; then
      ok=1
      break
    fi
    sleep 2
  done
  test "$ok" = "1"
  kill "$pf" 2>/dev/null || true
  kubectl -n "$ns" port-forward "deployment/${rel}-controller" 28081:8081 >/tmp/pf-gwc.log 2>&1 &
  local pf2=$!
  sleep 2
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:28081/healthz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:28081/readyz)" = "200"
  kill "$pf2" 2>/dev/null || true
}

kind_chart_assert_manager_probes() {
  local token
  token="$(kubectl -n "$ns" get secret "${rel}-titanium-manager-admin" -o jsonpath='{.data.admin-token}' | base64 -d)"
  test -n "$token"
  kubectl -n "$ns" port-forward "svc/${rel}-titanium-manager" 27700:7700 >/tmp/pf-mgr.log 2>&1 &
  local pf=$!
  sleep 3
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:27700/v1/healthz)" = "200"
  test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${token}" http://127.0.0.1:27700/v1/fleets)" = "200"
  kill "$pf" 2>/dev/null || true
}

kind_chart_assert_probes() {
  local which="$1"
  case "$which" in
    titanium) kind_chart_assert_titanium_probes ;;
    gateway) kind_chart_assert_gateway_probes ;;
    manager) kind_chart_assert_manager_probes ;;
  esac
}

# Recreate an empty Opaque TLS secret when a legacy published chart left none
# after uninstall/rollback. Candidate charts always render the Secret with keep.
kind_chart_ensure_gateway_tls_secret() {
  local tls="${rel}-data-tls"
  if kubectl -n "$ns" get secret "$tls" >/dev/null 2>&1; then
    kubectl -n "$ns" annotate secret "$tls" "helm.sh/resource-policy=keep" --overwrite >/dev/null 2>&1 || true
    return 0
  fi
  kubectl -n "$ns" create secret generic "$tls" --from-literal=placeholder=1 >/dev/null
  kubectl -n "$ns" label secret "$tls" \
    "app.kubernetes.io/instance=${rel}" \
    "app.kubernetes.io/name=titanium-gateway" \
    --overwrite >/dev/null 2>&1 || true
  kubectl -n "$ns" annotate secret "$tls" "helm.sh/resource-policy=keep" --overwrite >/dev/null 2>&1 || true
}

kind_chart_assert_no_instance_leftovers() {
  # "strict" reports kept ConfigMaps and Secrets. The default still clears them
  # so the pre-upgrade wait can finish; the final uninstall check is strict.
  local mode="${1:-clean}"
  local scope=()
  if [[ "${which:-}" == gateway ]]; then
    scope=(-A)
  else
    scope=(-n "$ns")
  fi
  # helm uninstall --wait can still leave terminating pods briefly; wait then force-delete.
  local attempt leftovers=""
  for attempt in $(seq 1 30); do
    leftovers=""
    for kind in all secrets configmaps serviceaccounts role rolebindings poddisruptionbudget pvc; do
      local chunk
      chunk="$(kubectl get "$kind" -l "app.kubernetes.io/instance=${rel}" "${scope[@]}" -o name 2>/dev/null || true)"
      if [[ -n "$chunk" ]]; then
        leftovers+="${chunk}"$'\n'
      fi
    done
    if [[ -z "$leftovers" ]]; then
      return 0
    fi
    if [[ "$attempt" -eq 1 || "$attempt" -eq 5 || "$attempt" -eq 15 ]]; then
      if [[ "$mode" == "strict" ]]; then
        kubectl delete pods,rs,deploy,sts,pvc -l "app.kubernetes.io/instance=${rel}" "${scope[@]}" \
          --force --grace-period=0 >/dev/null 2>&1 || true
      else
        kubectl delete pods,rs,deploy,sts,pvc,configmap,secret -l "app.kubernetes.io/instance=${rel}" "${scope[@]}" \
          --force --grace-period=0 >/dev/null 2>&1 || true
      fi
    fi
    sleep 2
  done
  echo "ASSERT: resources still labeled app.kubernetes.io/instance=${rel} after uninstall:" >&2
  printf '%s' "$leftovers" >&2
  return 1
}

kind_chart_assert_gateway_crds() {
  kubectl get crd gateways.gateway.networking.k8s.io httproutes.gateway.networking.k8s.io \
    gatewayclasses.gateway.networking.k8s.io >/dev/null
}

kind_chart_ensure_titanium_backend() {
  if ! kubectl -n "$ns" get deployment backend >/dev/null 2>&1; then
    kubectl -n "$ns" create deployment backend --image=nginx:1.27-alpine --port=80
    kubectl -n "$ns" expose deployment backend --port=80
  fi
  kubectl -n "$ns" rollout status deployment/backend --timeout=120s
}

kind_chart_ensure_gateway_backend() {
  local app_ns="${app_ns:-gw-smoke}"
  kubectl create namespace "$app_ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  if ! kubectl -n "$app_ns" get deployment backend >/dev/null 2>&1; then
    kubectl -n "$app_ns" create deployment backend --image=nginx:1.27-alpine --port=80
    kubectl -n "$app_ns" expose deployment backend --port=80
  fi
  kubectl -n "$app_ns" rollout status deployment/backend --timeout=120s
}

kind_chart_tgz_dir() {
  echo "${CHARTS_DIR:-$_kind_chart_root/charts}"
}