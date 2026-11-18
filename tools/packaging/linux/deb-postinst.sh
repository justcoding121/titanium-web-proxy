#!/bin/sh
set -e
# Make double-clicking a downloaded package offer Update for Titanium packages.
# Other packages are forwarded to App Center by open-downloaded-package.sh.
update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
apps=/etc/xdg/mimeapps.list
if ! grep -q 'titanium-inspector-package.desktop' "$apps" 2>/dev/null; then
  mkdir -p /etc/xdg
  if ! grep -q '\[Default Applications\]' "$apps" 2>/dev/null; then
    printf '\n[Default Applications]\n' >> "$apps"
  fi
  printf 'application/vnd.debian.binary-package=titanium-inspector-package.desktop\napplication/x-rpm=titanium-inspector-package.desktop\n' >> "$apps"
fi
# Enable the login cleanup for every user. It no-ops while Inspector is running.
if command -v systemctl >/dev/null 2>&1; then
  systemctl enable --global titanium-inspector-clear-proxy.service >/dev/null 2>&1 || true
  systemctl enable --global titanium-inspector-clear-proxy.timer >/dev/null 2>&1 || true
fi
# An upgrade's pre-remove belongs to the package being replaced, so it can
# leave Chromium Preferences pointed at the old port. Run the new cleanup now
# for the desktop user instead of waiting for the next login.
user="${SUDO_USER:-}"
if [ -z "$user" ] && [ -n "${PKEXEC_UID:-}" ]; then
  user=$(id -nu "$PKEXEC_UID" 2>/dev/null || true)
fi
if [ -n "$user" ] && [ "$user" != root ]; then
  uid=$(id -u "$user" 2>/dev/null || true)
  home=$(getent passwd "$user" 2>/dev/null | cut -d: -f6)
  clear_bin=/usr/lib/titanium-inspector/titanium-inspector-clear
  if [ -n "$uid" ] && [ -n "$home" ] && [ -x "$clear_bin" ]; then
    sudo -u "$user" \
      env HOME="$home" XDG_RUNTIME_DIR="/run/user/$uid" \
      DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$uid/bus" \
      "$clear_bin" >/dev/null 2>&1 || true
  fi
fi
