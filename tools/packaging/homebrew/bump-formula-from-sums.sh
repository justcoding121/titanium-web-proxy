#!/usr/bin/env bash
# Rewrite formula URL version segments, sha256 digests, and the optional
# version stanza from a release SHA256SUMS file. Edits the formula in place.
# Stable (X.Y.Z): no version stanza; URLs use literal /vX.Y.Z/.
# Prerelease (contains '-'): keep/insert version "…"; URLs use /vX.Y.Z-suffix/.
# Usage: bump-formula-from-sums.sh <version> <SHA256SUMS> [formula.rb]
#        bump-formula-from-sums.sh --self-test
set -euo pipefail

self_test() {
  local root tmp sums formula
  root="$(cd "$(dirname "$0")" && pwd)"
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' RETURN
  formula="$tmp/titanium.rb"
  cp "$root/titanium.rb" "$formula"
  cat >"$tmp/SHA256SUMS" <<'EOF'
aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  Titanium.Cli-osx-arm64.zip
bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  Titanium.Cli-osx-x64.zip
cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc  Titanium.Cli-linux-arm64.zip
dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd  Titanium.Cli-linux-x64.zip
eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee  other.zip
EOF
  bash "$root/bump-formula-from-sums.sh" "9.9.9-beta" "$tmp/SHA256SUMS" "$formula"
  grep -q 'version "9.9.9-beta"' "$formula"
  ver_line="$(grep -n 'version "9.9.9-beta"' "$formula" | head -1 | cut -d: -f1)"
  lic_line="$(grep -n 'license ' "$formula" | head -1 | cut -d: -f1)"
  if [[ -n "$lic_line" && "$ver_line" -gt "$lic_line" ]]; then
    echo "ASSERT: version stanza follows license" >&2
    exit 1
  fi
  grep -q '/v9.9.9-beta/Titanium.Cli-osx-arm64.zip' "$formula"
  grep -q 'sha256 "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"' "$formula"
  grep -q 'sha256 "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"' "$formula"
  grep -q 'sha256 "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"' "$formula"
  grep -q 'sha256 "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"' "$formula"
  bash "$root/bump-formula-from-sums.sh" "9.9.9" "$tmp/SHA256SUMS" "$formula"
  if grep -qE '^\s*version\s+"' "$formula"; then
    echo "ASSERT: stable bump left a version stanza" >&2
    exit 1
  fi
  grep -q '/v9.9.9/Titanium.Cli-linux-x64.zip' "$formula"
  echo "bump-formula-from-sums self-test ok"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

VERSION="${1:?version}"
SUMS="${2:?SHA256SUMS path}"
FORMULA="${3:-}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
if [[ -z "$FORMULA" ]]; then
  FORMULA="$ROOT/titanium.rb"
fi

hash_for() {
  local name="$1"
  awk -v n="$name" '$2 == n { print $1; exit }' "$SUMS"
}

need() {
  local name="$1" hash="$2"
  if [[ -z "$hash" || ${#hash} -ne 64 ]]; then
    echo "ASSERT: SHA256SUMS is missing a 64-char hash for $name" >&2
    exit 1
  fi
}

arm_osx="$(hash_for Titanium.Cli-osx-arm64.zip)"
x64_osx="$(hash_for Titanium.Cli-osx-x64.zip)"
arm_linux="$(hash_for Titanium.Cli-linux-arm64.zip)"
x64_linux="$(hash_for Titanium.Cli-linux-x64.zip)"
need Titanium.Cli-osx-arm64.zip "$arm_osx"
need Titanium.Cli-osx-x64.zip "$x64_osx"
need Titanium.Cli-linux-arm64.zip "$arm_linux"
need Titanium.Cli-linux-x64.zip "$x64_linux"

python3 - "$FORMULA" "$VERSION" "$arm_osx" "$x64_osx" "$arm_linux" "$x64_linux" <<'PY'
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
version, arm_osx, x64_osx, arm_linux, x64_linux = sys.argv[2:7]
text = path.read_text(encoding="utf-8")
prerelease = "-" in version

# Rewrite /v…/ in every download URL (literal or #{version}).
text, n_url = re.subn(
    r'(releases/download/)v[^/"]+',
    rf"\g<1>v{version}",
    text,
)
if n_url < 4:
    raise SystemExit(f"expected at least 4 download URLs to rewrite, got {n_url}")

# Drop any existing version stanza; re-add only for prerelease.
text = re.sub(r'^[ \t]*version\s+"[^"]+"\s*\n', "", text, flags=re.M)
if prerelease:
    if re.search(r'^[ \t]*license\s+', text, flags=re.M):
        text, n = re.subn(
            r'(^[ \t]*license\s+"[^"]+"\s*\n)',
            rf'  version "{version}"\n\1',
            text,
            count=1,
            flags=re.M,
        )
        if n != 1:
            raise SystemExit("could not insert version before license")
    elif re.search(r'^[ \t]*homepage\s+', text, flags=re.M):
        text, n = re.subn(
            r'(^[ \t]*homepage\s+"[^"]+"\s*\n)',
            rf'\1  version "{version}"\n',
            text,
            count=1,
            flags=re.M,
        )
        if n != 1:
            raise SystemExit("could not insert version after homepage")
    else:
        raise SystemExit("formula has no license/homepage to anchor version stanza")

def replace_block(src: str, marker: str, digest: str) -> str:
    pattern = (
        rf'(url\s+"[^"]*{re.escape(marker)}[^"]*"\s*\n\s*sha256\s+")[0-9a-fA-F]+(")'
    )
    new, n = re.subn(pattern, rf"\g<1>{digest}\2", src, count=1)
    if n != 1:
        raise SystemExit(f"could not rewrite sha256 for {marker}")
    return new

text = replace_block(text, "Titanium.Cli-osx-arm64.zip", arm_osx)
text = replace_block(text, "Titanium.Cli-osx-x64.zip", x64_osx)
text = replace_block(text, "Titanium.Cli-linux-arm64.zip", arm_linux)
text = replace_block(text, "Titanium.Cli-linux-x64.zip", x64_linux)
path.write_text(text, encoding="utf-8")
print(f"updated {path} for {version}")
PY
