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

Push-Location $wixDir
try {
    dotnet tool restore
    $outDir = Split-Path -Parent $OutputMsi
    if ($outDir) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        $OutputMsi = Join-Path (Resolve-Path $outDir) (Split-Path -Leaf $OutputMsi)
    }

    & dotnet wix build $wxs `
        -b "PayloadDir=$PayloadDir" `
        -d "ProductVersion=$Version" `
        -o $OutputMsi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
}
finally { Pop-Location }

if (-not (Test-Path $OutputMsi)) { throw "MSI was not produced: $OutputMsi" }
Write-Host "Built $OutputMsi"
