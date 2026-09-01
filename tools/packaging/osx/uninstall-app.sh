#!/usr/bin/env bash
# Remove Titanium Inspector.app installed by install-app.sh.
set -euo pipefail

APP_PATH="${HOME}/Applications/Titanium Inspector.app"

if [[ -d "${APP_PATH}" ]]; then
  echo "Removing ${APP_PATH} ..."
  rm -rf "${APP_PATH}"
  echo "Uninstalled Titanium Inspector."
else
  echo "Nothing to remove (${APP_PATH} not found)."
fi
