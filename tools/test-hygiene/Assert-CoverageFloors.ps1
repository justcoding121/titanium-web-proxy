# Fail when an assembly's line coverage in a VS coverage XML drops more than the allowance
# below tests/coverage-floors.json, unless that file is part of the diff.
param(
    [string]$CoverageXml = 'coverage/coverage.xml',
    [string]$Floors = 'tests/coverage-floors.json'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CoverageXml)) {
    Write-Error "No coverage file at $CoverageXml"
    exit 1
}

[xml]$doc = Get-Content -LiteralPath $CoverageXml
$floorsDoc = Get-Content -LiteralPath $Floors -Raw | ConvertFrom-Json
$allow = [double]$floorsDoc.dropAllowance
$failed = $false
foreach ($prop in $floorsDoc.assemblies.PSObject.Properties) {
    $name = $prop.Name
    $floor = [double]$prop.Value
    $modules = @($doc.SelectNodes("//module") | Where-Object { $_.name -like "*$name*" -or $_.moduleName -like "*$name*" })
    if ($modules.Count -eq 0) { continue }
    $covered = 0
    $total = 0
    foreach ($module in $modules) {
        foreach ($range in @($module.SelectNodes('.//range'))) {
            $lines = 1
            if ($range.start_line -and $range.end_line) {
                $lines = [int]$range.end_line - [int]$range.start_line + 1
                if ($lines -lt 1) { $lines = 1 }
            }
            $total += $lines
            $flag = [string]$range.covered
            if ($flag -eq 'yes' -or $flag -eq 'true' -or $flag -eq 'partial') { $covered += $lines }
        }
    }
    if ($total -eq 0) { continue }
    $pct = [math]::Round(100.0 * $covered / $total, 2)
    $limit = [math]::Round($floor - $allow, 2)
    Write-Host ("{0}: {1}% (floor {2})" -f $name, $pct, $floor)
    if ($pct + 0.001 -lt $limit) {
        Write-Host "::error::$name line coverage $pct is below $limit"
        $failed = $true
    }
}
if ($failed) { exit 1 }
exit 0
