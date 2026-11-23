#!/usr/bin/env bash
# MITM proof: decryptSsl on, access log contains a decrypted GET of example.com.
# Usage: cli-mitm.sh <titanium-bin> <out-dir>
set -euo pipefail
bin="${1:?titanium}"
out="${2:?out}"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
dotnet build "$root/tools/InspectorInstallSmoke/InspectorInstallSmoke.csproj" -c Release --nologo
driver="$root/tools/InspectorInstallSmoke/bin/Release/net10.0/InspectorInstallSmoke"
if [[ -f "$driver.exe" ]]; then
  driver="$driver.exe"
fi
chmod +x "$driver" 2>/dev/null || true
"$driver" cli --titanium "$bin" --out "$out"
