#!/usr/bin/env bash
# Push smoked Helm chart .tgz files to an OCI registry.
# Usage: push-charts.sh <charts-dir> <oci://registry/path>
set -euo pipefail
charts="${1:?charts dir}"
registry="${2:?oci registry e.g. oci://ghcr.io/justcoding121/charts}"

shopt -s nullglob
tgzs=("$charts"/*.tgz)
if [[ ${#tgzs[@]} -eq 0 ]]; then
  echo "ASSERT: no .tgz charts in $charts" >&2
  exit 1
fi

helm_push_args=()
if [[ "${HELM_PLAIN_HTTP:-}" == "1" ]] || [[ "$registry" == oci://localhost:* ]] || [[ "$registry" == oci://127.0.0.1:* ]]; then
  helm_push_args+=(--plain-http)
fi

for tgz in "${tgzs[@]}"; do
  echo "helm push $tgz $registry ${helm_push_args[*]:-}"
  helm push "$tgz" "$registry" ${helm_push_args[@]+"${helm_push_args[@]}"}
done

# Optional pull-back check: when PULL_BACK=1, fetch each chart by version.
if [[ "${PULL_BACK:-}" == "1" ]]; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  for tgz in "${tgzs[@]}"; do
    base="$(basename "$tgz" .tgz)"
    if [[ ! "$base" =~ ^(.*)-([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
      echo "ASSERT: cannot parse chart name/version from $base" >&2
      exit 1
    fi
    chart_name="${BASH_REMATCH[1]}"
    ver="${BASH_REMATCH[2]}"
    out="$tmp/$chart_name"
    mkdir -p "$out"
    helm pull "$registry/$chart_name" --version "$ver" -d "$out" \
      ${helm_push_args[@]+"${helm_push_args[@]}"}
    pulled="$(ls "$out"/*.tgz)"
    if ! cmp -s "$tgz" "$pulled"; then
      echo "ASSERT: pulled $pulled differs from smoked $tgz" >&2
      exit 1
    fi
    echo "pull-back ok $chart_name $ver"
  done
fi

echo "published ${#tgzs[@]} charts to $registry"
