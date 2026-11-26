#!/usr/bin/env bash
# Upgrade a published stable CLI zip to the candidate zip via a local feed.
# Uses the OLD client's `update` against a feed that serves the NEW zip.
# No HTTPS_PROXY (old clients may not bypass loopback MITM).
#
# Usage:
#   cli-upgrade-from-published.sh <published-cli-zip> <candidate-cli-zip> <work-dir> [candidate-version]
#   cli-upgrade-from-published.sh --self-test
set -euo pipefail

# Every file in the zip must match the install. A marker copied before a later
# failure is not a successful upgrade.
assert_files_match() {
  python3 - "$1" "$2" <<'PY'
import hashlib, os, sys, zipfile
zpath, dest = sys.argv[1:]
missing, mismatch = [], []
with zipfile.ZipFile(zpath) as zf:
    for info in zf.infolist():
        rel = info.filename.replace("\\", "/")
        if not rel or rel.endswith("/"):
            continue
        target = os.path.join(dest, *rel.split("/"))
        if not os.path.isfile(target):
            missing.append(rel)
            continue
        want = hashlib.sha256(zf.read(info)).hexdigest()
        with open(target, "rb") as fh:
            got = hashlib.sha256(fh.read()).hexdigest()
        if want != got:
            mismatch.append(rel)
print(f"hash-compare missing={len(missing)} mismatch={len(mismatch)}")
for rel in (missing + mismatch)[:20]:
    print(rel)
sys.exit(1 if missing or mismatch else 0)
PY
}

self_test() {
  if ! command -v python3 >/dev/null 2>&1; then
    echo "ASSERT: python3 required for cli-upgrade-from-published self-test" >&2
    exit 1
  fi
  local feed_port
  feed_port="$(python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
  [[ -n "$feed_port" ]] || { echo "ASSERT: self-test feed port missing" >&2; exit 1; }
  local tmp
  tmp="$(mktemp -d)"
  mkdir -p "$tmp/src"
  printf 'abc\n' >"$tmp/src/a.txt"
  python3 - "$tmp/src" "$tmp/c.zip" <<'PY'
import sys, zipfile
src, dest = sys.argv[1:]
with zipfile.ZipFile(dest, "w") as zf:
    zf.write(src + "/a.txt", arcname="a.txt")
PY
  cp "$tmp/src/a.txt" "$tmp/a.txt"
  assert_files_match "$tmp/c.zip" "$tmp"
  printf 'nope\n' >"$tmp/a.txt"
  if assert_files_match "$tmp/c.zip" "$tmp"; then
    echo "ASSERT: self-test hash compare should fail on a mismatch" >&2
    exit 1
  fi
  rm -rf "$tmp"
  echo "cli-upgrade-from-published self-test ok (feed=$feed_port)"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

published_zip="${1:?published cli zip}"
candidate_zip="${2:?candidate cli zip}"
work="${3:?work dir}"
version="${4:-99.0.0-smoke}"

published_zip="$(cd "$(dirname "$published_zip")" && pwd)/$(basename "$published_zip")"
candidate_zip="$(cd "$(dirname "$candidate_zip")" && pwd)/$(basename "$candidate_zip")"
mkdir -p "$work"
work="$(cd "$work" && pwd)"

if [[ ! -f "$published_zip" || ! -f "$candidate_zip" ]]; then
  echo "ASSERT: published or candidate zip missing" >&2
  exit 1
fi

echo "cli-upgrade-from-published published=${published_zip} bytes=$(wc -c <"$published_zip" | tr -d ' ')"
echo "cli-upgrade-from-published candidate=${candidate_zip} bytes=$(wc -c <"$candidate_zip" | tr -d ' ') offer=${version}"

old="$work/old"
rm -rf "$old"
mkdir -p "$old"
unzip -qo "$published_zip" -d "$old"
bin="$old/titanium"
[[ -f "$bin" ]] || bin="$old/titanium.exe"
if [[ ! -f "$bin" ]]; then
  echo "ASSERT: published zip has no titanium binary" >&2
  exit 1
fi
chmod +x "$bin" || true
# Shipped macOS zips unpack dylibs as 0444. Force that mode so a marker-only
# success cannot hide an overlay that stops on the first read-only file.
if [[ "$(uname -s)" == Darwin ]]; then
  chmod 0444 "$old"/*.dylib 2>/dev/null || true
  echo "simulated read-only dylibs before published upgrade"
fi

feed="$(mktemp -d)"
cp "$candidate_zip" "$feed/cli.zip"
# Marker only proves overlay when the *candidate* apply script runs. The published
# (old) client still emits its own apply script, so on Darwin with a shipped
# updater that fails on 0444 dylibs we assert that known failure instead.
marker_name="smoke-marker.txt"
marker_value="cli-upgrade-from-published-${version}"
printf '%s\n' "$marker_value" >"$feed/$marker_name"
if command -v zip >/dev/null 2>&1; then
  (cd "$feed" && zip -q cli.zip "$marker_name")
else
  python3 - "$feed/cli.zip" "$feed/$marker_name" "$marker_name" <<'PY'
import sys, zipfile
zpath, src, arc = sys.argv[1:4]
with zipfile.ZipFile(zpath, "a", compression=zipfile.ZIP_DEFLATED) as zf:
    zf.write(src, arcname=arc)
PY
fi
rm -f "$feed/$marker_name"
size="$(wc -c < "$feed/cli.zip" | tr -d ' ')"
sha="$(sha256sum "$feed/cli.zip" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$feed/cli.zip" | awk '{print $1}')"

# Pick the port in the parent, then python -m http.server. Binding via
# ThreadingHTTPServer((127.0.0.1, 0)) hung on macos-15-intel (same class as the
# update-ignores-proxy feed hang before that switch).
feed_port="$(python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
[[ -n "$feed_port" ]] || { echo "ASSERT: feed port missing" >&2; exit 1; }
printf '%s' "$feed_port" >"$feed/feed-port"
python3 -m http.server "$feed_port" --bind 127.0.0.1 --directory "$feed" >/tmp/upgrade-feed.log 2>&1 &
httpd=$!
cleanup() {
  kill "$httpd" 2>/dev/null || true
  wait "$httpd" 2>/dev/null || true
  rm -rf "$feed"
}
trap cleanup EXIT

for _ in $(seq 1 50); do
  if curl -fsS --max-time 1 "http://127.0.0.1:$feed_port/" >/dev/null 2>&1; then
    break
  fi
  sleep 0.1
done
if ! curl -fsS --max-time 2 "http://127.0.0.1:$feed_port/" >/dev/null 2>&1; then
  echo "ASSERT: upgrade feed not reachable on $feed_port" >&2
  cat /tmp/upgrade-feed.log >&2 || true
  exit 1
fi

python3 - "$feed" "$size" "$sha" "$version" "$feed_port" <<'PY'
import json, sys
feed, size, sha, version, port = sys.argv[1:]
url = f"http://127.0.0.1:{port}/cli.zip"
asset = {"url": url, "sha256": sha, "size": int(size)}
rids = ["linux-x64", "linux-arm64", "osx-arm64", "osx-x64", "win-x64",
        "linux-musl-x64", "linux-musl-arm64"]
manifest = {
  "version": version,
  "channel": "stable",
  "products": {
    "cli": {
      "assets": {rid: dict(asset) for rid in rids},
    }
  },
}
open(feed + "/release-manifest.json", "w", encoding="utf-8").write(json.dumps(manifest))
PY

# Match SpecialFolder.LocalApplicationData (identity written by apply script on
# candidate clients; older published clients still write it in the parent).
if [[ -n "${LOCALAPPDATA:-}" ]]; then
  identity_dir="$LOCALAPPDATA/TitaniumCli"
elif [[ "$(uname -s)" == Darwin ]]; then
  identity_dir="$HOME/Library/Application Support/TitaniumCli"
else
  identity_dir="${XDG_DATA_HOME:-$HOME/.local/share}/TitaniumCli"
fi
mkdir -p "$identity_dir"
printf '%s\n' '{"tag":"0.0.1","channel":"Stable"}' >"$identity_dir/installed-release.json"

# Clear any prior apply.done so we do not race against a stale success marker.
rm -rf "${TMPDIR:-/tmp}/TitaniumCli-update" /tmp/TitaniumCli-update 2>/dev/null || true
if [[ -n "${TEMP:-}${TMP:-}" ]]; then
  rm -rf "${TEMP:-$TMP}/TitaniumCli-update" 2>/dev/null || true
fi

# Capture detached apply stdout/stderr by pointing the workdir script's
# inherited streams is unreliable after the parent exits. Tee parent output,
# then poll for the marker / apply.done / Permission denied in the apply script
# residual log if the old client left one.
upd_log="$work/update.log"
apply_log="$work/apply-residual.log"
: >"$apply_log"
set +e
if command -v timeout >/dev/null 2>&1; then
  TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
    timeout 180 "$bin" update --channel stable >"$upd_log" 2>&1
  rc=$?
else
  TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
    "$bin" update --channel stable >"$upd_log" 2>&1
  rc=$?
fi
set -e
cat "$upd_log"

if [[ "$rc" -ne 0 ]]; then
  echo "ASSERT: published CLI update exited $rc" >&2
  exit 1
fi

# Wait for the detached apply to finish. Prefer apply.done from candidate
# clients; fall back to marker + runnable binary for older published clients
# that still write identity in the parent before StartDetached.
apply_done="${TMPDIR:-/tmp}/TitaniumCli-update/apply.done"
apply_script="${TMPDIR:-/tmp}/TitaniumCli-update/apply-cli-update.sh"
applied=0
for _ in $(seq 1 90); do
  if [[ -f "$apply_done" ]] && grep -qF "ok version=" "$apply_done" 2>/dev/null; then
    applied=1
    break
  fi
  if [[ -f "$old/$marker_name" ]] && grep -qF "$marker_value" "$old/$marker_name" 2>/dev/null; then
    if "$bin" version >/dev/null 2>&1; then
      applied=1
      break
    fi
  fi
  sleep 1
done
if [[ -f "$apply_done" ]]; then
  cat "$apply_done"
fi

shipped_readonly_updater=0
if [[ "$(uname -s)" == Darwin ]] \
  && [[ -f "$apply_script" ]] \
  && grep -Eq 'cp -a' "$apply_script" \
  && ! grep -q 'chmod -R u+w' "$apply_script"; then
  shipped_readonly_updater=1
fi

if [[ "$applied" -eq 1 ]]; then
  set +e
  assert_files_match "$feed/cli.zip" "$old"
  hash_rc=$?
  set -e
  if [[ "$hash_rc" -ne 0 ]]; then
    if [[ "$shipped_readonly_updater" -eq 1 ]]; then
      echo "KNOWN_FAILURE: shipped macOS CLI updater left the install different from the candidate zip."
      echo "KNOWN_FAILURE: workaround: chmod -R u+w <install dir> && titanium update, or reinstall."
      echo "cli upgrade from published known-failure (shipped-updater-readonly-dylib)"
      exit 0
    fi
    echo "ASSERT: install tree does not match the candidate zip" >&2
    exit 1
  fi
fi

if [[ "$applied" -ne 1 ]]; then
  # Shipped macOS CLI updaters emit `cp -a` without chmod u+w. Overlay fails on
  # 0444 Homebrew dylibs. Detect via the generated script + missing marker.
  if [[ "$(uname -s)" == Darwin ]] \
    && [[ -f "$apply_script" ]] \
    && grep -Eq 'cp -a' "$apply_script" \
    && ! grep -q 'chmod -R u+w' "$apply_script"; then
    echo "KNOWN_FAILURE: shipped macOS CLI updater cannot overwrite 0444 dylibs."
    echo "KNOWN_FAILURE: workaround: chmod -R u+w <install dir> && titanium update, or reinstall."
    echo "cli upgrade from published known-failure (shipped-updater-readonly-dylib)"
    exit 0
  fi
  echo "ASSERT: detached apply did not finish (no apply.done / marker)" >&2
  ls -la "$old/$marker_name" "$apply_done" "$apply_script" 2>&1 || true
  [[ -f "$apply_script" ]] && head -n 40 "$apply_script" >&2 || true
  "$bin" version >&2 || true
  exit 1
fi

if ! "$bin" version >/dev/null 2>&1; then
  echo "ASSERT: titanium version failed after upgrade-from-published" >&2
  "$bin" version >&2 || true
  exit 1
fi

echo "cli upgrade from published ok (version=$version)"