#!/usr/bin/env bash
# Install a Titanium Inspector AppImage into the same user-local layout as install.sh.
# Usage: ./install-appimage.sh /path/to/TitaniumInspector-*.AppImage
set -euo pipefail

APPIMAGE="${1:-}"
if [[ -z "${APPIMAGE}" || ! -f "${APPIMAGE}" ]]; then
  echo "usage: $0 /path/to/TitaniumInspector-*.AppImage" >&2
  exit 1
fi

APPIMAGE="$(cd "$(dirname "${APPIMAGE}")" && pwd)/$(basename "${APPIMAGE}")"
chmod +x "${APPIMAGE}"

PREFIX="${PREFIX:-${HOME}/.local}"
APP_DIR="${PREFIX}/share/TitaniumInspector"
BIN_DIR="${PREFIX}/bin"
APPS_DIR="${PREFIX}/share/applications"
MNT="$(mktemp -d "${TMPDIR:-/tmp}/ti-appimage-XXXXXX")"
cleanup() {
  if [[ -d "${MNT}" ]]; then
    "${APPIMAGE}" --appimage-extract >/dev/null 2>&1 || true
    # Prefer fuse unmount when the image was mounted; fall back to rm for extract dirs.
    fusermount -u "${MNT}" 2>/dev/null || umount "${MNT}" 2>/dev/null || rm -rf "${MNT}"
  fi
}
trap cleanup EXIT

# Extract (portable; works without FUSE).
EXTRACT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ti-appimage-extract-XXXXXX")"
(cd "${EXTRACT_DIR}" && "${APPIMAGE}" --appimage-extract >/dev/null)
SRC="${EXTRACT_DIR}/squashfs-root"
if [[ ! -d "${SRC}/usr/bin" && -x "${SRC}/AppRun" ]]; then
  :
fi

# Prefer usr/bin payload when present (appimagetool layout).
PAYLOAD="${SRC}"
if [[ -x "${SRC}/usr/bin/TitaniumInspector" ]]; then
  PAYLOAD="${SRC}/usr/bin"
elif [[ -x "${SRC}/TitaniumInspector" ]]; then
  PAYLOAD="${SRC}"
fi

mkdir -p "${APP_DIR}" "${BIN_DIR}" "${APPS_DIR}"
rm -rf "${APP_DIR}"
mkdir -p "${APP_DIR}"

if [[ -x "${SRC}/usr/bin/TitaniumInspector" ]]; then
  # Full AppDir tree under share for assets next to the binary helpers.
  cp -a "${SRC}/usr/." "${APP_DIR}/" 2>/dev/null || true
  if [[ ! -x "${APP_DIR}/bin/TitaniumInspector" && -x "${SRC}/usr/bin/TitaniumInspector" ]]; then
    mkdir -p "${APP_DIR}"
    cp -a "${SRC}/usr/bin/." "${APP_DIR}/"
  fi
  # Normalize: binary at APP_DIR/TitaniumInspector
  if [[ -x "${APP_DIR}/bin/TitaniumInspector" && ! -x "${APP_DIR}/TitaniumInspector" ]]; then
    cp -a "${APP_DIR}/bin/." "${APP_DIR}/"
  fi
else
  cp -a "${PAYLOAD}/." "${APP_DIR}/"
fi

chmod +x "${APP_DIR}/TitaniumInspector" 2>/dev/null || true

cat > "${BIN_DIR}/titanium-inspector" <<EOF
#!/usr/bin/env bash
exec "${APP_DIR}/TitaniumInspector" "\$@"
EOF
chmod +x "${BIN_DIR}/titanium-inspector"

cat > "${APPS_DIR}/titanium-inspector.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Titanium Inspector
Exec="${APP_DIR}/TitaniumInspector"
Terminal=false
Categories=Network;Development;
EOF

rm -rf "${EXTRACT_DIR}"
trap - EXIT

echo "Installed AppImage payload to ${APP_DIR}"
echo "Launcher: ${BIN_DIR}/titanium-inspector"
