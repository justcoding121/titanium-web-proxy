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

ICON_SRC=""
for candidate in "${PAYLOAD_DIR}/app.ico" "${APP_PATH}/Contents/MacOS/app.ico"; do
  if [[ -f "${candidate}" ]]; then
    ICON_SRC="${candidate}"
    break
  fi
done

ICON_FILE=""
if [[ -n "${ICON_SRC}" ]] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
  ICONSET="$(mktemp -d)/AppIcon.iconset"
  mkdir -p "${ICONSET}"
  TMP_PNG="$(mktemp).png"
  if sips -s format png "${ICON_SRC}" --out "${TMP_PNG}" >/dev/null 2>&1; then
    for size in 16 32 128 256 512; do
      sips -z "${size}" "${size}" "${TMP_PNG}" --out "${ICONSET}/icon_${size}x${size}.png" >/dev/null
      double=$((size * 2))
      sips -z "${double}" "${double}" "${TMP_PNG}" --out "${ICONSET}/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "${ICONSET}" -o "${APP_PATH}/Contents/Resources/AppIcon.icns"
    ICON_FILE="AppIcon.icns"
  fi
  rm -rf "$(dirname "${ICONSET}")" "${TMP_PNG}" 2>/dev/null || true
fi

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
$([ -n "${ICON_FILE}" ] && printf '\t<key>CFBundleIconFile</key>\n\t<string>%s</string>\n' "${ICON_FILE}")
</dict>
</plist>
EOF

echo "Built ${APP_PATH}"
