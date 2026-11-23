#!/usr/bin/env bash
# Lint, template, and package the three charts. appVersion must match the
# product version under test (prerelease suffix stripped). Writes SHA256SUMS
# beside the .tgz files the publish job will push unchanged.
# Usage: helm-static.sh <out-dir>
set -euo pipefail
out="${1:?out dir}"
version="${RELEASE_VERSION:?RELEASE_VERSION}"
prefix="${version%%-*}"
mkdir -p "$out"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
cd "$root"

lint_one() {
  local chart="$1"
  shift
  helm lint --strict "$chart"
  if [[ "$#" -gt 0 ]]; then
    helm lint --strict "$chart" -f "$1"
    helm template smoke "$chart" -f "$1" >/dev/null
  else
    helm template smoke "$chart" >/dev/null
  fi
}

lint_one deploy/helm/titanium deploy/helm/titanium/ci/smoke-values.yaml
lint_one deploy/helm/titanium-gateway deploy/helm/titanium-gateway/ci/kind-values.yaml
lint_one deploy/helm/titanium-manager deploy/helm/titanium-manager/ci/smoke-values.yaml

rm -f "$out"/*.tgz
helm package deploy/helm/titanium -d "$out"
helm package deploy/helm/titanium-gateway -d "$out"
helm package deploy/helm/titanium-manager -d "$out"

for tgz in "$out"/*.tgz; do
  member="$(tar -tf "$tgz" | awk '/\/Chart.yaml$/ { print; exit }')"
  if [[ -z "$member" ]]; then
    echo "ASSERT: $tgz has no Chart.yaml" >&2
    exit 1
  fi
  chart="$(tar -xOf "$tgz" "$member")"
  app="$(printf '%s\n' "$chart" | awk '/^appVersion:/ { gsub(/"/, "", $2); print $2; exit }')"
  if [[ "$app" != "$prefix" && "$app" != "$version" ]]; then
    echo "ASSERT: $(basename "$tgz") appVersion '$app' != release $version" >&2
    exit 1
  fi
done

(
  cd "$out"
  : > SHA256SUMS
  for tgz in *.tgz; do
    if command -v sha256sum >/dev/null 2>&1; then
      sha256sum "$tgz" >> SHA256SUMS
    else
      shasum -a 256 "$tgz" >> SHA256SUMS
    fi
  done
)
echo "packaged charts for $version (appVersion $prefix)"
ls -la "$out"
