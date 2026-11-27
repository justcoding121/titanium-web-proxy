#!/usr/bin/env bash
# OS service install → start → stop → uninstall (plus optional update-while-running probe).
#
# Usage:
#   cli-service-lifecycle.sh <titanium-bin>
#   cli-service-lifecycle.sh --self-test
#
# Linux: machine service (sudo). Skips with a named message when systemctl is absent (musl).
# macOS: per-user LaunchAgent (--user; no elevation on CI).
set -euo pipefail

self_test() {
  bash -n "$0"
  if ! command -v python3 >/dev/null 2>&1; then
    echo "ASSERT: python3 required for cli-service-lifecycle self-test" >&2
    exit 1
  fi
  python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.bind(("127.0.0.1", 0))
print("cli-service-lifecycle self-test ok (port=" + str(s.getsockname()[1]) + ")")
s.close()
PY
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

bin="${1:?titanium binary}"
if [[ ! -x "$bin" && ! -f "$bin" ]]; then
  echo "ASSERT: titanium binary missing: $bin" >&2
  exit 1
fi
bin="$(cd "$(dirname "$bin")" && pwd)/$(basename "$bin")"
if [[ ! -x "$bin" ]]; then
  chmod +x "$bin" 2>/dev/null || true
fi

echo "cli-service-lifecycle candidate:"
"$bin" version || true
if [[ -n "${SMOKE_CLI_ZIP:-}" && -f "${SMOKE_CLI_ZIP}" ]]; then
  echo "cli-service-lifecycle SMOKE_CLI_ZIP=${SMOKE_CLI_ZIP} bytes=$(wc -c <"$SMOKE_CLI_ZIP" | tr -d ' ')"
fi

uname_s="$(uname -s)"
user_mode=0
case "$uname_s" in
  Darwin) user_mode=1 ;;
  Linux)
    if ! command -v systemctl >/dev/null 2>&1; then
      echo "SKIP: musl-no-systemd (systemctl not found)"
      exit 0
    fi
    ;;
esac

if ! command -v python3 >/dev/null 2>&1; then
  echo "ASSERT: python3 required" >&2
  exit 1
fi

work="$(mktemp -d)"
origin_pid=""
httpd_pid=""
name="twp-smk-$(python3 - <<'PY'
import secrets
print(secrets.token_hex(6))
PY
)"
label="com.justcoding121.${name}"

cleanup() {
  set +e
  if [[ -n "${httpd_pid:-}" ]]; then kill "$httpd_pid" 2>/dev/null || true; fi
  if [[ -n "${origin_pid:-}" ]]; then kill "$origin_pid" 2>/dev/null || true; fi
  if [[ "$user_mode" -eq 1 ]]; then
  "$bin" service stop --name "$name" --user 2>/dev/null || true
    "$bin" service uninstall --name "$name" --user 2>/dev/null || true
  else
    if [[ "$uname_s" == Linux ]]; then
      sudo "$bin" service stop --name "$name" 2>/dev/null || true
      sudo "$bin" service uninstall --name "$name" 2>/dev/null || true
    fi
  fi
  # Machine-service update (sudo) can leave root-owned /tmp/TitaniumCli-update and
  # poison the next user-mode update leg with "Access … is denied".
  if [[ "$uname_s" == Linux && "$user_mode" -eq 0 ]]; then
    sudo rm -rf /tmp/TitaniumCli-update 2>/dev/null || true
  else
    rm -rf "${TMPDIR:-/tmp}/TitaniumCli-update" /tmp/TitaniumCli-update 2>/dev/null || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

# Print one port per line. Do not use ${ports##*} — that pattern matches the
# whole string and left origin_port empty (forwardPort blank → product defaulted
# to :80; origin python died on int('')).
listen_port="$(python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
origin_port="$(python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
[[ "$listen_port" =~ ^[0-9]+$ && "$origin_port" =~ ^[0-9]+$ ]] || {
  echo "ASSERT: failed to pick listen/origin ports (listen='$listen_port' origin='$origin_port')" >&2
  exit 1
}
echo "cli-service-lifecycle ports listen=${listen_port} origin=${origin_port}"

cfg="$work/twp-service-smoke.yaml"
cat >"$cfg" <<YAML
schemaVersion: "7.0"
listeners:
  - host: "127.0.0.1"
    port: ${listen_port}
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: ${origin_port}
YAML

python3 - "$origin_port" >/tmp/twp-origin-smoke.log 2>&1 <<'PY' &
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
port = int(sys.argv[1])
class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"ok")
HTTPServer(("127.0.0.1", port), H).serve_forever()
PY
origin_pid=$!
# Fail fast if the origin helper did not bind (empty port / crash).
for _ in $(seq 1 20); do
  if curl -fsS "http://127.0.0.1:${origin_port}/svc" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$origin_pid" 2>/dev/null; then
    echo "ASSERT: origin helper exited before bind" >&2
    cat /tmp/twp-origin-smoke.log >&2 || true
    exit 1
  fi
  sleep 0.1
done
curl -fsS "http://127.0.0.1:${origin_port}/svc" >/dev/null || {
  echo "ASSERT: origin helper not accepting HTTP on ${origin_port}" >&2
  cat /tmp/twp-origin-smoke.log >&2 || true
  exit 1
}

run_svc() {
  if [[ "$user_mode" -eq 1 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

user_args=()
[[ "$user_mode" -eq 1 ]] && user_args=(--user)

run_svc "$bin" service uninstall --name "$name" "${user_args[@]}" 2>/dev/null || true

out="$(run_svc "$bin" service install -c "$cfg" --name "$name" --no-start "${user_args[@]}" 2>&1)" || {
  echo "$out" >&2
  echo "ASSERT: service install failed" >&2
  exit 1
}

st_out="$(run_svc "$bin" service status --name "$name" "${user_args[@]}" 2>&1)" || {
  echo "$st_out" >&2
  echo "ASSERT: service status failed after install" >&2
  exit 1
}
echo "$st_out"
if ! grep -Eiq 'stopped|installed' <<<"$st_out"; then
  echo "ASSERT: expected installed/stopped status after --no-start" >&2
  exit 1
fi

run_svc "$bin" service start --name "$name" "${user_args[@]}" >/dev/null

st_run="$(run_svc "$bin" service status --name "$name" "${user_args[@]}" 2>&1)" || true
echo "$st_run"

http_ok=0
for _ in $(seq 1 40); do
  if curl -fsS "http://127.0.0.1:${listen_port}/svc" >/dev/null 2>&1; then
    http_ok=1
    break
  fi
  sleep 0.25
done
if [[ "$http_ok" -ne 1 ]]; then
  echo "ASSERT: proxy port ${listen_port} not accepting HTTP after service start" >&2
  echo "---- service smoke config ----" >&2
  cat "$cfg" >&2 || true
  echo "---- listeners / curl ----" >&2
  (ss -ltnp 2>/dev/null || netstat -ltn 2>/dev/null || true) | grep -E "${listen_port}|${origin_port}" >&2 || true
  curl -v --max-time 2 "http://127.0.0.1:${listen_port}/svc" >&2 || true
  if [[ "$uname_s" == Linux && "$user_mode" -eq 0 ]]; then
    echo "---- systemctl status ----" >&2
    sudo systemctl status "${name}.service" --no-pager -l >&2 || true
    echo "---- journalctl ----" >&2
    sudo journalctl -u "${name}.service" -n 80 --no-pager >&2 || true
  elif [[ "$uname_s" == Darwin ]]; then
    echo "---- launchctl print ----" >&2
    launchctl print "gui/$(id -u)/${label}" >&2 || true
  fi
  exit 1
fi

if [[ "$uname_s" == Linux && "$user_mode" -eq 0 ]]; then
  state="$(sudo systemctl is-active "${name}.service" 2>&1 || true)"
  echo "systemctl is-active ${name}.service: $state"
  if ! grep -qi '^active' <<<"$state"; then
    echo "ASSERT: systemd unit not active after start" >&2
    exit 1
  fi
fi

# Optional: update while service runs (service warning + CLI still runnable).
# Candidate BuildUnixScript removes each destination file before copy so the
# Mach-O gets a new inode (avoids macOS SIGKILL from a stale code-sign cache).
cli_zip="${SMOKE_CLI_ZIP:-}"
if [[ -z "$cli_zip" ]]; then
  cli_zip="$(find "${RUNNER_TEMP:-/tmp}" -maxdepth 4 -name 'Titanium.Cli-*.zip' 2>/dev/null | head -1 || true)"
fi
if [[ -n "$cli_zip" && -f "$cli_zip" ]]; then
  feed="$(mktemp -d)"
  cp "$cli_zip" "$feed/cli.zip"
  size="$(wc -c <"$feed/cli.zip" | tr -d ' ')"
  sha="$(sha256sum "$feed/cli.zip" | awk '{print $1}')"
  feed_port="$(python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
  python3 - "$feed" "$size" "$sha" "$feed_port" <<'PY'
import json, sys
feed, size, sha, port = sys.argv[1:5]
url = f"http://127.0.0.1:{port}/cli.zip"
asset = {"url": url, "sha256": sha, "size": int(size)}
rids = ["linux-x64", "linux-arm64", "osx-arm64", "osx-x64", "win-x64",
        "linux-musl-x64", "linux-musl-arm64"]
manifest = {
  "version": "9.9.9-smoke",
  "channel": "beta",
  "products": {"cli": {"assets": {rid: dict(asset) for rid in rids}}},
}
open(feed + "/release-manifest.json", "w", encoding="utf-8").write(json.dumps(manifest))
PY
  python3 -m http.server "$feed_port" --bind 127.0.0.1 --directory "$feed" >/tmp/twp-feed-smoke.log 2>&1 &
  httpd_pid=$!
  for _ in 1 2 3 4 5 6 7 8; do
    curl -fsS "http://127.0.0.1:${feed_port}/release-manifest.json" >/dev/null 2>&1 && break
    sleep 0.2
  done
  set +e
  upd_out="$(TITANIUM_UPDATE_FEED="http://127.0.0.1:${feed_port}/release-manifest.json" \
    "$bin" update --channel beta 2>&1)"
  upd_code=$?
  set -e
  echo "$upd_out"
  if ! grep -Eiq 'service|Warning|stop' <<<"$upd_out"; then
    echo "NOTE: update while service running had no service warning (exit=$upd_code)" >&2
  fi
  kill "$httpd_pid" 2>/dev/null || true
  httpd_pid=""
  rm -rf "$feed"
  # Clear root-owned update workdir before later user-mode legs.
  if [[ "$user_mode" -eq 0 ]]; then
    sudo rm -rf /tmp/TitaniumCli-update 2>/dev/null || true
  fi
fi

"$bin" version >/dev/null || { echo "ASSERT: titanium version failed after update probe" >&2; exit 1; }

run_svc "$bin" service stop --name "$name" "${user_args[@]}" >/dev/null

run_svc "$bin" service uninstall --name "$name" "${user_args[@]}" >/dev/null

if [[ "$uname_s" == Darwin && "$user_mode" -eq 1 ]]; then
  plist="${HOME}/Library/LaunchAgents/${label}.plist"
  if [[ -f "$plist" ]]; then
    echo "ASSERT: LaunchAgent plist still present: $plist" >&2
    exit 1
  fi
elif [[ "$uname_s" == Linux && "$user_mode" -eq 0 ]]; then
  unit_path="/etc/systemd/system/${name}.service"
  if [[ -f "$unit_path" ]]; then
    echo "ASSERT: systemd unit still present: $unit_path" >&2
    exit 1
  fi
fi

set +e
run_svc "$bin" service status --name "$name" "${user_args[@]}" 2>/dev/null
status_after=$?
set -e
if [[ "$status_after" -eq 0 ]]; then
  echo "ASSERT: service still reports installed after uninstall" >&2
  exit 1
fi

echo "cli-service-lifecycle ok ($name)"