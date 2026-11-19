#!/usr/bin/env bash
# deb/rpm pre-remove. The Inspector process may already be dead (kill -9, crash,
# or this script stops it). Clear proxy settings anyway so browsers do not hang
# on 127.0.0.1 after the package is gone.
# Stop the process first: a live process still has the original snapshot and
# would put the proxy back on SIGTERM if we cleared first.
set +e

EXE="/opt/titanium-inspector/TitaniumInspector"

if [[ -x "${EXE}" ]]; then
  pkill -TERM -f "^${EXE}( |$)" >/dev/null 2>&1 || true
  sleep 1
  pkill -KILL -f "^${EXE}( |$)" >/dev/null 2>&1 || true
fi

desktop_user() {
  local user=""
  if command -v loginctl >/dev/null 2>&1; then
    user="$(loginctl list-sessions --no-legend 2>/dev/null | awk '{print $3}' | grep -v '^root$' | head -1 || true)"
  fi
  if [[ -z "${user}" ]]; then
    user="$(who 2>/dev/null | awk '{print $1}' | grep -v '^root$' | head -1 || true)"
  fi
  if [[ -z "${user}" ]]; then
    user="${SUDO_USER:-}"
  fi
  printf '%s' "${user}"
}

USER_NAME="$(desktop_user)"
if [[ -n "${USER_NAME}" && -x "${EXE}" ]]; then
  UID_NUM="$(id -u "${USER_NAME}" 2>/dev/null || true)"
  if [[ -n "${UID_NUM}" && "$(id -u)" -eq 0 && "${USER_NAME}" != "root" ]]; then
    RUNTIME="/run/user/${UID_NUM}"
    # --clear-system-proxy already restores/clears loopback GNOME/KDE and blanks
    # only loopback-valued session proxy env. Do not force mode 'none' or blank a
    # corporate http_proxy the user configured themselves.
    sudo -u "${USER_NAME}" \
      env XDG_RUNTIME_DIR="${RUNTIME}" DBUS_SESSION_BUS_ADDRESS="unix:path=${RUNTIME}/bus" \
      "${EXE}" --clear-system-proxy >/dev/null 2>&1 || true
  else
    "${EXE}" --clear-system-proxy >/dev/null 2>&1 || true
  fi
fi

# Machine Chromium policies are root-owned. The user-level clear uses sudo -n
# and cannot prompt during package removal.
rm -f \
  /etc/opt/chrome/policies/managed/titanium-inspector-proxy.json \
  /etc/chromium/policies/managed/titanium-inspector-proxy.json \
  /etc/opt/edge/policies/managed/titanium-inspector-proxy.json \
  /etc/brave/policies/managed/titanium-inspector-proxy.json \
  /etc/opera/policies/managed/titanium-inspector-proxy.json

if command -v systemctl >/dev/null 2>&1; then
  systemctl disable --global titanium-inspector-clear-proxy.timer >/dev/null 2>&1 || true
  systemctl disable --global titanium-inspector-clear-proxy.service >/dev/null 2>&1 || true
fi

exit 0
