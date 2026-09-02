#!/usr/bin/env bash
# Wrap a self-contained publish directory as an AppImage (glibc linux-x64 / linux-arm64).
# Runs on x86_64 GHA runners for both target arches: download host appimagetool, set ARCH=target.
set -euo pipefail

PRODUCT="${1:?cli|inspector}"
PAYLOAD_DIR="${2:?payload dir}"
OUT_APPIMAGE="${3:?output .AppImage path}"
VERSION="${4:-0.0.0}"
ARCH_HINT="${5:-x86_64}" # target arch: x86_64 | aarch64

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

cat > "${APPDIR}/AppRun" <<EOF
#!/bin/sh
HERE="\$(dirname "\$(readlink -f "\$0")")"
exec "\${HERE}/usr/bin/${EXE_NAME}" "\$@"
EOF
chmod +x "${APPDIR}/AppRun"

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

ICON_SRC=""
for c in "${PAYLOAD_DIR}/app.ico" "${ROOT}/src/Titanium.Inspector/Assets/app.ico"; do
  [[ -f "$c" ]] && ICON_SRC="$c" && break
done
if [[ -n "${ICON_SRC}" ]] && command -v convert >/dev/null 2>&1; then
  convert "${ICON_SRC}" -resize 256x256 "${APPDIR}/usr/share/icons/hicolor/256x256/apps/${ICON_ID}.png" || true
  if [[ -f "${APPDIR}/usr/share/icons/hicolor/256x256/apps/${ICON_ID}.png" ]]; then
    cp -f "${APPDIR}/usr/share/icons/hicolor/256x256/apps/${ICON_ID}.png" "${APPDIR}/${ICON_ID}.png"
    cp -f "${APPDIR}/${ICON_ID}.png" "${APPDIR}/.DirIcon"
  fi
fi

# Host arch for the tool binary (GHA ubuntu-latest is always x86_64, even for linux-arm64 RID publishes)
HOST_ARCH="$(uname -m)"
case "${HOST_ARCH}" in
  x86_64|amd64) TOOL_ARCH=x86_64 ;;
  aarch64|arm64) TOOL_ARCH=aarch64 ;;
  *) TOOL_ARCH=x86_64 ;;
esac

TOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-${TOOL_ARCH}.AppImage"
TOOL_IMG="${WORK}/appimagetool.AppImage"
echo "Fetching appimagetool (${TOOL_ARCH} host) for target ARCH=${ARCH_HINT}"
curl -fsSL "${TOOL_URL}" -o "${TOOL_IMG}"
chmod +x "${TOOL_IMG}"

# Always extract — FUSE is unavailable on most GHA runners
cd "${WORK}"
export APPIMAGE_EXTRACT_AND_RUN=1
if ! "${TOOL_IMG}" --appimage-extract >/dev/null 2>&1; then
  # Fallback: some builds need the flag as argv
  "${TOOL_IMG}" --appimage-extract-and-run --appimage-extract >/dev/null || true
fi
if [[ -x "${WORK}/squashfs-root/AppRun" ]]; then
  TOOL="${WORK}/squashfs-root/AppRun"
elif [[ -x "${WORK}/squashfs-root/usr/bin/appimagetool" ]]; then
  TOOL="${WORK}/squashfs-root/usr/bin/appimagetool"
else
  echo "error: failed to extract appimagetool" >&2
  ls -la "${WORK}" >&2 || true
  exit 1
fi

export ARCH="${ARCH_HINT}"
export VERSION
OUT_DIR="$(cd "$(dirname "${OUT_APPIMAGE}")" && pwd)"
OUT_LEAF="$(basename "${OUT_APPIMAGE}")"
OUT_ABS="${OUT_DIR}/${OUT_LEAF}"

cd "${WORK}"
# Do not swallow stderr — failures must be visible in CI
"${TOOL}" "${APPDIR}" "${OUT_ABS}"

test -f "${OUT_ABS}"
chmod +x "${OUT_ABS}"
echo "Built ${OUT_APPIMAGE} ($(du -h "${OUT_ABS}" | awk '{print $1}'))"
