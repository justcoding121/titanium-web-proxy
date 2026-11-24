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

# Mirror LinuxSystemProxyBackend.ClearGnomeLoopbackManualProxy for when the
# managed EXE cannot talk to a session bus (headless CI). Only flips mode when
# GNOME is still aimed at loopback — never blanks a corporate proxy host.
clear_gnome_loopback_if_owned() {
  command -v gsettings >/dev/null 2>&1 || return 0
  local mode host
  mode="$(gsettings get org.gnome.system.proxy mode 2>/dev/null | tr -d "'\"" || true)"
  host="$(gsettings get org.gnome.system.proxy.http host 2>/dev/null | tr -d "'\"" || true)"
  if [[ "${mode}" != "manual" ]]; then
    return 0
  fi
  case "${host}" in
    127.0.0.1|localhost|::1|"")
      gsettings set org.gnome.system.proxy mode none >/dev/null 2>&1 || true
      gsettings set org.gnome.system.proxy.http enabled false >/dev/null 2>&1 || true
      ;;
  esac
}

run_as_desktop_user() {
  local user="$1"
  shift
  local uid_num runtime bus home
  uid_num="$(id -u "${user}" 2>/dev/null || true)"
  home="$(getent passwd "${user}" | cut -d: -f6)"
  if [[ -z "${uid_num}" ]]; then
    "$@" || true
    return 0
  fi
  runtime="/run/user/${uid_num}"
  bus="${runtime}/bus"
  if [[ -S "${bus}" ]]; then
    sudo -u "${user}" -H \
      env HOME="${home}" XDG_RUNTIME_DIR="${runtime}" DBUS_SESSION_BUS_ADDRESS="unix:path=${bus}" \
      "$@" >/dev/null 2>&1 || true
  elif command -v dbus-run-session >/dev/null 2>&1; then
    sudo -u "${user}" -H \
      env HOME="${home}" XDG_RUNTIME_DIR="${runtime}" \
      dbus-run-session -- "$@" >/dev/null 2>&1 || true
  else
    sudo -u "${user}" -H \
      env HOME="${home}" XDG_RUNTIME_DIR="${runtime}" \
      "$@" >/dev/null 2>&1 || true
  fi
}

USER_NAME="$(desktop_user)"
if [[ -n "${USER_NAME}" && "$(id -u)" -eq 0 && "${USER_NAME}" != "root" ]]; then
  # --clear-system-proxy restores/clears loopback GNOME/KDE and blanks only
  # loopback-valued session proxy env. Shell fallback covers headless CI when
  # the EXE clear cannot reach a usable session bus.
  if [[ -x "${EXE}" ]]; then
    run_as_desktop_user "${USER_NAME}" "${EXE}" --clear-system-proxy
  fi
  helper="$(mktemp)"
  cat > "${helper}" <<'EOF'
#!/usr/bin/env bash
command -v gsettings >/dev/null 2>&1 || exit 0
mode="$(gsettings get org.gnome.system.proxy mode 2>/dev/null | tr -d "'\"" || true)"
host="$(gsettings get org.gnome.system.proxy.http host 2>/dev/null | tr -d "'\"" || true)"
[[ "${mode}" == "manual" ]] || exit 0
case "${host}" in
  127.0.0.1|localhost|::1|"")
    gsettings set org.gnome.system.proxy mode none >/dev/null 2>&1 || true
    gsettings set org.gnome.system.proxy.http enabled false >/dev/null 2>&1 || true
    ;;
esac
EOF
  chmod 755 "${helper}"
  run_as_desktop_user "${USER_NAME}" "${helper}"
  rm -f "${helper}"
elif [[ -x "${EXE}" ]]; then
  "${EXE}" --clear-system-proxy >/dev/null 2>&1 || true
  clear_gnome_loopback_if_owned
fi

# Machine Chromium policies are root-owned. The user-level clear uses sudo -n
# and cannot prompt during package removal.
rm -f \
  /etc/opt/chrome/policies/managed/titanium-inspector-proxy.json \
  /etc/chromium/policies/managed/titanium-inspector-proxy.json \
  /etc/opt/edge/policies/managed/titanium-inspector-proxy.json \
  /etc/brave/policies/managed/titanium-inspector-proxy.json \
  /etc/opera/policies/managed/titanium-inspector-proxy.json

# On remove/purge only (not upgrade): drop the MIME defaults postinst appended so
# a leftover handler does not keep claiming .deb/.rpm after uninstall.
action="${1:-}"
if [[ "${action}" == "remove" || "${action}" == "purge" ]]; then
  apps=/etc/xdg/mimeapps.list
  if [[ -f "${apps}" ]]; then
    tmp="$(mktemp)"
    grep -v 'titanium-inspector-package.desktop' "${apps}" > "${tmp}" 2>/dev/null || true
    mv "${tmp}" "${apps}" 2>/dev/null || rm -f "${tmp}"
  fi
fi

if command -v systemctl >/dev/null 2>&1; then
  systemctl disable --global titanium-inspector-clear-proxy.timer >/dev/null 2>&1 || true
  systemctl disable --global titanium-inspector-clear-proxy.service >/dev/null 2>&1 || true
fi

exit 0
