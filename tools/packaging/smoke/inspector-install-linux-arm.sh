#!/usr/bin/env bash
# Blocking install smoke for ubuntu-24.04-arm. The full-browser linux leg stays
# on x64; this leg uses Firefox and Chrome only. Covers fresh, upgrade (when a
# previous arm64 deb exists), uninstall, update-under-decrypt, and in-app-apply.
set -euo pipefail
chmod +x tools/packaging/smoke/*.sh 2>/dev/null || true
sudo apt-get update
# zenity is a Depends of the Inspector .deb (open-downloaded-package); install it
# before dpkg -i so upgrades do not leave the package unconfigured until apt -f.
sudo apt-get install -y xvfb xdotool libnss3-tools curl ca-certificates wget gnupg rsync imagemagick libnuma1 dbus libgtk-3-bin zenity
export BROWSERS=required
bash tools/packaging/smoke/install-required-browsers.sh
ver=$(grep -oP '(?<=<VersionPrefix>)[^<]+' src/Titanium.Inspector/Titanium.Inspector.csproj)
out=artifacts/inspector/linux-arm64
dotnet publish src/Titanium.Inspector/Titanium.Inspector.csproj -c Release -r linux-arm64 --self-contained true -o "$out" -p:Version="$ver"
clear_out="${RUNNER_TEMP}/titanium-inspector-clear-linux-arm64"
mkdir -p "$clear_out"
dotnet publish src/Titanium.Inspector.ClearProxy/Titanium.Inspector.ClearProxy.csproj \
  -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true \
  -o "$clear_out" -p:Version="$ver"
cp -f "$clear_out/titanium-inspector-clear" "$out/titanium-inspector-clear"
chmod +x "$out/titanium-inspector-clear"
cp -f src/Titanium.Inspector/Assets/app.ico tools/packaging/linux/TitaniumInspector.desktop.in tools/packaging/desktop-icons.sh "$out/"
cp -f tools/packaging/icons/titanium-inspector.png tools/packaging/icons/titanium-inspector-*.png "$out/" || true
chmod +x tools/packaging/linux/build-deb-rpm.sh
./tools/packaging/linux/build-deb-rpm.sh inspector "$out" artifacts/candidate "$ver" linux-arm64
cand="$(ls artifacts/candidate/TitaniumInspector-linux-arm64.deb)"
cand_ver="$(dpkg-deb -f "$cand" Version)"
export LIFECYCLE_PROOF="${LIFECYCLE_PROOF:-${RUNNER_TEMP}/lifecycle-proof-install-smoke-arm.json}"

# Prefer a previous published arm64 deb when available for upgrade coverage.
mkdir -p artifacts/prev
prev=""
if [[ -n "${GH_TOKEN:-}" && -n "${GITHUB_REPOSITORY:-}" ]]; then
  tag="$(gh api "repos/${GITHUB_REPOSITORY}/releases" --jq '[.[] | select(.draft==false and .prerelease==false)][0].tag_name' || true)"
  if [[ -n "$tag" && "$tag" != "null" ]]; then
    gh release download "$tag" --pattern 'TitaniumInspector-linux-arm64.deb' --dir artifacts/prev --clobber || true
    prev="$(ls artifacts/prev/TitaniumInspector-linux-arm64.deb 2>/dev/null || true)"
  fi
fi

log="${RUNNER_TEMP}/install-smoke"
mkdir -p "$log"
dotnet build tools/InspectorInstallSmoke/InspectorInstallSmoke.csproj -c Release
driver="$PWD/tools/InspectorInstallSmoke/bin/Release/net10.0/InspectorInstallSmoke"
chmod +x "$driver"
run_driver() {
  set +e
  xvfb-run -a dbus-run-session -- "$driver" "$@" 2>&1 | tee -a "$log/driver.log"
  local code=${PIPESTATUS[0]}
  set -e
  echo "driver-exit ${code} $*" | tee -a "$log/driver.log"
  return "${code}"
}

if [[ -n "$prev" ]]; then
  prev_ver="$(dpkg-deb -f "$prev" Version)"
  sudo dpkg -i "$prev" || sudo apt-get install -y -f
  run_driver upgrade-seed --exe /opt/titanium-inspector/TitaniumInspector --browsers firefox --out "$log"
  sudo dpkg -i "$cand" || sudo apt-get install -y -f
  test "$(dpkg-query -W -f '${Version}' titanium-inspector)" = "$cand_ver"
  run_driver upgrade-verify --exe /opt/titanium-inspector/TitaniumInspector --browsers chrome,firefox --out "$log"
  test "$(dpkg-query -W -f '${Version}' titanium-inspector)" = "$cand_ver"
  # Leave proxy armed so dpkg prerm must clear it (do not pre-clear here).
  pkill -TERM -f '^/opt/titanium-inspector/TitaniumInspector( |$)' >/dev/null 2>&1 || true
  sleep 1
  sudo dpkg -r titanium-inspector || true
  if command -v gsettings >/dev/null; then
    mode="$(dbus-run-session -- gsettings get org.gnome.system.proxy mode 2>/dev/null || true)"
    host="$(dbus-run-session -- gsettings get org.gnome.system.proxy.http host 2>/dev/null || true)"
    mode="${mode//\'/}"
    host="${host//\'/}"
    if [[ -n "${mode}" && "${mode}" != "none" ]]; then
      echo "proxy mode still ${mode} (host=${host}) after arm64 uninstall" >&2
      exit 1
    fi
  fi
  bash tools/packaging/smoke/append-lifecycle-proof.sh "$LIFECYCLE_PROOF" inspector linux-arm64 deb upgrade \
    "$prev_ver" "$cand_ver" "$cand_ver" false
  bash tools/packaging/smoke/append-lifecycle-proof.sh "$LIFECYCLE_PROOF" inspector linux-arm64 deb uninstall \
    "$prev_ver" "$cand_ver" "" true
  echo "pass linux-arm64 upgrade+uninstall"
fi

sudo dpkg -i "$cand" || sudo apt-get install -y -f
test -x /opt/titanium-inspector/TitaniumInspector
run_driver fresh --exe /opt/titanium-inspector/TitaniumInspector --browsers chrome,firefox --out "$log"
if run_driver update-under-decrypt --exe /opt/titanium-inspector/TitaniumInspector --out "$log"; then
  echo "update-under-decrypt=pass" | tee -a "$log/advisory.txt"
else
  echo "update-under-decrypt=fail" | tee -a "$log/advisory.txt"
  exit 1
fi
if [[ -f "$cand" ]]; then
  if run_driver in-app-apply --exe /opt/titanium-inspector/TitaniumInspector --package "$cand" --out "$log"; then
    echo "in-app-apply=pass" | tee -a "$log/advisory.txt"
  else
    echo "in-app-apply=fail" | tee -a "$log/advisory.txt"
    exit 1
  fi
else
  echo "in-app-apply=skip" | tee -a "$log/advisory.txt"
fi
dotnet publish src/Titanium.Cli/Titanium.Cli.csproj -c Release -r linux-arm64 --self-contained true -o artifacts/cli/linux-arm64
./tools/packaging/linux/build-deb-rpm.sh cli artifacts/cli/linux-arm64 artifacts/candidate "$ver" linux-arm64
sudo dpkg -i artifacts/candidate/Titanium.Cli-linux-arm64.deb || sudo apt-get install -y -f
"$driver" cli --titanium /usr/bin/titanium --out "$log/cli"
sudo dpkg -r titanium-cli || true
if [[ -x /opt/titanium-cli/titanium ]]; then
  echo "ASSERT: CLI left after dpkg -r on arm64" >&2
  exit 1
fi
echo "### install-smoke (ubuntu-24.04-arm)" >> "$GITHUB_STEP_SUMMARY"
echo "fresh, cli, uninstall passed" >> "$GITHUB_STEP_SUMMARY"
