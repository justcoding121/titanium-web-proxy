#!/usr/bin/env bash
# Remove Titanium Inspector.app installed by install-app.sh.
set -euo pipefail

APP_PATH="${HOME}/Applications/Titanium Inspector.app"
SYSTEM_APP="/Applications/Titanium Inspector.app"
EXE=""
if [[ -x "${APP_PATH}/Contents/MacOS/TitaniumInspector" ]]; then
  EXE="${APP_PATH}/Contents/MacOS/TitaniumInspector"
elif [[ -x "${SYSTEM_APP}/Contents/MacOS/TitaniumInspector" ]]; then
  EXE="${SYSTEM_APP}/Contents/MacOS/TitaniumInspector"
fi

clear_loopback_proxies() {
  if [[ -n "${EXE}" ]]; then
    "${EXE}" --clear-system-proxy || true
  fi
  if ! command -v networksetup >/dev/null 2>&1; then
    return 0
  fi
  networksetup -listallnetworkservices 2>/dev/null | tail -n +2 | while IFS= read -r service; do
    service="${service#\*}"
    service="${service#"${service%%[![:space:]]*}"}"
    [[ -z "${service}" ]] && continue
    for pair in "-getwebproxy -setwebproxystate" "-getsecurewebproxy -setsecurewebproxystate" "-getsocksfirewallproxy -setsocksfirewallproxystate"; do
      get_flag="${pair%% *}"
      set_flag="${pair##* }"
      info="$(networksetup "${get_flag}" "${service}" 2>/dev/null || true)"
      printf '%s\n' "${info}" | grep -q 'Enabled: Yes' || continue
      printf '%s\n' "${info}" | grep -Eq 'Server: (127\.0\.0\.1|localhost|::1)$' || continue
      networksetup "${set_flag}" "${service}" off || true
    done
  done
}

clear_loopback_proxies

if [[ -d "${APP_PATH}" ]]; then
  echo "Removing ${APP_PATH} ..."
  rm -rf "${APP_PATH}"
  echo "Uninstalled Titanium Inspector."
else
  echo "Nothing to remove (${APP_PATH} not found)."
fi
