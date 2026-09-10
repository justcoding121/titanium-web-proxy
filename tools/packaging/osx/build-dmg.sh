#!/usr/bin/env bash
# Create a simple drag-to-Applications DMG from a .app bundle.
set -euo pipefail

APP_PATH="${1:?path to .app}"
DMG_PATH="${2:?output .dmg path}"
VOLUME_NAME="${3:-Titanium Inspector}"

APP_PATH="$(cd "$(dirname "${APP_PATH}")" && pwd)/$(basename "${APP_PATH}")"
STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT

cp -R "${APP_PATH}" "${STAGE}/"
ln -s /Applications "${STAGE}/Applications"

# UDZO compressed read-only image
hdiutil create -volname "${VOLUME_NAME}" -srcfolder "${STAGE}" -ov -format UDZO "${DMG_PATH}"
echo "Built ${DMG_PATH}"
