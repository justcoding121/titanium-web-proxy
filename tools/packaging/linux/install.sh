#!/usr/bin/env bash
# Install Titanium Inspector into a user-local prefix and register a desktop entry.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${PREFIX:-${HOME}/.local}"
APP_DIR="${PREFIX}/share/TitaniumInspector"
BIN_DIR="${PREFIX}/bin"
APPS_DIR="${PREFIX}/share/applications"
ICONS_DIR="${PREFIX}/share/icons/hicolor/256x256/apps"
DESKTOP_SRC="${SCRIPT_DIR}/TitaniumInspector.desktop.in"
EXE_NAME="TitaniumInspector"

if [[ ! -x "${SCRIPT_DIR}/${EXE_NAME}" && ! -f "${SCRIPT_DIR}/${EXE_NAME}" ]]; then
  echo "error: ${EXE_NAME} not found next to install.sh (extract the full zip first)" >&2
  exit 1
fi

if [[ ! -f "${DESKTOP_SRC}" ]]; then
  echo "error: missing ${DESKTOP_SRC}" >&2
  exit 1
fi

mkdir -p "${APP_DIR}" "${BIN_DIR}" "${APPS_DIR}" "${ICONS_DIR}"

echo "Installing payload to ${APP_DIR} ..."
# Refresh install dir; keep a clean copy of the zip contents (including helpers).
rm -rf "${APP_DIR}"
mkdir -p "${APP_DIR}"
cp -a "${SCRIPT_DIR}/." "${APP_DIR}/"
chmod +x "${APP_DIR}/${EXE_NAME}" "${APP_DIR}/install.sh" "${APP_DIR}/uninstall.sh" 2>/dev/null || true

ICON_SRC=""
for candidate in "${APP_DIR}/app.ico" "${APP_DIR}/Assets/app.ico" "${SCRIPT_DIR}/app.ico"; do
  if [[ -f "${candidate}" ]]; then
    ICON_SRC="${candidate}"
    break
  fi
done

ICON_DEST="${ICONS_DIR}/titanium-inspector.png"
if [[ -n "${ICON_SRC}" ]]; then
  if command -v convert >/dev/null 2>&1; then
    convert "${ICON_SRC}" -thumbnail 256x256 "${ICON_DEST}" || cp -f "${ICON_SRC}" "${ICONS_DIR}/titanium-inspector.ico"
  else
    # Many desktops accept .ico; also keep a copy named .png path fallback via Icon= absolute .ico
    cp -f "${ICON_SRC}" "${ICONS_DIR}/titanium-inspector.ico"
    ICON_DEST="${ICONS_DIR}/titanium-inspector.ico"
  fi
else
  ICON_DEST=""
fi

ln -sfn "${APP_DIR}/${EXE_NAME}" "${BIN_DIR}/titanium-inspector"

DESKTOP_OUT="${APPS_DIR}/TitaniumInspector.desktop"
EXEC_LINE="${APP_DIR}/${EXE_NAME}"
ICON_LINE="${ICON_DEST}"
sed -e "s|@EXEC@|${EXEC_LINE}|g" -e "s|@ICON@|${ICON_LINE}|g" "${DESKTOP_SRC}" > "${DESKTOP_OUT}"
chmod 644 "${DESKTOP_OUT}"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "${APPS_DIR}" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1 && [[ -d "${PREFIX}/share/icons/hicolor" ]]; then
  gtk-update-icon-cache -f -t "${PREFIX}/share/icons/hicolor" >/dev/null 2>&1 || true
fi

echo "Installed Titanium Inspector."
echo "  App dir:    ${APP_DIR}"
echo "  Launcher:   ${DESKTOP_OUT}"
echo "  PATH link:  ${BIN_DIR}/titanium-inspector"
echo "Uninstall with: ${APP_DIR}/uninstall.sh"
echo "  or: PREFIX=${PREFIX} ${SCRIPT_DIR}/uninstall.sh"
