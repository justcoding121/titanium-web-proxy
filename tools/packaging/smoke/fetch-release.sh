#!/usr/bin/env bash
# Download published release assets when SOURCE=release-tag.
# Run-artifact jobs download with actions/download-artifact instead.
# Usage: fetch-release.sh <gh release download pattern>...
set -euo pipefail
mkdir -p dist
if [[ "${SOURCE:-run-artifacts}" != "release-tag" ]]; then
  exit 0
fi
tag="${RELEASE_TAG:-}"
if [[ -z "$tag" ]]; then
  tag="$(gh api "repos/${GITHUB_REPOSITORY}/releases" --jq '[.[] | select(.prerelease==true)][0].tag_name')"
fi
if [[ -z "$tag" || "$tag" == "null" ]]; then
  echo "ASSERT: no release tag to audit" >&2
  exit 1
fi
echo "release-tag $tag"
version="${tag#v}"
if [[ -n "${GITHUB_ENV:-}" ]]; then
  echo "RELEASE_TAG=$tag" >> "$GITHUB_ENV"
  echo "RELEASE_VERSION=$version" >> "$GITHUB_ENV"
fi
args=()
for pattern in "$@"; do
  args+=(--pattern "$pattern")
done
gh release download "$tag" --dir dist --clobber "${args[@]}"
