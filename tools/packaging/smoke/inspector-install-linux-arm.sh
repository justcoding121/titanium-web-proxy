#!/usr/bin/env bash
# Non-blocking install smoke for ubuntu-24.04-arm. The required linux leg stays
# on x64 with its full browser set. This leg uses Firefox and Chrome only.
set -euo pipefail
sudo apt-get update
sudo apt-get install -y xvfb xdotool libnss3-tools curl ca-certificates wget gnupg rsync imagemagick libnuma1
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
sudo dpkg -i "$cand" || sudo apt-get install -y -f
test -x /opt/titanium-inspector/TitaniumInspector
log="${RUNNER_TEMP}/install-smoke"
mkdir -p "$log"
dotnet build tools/InspectorInstallSmoke/InspectorInstallSmoke.csproj -c Release
driver="$PWD/tools/InspectorInstallSmoke/bin/Release/net10.0/InspectorInstallSmoke"
chmod +x "$driver"
xvfb-run -a dbus-run-session -- "$driver" fresh --exe /opt/titanium-inspector/TitaniumInspector --browsers chrome,firefox --out "$log"
dotnet publish src/Titanium.Cli/Titanium.Cli.csproj -c Release -r linux-arm64 --self-contained true -o artifacts/cli/linux-arm64
./tools/packaging/linux/build-deb-rpm.sh cli artifacts/cli/linux-arm64 artifacts/candidate "$ver" linux-arm64
sudo dpkg -i artifacts/candidate/Titanium.Cli-linux-arm64.deb || sudo apt-get install -y -f
"$driver" cli --titanium /usr/bin/titanium --out "$log/cli"
echo "### install-smoke (ubuntu-24.04-arm)" >> "$GITHUB_STEP_SUMMARY"
echo "fresh and cli passed" >> "$GITHUB_STEP_SUMMARY"
