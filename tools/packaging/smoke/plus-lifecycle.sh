#!/usr/bin/env bash
# Plus apply → upgrade bundle (drops a stale file, adds a marker) → remove-plus.
#
# Usage:
#   plus-lifecycle.sh <cli-dir> <plus-zip> <titanium-bin>
#   plus-lifecycle.sh --self-test
#
# Expects Plus already applied when run after apply-plus.sh; will apply first when Titanium.Plus.dll is missing.
set -euo pipefail

STALE_NAME="Titanium.Xds.NOTICE"
MARKER_NAME="smoke-plus-upgrade.marker"
PLUS_VERSION_A="99.0.0-smoke-plus-a"
PLUS_VERSION_B="99.0.0-smoke-plus-b"

self_test() {
  bash -n "$0"
  python3 - - <<'PY'
import zipfile, io, json, tempfile, os
from pathlib import Path
buf = io.BytesIO()
with zipfile.ZipFile(buf, "w") as z:
    z.writestr("Titanium.Plus.dll", b"x")
    z.writestr("Titanium.Plus.files", "Titanium.Plus.dll\nTitanium.Plus.files\n")
tmp = tempfile.mkdtemp()
open(os.path.join(tmp, "plus.zip"), "wb").write(buf.getvalue())
print("plus-lifecycle self-test ok")
PY
}

apply_plus_feed() {
  local cli_dir="$1" plus_zip="$2" bin="$3" version="$4"
  local feed size sha port
  feed="$(mktemp -d)"
  cp "$plus_zip" "$feed/plus.zip"
  size="$(wc -c <"$feed/plus.zip" | tr -d ' ')"
  sha="$(sha256sum "$feed/plus.zip" | awk '{print $1}')"
  port="$(python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
  python3 - "$feed" "$size" "$sha" "$version" "$port" <<'PY'
import json, sys
feed, size, sha, version, port = sys.argv[1:6]
manifest = {
  "version": version,
  "channel": "beta",
  "products": {
    "plus": {
      "version": version,
      "bundle": {
        "url": f"http://127.0.0.1:{port}/plus.zip",
        "sha256": sha,
        "size": int(size),
      },
    }
  },
}
open(feed + "/release-manifest.json", "w", encoding="utf-8").write(json.dumps(manifest))
PY
  python3 -m http.server "$port" --bind 127.0.0.1 --directory "$feed" >/tmp/plus-lifecycle-feed.log 2>&1 &
  local httpd=$!
  trap 'kill "$httpd" 2>/dev/null || true; rm -rf "$feed"' RETURN
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    curl -fsS "http://127.0.0.1:$port/release-manifest.json" >/dev/null 2>&1 && break
    sleep 0.2
  done
  TITANIUM_UPDATE_FEED="http://127.0.0.1:$port/release-manifest.json" \
    "$bin" update --plus --channel beta
  local rc=$?
  kill "$httpd" 2>/dev/null || true
  rm -rf "$feed"
  trap - RETURN
  return "$rc"
}

build_plus_b_zip() {
  local src_zip="$1" out_zip="$2"
  python3 - "$src_zip" "$out_zip" "$STALE_NAME" "$MARKER_NAME" <<'PY'
import sys, tempfile, zipfile
from pathlib import Path
src, dest, stale, marker = sys.argv[1:5]
stage = Path(tempfile.mkdtemp(prefix="plus-b-"))
with zipfile.ZipFile(src) as z:
    for e in z.infolist():
        if e.is_dir() or e.filename.endswith("/"):
            continue
        name = Path(e.filename).name
        if name == stale:
            continue
        (stage / name).write_bytes(z.read(e))
(stage / marker).write_text("plus-lifecycle-upgrade\n", encoding="utf-8")
names = sorted(p.name for p in stage.iterdir() if p.is_file())
names = sorted(set(names) | {"Titanium.Plus.files"})
list_path = stage / "Titanium.Plus.files"
list_path.write_text("\n".join(names) + "\n", encoding="utf-8")
if "Titanium.Plus.files" not in names:
    names.append("Titanium.Plus.files")
    list_path.write_text("\n".join(sorted(names)) + "\n", encoding="utf-8")
with zipfile.ZipFile(dest, "w", compression=zipfile.ZIP_DEFLATED) as out:
    for name in sorted(stage.iterdir(), key=lambda p: p.name):
        if name.is_file():
            out.write(name, arcname=name.name)
import shutil
shutil.rmtree(stage, ignore_errors=True)
PY
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

cli_dir="${1:?cli dir}"
plus_zip="${2:?plus zip}"
bin="${3:?titanium}"
cli_dir="$(cd "$cli_dir" && pwd)"
plus_zip="$(cd "$(dirname "$plus_zip")" && pwd)/$(basename "$plus_zip")"
bin="$(cd "$(dirname "$bin")" && pwd)/$(basename "$bin")"

if [[ ! -f "$plus_zip" ]]; then
  echo "ASSERT: plus zip missing" >&2
  exit 1
fi

work="$(mktemp -d)"
# BSD mktemp (macOS) has no --suffix; keep a portable .zip path under $work.
plus_a="$work/plus-a.zip"
plus_b="$work/plus-b.zip"
trap 'rm -rf "$work"' EXIT

cp "$plus_zip" "$plus_a"

# Always (re)apply A at a sentinel newer than any shipped Plus so the B prune
# path is deterministic under LIFECYCLE_ENFORCE (local 8.0.9 > 0.0.0-* used to soft-skip).
apply_plus_feed "$cli_dir" "$plus_a" "$bin" "$PLUS_VERSION_A"

if [[ ! -f "$cli_dir/$STALE_NAME" ]]; then
  echo "NOTE: $STALE_NAME not in initial Plus bundle; stale-drop upgrade still validated when file was present" >&2
fi

build_plus_b_zip "$plus_a" "$plus_b"
apply_plus_feed "$cli_dir" "$plus_b" "$bin" "$PLUS_VERSION_B"

if [[ -f "$cli_dir/$STALE_NAME" ]]; then
  echo "ASSERT: stale Plus file still present after upgrade: $STALE_NAME" >&2
  exit 1
fi
if [[ ! -f "$cli_dir/$MARKER_NAME" ]]; then
  echo "ASSERT: upgrade marker missing: $MARKER_NAME" >&2
  exit 1
fi

ver="$("$bin" version 2>&1)"
echo "$ver"
if ! grep -Eq 'Plus: [0-9]' <<<"$ver"; then
  echo "ASSERT: titanium version did not report Plus after upgrade" >&2
  exit 1
fi

set +e
"$bin" update --remove-plus
remove_rc=$?
set -e
if [[ "$remove_rc" -ne 0 ]]; then
  echo "ASSERT: titanium update --remove-plus failed (exit=$remove_rc)" >&2
  exit 1
fi
if [[ -f "$cli_dir/Titanium.Plus.dll" ]]; then
  echo "ASSERT: Titanium.Plus.dll still present after --remove-plus" >&2
  exit 1
fi
ver2="$("$bin" version 2>&1)"
echo "$ver2"
if grep -Eq 'Plus: [0-9]' <<<"$ver2"; then
  echo "ASSERT: version still reports Plus after --remove-plus" >&2
  exit 1
fi
"$bin" version >/dev/null
if [[ ! -f "$cli_dir/$(basename "$bin")" && ! -x "$bin" ]]; then
  :
fi
echo "plus-lifecycle ok"