#!/usr/bin/env bash
# GPG-sign SHA256SUMS (and optional release-manifest.json) when GPG_PRIVATE_KEY is set.
set -euo pipefail

DIST_DIR="${1:?dist directory}"
cd "${DIST_DIR}"

# Build checksums for all release artifacts
: > SHA256SUMS
for f in *; do
  [[ -f "$f" ]] || continue
  [[ "$f" == SHA256SUMS* ]] && continue
  sha256sum "$f" >> SHA256SUMS
done

if [[ -z "${GPG_PRIVATE_KEY:-}" ]]; then
  echo "warn: GPG_PRIVATE_KEY not set — wrote SHA256SUMS without signature"
  exit 0
fi

GNUPGHOME="$(mktemp -d)"
export GNUPGHOME
trap 'rm -rf "${GNUPGHOME}"' EXIT
chmod 700 "${GNUPGHOME}"

printf '%s\n' "${GPG_PRIVATE_KEY}" | gpg --batch --import
# Optional passphrase via GPG_PASSPHRASE
ARGS=(--batch --yes --detach-sign --armor)
if [[ -n "${GPG_PASSPHRASE:-}" ]]; then
  ARGS+=(--pinentry-mode loopback --passphrase "${GPG_PASSPHRASE}")
fi
gpg "${ARGS[@]}" -o SHA256SUMS.asc SHA256SUMS
if [[ -f release-manifest.json ]]; then
  gpg "${ARGS[@]}" -o release-manifest.json.asc release-manifest.json
fi
echo "Wrote SHA256SUMS and signatures"
