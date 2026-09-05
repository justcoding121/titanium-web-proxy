#!/usr/bin/env bash
# Remove a user-local Titanium Inspector install created by install.sh.
set -euo pipefail

PREFIX="${PREFIX:-${HOME}/.local}"
APP_DIR="${PREFIX}/share/TitaniumInspector"
BIN_DIR="${PREFIX}/bin"
APPS_DIR="${PREFIX}/share/applications"
HICOLOR_ROOT="${PREFIX}/share/icons/hicolor"
ICON_ID="titanium-inspector"

echo "Removing Titanium Inspector from ${PREFIX} ..."
rm -f "${BIN_DIR}/titanium-inspector"
rm -f "${APPS_DIR}/TitaniumInspector.desktop"
for size in 16 32 48 128 256 512; do
  rm -f "${HICOLOR_ROOT}/${size}x${size}/apps/${ICON_ID}.png"
done
rm -f "${HICOLOR_ROOT}/256x256/apps/${ICON_ID}.ico"
rm -rf "${APP_DIR}"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "${APPS_DIR}" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1 && [[ -d "${HICOLOR_ROOT}" ]]; then
  gtk-update-icon-cache -f -t "${HICOLOR_ROOT}" >/dev/null 2>&1 || true
fi

echo "Uninstalled Titanium Inspector."
