#!/usr/bin/env bash
# Run portable E2E-UI tests on Linux inside Docker (local developer path).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IMAGE="${TWP_INSPECTOR_UI_IMAGE:-twp-inspector-ui}"

docker build -f "$ROOT/tools/InspectorUiDocker/Dockerfile" -t "$IMAGE" "$ROOT/tools/InspectorUiDocker"

docker run --rm \
  -v "$ROOT:/src" \
  -w /src \
  "$IMAGE" \
  -lc 'dotnet test tests/Titanium.E2E.Tests/Titanium.E2E.Tests.csproj -c Release --filter "TestCategory=E2E-UI" --nologo'
