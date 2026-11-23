#!/usr/bin/env bash
# Install a CLI package. Prints the titanium binary path.
# Usage: install-cli.sh <package> <dest-dir>
set -euo pipefail
pkg="${1:?package}"
dest="${2:?dest}"
mkdir -p "$dest"
base="$(basename "$pkg")"
case "$base" in
  *.zip)
    rm -rf "$dest"
    mkdir -p "$dest"
    unzip -q "$pkg" -d "$dest"
    if [[ -f "$dest/titanium.exe" ]]; then
      echo "$dest/titanium.exe"
    else
      chmod +x "$dest/titanium"
      echo "$dest/titanium"
    fi
    ;;
  *.deb)
    sudo dpkg -i "$pkg" >&2 || sudo apt-get install -y -f >&2
    test -x /opt/titanium-cli/titanium
    echo /opt/titanium-cli/titanium
    ;;
  *.rpm)
    if command -v dnf >/dev/null 2>&1; then
      sudo dnf install -y "$pkg" >&2
    else
      sudo rpm -Uvh "$pkg" >&2
    fi
    test -x /opt/titanium-cli/titanium
    echo /opt/titanium-cli/titanium
    ;;
  *.AppImage)
    chmod +x "$pkg"
    export APPIMAGE_EXTRACT_AND_RUN=1
    work="$dest/extract"
    rm -rf "$work"
    mkdir -p "$work"
    (cd "$work" && "$pkg" --appimage-extract >/dev/null)
    bin="$work/squashfs-root/titanium"
    test -x "$bin" || chmod +x "$bin"
    "$bin" version
    echo "$bin"
    ;;
  *)
    echo "ASSERT: unsupported cli package $base" >&2
    exit 1
    ;;
esac
