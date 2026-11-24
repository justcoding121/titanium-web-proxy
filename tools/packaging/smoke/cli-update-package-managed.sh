#!/usr/bin/env bash
# Package-managed CLI cannot self-update without elevation.
# Installs deb as root, runs `titanium update` as an unprivileged user, and checks
# the install tree is unchanged (CLI does not emit InstallDirNotWritableMessage today).
#
# Usage:
#   cli-update-package-managed.sh <candidate.deb> <candidate.zip> <candidate.AppImage> [version]
#   cli-update-package-managed.sh --self-test
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

NOT_WRITABLE_MSG='The install directory is not writable. Run the same command with sudo.'

feed_dir=""
feed_port=""
feed_pid=""

self_test() {
  bash -n "$0"
  grep -q 'install directory is not writable' "$0"
  echo "cli-update-package-managed self-test ok"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

deb="${1:?candidate deb}"
zip="${2:?candidate zip}"
appimage="${3:?candidate AppImage}"
version="${4:-8.0.9-smoke}"

deb="$(cd "$(dirname "$deb")" && pwd)/$(basename "$deb")"
zip="$(cd "$(dirname "$zip")" && pwd)/$(basename "$zip")"
appimage="$(cd "$(dirname "$appimage")" && pwd)/$(basename "$appimage")"

for f in "$deb" "$zip" "$appimage"; do
  if [[ ! -f "$f" ]]; then
    echo "ASSERT: missing input $f" >&2
    exit 1
  fi
done

if ! command -v python3 >/dev/null 2>&1; then
  echo "ASSERT: python3 required" >&2
  exit 1
fi

ensure_smoke_user() {
  if ! id smokecli >/dev/null 2>&1; then
    sudo useradd -m -s /bin/bash smokecli
  fi
}

stop_local_feed() {
  if [[ -n "$feed_pid" ]]; then
    kill "$feed_pid" 2>/dev/null || true
    wait "$feed_pid" 2>/dev/null || true
    feed_pid=""
  fi
  if [[ -n "$feed_dir" ]]; then
    rm -rf "$feed_dir"
    feed_dir=""
  fi
  feed_port=""
}

start_local_feed() {
  local asset_zip="$1"
  local ver="$2"
  stop_local_feed
  feed_dir="$(mktemp -d)"
  cp "$asset_zip" "$feed_dir/cli.zip"
  local size sha
  size="$(wc -c < "$feed_dir/cli.zip" | tr -d ' ')"
  sha="$(sha256sum "$feed_dir/cli.zip" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$feed_dir/cli.zip" | awk '{print $1}')"

  python3 - "$feed_dir" <<'PY' >/tmp/pkg-managed-feed.log 2>&1 &
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
  feed_pid=$!

  local _
  for _ in $(seq 1 50); do
    [[ -f "$feed_dir/feed-port" ]] && break
    sleep 0.1
  done
  feed_port="$(cat "$feed_dir/feed-port")"

  python3 - "$feed_dir" "$size" "$sha" "$ver" "$feed_port" <<'PY'
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
}

wait_install_stable() {
  local bin="$1" baseline_hash="$2"
  local tries=0
  while [[ "$tries" -lt 45 ]]; do
    tries=$((tries + 1))
    if [[ "$(sha256sum "$bin" 2>/dev/null | awk '{print $1}')" != "$baseline_hash" ]]; then
      echo "ASSERT: binary changed without elevation ($bin)" >&2
      return 1
    fi
    sleep 1
  done
  return 0
}

run_unpriv_update() {
  local bin="$1" cwd="${2:-}"
  local log
  log="$(mktemp)"
  ensure_smoke_user
  local identity="/home/smokecli/.local/share/TitaniumCli"
  sudo mkdir -p "$identity"
  sudo chown -R smokecli:smokecli "/home/smokecli/.local"
  sudo -u smokecli bash -c "printf '%s\n' '{\"tag\":\"0.0.1\",\"channel\":\"Beta\"}' > '$identity/installed-release.json'"

  set +e
  if [[ -n "$cwd" ]]; then
    sudo -u smokecli env TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
      bash -c "cd '$cwd' && '$bin' update --channel beta" >"$log" 2>&1
  else
    sudo -u smokecli env TITANIUM_UPDATE_FEED="http://127.0.0.1:$feed_port/release-manifest.json" \
      "$bin" update --channel beta >"$log" 2>&1
  fi
  local rc=$?
  set -e
  cat "$log"
  if grep -Fq "$NOT_WRITABLE_MSG" "$log"; then
    echo "update refused with InstallDirNotWritableMessage"
    rm -f "$log"
    return 2
  fi
  if [[ "$rc" -ne 0 ]]; then
    echo "unprivileged update exited $rc"
  fi
  rm -f "$log"
  return "$rc"
}

assert_deb_managed_update() {
  sudo dpkg -r titanium-cli >/dev/null 2>&1 || true
  sudo dpkg -i "$deb" >&2 || sudo apt-get install -y -f >&2
  test -x /opt/titanium-cli/titanium
  local baseline deb_ver
  baseline="$(sha256sum /opt/titanium-cli/titanium | awk '{print $1}')"
  deb_ver="$(dpkg-deb -f "$deb" Version)"

  start_local_feed "$zip" "$version"
  trap stop_local_feed EXIT

  run_unpriv_update /opt/titanium-cli/titanium || true
  wait_install_stable /opt/titanium-cli/titanium "$baseline"
  if /opt/titanium-cli/titanium version 2>/dev/null | grep -qF "$version"; then
    echo "ASSERT: dpkg install reported feed version without sudo" >&2
    exit 1
  fi
  installed="$(dpkg-query -W -f='${Version}' titanium-cli)"
  if [[ "$installed" != "$deb_ver" ]]; then
    echo "ASSERT: dpkg version changed under unprivileged update ($installed vs $deb_ver)" >&2
    exit 1
  fi
  echo "deb package-managed update ok (dpkg=$deb_ver unchanged)"
  sudo dpkg -r titanium-cli >/dev/null 2>&1 || true
  stop_local_feed
  trap - EXIT
}

assert_appimage_managed_update() {
  local work bin root baseline
  work="$(mktemp -d)"
  chmod +x "$appimage"
  export APPIMAGE_EXTRACT_AND_RUN=1
  (cd "$work" && "$appimage" --appimage-extract >/dev/null)
  root="$work/squashfs-root"
  bin="$root/usr/bin/titanium"
  [[ -x "$bin" ]] || bin="$root/titanium"
  if [[ ! -x "$bin" ]]; then
    echo "ASSERT: AppImage extract missing titanium" >&2
    exit 1
  fi
  baseline="$(sha256sum "$bin" | awk '{print $1}')"

  start_local_feed "$zip" "$version"
  trap stop_local_feed EXIT

  run_unpriv_update "$bin" "$root" || true
  wait_install_stable "$bin" "$baseline"

  echo "AppImage package-managed update ok"
  rm -rf "$work"
  stop_local_feed
  trap - EXIT
}

assert_deb_managed_update
assert_appimage_managed_update
echo "cli-update-package-managed ok"