#!/usr/bin/env bash
# Login hook. Inspector no longer writes environment.d or systemd --user proxy
# variables. This still deletes leftovers from older builds, and if nothing is
# listening it clears Firefox, Chromium, and desktop hooks that still point at
# the Inspector port. When Inspector is running, leave those hooks alone.
set +e

inspector_running() {
  local pid cmd
  for pid in /proc/[0-9]*; do
    cmd="$(tr '\0' ' ' < "${pid}/cmdline" 2>/dev/null || true)"
    case "${cmd}" in
      *--clear-system-proxy*) continue ;;
    esac
    # Match the executable only. The fail-open helper's path contains the
    # TitaniumInspector config directory and must not count as the app.
    exe="${cmd%% *}"
    case "${exe}" in
      /opt/titanium-inspector/TitaniumInspector) return 0 ;;
      */TitaniumInspector) return 0 ;;
    esac
  done
  return 1
}

if inspector_running; then
  exit 0
fi

# True when some already-started process still sends traffic to the Inspector port.
# Those processes keep the value they inherited; killing the only listener drops them.
session_still_uses_proxy() {
  local pid
  for pid in /proc/[0-9]*; do
    if [[ ! -r "${pid}/environ" ]]; then
      continue
    fi
    if tr '\0' '\n' < "${pid}/environ" 2>/dev/null |
      grep -q -E '^(http_proxy|https_proxy|HTTP_PROXY|HTTPS_PROXY)=http://(127\.0\.0\.1|localhost)(:|/)'; then
      return 0
    fi
  done
  return 1
}

stop_fail_open() {
  local home="$1"
  if session_still_uses_proxy; then
    return 0
  fi
  local pidfile="${home}/.config/TitaniumInspector/fail-open-proxy.pid"
  local pid=""
  if [[ -f "${pidfile}" ]]; then
    pid="$(tr -d '[:space:]' < "${pidfile}" 2>/dev/null || true)"
    if [[ "${pid}" =~ ^[0-9]+$ ]] && [[ "${pid}" -gt 1 ]]; then
      kill -TERM "${pid}" >/dev/null 2>&1 || true
    fi
    rm -f "${pidfile}"
  fi
}

home="${HOME}"
if [[ -z "${home}" && -n "${USER}" ]]; then
  home="$(getent passwd "${USER}" | cut -d: -f6)"
fi

needs=0
if [[ -f "${home}/.config/environment.d/90-titanium-inspector-proxy.conf" ]]; then
  needs=1
fi
if [[ -f "${home}/.config/TitaniumInspector/chrome-profile-proxy.json" ]] ||
   [[ -f "${home}/.config/TitaniumInspector/firefox-proxy-backup.json" ]] ||
   [[ -f "${home}/.config/TitaniumInspector/proxy-snapshot.json" ]]; then
  needs=1
fi
if grep -q titanium.inspector.proxy.managed \
    "${home}/.mozilla/firefox/"*/prefs.js \
    "${home}/snap/firefox/common/.mozilla/firefox/"*/prefs.js \
    2>/dev/null; then
  needs=1
fi
if systemctl --user show-environment 2>/dev/null |
  grep -E '^(http_proxy|https_proxy|HTTP_PROXY|HTTPS_PROXY)=' |
  grep -q -E '127\.0\.0\.1|localhost'; then
  needs=1
fi
if command -v gsettings >/dev/null 2>&1; then
  mode="$(gsettings get org.gnome.system.proxy mode 2>/dev/null || true)"
  host="$(gsettings get org.gnome.system.proxy.http host 2>/dev/null || true)"
  case "${mode}" in
    *manual*)
      case "${host}" in
        *127.0.0.1*|*localhost*) needs=1 ;;
      esac
      ;;
  esac
fi
for policy in \
  "${home}/.config/google-chrome/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/chromium/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/microsoft-edge/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/BraveSoftware/Brave-Browser/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/opera/policies/managed/titanium-inspector-proxy.json" \
  /etc/opt/chrome/policies/managed/titanium-inspector-proxy.json \
  /etc/chromium/policies/managed/titanium-inspector-proxy.json \
  /etc/opt/edge/policies/managed/titanium-inspector-proxy.json \
  /etc/brave/policies/managed/titanium-inspector-proxy.json \
  /etc/opera/policies/managed/titanium-inspector-proxy.json
do
  if [[ -f "${policy}" ]]; then
    needs=1
  fi
done

if [[ "${needs}" -eq 0 ]]; then
  # Hooks are already gone. A leftover fail-open process would still occupy the
  # Inspector port and make the next launch look like a duplicate.
  stop_fail_open "${home}"
  exit 0
fi

if [[ -x /opt/titanium-inspector/TitaniumInspector ]]; then
  /opt/titanium-inspector/TitaniumInspector --clear-system-proxy >/dev/null 2>&1 || true
fi

# If the installed binary is an older build and left Firefox pinned, drop the
# managed proxy prefs. A direct connection is the safe result after a kill.
python3 - "${home}" <<'PY' || true
import glob, sys
home = sys.argv[1]
marker = "titanium.inspector.proxy.managed"
paths = glob.glob(home + "/.mozilla/firefox/*/prefs.js")
paths += glob.glob(home + "/snap/firefox/common/.mozilla/firefox/*/prefs.js")
keys = (
    "network.proxy.type",
    "network.proxy.http",
    "network.proxy.http_port",
    "network.proxy.ssl",
    "network.proxy.ssl_port",
    "network.proxy.share_proxy_settings",
    "network.proxy.no_proxies_on",
    "network.http.http3.enabled",
    marker,
)
for path in paths:
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except OSError:
        continue
    if marker not in text:
        continue
    kept = []
    for line in text.splitlines():
        if line.strip().startswith("user_pref(") and any(key in line for key in keys):
            continue
        kept.append(line)
    kept.append('user_pref("network.proxy.type", 5);')
    try:
        open(path, "w", encoding="utf-8").write("\n".join(kept) + "\n")
    except OSError:
        pass
PY

rm -f "${home}/.config/environment.d/90-titanium-inspector-proxy.conf"
# Empty override, not unset. Unset puts a login-time http://127.0.0.1:8866 value back.
systemctl --user set-environment http_proxy= https_proxy= HTTP_PROXY= HTTPS_PROXY= no_proxy= NO_PROXY= >/dev/null 2>&1 || true
if command -v gsettings >/dev/null 2>&1; then
  host="$(gsettings get org.gnome.system.proxy.http host 2>/dev/null || true)"
  case "${host}" in
    *127.0.0.1*|*localhost*)
      gsettings set org.gnome.system.proxy mode 'none' >/dev/null 2>&1 || true
      gsettings set org.gnome.system.proxy.http enabled false >/dev/null 2>&1 || true
      ;;
  esac
fi
rm -f \
  "${home}/.config/google-chrome/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/chromium/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/microsoft-edge/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/BraveSoftware/Brave-Browser/policies/managed/titanium-inspector-proxy.json" \
  "${home}/.config/opera/policies/managed/titanium-inspector-proxy.json" \
  /etc/opt/chrome/policies/managed/titanium-inspector-proxy.json \
  /etc/chromium/policies/managed/titanium-inspector-proxy.json \
  /etc/opt/edge/policies/managed/titanium-inspector-proxy.json \
  /etc/brave/policies/managed/titanium-inspector-proxy.json \
  /etc/opera/policies/managed/titanium-inspector-proxy.json

# Give a browser relaunch a moment, then drop the fail-open helper. Leaving it
# on the Inspector port makes the next launch look like a duplicate.
sleep 1
stop_fail_open "${home}"
exit 0
