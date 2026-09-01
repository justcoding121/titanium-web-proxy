<#
.SYNOPSIS
  Builds TitaniumInspector-win-x64.msi from a published self-contained folder using WiX 5.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadDir,
    [Parameter(Mandatory = $true)]
    [string] $OutputMsi,
    [string] $Version = "7.0.0"
)

$ErrorActionPreference = "Stop"
$wixDir = Join-Path $PSScriptRoot "wix"
$wxs = Join-Path $wixDir "TitaniumInspector.wxs"
$PayloadDir = (Resolve-Path $PayloadDir).Path
if (-not (Test-Path (Join-Path $PayloadDir "TitaniumInspector.exe"))) {
    throw "TitaniumInspector.exe missing under $PayloadDir"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$iconSrc = Join-Path $repoRoot "src\Titanium.Inspector\Assets\app.ico"
if (-not (Test-Path $iconSrc)) { throw "Application icon missing: $iconSrc" }
Copy-Item -Force $iconSrc (Join-Path $wixDir "app.ico")

# Always absolute: `dotnet wix -o` is relative to the WiX cwd, while Test-Path after
# Pop-Location is relative to the caller's cwd (repo root on CI).
$msiLeaf = Split-Path -Leaf $OutputMsi
$outDir = Split-Path -Parent $OutputMsi
if ([string]::IsNullOrWhiteSpace($outDir)) {
    $outDir = (Get-Location).Path
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$OutputMsi = Join-Path (Resolve-Path $outDir) $msiLeaf

Push-Location $wixDir
try {
    dotnet tool restore

    # UI + Util extensions (InstallDir wizard, Launch on exit).
    & dotnet wix extension add "WixToolset.UI.wixext/5.0.2"
    & dotnet wix extension add "WixToolset.Util.wixext/5.0.2"

    & dotnet wix build $wxs `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        -b "PayloadDir=$PayloadDir" `
        -d "ProductVersion=$Version" `
        -o $OutputMsi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
}
finally { Pop-Location }

if (-not (Test-Path $OutputMsi)) { throw "MSI was not produced: $OutputMsi" }
Write-Host "Built $OutputMsi"
