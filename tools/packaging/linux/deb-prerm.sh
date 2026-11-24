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

# dpkg runs maintainer scripts with a cleaned environment, so SUDO_USER is often
# missing. Collect every interactive home owner (plus loginctl/who/SUDO_USER when
# present) and clear loopback GNOME proxy for each — headless CI only has
# /home/runner and no login session.
desktop_users() {
  local -a users=()
  local u home
  if command -v loginctl >/dev/null 2>&1; then
    while read -r u; do
      [[ -n "$u" && "$u" != "root" ]] || continue
      users+=("$u")
    done < <(loginctl list-sessions --no-legend 2>/dev/null | awk '{print $3}' || true)
  fi
  while read -r u; do
    [[ -n "$u" && "$u" != "root" ]] || continue
    users+=("$u")
  done < <(who 2>/dev/null | awk '{print $1}' || true)
  if [[ -n "${SUDO_USER:-}" && "${SUDO_USER}" != "root" ]]; then
    users+=("${SUDO_USER}")
  fi
  if [[ -d /home ]]; then
    for home in /home/*; do
      [[ -d "${home}" ]] || continue
      u="$(basename "${home}")"
      [[ "${u}" != "root" ]] || continue
      users+=("${u}")
    done
  fi
  # Unique preserve order
  printf '%s\n' "${users[@]}" | awk 'NF && !seen[$0]++'
}

# Mirror LinuxSystemProxyBackend.ClearGnomeLoopbackManualProxy. Only flips mode
# when GNOME is still aimed at loopback — never blanks a corporate proxy host.
write_clear_helper() {
  local path="$1"
  cat > "${path}" <<'EOF'
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
  chmod 755 "${path}"
}

ensure_user_runtime_dir() {
  local user="$1" uid_num="$2" runtime="$3"
  # Headless CI (GitHub-hosted) often has no logind session, so /run/user/UID is
  # missing. dconf/gsettings need a writable XDG_RUNTIME_DIR or writes no-op.
  if [[ -d "${runtime}" ]]; then
    return 0
  fi
  mkdir -p "${runtime}" 2>/dev/null || true
  chown "${uid_num}:${uid_num}" "${runtime}" 2>/dev/null || chown "${user}" "${runtime}" 2>/dev/null || true
  chmod 700 "${runtime}" 2>/dev/null || true
}

run_as_desktop_user() {
  local user="$1"
  shift
  local uid_num runtime bus home
  uid_num="$(id -u "${user}" 2>/dev/null || true)"
  home="$(getent passwd "${user}" | cut -d: -f6)"
  if [[ -z "${uid_num}" || -z "${home}" ]]; then
    return 0
  fi
  runtime="/run/user/${uid_num}"
  ensure_user_runtime_dir "${user}" "${uid_num}" "${runtime}"
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

helper="$(mktemp)"
write_clear_helper "${helper}"
prerm_log="/tmp/titanium-inspector-prerm.log"
{
  echo "prerm users: $(desktop_users | tr '\n' ' ')"
  echo "runtime dirs:"
  ls -ld /run/user/* 2>/dev/null || echo "(none)"
} > "${prerm_log}" 2>&1 || true

while read -r USER_NAME; do
  [[ -n "${USER_NAME}" ]] || continue
  echo "clear for ${USER_NAME}" >> "${prerm_log}" 2>&1 || true
  if [[ -x "${EXE}" ]]; then
    # --clear-system-proxy restores/clears loopback GNOME/KDE and blanks only
    # loopback-valued session proxy env.
    run_as_desktop_user "${USER_NAME}" "${EXE}" --clear-system-proxy
  fi
  run_as_desktop_user "${USER_NAME}" "${helper}"
  run_as_desktop_user "${USER_NAME}" bash -c \
    'echo "after: mode=$(gsettings get org.gnome.system.proxy mode 2>/dev/null) host=$(gsettings get org.gnome.system.proxy.http host 2>/dev/null)" >> /tmp/titanium-inspector-prerm.log'
done < <(desktop_users)

rm -f "${helper}"

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
