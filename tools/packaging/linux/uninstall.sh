#!/usr/bin/env bash
# Remove a user-local Titanium Inspector install created by install.sh.
set -euo pipefail

PREFIX="${PREFIX:-${HOME}/.local}"
APP_DIR="${PREFIX}/share/TitaniumInspector"
BIN_DIR="${PREFIX}/bin"
APPS_DIR="${PREFIX}/share/applications"
ICONS_DIR="${PREFIX}/share/icons/hicolor/256x256/apps"

echo "Removing Titanium Inspector from ${PREFIX} ..."
rm -f "${BIN_DIR}/titanium-inspector"
rm -f "${APPS_DIR}/TitaniumInspector.desktop"
rm -f "${ICONS_DIR}/titanium-inspector.png" "${ICONS_DIR}/titanium-inspector.ico"
rm -rf "${APP_DIR}"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "${APPS_DIR}" >/dev/null 2>&1 || true
fi

echo "Uninstalled Titanium Inspector."
