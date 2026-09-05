#!/usr/bin/env bash
# Shared desktop icon helpers for Inspector packaging (Linux hicolor + macOS icns).
# Sourced by install/build scripts — not meant to be run standalone.
#
# Prebuilt assets live in tools/packaging/icons/ (committed). Fallbacks:
# payload app.ico / ImageMagick convert / sips+iconutil.

# Capture this file's directory at source time (bash or zsh).
if [[ -n "${BASH_SOURCE[0]:-}" ]]; then
  _DESKTOP_ICONS_PACKAGING_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
elif [[ -n "${ZSH_VERSION:-}" ]]; then
  # zsh: %x is this file when sourced/executed
  # shellcheck disable=SC2296
  _DESKTOP_ICONS_PACKAGING_DIR="$(cd "$(dirname "${(%):-%x}")" && pwd)"
else
  _DESKTOP_ICONS_PACKAGING_DIR="$(cd "$(dirname "$0")" && pwd)"
fi

_desktop_icons_dir() {
  if [[ -d "${_DESKTOP_ICONS_PACKAGING_DIR}/icons" ]]; then
    printf '%s' "${_DESKTOP_ICONS_PACKAGING_DIR}/icons"
  else
    # Release zips ship PNGs/icns next to desktop-icons.sh
    printf '%s' "${_DESKTOP_ICONS_PACKAGING_DIR}"
  fi
}

# Resolve a source .ico from common payload / repo locations.
# Args: optional extra candidate paths…
_desktop_icons_find_ico() {
  local c repo_ico
  repo_ico="$(cd "${_DESKTOP_ICONS_PACKAGING_DIR}/../.." && pwd)/src/Titanium.Inspector/Assets/app.ico"
  for c in "$@" \
    "${PAYLOAD_DIR:-}/app.ico" \
    "${SCRIPT_DIR:-}/app.ico" \
    "${repo_ico}"; do
    if [[ -n "${c}" && -f "${c}" ]]; then
      printf '%s' "${c}"
      return 0
    fi
  done
  return 1
}

# Install freedesktop hicolor icons named <icon_id>.png at standard sizes.
# Args: <hicolor_root> <icon_id> [extra ico candidates…]
# Example hicolor_root: "$STAGE/usr/share/icons/hicolor"
install_hicolor_icons() {
  local hicolor_root="${1:?hicolor root}"
  local icon_id="${2:?icon id}"
  shift 2
  local icons_dir size dest src ico
  icons_dir="$(_desktop_icons_dir)"

  for size in 16 32 48 128 256 512; do
    mkdir -p "${hicolor_root}/${size}x${size}/apps"
    dest="${hicolor_root}/${size}x${size}/apps/${icon_id}.png"
    src="${icons_dir}/${icon_id}-${size}.png"
    if [[ ! -f "${src}" ]]; then
      src="${icons_dir}/${icon_id}.png"
    fi
    if [[ ! -f "${src}" ]]; then
      src="${icons_dir}/titanium-inspector-${size}.png"
    fi
    if [[ ! -f "${src}" ]]; then
      src="${icons_dir}/titanium-inspector.png"
    fi
    if [[ -f "${src}" ]]; then
      cp -f "${src}" "${dest}"
      continue
    fi
    # Last resort: convert from .ico when ImageMagick is available.
    if ico="$(_desktop_icons_find_ico "$@")" && command -v convert >/dev/null 2>&1; then
      convert "${ico}" -thumbnail "${size}x${size}" "${dest}" 2>/dev/null || true
    fi
  done

  # Ensure at least 256x256 exists (desktop entries / AppImage expect it).
  dest="${hicolor_root}/256x256/apps/${icon_id}.png"
  if [[ ! -f "${dest}" ]]; then
    mkdir -p "$(dirname "${dest}")"
    if [[ -f "${icons_dir}/titanium-inspector.png" ]]; then
      cp -f "${icons_dir}/titanium-inspector.png" "${dest}"
    elif ico="$(_desktop_icons_find_ico "$@")" && command -v convert >/dev/null 2>&1; then
      convert "${ico}" -thumbnail 256x256 "${dest}" 2>/dev/null || true
    fi
  fi

  [[ -f "${dest}" ]]
}

# Remove hicolor icons installed for <icon_id>.
# Args: <hicolor_root> <icon_id>
remove_hicolor_icons() {
  local hicolor_root="${1:?hicolor root}"
  local icon_id="${2:?icon id}"
  local size
  for size in 16 32 48 128 256 512; do
    rm -f "${hicolor_root}/${size}x${size}/apps/${icon_id}.png"
  done
  rm -f "${hicolor_root}/256x256/apps/${icon_id}.ico"
}

# Copy AppIcon.icns into Resources. Prefers committed icns; else builds from .ico.
# Args: <resources_dir> [extra ico candidates…]
# Prints the CFBundleIconFile value (without .icns) on success; empty on failure.
install_macos_app_icon() {
  local resources_dir="${1:?Resources dir}"
  shift
  local icons_dir ico iconset tmp_png size double
  icons_dir="$(_desktop_icons_dir)"
  mkdir -p "${resources_dir}"

  if [[ -f "${icons_dir}/AppIcon.icns" ]]; then
    cp -f "${icons_dir}/AppIcon.icns" "${resources_dir}/AppIcon.icns"
    printf 'AppIcon'
    return 0
  fi

  # Payload may already carry a prebuilt icns (release zip).
  if [[ -f "${PAYLOAD_DIR:-}/AppIcon.icns" ]]; then
    cp -f "${PAYLOAD_DIR}/AppIcon.icns" "${resources_dir}/AppIcon.icns"
    printf 'AppIcon'
    return 0
  fi
  if [[ -f "${SCRIPT_DIR:-}/AppIcon.icns" ]]; then
    cp -f "${SCRIPT_DIR}/AppIcon.icns" "${resources_dir}/AppIcon.icns"
    printf 'AppIcon'
    return 0
  fi

  if ! ico="$(_desktop_icons_find_ico "$@")"; then
    return 1
  fi
  if ! command -v sips >/dev/null 2>&1 || ! command -v iconutil >/dev/null 2>&1; then
    return 1
  fi

  iconset="$(mktemp -d)/AppIcon.iconset"
  mkdir -p "${iconset}"
  tmp_png="$(mktemp).png"
  if ! sips -s format png "${ico}" --out "${tmp_png}" >/dev/null 2>&1; then
    rm -rf "$(dirname "${iconset}")" "${tmp_png}" 2>/dev/null || true
    return 1
  fi
  for size in 16 32 128 256 512; do
    sips -z "${size}" "${size}" "${tmp_png}" --out "${iconset}/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "${double}" "${double}" "${tmp_png}" --out "${iconset}/icon_${size}x${size}@2x.png" >/dev/null
  done
  if iconutil -c icns "${iconset}" -o "${resources_dir}/AppIcon.icns" 2>/dev/null; then
    rm -rf "$(dirname "${iconset}")" "${tmp_png}" 2>/dev/null || true
    printf 'AppIcon'
    return 0
  fi
  rm -rf "$(dirname "${iconset}")" "${tmp_png}" 2>/dev/null || true
  return 1
}
