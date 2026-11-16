#!/usr/bin/env bash
# Create a drag-to-Applications DMG with Finder icon layout from a .app bundle.
set -euo pipefail

APP_PATH="${1:?path to .app}"
DMG_PATH="${2:?output .dmg path}"
VOLUME_NAME="${3:-Titanium Inspector}"

APP_PATH="$(cd "$(dirname "${APP_PATH}")" && pwd)/$(basename "${APP_PATH}")"
if [[ ! -d "${APP_PATH}" ]]; then
  echo "error: .app not found: ${APP_PATH}" >&2
  exit 1
fi

# Finder-facing name (space) regardless of CI staging name (TitaniumInspector.app).
STAGE_APP_NAME="Titanium Inspector.app"
STAGE="$(mktemp -d)"
RW_DMG="$(mktemp -u /tmp/titanium-inspector-dmg-XXXX).dmg"
MOUNT_ROOT=""
cleanup() {
  if [[ -n "${MOUNT_ROOT}" && -d "${MOUNT_ROOT}" ]]; then
    hdiutil detach "${MOUNT_ROOT}" -quiet 2>/dev/null || \
      hdiutil detach "${MOUNT_ROOT}" -force -quiet 2>/dev/null || true
  fi
  rm -rf "${STAGE}"
  rm -f "${RW_DMG}"
}
trap cleanup EXIT

mkdir -p "${STAGE}"
cp -R "${APP_PATH}" "${STAGE}/${STAGE_APP_NAME}"
ln -s /Applications "${STAGE}/Applications"

# RW image so Finder can persist .DS_Store / icon positions, then compress to UDZO.
hdiutil create -volname "${VOLUME_NAME}" -srcfolder "${STAGE}" -ov -format UDRW "${RW_DMG}"

ATTACH_OUT="$(hdiutil attach -readwrite -noverify -noautoopen "${RW_DMG}")"
MOUNT_ROOT="$(echo "${ATTACH_OUT}" | awk -F'\t' '/\/Volumes\//{print $NF; exit}')"
if [[ -z "${MOUNT_ROOT}" || ! -d "${MOUNT_ROOT}" ]]; then
  MOUNT_ROOT="$(echo "${ATTACH_OUT}" | grep -o '/Volumes/[^[:space:]]*' | tail -1)"
fi
if [[ -z "${MOUNT_ROOT}" || ! -d "${MOUNT_ROOT}" ]]; then
  echo "error: could not mount RW DMG" >&2
  exit 1
fi

# Icon layout: app on the left, Applications shortcut on the right (classic installer DMG).
# Best-effort — CI/headless agents still get a valid Applications-symlink DMG.
# Set TITANIUM_DMG_SKIP_LAYOUT=1 to skip Finder scripting (faster / no GUI).
if [[ "${TITANIUM_DMG_SKIP_LAYOUT:-}" != "1" ]]; then
  # Cap Finder scripting so a stuck GUI cannot hang the release job.
  perl -e 'alarm shift; exec @ARGV' 45 osascript <<EOF || true
tell application "Finder"
  tell disk "${VOLUME_NAME}"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 840, 480}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 96
    set position of item "${STAGE_APP_NAME}" of container window to {160, 180}
    set position of item "Applications" of container window to {480, 180}
    update without registering applications
    delay 0.3
    close
  end tell
end tell
EOF
  sync
  sleep 1
fi

# The first detach can unmount and still return non-zero. The follow-up then
# fails with "No such file or directory" and, under set -e, fails the package.
if ! hdiutil detach "${MOUNT_ROOT}" -quiet; then
  hdiutil detach "${MOUNT_ROOT}" -force -quiet || true
fi
if hdiutil info | grep -F "${MOUNT_ROOT}" >/dev/null; then
  echo "error: could not detach ${MOUNT_ROOT}" >&2
  exit 1
fi
MOUNT_ROOT=""

rm -f "${DMG_PATH}"
hdiutil convert "${RW_DMG}" -format UDZO -imagekey zlib-level=9 -o "${DMG_PATH}"
echo "Built ${DMG_PATH}"
