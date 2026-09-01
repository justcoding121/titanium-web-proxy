# Run portable E2E-UI tests on Linux via Docker (from Windows or any Docker host).
$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Image = if ($env:TWP_INSPECTOR_UI_IMAGE) { $env:TWP_INSPECTOR_UI_IMAGE } else { "twp-inspector-ui" }

docker build -f (Join-Path $Root "tools\InspectorUiDocker\Dockerfile") -t $Image (Join-Path $Root "tools\InspectorUiDocker")
docker run --rm `
  -v "${Root}:/src" `
  -w /src `
  $Image `
  -lc 'dotnet test tests/Titanium.E2E.Tests/Titanium.E2E.Tests.csproj -c Release --filter "TestCategory=E2E-UI" --nologo'
