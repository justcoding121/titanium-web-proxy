#!/usr/bin/env bash
# deb/rpm pre-remove. The Inspector process may already be dead (kill -9, crash,
# or this script stops it). Clear proxy settings anyway so browsers do not hang
# on 127.0.0.1 after the package is gone.
# Stop the process first: a live process still has the original snapshot and
# would put the proxy back on SIGTERM if we cleared first.
set +e

EXE="/opt/titanium-inspector/TitaniumInspector"
PRERM_LOG="/tmp/titanium-inspector-prerm.log"

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
  printf '%s\n' "${users[@]}" | awk 'NF && !seen[$0]++'
}

ensure_user_runtime_dir() {
  local user="$1" uid_num="$2" runtime="$3"
  if [[ -d "${runtime}" ]]; then
    return 0
  fi
  mkdir -p "${runtime}" 2>/dev/null || true
  chown "${uid_num}:${uid_num}" "${runtime}" 2>/dev/null || chown "${user}" "${runtime}" 2>/dev/null || true
  chmod 700 "${runtime}" 2>/dev/null || true
}

write_clear_helper() {
  local path="$1"
  cat > "${path}" <<'EOF'
#!/usr/bin/env bash
# Mirror LinuxSystemProxyBackend.ClearGnomeLoopbackManualProxy.
set +e
command -v gsettings >/dev/null 2>&1 || exit 0
mode="$(gsettings get org.gnome.system.proxy mode 2>/dev/null | tr -d "'\"" || true)"
host="$(gsettings get org.gnome.system.proxy.http host 2>/dev/null | tr -d "'\"" || true)"
echo "before: mode=${mode} host=${host}" >> /tmp/titanium-inspector-prerm.log
[[ "${mode}" == "manual" ]] || exit 0
case "${host}" in
  127.0.0.1|localhost|::1|"")
    gsettings set org.gnome.system.proxy mode none
    rc1=$?
    gsettings set org.gnome.system.proxy.http enabled false
    rc2=$?
    echo "gsettings set rc=${rc1}/${rc2}" >> /tmp/titanium-inspector-prerm.log
    ;;
  *)
    echo "skip host=${host}" >> /tmp/titanium-inspector-prerm.log
    ;;
esac
mode="$(gsettings get org.gnome.system.proxy mode 2>/dev/null || true)"
host="$(gsettings get org.gnome.system.proxy.http host 2>/dev/null || true)"
echo "after: mode=${mode} host=${host}" >> /tmp/titanium-inspector-prerm.log
EOF
  chmod 755 "${path}"
}

clear_user_loopback_gnome() {
  local user="$1" uid_num home runtime helper
  uid_num="$(id -u "${user}" 2>/dev/null || true)"
  home="$(getent passwd "${user}" | cut -d: -f6)"
  if [[ -z "${uid_num}" || -z "${home}" ]]; then
    echo "skip ${user}: no home" >> "${PRERM_LOG}" 2>&1 || true
    return 0
  fi
  runtime="/run/user/${uid_num}"
  ensure_user_runtime_dir "${user}" "${uid_num}" "${runtime}"
  helper="$(mktemp)"
  write_clear_helper "${helper}"
  chown "${uid_num}:${uid_num}" "${helper}" 2>/dev/null || true

  # Always use dbus-run-session so clears hit the same dconf writer path as the
  # install-smoke driver (xvfb-run dbus-run-session). Pointing at a stale
  # /run/user/UID/bus can leave the smoke-visible mode=manual untouched.
  if [[ -x "${EXE}" ]]; then
    if command -v dbus-run-session >/dev/null 2>&1; then
      sudo -u "${user}" -H \
        env HOME="${home}" XDG_RUNTIME_DIR="${runtime}" \
        dbus-run-session -- "${EXE}" --clear-system-proxy >>"${PRERM_LOG}" 2>&1 || true
    else
      sudo -u "${user}" -H \
        env HOME="${home}" XDG_RUNTIME_DIR="${runtime}" \
        "${EXE}" --clear-system-proxy >>"${PRERM_LOG}" 2>&1 || true
    fi
  fi
  if command -v dbus-run-session >/dev/null 2>&1; then
    sudo -u "${user}" -H \
      env HOME="${home}" XDG_RUNTIME_DIR="${runtime}" \
      dbus-run-session -- "${helper}" >>"${PRERM_LOG}" 2>&1 || true
  else
    sudo -u "${user}" -H \
      env HOME="${home}" XDG_RUNTIME_DIR="${runtime}" \
      "${helper}" >>"${PRERM_LOG}" 2>&1 || true
  fi
  rm -f "${helper}"
}

{
  echo "prerm users: $(desktop_users | tr '\n' ' ')"
  echo "PATH=${PATH}"
  command -v gsettings || echo "gsettings missing"
  command -v sudo || echo "sudo missing"
  command -v dbus-run-session || echo "dbus-run-session missing"
  ls -ld /run/user /run/user/* 2>&1 || echo "(no /run/user entries)"
} > "${PRERM_LOG}" 2>&1 || true
# Helper runs as each desktop user and appends probes; root-owned 644 blocks that.
chmod 666 "${PRERM_LOG}" 2>/dev/null || true

while read -r USER_NAME; do
  [[ -n "${USER_NAME}" ]] || continue
  echo "clear for ${USER_NAME}" >> "${PRERM_LOG}" 2>&1 || true
  clear_user_loopback_gnome "${USER_NAME}"
done < <(desktop_users)

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
