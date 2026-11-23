#!/usr/bin/env bash
# Install one Inspector package and print the installed executable path.
# Usage: install-inspector.sh <package> <log-dir>
set -euo pipefail
pkg="${1:?package}"
log="${2:?log dir}"
mkdir -p "$log"
base="$(basename "$pkg")"

verify_checksum() {
  local dir file sums
  dir="$(cd "$(dirname "$pkg")" && pwd)"
  file="$(basename "$pkg")"
  sums="$dir/SHA256SUMS"
  if [[ ! -f "$sums" ]]; then
    echo "no SHA256SUMS beside $file; checksum skipped"
    return 0
  fi
  if command -v sha256sum >/dev/null 2>&1; then
    (cd "$dir" && sha256sum -c "$sums" --ignore-missing) || {
      echo "ASSERT: checksum mismatch for $file" >&2
      exit 1
    }
  else
    (cd "$dir" && shasum -a 256 -c "$sums") || {
      echo "ASSERT: checksum mismatch for $file" >&2
      exit 1
    }
  fi
}

verify_checksum

case "$base" in
  *.deb)
    sudo dpkg -i "$pkg" >&2 || sudo apt-get install -y -f >&2
    test -x /opt/titanium-inspector/TitaniumInspector
    echo /opt/titanium-inspector/TitaniumInspector
    ;;
  *.rpm)
    if command -v dnf >/dev/null 2>&1; then
      sudo dnf install -y "$pkg" >&2
    else
      sudo rpm -Uvh "$pkg" >&2
    fi
    test -x /opt/titanium-inspector/TitaniumInspector
    echo /opt/titanium-inspector/TitaniumInspector
    ;;
  *.dmg)
    # Quarantine matches a browser download so Gatekeeper runs the same checks a user gets.
    xattr -w com.apple.quarantine "0083;$(date +%s);Safari;$(uuidgen)" "$pkg" || true
    # Volume names contain spaces ("Titanium Inspector"). Field 3 stops at the space.
    mount="$(hdiutil attach "$pkg" -nobrowse | sed -n 's/^.*\(\/Volumes\/.*\)$/\1/p' | tail -1)"
    if [[ -z "${mount:-}" ]]; then
      echo "ASSERT: hdiutil did not mount $pkg" >&2
      exit 1
    fi
    app="$(find "$mount" -name 'Titanium Inspector.app' -maxdepth 2 | head -1)"
    ditto "$app" "/Applications/Titanium Inspector.app"
    hdiutil detach "$mount" || true
    exe="/Applications/Titanium Inspector.app/Contents/MacOS/TitaniumInspector"
    test -x "$exe"
    spctl --assess --type execute "/Applications/Titanium Inspector.app" || {
      echo "ASSERT: spctl rejected the Inspector app" >&2
      exit 1
    }
    xcrun stapler validate "/Applications/Titanium Inspector.app" || xcrun stapler validate "$pkg" || {
      echo "ASSERT: stapler did not validate the notarized app" >&2
      exit 1
    }
    echo "$exe"
    ;;
  *.AppImage)
    chmod +x "$pkg"
    export APPIMAGE_EXTRACT_AND_RUN=1
    work="$log/appimage"
    rm -rf "$work"
    mkdir -p "$work"
    (cd "$work" && "$pkg" --appimage-extract >/dev/null)
    exe="$(find "$work/squashfs-root" -type f -name TitaniumInspector | head -1)"
    test -n "$exe"
    chmod +x "$exe"
    echo "$exe"
    ;;
  *.zip)
    dest="$log/musl"
    rm -rf "$dest"
    mkdir -p "$dest"
    unzip -q "$pkg" -d "$dest"
    exe="$(find "$dest" -type f -name TitaniumInspector | head -1)"
    test -n "$exe"
    chmod +x "$exe"
    echo "$exe"
    ;;
  *)
    echo "ASSERT: unsupported inspector package $base" >&2
    exit 1
    ;;
esac
