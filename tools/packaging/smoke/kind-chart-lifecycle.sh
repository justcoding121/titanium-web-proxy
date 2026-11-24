#!/usr/bin/env bash
# After kind-chart.sh: upgrade from published OCI, helm rollback, uninstall hygiene.
# Usage: kind-chart-lifecycle.sh titanium|gateway|manager
set -euo pipefail
which="${1:?titanium|gateway|manager}"
source "$(dirname "$0")/kind-chart-common.sh"
root="$_kind_chart_root"
source_mode="${SOURCE:-run-artifacts}"
kind_chart_resolve_versions
version="${RELEASE_VERSION:-candidate}"
chart_version="${CHART_VERSION:-}"
charts_dir="$(kind_chart_tgz_dir)"
kind_chart_set_context "$which"

skip_upgrade() {
  echo "skip chart upgrade-from-published ($1)"
  return 0
}

resolve_prev_published() {
  PREV_RELEASE_VERSION="${PREV_RELEASE_VERSION:-}"
  PREV_CHART_VERSION="${PREV_CHART_VERSION:-}"
  if [[ -n "$PREV_RELEASE_VERSION" && -n "$PREV_CHART_VERSION" ]]; then
    return 0
  fi
  if [[ -z "${GH_TOKEN:-}" ]] || ! command -v gh >/dev/null 2>&1; then
    return 1
  fi
  local tag
  tag="$(gh api "repos/${GITHUB_REPOSITORY}/releases?per_page=15" \
    --jq '[.[] | select(.draft==false)][0].tag_name' 2>/dev/null || true)"
  if [[ -z "$tag" || "$tag" == "null" ]]; then
    return 1
  fi
  PREV_RELEASE_VERSION="${tag#v}"
  PREV_CHART_VERSION="$(kind_chart_product_to_chart_semver "$PREV_RELEASE_VERSION")"
}

oci_chart_exists() {
  helm show chart "oci://ghcr.io/justcoding121/charts/${chart_oci}" --version "$1" >/dev/null 2>&1
}

install_prev_oci() {
  case "$which" in
    titanium)
      kind_chart_ensure_titanium_backend
      helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium" --version "$PREV_CHART_VERSION" \
        -n "$ns" -f "$root/deploy/helm/titanium/ci/smoke-values.yaml" \
        --set serviceMonitor.enabled=false --set admin.service.enabled=true \
        --set image.repository=ghcr.io/justcoding121/titanium-web-proxy \
        --set image.tag="$PREV_RELEASE_VERSION" \
        --set image.pullPolicy=IfNotPresent \
        --wait --timeout 180s
      ;;
    gateway)
      kind_chart_ensure_gateway_backend
      kind_chart_preload_published "ghcr.io/justcoding121/titanium-gateway:${PREV_RELEASE_VERSION}"
      kind_chart_preload_published "ghcr.io/justcoding121/titanium-web-proxy:${PREV_RELEASE_VERSION}"
      helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium-gateway" --version "$PREV_CHART_VERSION" \
        --namespace "$ns" --create-namespace \
        -f "$root/deploy/helm/titanium-gateway/ci/kind-values.yaml" \
        --set dataPlane.service.type=ClusterIP \
        --set controller.image.repository=ghcr.io/justcoding121/titanium-gateway \
        --set controller.image.tag="$PREV_RELEASE_VERSION" \
        --set controller.image.pullPolicy=IfNotPresent \
        --set dataPlane.image.repository=ghcr.io/justcoding121/titanium-web-proxy \
        --set dataPlane.image.tag="$PREV_RELEASE_VERSION" \
        --set dataPlane.image.pullPolicy=IfNotPresent \
        --wait --timeout 8m
      ;;
    manager)
      helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium-manager" --version "$PREV_CHART_VERSION" \
        -n "$ns" -f "$root/deploy/helm/titanium-manager/ci/smoke-values.yaml" \
        --set image.repository=ghcr.io/justcoding121/titanium-manager \
        --set image.tag="$PREV_RELEASE_VERSION" \
        --set image.pullPolicy=IfNotPresent \
        --wait --timeout 5m
      ;;
  esac
}

upgrade_to_candidate() {
  local tgz
  tgz="$(ls "$charts_dir"/$tgz_glob | head -1)"
  case "$which" in
    titanium)
      kind_chart_load_local proxy titanium-web-proxy:ci
      kind load docker-image titanium-web-proxy:ci --name "$cluster"
      helm upgrade "$rel" "$tgz" -n "$ns" \
        -f "$root/deploy/helm/titanium/ci/smoke-values.yaml" \
        --set serviceMonitor.enabled=false --set admin.service.enabled=true \
        --wait --timeout 180s
      ;;
    gateway)
      kind_chart_load_local proxy titanium-web-proxy:ci
      kind_chart_load_local gateway titanium-gateway:ci
      kind load docker-image titanium-web-proxy:ci titanium-gateway:ci --name "$cluster"
      helm upgrade "$rel" "$tgz" --namespace "$ns" \
        -f "$root/deploy/helm/titanium-gateway/ci/kind-values.yaml" \
        --set dataPlane.service.type=ClusterIP \
        --wait --timeout 8m
      ;;
    manager)
      kind_chart_load_local manager titanium-manager:ci
      kind load docker-image titanium-manager:ci --name "$cluster"
      helm upgrade "$rel" "$tgz" -n "$ns" \
        -f "$root/deploy/helm/titanium-manager/ci/smoke-values.yaml" \
        --wait --timeout 5m
      ;;
  esac
}

install_candidate_release_tag() {
  case "$which" in
    titanium)
      kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
      kind_chart_ensure_titanium_backend
      helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium" --version "$chart_version" \
        -n "$ns" -f "$root/deploy/helm/titanium/ci/smoke-values.yaml" \
        --set serviceMonitor.enabled=false --set admin.service.enabled=true \
        --set image.repository=ghcr.io/justcoding121/titanium-web-proxy \
        --set image.tag="$version" \
        --set image.pullPolicy=IfNotPresent \
        --wait --timeout 180s
      ;;
    gateway)
      kind_chart_ensure_gateway_backend
      kind_chart_preload_published "ghcr.io/justcoding121/titanium-gateway:${version}"
      kind_chart_preload_published "ghcr.io/justcoding121/titanium-web-proxy:${version}"
      helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium-gateway" --version "$chart_version" \
        --namespace "$ns" --create-namespace \
        -f "$root/deploy/helm/titanium-gateway/ci/kind-values.yaml" \
        --set dataPlane.service.type=ClusterIP \
        --set controller.image.repository=ghcr.io/justcoding121/titanium-gateway \
        --set controller.image.tag="$version" \
        --set controller.image.pullPolicy=IfNotPresent \
        --set dataPlane.image.repository=ghcr.io/justcoding121/titanium-web-proxy \
        --set dataPlane.image.tag="$version" \
        --set dataPlane.image.pullPolicy=IfNotPresent \
        --wait --timeout 8m
      ;;
    manager)
      kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
      helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium-manager" --version "$chart_version" \
        -n "$ns" -f "$root/deploy/helm/titanium-manager/ci/smoke-values.yaml" \
        --set image.repository=ghcr.io/justcoding121/titanium-manager \
        --set image.tag="$version" \
        --set image.pullPolicy=IfNotPresent \
        --wait --timeout 5m
      ;;
  esac
}

install_candidate_tgz() {
  local tgz
  tgz="$(ls "$charts_dir"/$tgz_glob | head -1)"
  case "$which" in
    titanium)
      kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
      kind_chart_ensure_titanium_backend
      kind_chart_load_local proxy titanium-web-proxy:ci
      kind load docker-image titanium-web-proxy:ci --name "$cluster"
      helm install "$rel" "$tgz" -n "$ns" \
        -f "$root/deploy/helm/titanium/ci/smoke-values.yaml" \
        --set serviceMonitor.enabled=false --set admin.service.enabled=true \
        --wait --timeout 180s
      ;;
    gateway)
      kind_chart_ensure_gateway_backend
      kind_chart_load_local proxy titanium-web-proxy:ci
      kind_chart_load_local gateway titanium-gateway:ci
      kind load docker-image titanium-web-proxy:ci titanium-gateway:ci --name "$cluster"
      helm install "$rel" "$tgz" --namespace "$ns" --create-namespace \
        -f "$root/deploy/helm/titanium-gateway/ci/kind-values.yaml" \
        --set dataPlane.service.type=ClusterIP \
        --wait --timeout 8m
      ;;
    manager)
      kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
      kind_chart_load_local manager titanium-manager:ci
      kind load docker-image titanium-manager:ci --name "$cluster"
      helm install "$rel" "$tgz" -n "$ns" \
        -f "$root/deploy/helm/titanium-manager/ci/smoke-values.yaml" \
        --wait --timeout 5m
      ;;
  esac
}

# --- upgrade from published ---
upgrade_ok=0
if [[ "$source_mode" != "run-artifacts" ]]; then
  skip_upgrade "SOURCE=$source_mode"
elif ! resolve_prev_published; then
  export FIRST_RELEASE=true
  skip_upgrade "no published release metadata"
elif [[ -z "$PREV_CHART_VERSION" ]]; then
  export FIRST_RELEASE=true
  skip_upgrade "could not map prev chart semver"
elif ! oci_chart_exists "$PREV_CHART_VERSION"; then
  export FIRST_RELEASE=true
  skip_upgrade "oci chart $chart_oci:$PREV_CHART_VERSION unavailable"
elif [[ "$PREV_CHART_VERSION" == "$chart_version" ]]; then
  export FIRST_RELEASE=true
  skip_upgrade "prev chart version equals candidate ($chart_version)"
else
  helm uninstall "$rel" -n "$ns" --ignore-not-found >/dev/null 2>&1 || true
  helm uninstall "$rel" --namespace "$ns" --ignore-not-found >/dev/null 2>&1 || true
  kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  install_prev_oci
  upgrade_to_candidate
  kind_chart_assert_probes "$which"
  echo "chart $which upgrade $PREV_CHART_VERSION -> candidate ok"
  upgrade_ok=1
fi

# --- rollback ---
if [[ "$upgrade_ok" == "1" ]]; then
  if [[ "$which" == gateway ]]; then
    helm rollback "$rel" 1 --namespace "$ns" --wait --timeout 8m
  else
    helm rollback "$rel" 1 -n "$ns" --wait --timeout 180s
  fi
  kind_chart_assert_probes "$which"
  echo "chart $which rollback ok"
else
  echo "skip chart rollback (upgrade-from-published did not run)"
fi

# --- uninstall hygiene ---
helm uninstall "$rel" -n "$ns" --ignore-not-found --wait --timeout 120s >/dev/null 2>&1 || true
helm uninstall "$rel" --namespace "$ns" --ignore-not-found --wait --timeout 180s >/dev/null 2>&1 || true
kind_chart_assert_no_instance_leftovers
if [[ "$which" == gateway ]]; then
  kind_chart_assert_gateway_crds
fi
if [[ "$source_mode" == "release-tag" ]]; then
  install_candidate_release_tag
else
  install_candidate_tgz
fi
kind_chart_assert_probes "$which"
echo "chart $which uninstall hygiene ok"