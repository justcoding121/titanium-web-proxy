#!/usr/bin/env bash
# Build Titanium Inspector.app from a published self-contained folder (CI).
set -euo pipefail

PAYLOAD_DIR="${1:?payload dir}"
APP_PATH="${2:?output .app path}"
VERSION="${3:-7.0.0}"
EXE_NAME="TitaniumInspector"

PAYLOAD_DIR="$(cd "$PAYLOAD_DIR" && pwd)"
if [[ ! -f "${PAYLOAD_DIR}/${EXE_NAME}" ]]; then
  echo "error: ${EXE_NAME} missing under ${PAYLOAD_DIR}" >&2
  exit 1
fi

rm -rf "${APP_PATH}"
mkdir -p "${APP_PATH}/Contents/MacOS" "${APP_PATH}/Contents/Resources"

# Copy publish output into MacOS (exclude helper scripts).
rsync -a --exclude 'install-app.sh' --exclude 'uninstall-app.sh' \
  "${PAYLOAD_DIR}/" "${APP_PATH}/Contents/MacOS/" 2>/dev/null \
  || {
    cp -R "${PAYLOAD_DIR}/." "${APP_PATH}/Contents/MacOS/"
    rm -f "${APP_PATH}/Contents/MacOS/install-app.sh" "${APP_PATH}/Contents/MacOS/uninstall-app.sh"
  }

chmod +x "${APP_PATH}/Contents/MacOS/${EXE_NAME}"

# shellcheck source=../desktop-icons.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/desktop-icons.sh"
ICON_KEY="$(PAYLOAD_DIR="${PAYLOAD_DIR}" install_macos_app_icon \
  "${APP_PATH}/Contents/Resources" "${PAYLOAD_DIR}/app.ico" || true)"

cat > "${APP_PATH}/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleExecutable</key>
	<string>${EXE_NAME}</string>
	<key>CFBundleIdentifier</key>
	<string>io.github.justcoding121.TitaniumInspector</string>
	<key>CFBundleName</key>
	<string>Titanium Inspector</string>
	<key>CFBundleDisplayName</key>
	<string>Titanium Inspector</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>${VERSION}</string>
	<key>CFBundleVersion</key>
	<string>${VERSION}</string>
	<key>LSMinimumSystemVersion</key>
	<string>12.0</string>
	<key>NSHighResolutionCapable</key>
	<true/>
$([ -n "${ICON_KEY}" ] && printf '\t<key>CFBundleIconFile</key>\n\t<string>%s</string>\n' "${ICON_KEY}")
</dict>
</plist>
EOF

echo "Built ${APP_PATH}"
