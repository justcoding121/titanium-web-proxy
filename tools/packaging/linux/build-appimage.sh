#!/usr/bin/env bash
# Wrap a self-contained publish directory as an AppImage (glibc linux-x64 / linux-arm64).
# Runs on x86_64 GHA runners for both target arches: download host appimagetool, set ARCH=target.
set -euo pipefail

PRODUCT="${1:?cli|inspector}"
PAYLOAD_DIR="${2:?payload dir}"
OUT_APPIMAGE="${3:?output .AppImage path}"
VERSION="${4:-0.0.0}"
ARCH_HINT="${5:-x86_64}" # target arch: x86_64 | aarch64

# Resolve output path BEFORE any cd into temp dirs (relative paths must stay in caller CWD).
CALLER_PWD="$(pwd)"
if [[ "${OUT_APPIMAGE}" != /* ]]; then
  OUT_ABS="${CALLER_PWD}/${OUT_APPIMAGE}"
else
  OUT_ABS="${OUT_APPIMAGE}"
fi
OUT_DIR="$(dirname "${OUT_ABS}")"
OUT_LEAF="$(basename "${OUT_ABS}")"
mkdir -p "${OUT_DIR}"

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

rsync -a "${PAYLOAD_DIR}/" "${APPDIR}/usr/bin/"
chmod +x "${APPDIR}/usr/bin/${EXE_NAME}" 2>/dev/null || true

cat > "${APPDIR}/AppRun" <<EOF
#!/bin/sh
HERE="\$(dirname "\$(readlink -f "\$0")")"
exec "\${HERE}/usr/bin/${EXE_NAME}" "\$@"
EOF
chmod +x "${APPDIR}/AppRun"

# One main category only (appimagetool warns on multiple)
cat > "${APPDIR}/usr/share/applications/${ICON_ID}.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=${DESKTOP_NAME}
Exec=${EXE_NAME}
Icon=${ICON_ID}
Categories=Development;
Terminal=$([ "${PRODUCT}" = "cli" ] && echo true || echo false)
EOF
cp "${APPDIR}/usr/share/applications/${ICON_ID}.desktop" "${APPDIR}/${ICON_ID}.desktop"

ICON_PNG="${APPDIR}/usr/share/icons/hicolor/256x256/apps/${ICON_ID}.png"
# shellcheck source=../desktop-icons.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/desktop-icons.sh"
install_hicolor_icons "${APPDIR}/usr/share/icons/hicolor" "${ICON_ID}" \
  "${PAYLOAD_DIR}/app.ico" "${ROOT}/src/Titanium.Inspector/Assets/app.ico" || true
# appimagetool requires the icon named in the desktop file to exist
if [[ ! -f "${ICON_PNG}" ]]; then
  if command -v convert >/dev/null 2>&1; then
    convert -size 256x256 xc:'#2563eb' -fill white -gravity center \
      -pointsize 48 -annotate 0 'T' "${ICON_PNG}"
  else
    # Minimal valid 1x1 PNG
    printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x02\x00\x00\x00\x90wS\xde\x00\x00\x00\x0cIDATx\x9cc\xf8\x0f\x00\x00\x01\x01\x00\x05\x18\xd8N\x00\x00\x00\x00IEND\xaeB`\x82' \
      > "${ICON_PNG}"
  fi
fi
cp -f "${ICON_PNG}" "${APPDIR}/${ICON_ID}.png"
cp -f "${ICON_PNG}" "${APPDIR}/.DirIcon"

HOST_ARCH="$(uname -m)"
case "${HOST_ARCH}" in
  x86_64|amd64) TOOL_ARCH=x86_64 ;;
  aarch64|arm64) TOOL_ARCH=aarch64 ;;
  *) TOOL_ARCH=x86_64 ;;
esac

TOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-${TOOL_ARCH}.AppImage"
TOOL_IMG="${WORK}/appimagetool.AppImage"
echo "Fetching appimagetool (${TOOL_ARCH} host) for target ARCH=${ARCH_HINT}"
echo "Output: ${OUT_ABS}"
curl -fsSL "${TOOL_URL}" -o "${TOOL_IMG}"
chmod +x "${TOOL_IMG}"

cd "${WORK}"
export APPIMAGE_EXTRACT_AND_RUN=1
"${TOOL_IMG}" --appimage-extract >/dev/null
if [[ -x "${WORK}/squashfs-root/AppRun" ]]; then
  TOOL="${WORK}/squashfs-root/AppRun"
elif [[ -x "${WORK}/squashfs-root/usr/bin/appimagetool" ]]; then
  TOOL="${WORK}/squashfs-root/usr/bin/appimagetool"
else
  echo "error: failed to extract appimagetool" >&2
  find "${WORK}/squashfs-root" -maxdepth 3 -type f 2>/dev/null | head >&2 || true
  exit 1
fi

export ARCH="${ARCH_HINT}"
export VERSION
cd "${WORK}"
"${TOOL}" "${APPDIR}" "${OUT_ABS}"

test -f "${OUT_ABS}"
chmod +x "${OUT_ABS}"
echo "Built ${OUT_ABS} ($(du -h "${OUT_ABS}" | awk '{print $1}'))"
