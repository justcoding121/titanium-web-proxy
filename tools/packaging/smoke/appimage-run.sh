#!/usr/bin/env bash
# Extract an AppImage and run `titanium version` (CLI) or confirm the Inspector
# binary is executable (GUI). APPIMAGE_EXTRACT_AND_RUN is set for a direct run too.
# Usage: appimage-run.sh <image> <work-dir>
set -euo pipefail
img="${1:?appimage}"
work="${2:?work}"
img="$(cd "$(dirname "$img")" && pwd)/$(basename "$img")"
chmod +x "$img"
export APPIMAGE_EXTRACT_AND_RUN=1
rm -rf "$work"
mkdir -p "$work"
(cd "$work" && "$img" --appimage-extract >/dev/null)
root="$work/squashfs-root"
if [[ -f "$root/titanium" ]]; then
  chmod +x "$root/titanium"
  "$root/titanium" version
elif [[ -f "$root/TitaniumInspector" ]]; then
  chmod +x "$root/TitaniumInspector"
  # Headless start: the GUI must stay up long enough to be a real process, then we stop it.
  if command -v xvfb-run >/dev/null 2>&1; then
    xvfb-run -a "$root/TitaniumInspector" >/tmp/inspector-appimage.log 2>&1 &
  else
    "$root/TitaniumInspector" >/tmp/inspector-appimage.log 2>&1 &
  fi
  pid=$!
  sleep 3
  if ! kill -0 "$pid" 2>/dev/null; then
    echo "ASSERT: Inspector AppImage exited during startup" >&2
    cat /tmp/inspector-appimage.log >&2 || true
    exit 1
  fi
  kill "$pid" 2>/dev/null || true
  echo "inspector appimage started"
else
  echo "ASSERT: AppImage has neither titanium nor TitaniumInspector" >&2
  exit 1
fi
