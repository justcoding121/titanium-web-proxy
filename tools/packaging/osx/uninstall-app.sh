#!/usr/bin/env bash
# Remove Titanium Inspector.app from ~/Applications and /Applications.
set -euo pipefail

APP_PATH="${HOME}/Applications/Titanium Inspector.app"
SYSTEM_APP="/Applications/Titanium Inspector.app"
# install-app.sh writes the first id. The DMG (build-app-bundle.sh) writes the second.
BUNDLE_IDS="com.justcoding121.titaniuminspector io.github.justcoding121.TitaniumInspector"
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

bundle_id_is_ours() {
  local id="$1"
  local known
  for known in ${BUNDLE_IDS}; do
    [[ "${id}" == "${known}" ]] && return 0
  done
  return 1
}

is_titanium_inspector_app() {
  local app="$1"
  [[ -d "${app}" ]] || return 1
  [[ -x "${app}/Contents/MacOS/TitaniumInspector" ]] || return 1
  local plist="${app}/Contents/Info.plist"
  [[ -f "${plist}" ]] || return 0
  local id=""
  if [[ -x /usr/libexec/PlistBuddy ]]; then
    id="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${plist}" 2>/dev/null || true)"
  fi
  if [[ -n "${id}" ]] && ! bundle_id_is_ours "${id}"; then
    echo "Skipping ${app} (bundle id ${id})." >&2
    return 1
  fi
  return 0
}

remove_tree() {
  local app="$1"
  if [[ -w "$(dirname "${app}")" ]]; then
    rm -rf "${app}" && return 0
  fi
  if sudo -n true >/dev/null 2>&1; then
    sudo -n rm -rf "${app}" && return 0
  fi
  echo "Administrator permission is required to remove ${app}." >&2
  sudo rm -rf "${app}"
}

REMOVED=0
remove_app() {
  local app="$1"
  [[ -d "${app}" ]] || return 0
  is_titanium_inspector_app "${app}" || return 0
  echo "Removing ${app} ..."
  remove_tree "${app}"
  if [[ -d "${app}" ]]; then
    echo "Could not remove ${app}." >&2
    return 1
  fi
  REMOVED=1
}

if pgrep -x TitaniumInspector >/dev/null 2>&1; then
  osascript -e 'tell application "Titanium Inspector" to quit' >/dev/null 2>&1 || true
  for _ in 1 2 3 4 5 6 7 8; do
    pgrep -x TitaniumInspector >/dev/null 2>&1 || break
    sleep 0.5
  done
  if pgrep -x TitaniumInspector >/dev/null 2>&1; then
    killall TitaniumInspector >/dev/null 2>&1 || true
    sleep 1
  fi
fi

clear_loopback_proxies
remove_app "${APP_PATH}"
remove_app "${SYSTEM_APP}"

if [[ "${REMOVED}" -eq 0 ]]; then
  echo "Nothing to remove."
else
  echo "Uninstalled Titanium Inspector."
fi
