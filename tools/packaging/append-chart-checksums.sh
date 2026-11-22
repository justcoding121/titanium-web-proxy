#!/usr/bin/env bash
# Add Helm chart archives to an existing SHA256SUMS file.
# Chart packages are uploaded after the first sums file is signed, so the
# chart job rebuilds those lines and the release job signs the file again.
# Usage: append-chart-checksums.sh SHA256SUMS charts-dir
set -euo pipefail

sums="${1:?SHA256SUMS path}"
dir="${2:?directory of .tgz files}"

tmp="$(mktemp)"
# Drop any previous chart lines so a re-run does not duplicate them.
grep -vE '[[:space:]][^[:space:]]+\.tgz$' "$sums" > "$tmp" || true
shopt -s nullglob
found=0
for tgz in "$dir"/*.tgz; do
  found=1
  (
    cd "$dir"
    sha256sum "$(basename "$tgz")"
  ) >> "$tmp"
done
if [[ "$found" -eq 0 ]]; then
  echo "no chart archives in $dir" >&2
  exit 1
fi
mv "$tmp" "$sums"
