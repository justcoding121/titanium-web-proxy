#Requires -Version 5.1
<#
.SYNOPSIS
  Assert every expected release asset exists in a staged directory.
#>
param(
    [string] $StageDir = '',
    [string] $ExpectedPath = '',
    [ValidateSet('cli', 'inspector', 'plus', 'charts', 'all')]
    [string] $Product = 'all',
    [string] $Rid = '',
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$ciRoot = $PSScriptRoot
if (-not $ExpectedPath) { $ExpectedPath = Join-Path $ciRoot 'expected-release-assets.json' }

function Get-ExpectedAssetLists {
    param(
        $Doc,
        [string] $Product,
        [string] $Rid
    )

    $files = @($Doc.files)
    $globs = @($Doc.globs)

    if ($Product -eq 'all' -and [string]::IsNullOrWhiteSpace($Rid)) {
        return $files, $globs
    }

    if ($Product -eq 'plus') {
        $globs = @($globs | Where-Object { $_ -like 'Titanium.Plus-*' })
        return @(), $globs
    }

    if ($Product -eq 'charts') {
        # Chart archives only (titanium-1.0.N.tgz). Do not match Titanium.Plus-*
        # — PowerShell -like is case-insensitive.
        $globs = @($globs | Where-Object { $_ -like 'titanium-*.tgz' -or $_ -like 'titanium-gateway-*.tgz' -or $_ -like 'titanium-manager-*.tgz' })
        return @('SHA256SUMS'), $globs
    }

    $prefix = switch ($Product) {
        'cli' { 'Titanium.Cli-' }
        'inspector' { 'TitaniumInspector-' }
        default { '' }
    }

    if ($prefix) {
        $files = @(
            $files | Where-Object {
                $_ -eq 'SHA256SUMS' -or (
                    $_ -like "$prefix*" -and (
                        [string]::IsNullOrWhiteSpace($Rid) -or $_ -like "*$Rid*"
                    )
                )
            }
        )
        $globs = @()
    }

    return $files, $globs
}

function Test-ReleaseAssets {
    param(
        [string] $Dir,
        [string] $Expected,
        [string] $Product,
        [string] $Rid
    )
    if (-not (Test-Path $Dir)) { throw "missing stage dir: $Dir" }
    if (-not (Test-Path $Expected)) { throw "missing expected file: $Expected" }

    $doc = Get-Content -Path $Expected -Raw | ConvertFrom-Json
    $files, $globs = Get-ExpectedAssetLists -Doc $doc -Product $Product -Rid $Rid

    $names = @(Get-ChildItem -Path $Dir -File | ForEach-Object { $_.Name })
    $missing = [System.Collections.Generic.List[string]]::new()

    foreach ($file in $files) {
        if ($names -notcontains $file) { [void]$missing.Add($file) }
    }
    foreach ($glob in $globs) {
        $pattern = if ($glob -like '*.*') { $glob } else { $glob }
        $hit = @(Get-ChildItem -Path $Dir -File | Where-Object { $_.Name -like $pattern })
        if ($hit.Count -lt 1) { [void]$missing.Add($glob) }
    }

    if ($missing.Count -gt 0) {
        Write-Host 'missing release assets:'
        foreach ($m in $missing) { Write-Host "  $m" }
        $scope = if ($Product -ne 'all' -or $Rid) { " product=$Product rid=$Rid" } else { '' }
        Write-Host "checked in $Dir$scope"
        return $false
    }
    $total = $files.Count + $globs.Count
    $scope = if ($Product -ne 'all' -or $Rid) { " ($Product$(if ($Rid) { "/$Rid" }))" } else { '' }
    Write-Host "release assets ok ($total checks$scope in $Dir)"
    return $true
}

if ($SelfTest) {
    $tmpdir = Join-Path ([System.IO.Path]::GetTempPath()) ("twp-rel-assets-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmpdir -Force | Out-Null
    try {
        $incomplete = Join-Path $tmpdir 'incomplete'
        $complete = Join-Path $tmpdir 'complete'
        $cliOnly = Join-Path $tmpdir 'cli-win-x64'
        New-Item -ItemType Directory -Path $incomplete, $complete, $cliOnly -Force | Out-Null

        $doc = Get-Content -Path $ExpectedPath -Raw | ConvertFrom-Json
        foreach ($file in $doc.files) {
            New-Item -ItemType File -Path (Join-Path $complete $file) -Force | Out-Null
        }
        foreach ($name in @(
            'Titanium.Plus-8.0.8.dll', 'Titanium.Plus-8.0.8.zip',
            'titanium-1.0.8.tgz', 'titanium-gateway-1.0.8.tgz', 'titanium-manager-1.0.8.tgz'
        )) {
            New-Item -ItemType File -Path (Join-Path $complete $name) -Force | Out-Null
        }

        New-Item -ItemType File -Path (Join-Path $cliOnly 'Titanium.Cli-win-x64.zip') -Force | Out-Null

        # Incomplete: only SHA256SUMS
        New-Item -ItemType File -Path (Join-Path $incomplete 'SHA256SUMS') -Force | Out-Null

        if (Test-ReleaseAssets -Dir $incomplete -Expected $ExpectedPath -Product all -Rid '') {
            throw 'self-test: incomplete stage should fail'
        }
        if (-not (Test-ReleaseAssets -Dir $complete -Expected $ExpectedPath -Product all -Rid '')) {
            throw 'self-test: complete stage should fail'
        }
        if (Test-ReleaseAssets -Dir $cliOnly -Expected $ExpectedPath -Product cli -Rid 'win-x64') {
            throw 'self-test: cli-only missing SHA256 should fail'
        }
        New-Item -ItemType File -Path (Join-Path $cliOnly 'SHA256SUMS') -Force | Out-Null
        if (-not (Test-ReleaseAssets -Dir $cliOnly -Expected $ExpectedPath -Product cli -Rid 'win-x64')) {
            throw 'self-test: cli win-x64 slice should pass'
        }

        $chartsOnly = Join-Path $tmpdir 'charts-only'
        New-Item -ItemType Directory -Path $chartsOnly -Force | Out-Null
        foreach ($name in @('titanium-1.0.9.tgz', 'titanium-gateway-1.0.9.tgz', 'titanium-manager-1.0.9.tgz', 'SHA256SUMS')) {
            New-Item -ItemType File -Path (Join-Path $chartsOnly $name) -Force | Out-Null
        }
        # Plus files must not be required for -Product charts (case-insensitive titanium* trap).
        New-Item -ItemType File -Path (Join-Path $chartsOnly 'Titanium.Plus-9.9.9.zip') -Force | Out-Null
        if (-not (Test-ReleaseAssets -Dir $chartsOnly -Expected $ExpectedPath -Product charts -Rid '')) {
            throw 'self-test: charts-only stage should pass without requiring Plus assets'
        }

        Write-Host 'assert-release-assets self-test ok'
        exit 0
    }
    finally {
        Remove-Item -Path $tmpdir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $StageDir) {
    throw 'Pass -StageDir or -SelfTest'
}

if ($Product -ne 'all' -and [string]::IsNullOrWhiteSpace($Rid) -and $Product -in @('cli', 'inspector')) {
    throw "Product $Product requires -Rid for per-matrix checks"
}

if (Test-ReleaseAssets -Dir $StageDir -Expected $ExpectedPath -Product $Product -Rid $Rid) {
    exit 0
}
exit 1