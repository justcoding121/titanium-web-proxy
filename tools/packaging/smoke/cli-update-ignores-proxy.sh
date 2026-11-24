#!/usr/bin/env bash
# Prove titanium update does not use a loopback HTTPS_PROXY (own MITM / local interceptor).
# Seeds an older install identity, serves a local candidate feed, points HTTPS_PROXY at a
# recording listener, runs update, asserts zero proxy hits.
# Usage: cli-update-ignores-proxy.sh <cli-dir> <cli-zip> <titanium-bin> [version]
set -euo pipefail
cli_dir="${1:?cli dir}"
cli_zip="${2:?cli zip}"
bin="${3:?titanium}"
version="${4:-8.0.9-smoke}"
cli_dir="$(cd "$cli_dir" && pwd)"
cli_zip="$(cd "$(dirname "$cli_zip")" && pwd)/$(basename "$cli_zip")"
bin="$(cd "$(dirname "$bin")" && pwd)/$(basename "$bin")"

if [[ ! -f "$cli_zip" || ! -x "$bin" && ! -f "$bin" ]]; then
  echo "ASSERT: cli zip or binary missing" >&2
  exit 1
fi
chmod +x "$bin" || true

feed="$(mktemp -d)"
proxy_dir="$(mktemp -d)"
cp "$cli_zip" "$feed/cli.zip"
size="$(wc -c < "$feed/cli.zip" | tr -d ' ')"
sha="$(sha256sum "$feed/cli.zip" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$feed/cli.zip" | awk '{print $1}')"
asset_name="cli.zip"

# RID keys must match UpdateFeedClient ResolveRid / SuggestRid.
python3 - "$feed" "$size" "$sha" "$version" <<'PY'
import json, sys
feed, size, sha, version = sys.argv[1:]
url = "http://127.0.0.1:9/cli.zip"
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

feed_port=8766
python3 -m http.server "$feed_port" --bind 127.0.0.1 --directory "$feed" >/tmp/update-feed.log 2>&1 &
httpd=$!
cleanup() {
  kill "$httpd" "$proxy_py" 2>/dev/null || true
  rm -rf "$feed" "$proxy_dir"
}
trap cleanup EXIT

for _ in $(seq 1 50); do
  [[ -f "$proxy_dir/port" ]] && break
  sleep 0.1
done
proxy_port="$(cat "$proxy_dir/port")"
for _ in $(seq 1 50); do
  if curl -fsS "http://127.0.0.1:$feed_port/release-manifest.json" >/dev/null 2>&1; then
    break
  fi
  sleep 0.1
done

python3 - "$feed/release-manifest.json" "$feed_port" <<'PY'
import json, sys
path, port = sys.argv[1:]
doc = json.load(open(path, encoding="utf-8"))
url = f"http://127.0.0.1:{port}/cli.zip"
for a in doc["products"]["cli"]["assets"].values():
    a["url"] = url
json.dump(doc, open(path, "w", encoding="utf-8"))
PY

# Seed an older installed identity so the feed is offered.
if [[ -n "${LOCALAPPDATA:-}" ]]; then
  identity_dir="$LOCALAPPDATA/TitaniumCli"
else
  identity_dir="${XDG_DATA_HOME:-$HOME/.local/share}/TitaniumCli"
fi
mkdir -p "$identity_dir"
printf '%s\n' '{"tag":"0.0.1","channel":"Beta"}' >"$identity_dir/installed-release.json"

export HTTP_PROXY="http://127.0.0.1:$proxy_port"
export HTTPS_PROXY="http://127.0.0.1:$proxy_port"
export http_proxy="$HTTP_PROXY"
export https_proxy="$HTTPS_PROXY"
export NO_PROXY=""
export no_proxy=""

TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
  "$bin" update --channel beta

hit_count="$(tr -d ' \r\n' <"$hits" 2>/dev/null || echo 0)"
hit_count="${hit_count:-0}"
if [[ "$hit_count" != "0" ]]; then
  echo "ASSERT: update used loopback HTTPS_PROXY ($hit_count hits) — expected direct egress" >&2
  exit 1
fi

echo "cli update ignored loopback proxy ok (hits=0 version=$version)"
