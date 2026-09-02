#!/usr/bin/env bash
# Wrap a self-contained publish directory as an AppImage (glibc linux-x64 / linux-arm64).
set -euo pipefail

PRODUCT="${1:?cli|inspector}"
PAYLOAD_DIR="${2:?payload dir}"
OUT_APPIMAGE="${3:?output .AppImage path}"
VERSION="${4:-0.0.0}"
ARCH_HINT="${5:-x86_64}" # x86_64 | aarch64

PAYLOAD_DIR="$(cd "${PAYLOAD_DIR}" && pwd)"
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

case "${PRODUCT}" in
  cli)
    EXE_NAME="titanium"
    DESKTOP_NAME="Titanium CLI"
    ICON_ID="titanium-cli"
    ;;
  inspector)
    EXE_NAME="TitaniumInspector"
    DESKTOP_NAME="Titanium Inspector"
    ICON_ID="titanium-inspector"
    ;;
  *)
    echo "error: product must be cli or inspector" >&2
    exit 1
    ;;
esac

if [[ ! -f "${PAYLOAD_DIR}/${EXE_NAME}" && ! -f "${PAYLOAD_DIR}/${EXE_NAME}.exe" ]]; then
  echo "error: ${EXE_NAME} missing under ${PAYLOAD_DIR}" >&2
  exit 1
fi

APPDIR="${WORK}/AppDir"
mkdir -p "${APPDIR}/usr/bin" "${APPDIR}/usr/share/applications" "${APPDIR}/usr/share/icons/hicolor/256x256/apps"

# Copy entire payload beside the binary so natives resolve via $ORIGIN.
rsync -a "${PAYLOAD_DIR}/" "${APPDIR}/usr/bin/"
chmod +x "${APPDIR}/usr/bin/${EXE_NAME}" 2>/dev/null || true
# AppImage entry must be AppRun
cat > "${APPDIR}/AppRun" <<EOF
#!/bin/sh
HERE="\$(dirname "\$(readlink -f "\$0")")"
exec "\${HERE}/usr/bin/${EXE_NAME}" "\$@"
EOF
chmod +x "${APPDIR}/AppRun"

# Desktop entry
cat > "${APPDIR}/usr/share/applications/${ICON_ID}.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=${DESKTOP_NAME}
Exec=${EXE_NAME}
Icon=${ICON_ID}
Categories=Development;Network;
Terminal=$([ "${PRODUCT}" = "cli" ] && echo true || echo false)
EOF
cp "${APPDIR}/usr/share/applications/${ICON_ID}.desktop" "${APPDIR}/${ICON_ID}.desktop"

# Icon (best-effort from app.ico)
ICON_SRC=""
for c in "${PAYLOAD_DIR}/app.ico" "${ROOT}/src/Titanium.Inspector/Assets/app.ico"; do
  [[ -f "$c" ]] && ICON_SRC="$c" && break
done
if [[ -n "${ICON_SRC}" ]] && command -v convert >/dev/null 2>&1; then
  convert "${ICON_SRC}" -resize 256x256 "${APPDIR}/usr/share/icons/hicolor/256x256/apps/${ICON_ID}.png" || true
  cp -f "${APPDIR}/usr/share/icons/hicolor/256x256/apps/${ICON_ID}.png" "${APPDIR}/${ICON_ID}.png" 2>/dev/null || true
elif [[ -n "${ICON_SRC}" ]]; then
  # Fallback: leave no png; appimagetool still works
  :
fi

# Fetch appimagetool
TOOL_ARCH="${ARCH_HINT}"
TOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-${TOOL_ARCH}.AppImage"
TOOL="${WORK}/appimagetool.AppImage"
curl -fsSL "${TOOL_URL}" -o "${TOOL}"
chmod +x "${TOOL}"

export ARCH="${ARCH_HINT}"
export VERSION
# Extract tool if FUSE unavailable (common on GHA)
if ! "${TOOL}" --appimage-extract-and-run --version >/dev/null 2>&1; then
  cd "${WORK}"
  "${TOOL}" --appimage-extract >/dev/null
  TOOL="${WORK}/squashfs-root/AppRun"
fi

OUT_DIR="$(cd "$(dirname "${OUT_APPIMAGE}")" && pwd)"
OUT_LEAF="$(basename "${OUT_APPIMAGE}")"
cd "${WORK}"
"${TOOL}" --appimage-extract-and-run "${APPDIR}" "${OUT_DIR}/${OUT_LEAF}" 2>/dev/null \
  || "${TOOL}" "${APPDIR}" "${OUT_DIR}/${OUT_LEAF}"

echo "Built ${OUT_APPIMAGE}"
