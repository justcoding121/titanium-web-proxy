#!/usr/bin/env bash
# Opens a downloaded .deb/.rpm. Titanium packages get an install or update prompt.
# Every other package is handed back to App Center so this handler can be the
# default double-click action without changing other installs.
set -euo pipefail

file="${1:-}"
if [[ -z "${file}" || ! -f "${file}" ]]; then
  exit 1
fi

forward_to_app_center() {
  # Guard against xdg-open re-entering this script when it is the MIME default
  # for .deb/.rpm and neither Snap Store nor GNOME Software is installed.
  if [[ -n "${TITANIUM_PACKAGE_FORWARD:-}" ]]; then
    exit 1
  fi
  export TITANIUM_PACKAGE_FORWARD=1

  if [[ -x /snap/bin/snap-store ]]; then
    exec /snap/bin/snap-store "$file"
  fi
  if [[ -x /usr/bin/gnome-software ]]; then
    exec /usr/bin/gnome-software --local-filename="$file"
  fi
  # Do not xdg-open the same MIME: that loops back into this handler.
  exit 1
}

pkg=""
newver=""
if [[ "${file}" == *.deb ]] || dpkg-deb -I "${file}" >/dev/null 2>&1; then
  pkg="$(dpkg-deb -f "${file}" Package 2>/dev/null || true)"
  newver="$(dpkg-deb -f "${file}" Version 2>/dev/null || true)"
elif command -v rpm >/dev/null 2>&1; then
  pkg="$(rpm -qp --queryformat '%{NAME}' "${file}" 2>/dev/null || true)"
  newver="$(rpm -qp --queryformat '%{VERSION}' "${file}" 2>/dev/null || true)"
fi

case "${pkg}" in
  titanium-inspector|titanium-cli) ;;
  *) forward_to_app_center ;;
esac

oldver=""
if command -v dpkg-query >/dev/null 2>&1; then
  oldver="$(dpkg-query -W -f '${Version}' "${pkg}" 2>/dev/null || true)"
fi
if [[ -z "${oldver}" ]] && command -v rpm >/dev/null 2>&1; then
  oldver="$(rpm -q --queryformat '%{VERSION}' "${pkg}" 2>/dev/null || true)"
  [[ "${oldver}" == "package "* ]] && oldver=""
fi

if [[ -z "${oldver}" ]]; then
  title="Install ${pkg}"
  text="Install ${pkg} ${newver}?"
elif dpkg --compare-versions "${newver}" gt "${oldver}"; then
  title="Update ${pkg}"
  text="Update ${pkg} from ${oldver} to ${newver}?"
else
  # Same or older file. Match the Mac disk image: keep the installed copy.
  zenity --info --title="${pkg} is already installed" \
    --text="${pkg} ${oldver} is already installed. This file is ${newver}, so the installed copy was left in place."
  exit 0
fi

if ! zenity --question --title="${title}" --text="${text}" --ok-label="Continue" --cancel-label="Cancel"; then
  exit 0
fi

install_pkg() {
  if [[ "${file}" == *.rpm ]]; then
    "$@" rpm -Uvh "${file}"
  else
    "$@" dpkg -i "${file}"
  fi
}

# sudo -n is the headless path (CI, an already-authorized shell). A desktop
# session falls through to the pkexec password dialog.
if sudo -n true >/dev/null 2>&1; then
  install_pkg sudo -n
elif [[ -x /usr/bin/pkexec ]]; then
  install_pkg pkexec
elif [[ -x /usr/bin/sudo ]]; then
  install_pkg sudo
else
  zenity --error --text="Need pkexec or sudo to install ${pkg}."
  exit 1
fi

if [[ "${pkg}" == "titanium-inspector" ]]; then
  gtk-launch titanium-inspector >/dev/null 2>&1 || true
fi
