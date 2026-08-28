# Run PR2 spot matrix inside the twp-rps-linux Docker image (repo mounted at /src).
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $repoRoot
try {
    & dotnet build src/Titanium.Web.Proxy.sln -c Release --warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
    & pwsh tools/RpsLoadProbe/run-spot-matrix.ps1 @args
}
finally {
    Pop-Location
}
