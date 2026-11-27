#Requires -Version 5.1
<#
.SYNOPSIS
  Append one lifecycle-proof row to a JSON array (create the file if missing).
#>
param(
    [Parameter(Mandatory)]
    [string] $Path,
    [Parameter(Mandatory)]
    [string] $Product,
    [Parameter(Mandatory)]
    [string] $Rid,
    [Parameter(Mandatory)]
    [string] $Format,
    [Parameter(Mandatory)]
    [string] $Phase,
    [Parameter(Mandatory)]
    [string] $FromVersion,
    [Parameter(Mandatory)]
    [string] $ToVersion,
    [Parameter(Mandatory)]
    [string] $InstalledVersionAfter,
    [Parameter(Mandatory)]
    [bool] $StateRestored
)

$ErrorActionPreference = 'Stop'

$row = [ordered]@{
    product                  = $Product
    rid                      = $Rid
    format                   = $Format
    phase                    = $Phase
    from_version             = $FromVersion
    to_version               = $ToVersion
    installed_version_after  = $InstalledVersionAfter
    state_restored           = $StateRestored
}

$rows = @()
if (Test-Path $Path) {
    $raw = Get-Content -Path $Path -Raw
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        $rows = @($raw | ConvertFrom-Json)
    }
}

$rows += [pscustomobject]$row
$parent = Split-Path -Parent $Path
if ($parent -and -not (Test-Path $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
[System.IO.File]::WriteAllText($Path, (($rows | ConvertTo-Json -Depth 5) + "`n"))
Write-Host "appended $Product/$Rid/$Format/$Phase"