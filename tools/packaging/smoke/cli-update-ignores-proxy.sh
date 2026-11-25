#!/usr/bin/env bash
# Prove titanium update (CLI zip or --plus) does not use a loopback HTTPS_PROXY.
# Seeds an older install identity, serves a local candidate feed on an ephemeral
# port, points HTTPS_PROXY at a recording listener, runs update, asserts zero hits.
#
# Usage:
#   cli-update-ignores-proxy.sh [--plus] <cli-dir> <asset-zip> <titanium-bin> [version]
#   cli-update-ignores-proxy.sh --self-test
set -euo pipefail

self_test() {
  # Dry parse + ephemeral bind (no titanium binary, no update network).
  if ! command -v python3 >/dev/null 2>&1; then
    echo "ASSERT: python3 required for cli-update-ignores-proxy self-test" >&2
    exit 1
  fi
  local ports
  ports="$(python3 - <<'PY'
import socket
ports = []
for _ in range(2):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", 0))
    ports.append(str(s.getsockname()[1]))
    s.close()
print(" ".join(ports))
PY
)"
  # shellcheck disable=SC2086
  set -- $ports
  local feed_port="$1" proxy_port="$2"
  [[ -n "$feed_port" && -n "$proxy_port" ]] || {
    echo "ASSERT: self-test ports missing" >&2
    exit 1
  }
  echo "cli-update-ignores-proxy self-test ok (feed=$feed_port proxy=$proxy_port)"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

plus=0
if [[ "${1:-}" == "--plus" ]]; then
  plus=1
  shift
fi

cli_dir="${1:?cli dir}"
asset_zip="${2:?asset zip}"
bin="${3:?titanium}"
version="${4:-99.0.0-smoke}"
cli_dir="$(cd "$cli_dir" && pwd)"
asset_zip="$(cd "$(dirname "$asset_zip")" && pwd)/$(basename "$asset_zip")"
bin="$(cd "$(dirname "$bin")" && pwd)/$(basename "$bin")"

if [[ ! -f "$asset_zip" || ! -x "$bin" && ! -f "$bin" ]]; then
  echo "ASSERT: asset zip or binary missing" >&2
  exit 1
fi
chmod +x "$bin" || true

feed="$(mktemp -d)"
proxy_dir="$(mktemp -d)"
if [[ "$plus" -eq 1 ]]; then
  asset_name="plus.zip"
  cp "$asset_zip" "$feed/$asset_name"
else
  asset_name="cli.zip"
  cp "$asset_zip" "$feed/$asset_name"
fi
size="$(wc -c < "$feed/$asset_name" | tr -d ' ')"
sha="$(sha256sum "$feed/$asset_name" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$feed/$asset_name" | awk '{print $1}')"

hits="$proxy_dir/hits"
: >"$hits"
python3 - "$proxy_dir" <<'PY' >/tmp/update-proxy.log 2>&1 &
import socket, sys, pathlib, threading
root = pathlib.Path(sys.argv[1])
hits = root / "hits"
port_file = root / "port"
srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("127.0.0.1", 0))
srv.listen(32)
port_file.write_text(str(srv.getsockname()[1]), encoding="utf-8")

def one(conn):
    try:
        hits.write_text(str(int((hits.read_text() or "0").strip() or "0") + 1), encoding="utf-8")
    except Exception:
        hits.write_text("1", encoding="utf-8")
    try:
        conn.recv(64)
    finally:
        conn.close()

while True:
    c, _ = srv.accept()
    threading.Thread(target=one, args=(c,), daemon=True).start()
PY
proxy_py=$!

python3 - "$feed" <<'PY' >/tmp/update-feed.log 2>&1 &
import sys, pathlib, http.server
root = pathlib.Path(sys.argv[1])
port_file = root / "feed-port"

class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(root), **kwargs)
    def log_message(self, fmt, *args):
        pass

httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
port_file.write_text(str(httpd.server_address[1]), encoding="utf-8")
httpd.serve_forever()
PY
httpd=$!

cleanup() {
  kill "$httpd" "$proxy_py" 2>/dev/null || true
  wait "$httpd" "$proxy_py" 2>/dev/null || true
  rm -rf "$feed" "$proxy_dir"
}
trap cleanup EXIT

for _ in $(seq 1 50); do
  [[ -f "$proxy_dir/port" && -f "$feed/feed-port" ]] && break
  sleep 0.1
done
if [[ ! -f "$proxy_dir/port" || ! -f "$feed/feed-port" ]]; then
  echo "ASSERT: feed or proxy port file missing" >&2
  exit 1
fi
proxy_port="$(cat "$proxy_dir/port")"
feed_port="$(cat "$feed/feed-port")"

for _ in $(seq 1 50); do
  if curl -fsS --max-time 1 "http://127.0.0.1:$feed_port/" >/dev/null 2>&1; then
    break
  fi
  sleep 0.1
done

if [[ "$plus" -eq 1 ]]; then
  python3 - "$feed/release-manifest.json" "$feed_port" "$size" "$sha" "$version" <<'PY'
import json, sys
path, port, size, sha, version = sys.argv[1:]
url = f"http://127.0.0.1:{port}/plus.zip"
manifest = {
  "version": version,
  "channel": "beta",
  "products": {
    "plus": {
      "version": version,
      "bundle": {"url": url, "sha256": sha, "size": int(size)},
    }
  },
}
open(path, "w", encoding="utf-8").write(json.dumps(manifest))
PY
else
  python3 - "$feed" "$size" "$sha" "$version" "$feed_port" <<'PY'
import json, sys
feed, size, sha, version, port = sys.argv[1:]
url = f"http://127.0.0.1:{port}/cli.zip"
asset = {"url": url, "sha256": sha, "size": int(size)}
rids = ["linux-x64", "linux-arm64", "osx-arm64", "osx-x64", "win-x64",
        "linux-musl-x64", "linux-musl-arm64"]
manifest = {
  "version": version,
  "channel": "beta",
  "products": {
    "cli": {
      "assets": {rid: dict(asset) for rid in rids},
    }
  },
}
open(feed + "/release-manifest.json", "w", encoding="utf-8").write(json.dumps(manifest))
PY
fi

# Seed an older installed identity so the feed is offered (CLI path only).
if [[ "$plus" -eq 0 ]]; then
  if [[ -n "${LOCALAPPDATA:-}" ]]; then
    identity_dir="$LOCALAPPDATA/TitaniumCli"
  else
    identity_dir="${XDG_DATA_HOME:-$HOME/.local/share}/TitaniumCli"
  fi
  mkdir -p "$identity_dir"
  printf '%s\n' '{"tag":"0.0.1","channel":"Beta"}' >"$identity_dir/installed-release.json"
fi

export HTTP_PROXY="http://127.0.0.1:$proxy_port"
export HTTPS_PROXY="http://127.0.0.1:$proxy_port"
export http_proxy="$HTTP_PROXY"
export https_proxy="$HTTPS_PROXY"
export NO_PROXY=""
export no_proxy=""

update_cmd=("$bin" update --channel beta)
if [[ "$plus" -eq 1 ]]; then
  update_cmd=("$bin" update --plus --channel beta)
fi

# Prove the loopback feed answers before blaming titanium / proxy bypass.
# Must bypass HTTP(S)_PROXY — the recording listener black-holes curl otherwise.
if ! curl --noproxy '*' -fsS --max-time 2 "http://127.0.0.1:$feed_port/release-manifest.json" >/dev/null; then
  echo "ASSERT: loopback update feed not reachable before titanium update" >&2
  exit 1
fi

set +e
if command -v timeout >/dev/null 2>&1; then
  TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
    timeout 120 "${update_cmd[@]}"
  update_rc=$?
else
  TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
    "${update_cmd[@]}"
  update_rc=$?
fi
set -e
if [[ "$update_rc" -ne 0 ]]; then
  echo "ASSERT: titanium update exited $update_rc" >&2
  echo "feed-port=$feed_port proxy-port=$proxy_port" >&2
  curl --noproxy '*' -fsS --max-time 2 "http://127.0.0.1:$feed_port/release-manifest.json" >&2 || true
  exit 1
fi

assert_version_applied() {
  local target="$1"
  local tries=0
  while [[ "$tries" -lt 90 ]]; do
    tries=$((tries + 1))
    if [[ -f "$identity_dir/installed-release.json" ]] \
      && grep -qF "$target" "$identity_dir/installed-release.json" 2>/dev/null; then
      return 0
    fi
    if "$bin" version 2>/dev/null | grep -qF "$target"; then
      return 0
    fi
    sleep 1
  done
  echo "ASSERT: installed identity did not advance to $target after update" >&2
  "$bin" version >&2 || true
  cat "$identity_dir/installed-release.json" >&2 2>/dev/null || true
  return 1
}

if [[ "$plus" -eq 0 ]]; then
  assert_version_applied "$version"
fi

hit_count="$(tr -d ' \r\n' <"$hits" 2>/dev/null || echo 0)"
hit_count="${hit_count:-0}"
if [[ "$hit_count" != "0" ]]; then
  echo "ASSERT: update used loopback HTTPS_PROXY ($hit_count hits) — expected direct egress" >&2
  exit 1
fi

if [[ "$plus" -eq 1 ]]; then
  echo "plus update ignored loopback proxy ok (hits=0 version=$version)"
else
  echo "cli update ignored loopback proxy ok (hits=0 version=$version identity=$version)"
fi
