#!/usr/bin/env bash
# Fresh Inspector decrypt proof. Tier A browsers come from the caller.
# Usage: prove-inspector.sh <exe> <browsers> <out-dir>
set -euo pipefail
exe="${1:?exe}"
browsers="${2:?browsers}"
out="${3:?out}"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
dotnet build "$root/tools/InspectorInstallSmoke/InspectorInstallSmoke.csproj" -c Release --nologo
driver="$root/tools/InspectorInstallSmoke/bin/Release/net10.0/InspectorInstallSmoke"
chmod +x "$driver" 2>/dev/null || true
mkdir -p "$out"
if [[ "$(uname -s)" == "Linux" ]]; then
  xvfb-run -a dbus-run-session -- "$driver" fresh --exe "$exe" --browsers "$browsers" --out "$out"
else
  "$driver" fresh --exe "$exe" --browsers "$browsers" --out "$out"
fi
