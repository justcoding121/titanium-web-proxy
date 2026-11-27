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

# 8.0.N ships chart 1.0.N so a missed bump cannot overwrite the previous OCI chart.
expected_chart="1.0.${prefix##*.}"
for chart_yaml in deploy/helm/titanium/Chart.yaml \
  deploy/helm/titanium-gateway/Chart.yaml \
  deploy/helm/titanium-manager/Chart.yaml; do
  chart_ver="$(awk '/^version:/ { print $2; exit }' "$chart_yaml")"
  if [[ "$chart_ver" != "$expected_chart" ]]; then
    echo "ASSERT: $chart_yaml version '$chart_ver' should be $expected_chart for product $prefix" >&2
    exit 1
  fi
done
# Stable reuses the beta chart semver (8.0.N → 1.0.N). Ignore prerelease
# releases so a GA dry-run/cut does not fail on the beta's titanium-1.0.N.tgz.
# Beta cuts still scan every non-draft release so a missed bump cannot overwrite.
# source=release-tag is a check of an already published release, so the chart
# archive is supposed to be there.
if [[ "${SOURCE:-}" == "release-tag" ]]; then
  echo "skip published-chart overwrite guard (SOURCE=release-tag)"
elif [[ -n "${GH_TOKEN:-}" ]] && command -v gh >/dev/null 2>&1; then
  if [[ "$version" == *-* ]]; then
    jq_filter='[.[] | select(.draft==false) | .assets[].name | select(test("^titanium-[0-9]+\\.[0-9]+\\.[0-9]+\\.tgz$"))][0]'
  else
    jq_filter='[.[] | select(.draft==false and .prerelease==false) | .assets[].name | select(test("^titanium-[0-9]+\\.[0-9]+\\.[0-9]+\\.tgz$"))][0]'
  fi
  published="$(gh api "repos/${GITHUB_REPOSITORY}/releases?per_page=20" \
    --jq "$jq_filter" 2>/dev/null || true)"
  if [[ -n "$published" ]]; then
    published_ver="${published#titanium-}"
    published_ver="${published_ver%.tgz}"
    if [[ "$published_ver" == "$expected_chart" ]]; then
      echo "ASSERT: chart $expected_chart is already on a published GitHub Release ($published)" >&2
      exit 1
    fi
  fi
fi

rm -f "$out"/*.tgz
helm package deploy/helm/titanium -d "$out"
helm package deploy/helm/titanium-gateway -d "$out"
helm package deploy/helm/titanium-manager -d "$out"

for tgz in "$out"/*.tgz; do
  listing="$(tar -tf "$tgz")"
  member="$(printf '%s\n' "$listing" | awk '/\/Chart.yaml$/ { print; exit }')"
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
