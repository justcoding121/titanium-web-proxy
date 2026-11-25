#!/usr/bin/env bash
# Upgrade a published stable CLI zip to the candidate zip via a local feed.
# Uses the OLD client's `update` against a feed that serves the NEW zip.
# No HTTPS_PROXY (old clients may not bypass loopback MITM).
#
# Usage:
#   cli-upgrade-from-published.sh <published-cli-zip> <candidate-cli-zip> <work-dir> [candidate-version]
#   cli-upgrade-from-published.sh --self-test
set -euo pipefail

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

feed="$(mktemp -d)"
cp "$candidate_zip" "$feed/cli.zip"
size="$(wc -c < "$feed/cli.zip" | tr -d ' ')"
sha="$(sha256sum "$feed/cli.zip" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$feed/cli.zip" | awk '{print $1}')"

python3 - "$feed" <<'PY' >/tmp/upgrade-feed.log 2>&1 &
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
  kill "$httpd" 2>/dev/null || true
  wait "$httpd" 2>/dev/null || true
  rm -rf "$feed"
}
trap cleanup EXIT

for _ in $(seq 1 50); do
  [[ -f "$feed/feed-port" ]] && break
  sleep 0.1
done
feed_port="$(cat "$feed/feed-port")"

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

if [[ -n "${LOCALAPPDATA:-}" ]]; then
  identity_dir="$LOCALAPPDATA/TitaniumCli"
else
  identity_dir="${XDG_DATA_HOME:-$HOME/.local/share}/TitaniumCli"
fi
mkdir -p "$identity_dir"
printf '%s\n' '{"tag":"0.0.1","channel":"Stable"}' >"$identity_dir/installed-release.json"

set +e
if command -v timeout >/dev/null 2>&1; then
  TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
    timeout 180 "$bin" update --channel stable
  rc=$?
else
  TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
    "$bin" update --channel stable
  rc=$?
fi
set -e
if [[ "$rc" -ne 0 ]]; then
  echo "ASSERT: published CLI update exited $rc" >&2
  exit 1
fi

# After update the binary in $old should report the candidate tag (or at least
# the installed-release identity should advance).
if [[ -f "$identity_dir/installed-release.json" ]]; then
  if ! grep -q "$version" "$identity_dir/installed-release.json" 2>/dev/null; then
    # Some older clients write only the tag; accept either form.
    if ! grep -Eqi "${version%%-*}|smoke" "$identity_dir/installed-release.json" 2>/dev/null; then
      echo "ASSERT: installed-release.json was not updated to $version" >&2
      cat "$identity_dir/installed-release.json" >&2 || true
      exit 1
    fi
  fi
fi

echo "cli upgrade from published ok (version=$version)"
