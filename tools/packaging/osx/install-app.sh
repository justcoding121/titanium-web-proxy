#!/usr/bin/env bash
# Build a minimal Titanium Inspector.app under ~/Applications from this zip.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE_NAME="TitaniumInspector"
APP_ROOT="${HOME}/Applications"
APP_NAME="Titanium Inspector.app"
APP_PATH="${APP_ROOT}/${APP_NAME}"

if [[ ! -f "${SCRIPT_DIR}/${EXE_NAME}" ]]; then
  echo "error: ${EXE_NAME} not found next to install-app.sh (extract the full zip first)" >&2
  exit 1
fi

mkdir -p "${APP_ROOT}"
rm -rf "${APP_PATH}"
mkdir -p "${APP_PATH}/Contents/MacOS" "${APP_PATH}/Contents/Resources"

echo "Installing ${APP_PATH} ..."

# Copy self-contained publish output into MacOS (exclude helper scripts from cluttering MacOS root is optional;
# include everything so natives and deps stay beside the binary).
rsync -a --exclude 'install-app.sh' --exclude 'uninstall-app.sh' \
  "${SCRIPT_DIR}/" "${APP_PATH}/Contents/MacOS/" 2>/dev/null \
  || { cp -R "${SCRIPT_DIR}/." "${APP_PATH}/Contents/MacOS/"; rm -f "${APP_PATH}/Contents/MacOS/install-app.sh" "${APP_PATH}/Contents/MacOS/uninstall-app.sh"; }

chmod +x "${APP_PATH}/Contents/MacOS/${EXE_NAME}"

ICON_KEY=""
if [[ -f "${SCRIPT_DIR}/desktop-icons.sh" ]]; then
  # shellcheck source=../desktop-icons.sh
  source "${SCRIPT_DIR}/desktop-icons.sh"
  ICON_KEY="$(PAYLOAD_DIR="${SCRIPT_DIR}" SCRIPT_DIR="${SCRIPT_DIR}" \
    install_macos_app_icon "${APP_PATH}/Contents/Resources" \
    "${SCRIPT_DIR}/app.ico" || true)"
elif [[ -f "${SCRIPT_DIR}/AppIcon.icns" ]]; then
  cp -f "${SCRIPT_DIR}/AppIcon.icns" "${APP_PATH}/Contents/Resources/AppIcon.icns"
  ICON_KEY="AppIcon"
elif [[ -f "${SCRIPT_DIR}/app.ico" ]] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
  ICONSET="$(mktemp -d)/AppIcon.iconset"
  mkdir -p "${ICONSET}"
  sips -s format png "${SCRIPT_DIR}/app.ico" --out /tmp/titanium-inspector-icon.png >/dev/null 2>&1 || true
  if [[ -f /tmp/titanium-inspector-icon.png ]]; then
    for size in 16 32 128 256 512; do
      sips -z "${size}" "${size}" /tmp/titanium-inspector-icon.png --out "${ICONSET}/icon_${size}x${size}.png" >/dev/null
      double=$((size * 2))
      sips -z "${double}" "${double}" /tmp/titanium-inspector-icon.png --out "${ICONSET}/icon_${size}x${size}@2x.png" >/dev/null
    done
    if iconutil -c icns "${ICONSET}" -o "${APP_PATH}/Contents/Resources/AppIcon.icns" 2>/dev/null; then
      ICON_KEY="AppIcon"
    fi
  fi
  rm -rf "$(dirname "${ICONSET}")"
fi

cat > "${APP_PATH}/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>Titanium Inspector</string>
  <key>CFBundleExecutable</key>
  <string>${EXE_NAME}</string>
  <key>CFBundleIdentifier</key>
  <string>com.justcoding121.titaniuminspector</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Titanium Inspector</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>7.0.2</string>
  <key>CFBundleVersion</key>
  <string>7.0.2</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
$(if [[ -n "${ICON_KEY}" ]]; then printf '  <key>CFBundleIconFile</key>\n  <string>%s</string>\n' "${ICON_KEY}"; fi)
</dict>
</plist>
EOF

# Clear quarantine on the copied tree so Gatekeeper is less surprising for unsigned builds.
if command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "${APP_PATH}" 2>/dev/null || true
fi

echo "Installed ${APP_PATH}"
echo "Launch from Launchpad/Spotlight, or: open \"${APP_PATH}\""
echo "Uninstall with: \"${SCRIPT_DIR}/uninstall-app.sh\""
