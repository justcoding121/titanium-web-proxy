#!/usr/bin/env bash
# Fast kind proof of one packaged chart. Installs the .tgz (or the OCI chart
# when SOURCE=release-tag) and checks the same probes as kubernetes-smoke.yml.
# Deep reload and rolling restart stay in the nightly workflow.
# Usage: kind-chart.sh titanium|gateway|manager
set -euo pipefail
which="${1:?titanium|gateway|manager}"
source "$(dirname "$0")/kind-chart-common.sh"
root="$_kind_chart_root"
source_mode="${SOURCE:-run-artifacts}"
version="${RELEASE_VERSION:-}"
chart_version="${CHART_VERSION:-}"
tag="${RELEASE_TAG:-}"
kind_chart_resolve_versions
version="${RELEASE_VERSION:-}"
chart_version="${CHART_VERSION:-}"
charts_dir="$(kind_chart_tgz_dir)"

kind_chart_set_context "$which"

load_local() { kind_chart_load_local "$@"; }
preload_published() { kind_chart_preload_published "$@"; }
dump_failed_install() { kind_chart_dump_failed_install; }

if [[ "$which" == "titanium" ]]; then
  kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  helm uninstall "$rel" -n "$ns" --ignore-not-found >/dev/null 2>&1 || true
  kind_chart_ensure_titanium_backend
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
  kind_chart_assert_probes titanium
fi

if [[ "$which" == "gateway" ]]; then
  kind_chart_ensure_gateway_backend
  helm uninstall "$rel" --namespace "$ns" --ignore-not-found >/dev/null 2>&1 || true
  args=(--namespace "$ns" --create-namespace -f "$root/deploy/helm/titanium-gateway/ci/kind-values.yaml" --wait --timeout 8m --set dataPlane.service.type=ClusterIP)
  if [[ "$source_mode" == "release-tag" ]]; then
    preload_published "ghcr.io/justcoding121/titanium-gateway:${version}"
    preload_published "ghcr.io/justcoding121/titanium-web-proxy:${version}"
    if ! helm install "$rel" "oci://ghcr.io/justcoding121/charts/titanium-gateway" --version "$chart_version" "${args[@]}" \
      --set controller.image.repository=ghcr.io/justcoding121/titanium-gateway \
      --set controller.image.tag="$version" \
      --set controller.image.pullPolicy=IfNotPresent \
      --set dataPlane.image.repository=ghcr.io/justcoding121/titanium-web-proxy \
      --set dataPlane.image.tag="$version" \
      --set dataPlane.image.pullPolicy=IfNotPresent; then
      dump_failed_install
      exit 1
    fi
  else
    load_local proxy titanium-web-proxy:ci
    load_local gateway titanium-gateway:ci
    kind load docker-image titanium-web-proxy:ci titanium-gateway:ci --name "$cluster"
    tgz="$(ls "$charts_dir"/titanium-gateway-*.tgz | head -1)"
    if ! helm install "$rel" "$tgz" "${args[@]}"; then
      dump_failed_install
      exit 1
    fi
  fi
  kind_chart_assert_probes gateway
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
  kind_chart_assert_probes manager
fi
echo "kind chart $which ok"