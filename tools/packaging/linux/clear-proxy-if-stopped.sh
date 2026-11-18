#!/usr/bin/env bash
# Login hook. Inspector no longer writes environment.d or systemd --user proxy
# variables. This still deletes leftovers from older builds, and if nothing is
# listening it clears Firefox, Chromium, and desktop hooks that still point at
# the Inspector port. GNOME gsettings, KDE, XFCE helpers, and Flatpak or Snap
# browser profiles each keep their own copy. When Inspector is running, leave
# those hooks alone.
set +e

inspector_running() {
  local pid cmd
  for pid in /proc/[0-9]*; do
    # The shell opens the redirect itself. A process that exits mid-scan, or a
    # /proc entry we cannot read, must not print on the login hook.
    cmd="$( { tr '\0' ' ' < "${pid}/cmdline"; } 2>/dev/null || true )"
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
    # Mode bits can say readable while the kernel still returns EACCES.
    # The group redirect hides that open error from the login journal.
    if { tr '\0' '\n' < "${pid}/environ"; } 2>/dev/null |
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
# Firefox marker check is below, with Flatpak and loopback prefs. A glob that
# matches nothing would make grep fail the whole script under set -e.
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
# Flatpak and snap keep the same file under the home directory, not /etc.
for root in "${home}/.var" "${home}/snap"; do
  [[ -d "${root}" ]] || continue
  while IFS= read -r policy; do
    [[ -n "${policy}" ]] || continue
    needs=1
  done < <(find "${root}" -name titanium-inspector-proxy.json -type f 2>/dev/null)
done

# Chromium Preferences outlive the marker file. A loopback fixed_servers entry
# ignores the desktop proxy switch, so a kill or uninstall still breaks browsing
# after GNOME/KDE proxy is off. Other fixed proxies are left alone.
if python3 - "${home}" <<'PY'
import json, os, sys
home = sys.argv[1]
roots = (
    ".config/google-chrome",
    ".config/google-chrome-beta",
    ".config/google-chrome-unstable",
    ".config/chromium",
    ".config/microsoft-edge",
    ".config/BraveSoftware/Brave-Browser",
    ".config/opera",
    "snap/chromium/common/chromium",
    "snap/microsoft-edge/common/microsoft-edge",
    ".var/app/com.google.Chrome/config/google-chrome",
    ".var/app/org.chromium.Chromium/config/chromium",
    ".var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser",
    ".var/app/com.microsoft.Edge/config/microsoft-edge",
)
changed = False
for rel in roots:
    root = os.path.join(home, rel)
    if not os.path.isdir(root):
        continue
    names = ["Default"]
    try:
        names += [n for n in os.listdir(root) if n.startswith("Profile ")]
    except OSError:
        pass
    for name in names:
        path = os.path.join(root, name, "Preferences")
        if not os.path.isfile(path):
            continue
        try:
            doc = json.load(open(path, encoding="utf-8"))
        except (OSError, ValueError):
            continue
        proxy = doc.get("proxy")
        if not isinstance(proxy, dict):
            continue
        if proxy.get("mode") != "fixed_servers":
            continue
        server = str(proxy.get("server") or "")
        if not any(host in server for host in ("127.0.0.1", "localhost", "[::1]")):
            continue
        doc["proxy"] = {"mode": "system"}
        tmp = path + ".tmp"
        try:
            json.dump(doc, open(tmp, "w", encoding="utf-8"), separators=(",", ":"))
            os.replace(tmp, path)
            changed = True
        except OSError:
            pass
sys.exit(0 if changed else 1)
PY
then
  needs=1
fi

# KDE stores its own proxy. gsettings "none" does not turn that off.
kio="${home}/.config/kioslaverc"
if [[ -f "${kio}" ]] && python3 - "${kio}" <<'PY'
import sys
path = sys.argv[1]
lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
in_proxy = False
type_idx = None
loopback = False
for i, line in enumerate(lines):
    stripped = line.strip()
    if stripped.startswith("[") and stripped.endswith("]"):
        in_proxy = stripped.lower() == "[proxy settings]"
        continue
    if not in_proxy or "=" not in line:
        continue
    key, val = line.split("=", 1)
    key = key.strip()
    if key == "ProxyType":
        type_idx = i
    if key in ("httpProxy", "httpsProxy", "ftpProxy", "socksProxy") and any(
        host in val for host in ("127.0.0.1", "localhost", "[::1]")
    ):
        loopback = True
if not loopback or type_idx is None or lines[type_idx].split("=", 1)[-1].strip() != "1":
    sys.exit(1)
lines[type_idx] = "ProxyType=0"
open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
sys.exit(0)
PY
then
  needs=1
fi

# XFCE and other desktops launch Chromium from a user .desktop or helper that
# still carries --proxy-server after the desktop proxy is off.
marker="X-Titanium-Inspector-Proxy=true"
for desk in \
  "${home}/.local/share/applications/google-chrome.desktop" \
  "${home}/.local/share/applications/google-chrome-stable.desktop" \
  "${home}/.local/share/applications/chromium.desktop" \
  "${home}/.local/share/applications/chromium-browser.desktop" \
  "${home}/.local/share/applications/brave-browser.desktop" \
  "${home}/.local/share/applications/microsoft-edge.desktop" \
  "${home}/.local/share/applications/microsoft-edge-stable.desktop" \
  "${home}/.local/share/applications/opera.desktop" \
  "${home}/.local/share/xfce4/helpers/google-chrome.desktop"
do
  if [[ -f "${desk}" ]] && grep -q "${marker}" "${desk}"; then
    rm -f "${desk}"
    needs=1
  fi
done
xfce_rc="${home}/.config/xfce4/helpers.rc"
if [[ -f "${xfce_rc}" ]] && grep -q "${marker}" "${xfce_rc}"; then
  rm -f "${xfce_rc}"
  needs=1
fi
launch_dir="${home}/.local/share/titanium-inspector/launchers"
if [[ -d "${launch_dir}" ]]; then
  for launcher in "${launch_dir}"/*.sh; do
    [[ -f "${launcher}" ]] || continue
    if grep -q -E -- '--proxy-server=http://(127\.0\.0\.1|localhost|\[::1\])' "${launcher}"; then
      rm -f "${launcher}"
      needs=1
    fi
  done
fi

# Classic, Snap, and Flatpak Firefox. A loopback manual proxy ignores the
# desktop proxy switch even after the marker pref is gone.
if python3 - "${home}" <<'PY'
import glob, sys
home = sys.argv[1]
roots = (
    home + "/.mozilla/firefox",
    home + "/snap/firefox/common/.mozilla/firefox",
    home + "/.var/app/org.mozilla.firefox/.mozilla/firefox",
    home + "/.var/app/org.mozilla.Firefox/.mozilla/firefox",
)
marker = "titanium.inspector.proxy.managed"
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
changed = False
for root in roots:
    for path in glob.glob(root + "/*/prefs.js"):
        try:
            text = open(path, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        loopback = False
        for line in text.splitlines():
            if "network.proxy.http\"" not in line and "network.proxy.ssl\"" not in line:
                continue
            if any(host in line for host in ("127.0.0.1", "localhost", "[::1]")):
                loopback = True
                break
        if marker not in text and not loopback:
            continue
        kept = []
        for line in text.splitlines():
            if line.strip().startswith("user_pref(") and any(key in line for key in keys):
                continue
            kept.append(line)
        kept.append('user_pref("network.proxy.type", 5);')
        try:
            open(path, "w", encoding="utf-8").write("\n".join(kept) + "\n")
            changed = True
        except OSError:
            pass
sys.exit(0 if changed else 1)
PY
then
  needs=1
fi

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
paths = []
for root in (
    home + "/.mozilla/firefox",
    home + "/snap/firefox/common/.mozilla/firefox",
    home + "/.var/app/org.mozilla.firefox/.mozilla/firefox",
    home + "/.var/app/org.mozilla.Firefox/.mozilla/firefox",
):
    paths += glob.glob(root + "/*/prefs.js")
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
for root in "${home}/.var" "${home}/snap"; do
  [[ -d "${root}" ]] || continue
  while IFS= read -r policy; do
    rm -f "${policy}"
  done < <(find "${root}" -name titanium-inspector-proxy.json -type f 2>/dev/null)
done

# Give a browser relaunch a moment, then drop the fail-open helper. Leaving it
# on the Inspector port makes the next launch look like a duplicate.
sleep 1
stop_fail_open "${home}"
exit 0
