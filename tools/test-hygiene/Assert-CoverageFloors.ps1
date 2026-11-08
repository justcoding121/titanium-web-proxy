# Fail when an assembly's line coverage in a VS coverage XML drops more than the allowance
# below tests/coverage-floors.json, unless that file is part of the diff.
#
# Counts lines from //range under each matched //module, skipping source files that match
# tests/coverage-exclusions.txt (same paths as sonar.coverage.exclusions). That keeps the
# floor aligned with Sonar line coverage instead of the raw module aggregate.
param(
    [string]$CoverageXml = 'coverage/coverage.xml',
    [string]$Floors = 'tests/coverage-floors.json',
    [string]$Exclusions = 'tests/coverage-exclusions.txt'
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

# Longest name first so Titanium.Web.Proxy does not match Titanium.Web.Proxy.Scripting.
$assemblies = @($floorsDoc.assemblies.PSObject.Properties) | Sort-Object { $_.Name.Length } -Descending

# Product AssemblyName overrides (csproj) — coverage XML uses the DLL file name.
$aliases = @{
    'Titanium.Cli' = @('titanium.dll')
    'Titanium.Gateway' = @('titanium-gateway.dll')
    'Titanium.Manager' = @('titanium-manager.dll')
    'Titanium.Inspector' = @('TitaniumInspector.dll')
}

$exclusionGlobs = @()
if (Test-Path -LiteralPath $Exclusions) {
    $exclusionGlobs = @(
        Get-Content -LiteralPath $Exclusions |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') }
    )
}

function Test-ExcludedPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    $normalized = ($path -replace '\\', '/').Trim()
    foreach ($glob in $exclusionGlobs) {
        $pattern = ($glob -replace '\\', '/').Trim()
        if ([string]::IsNullOrWhiteSpace($pattern)) { continue }
        # Prefer a path-suffix match for **/file.cs and a contains match for **/dir/**
        if ($pattern.EndsWith('/**')) {
            $prefix = $pattern.Substring(0, $pattern.Length - 3).TrimStart('*').TrimStart('/')
            if ($prefix -and $normalized.IndexOf('/' + $prefix + '/', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
            if ($prefix -and $normalized.StartsWith($prefix + '/', [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
            continue
        }

        $suffix = $pattern.TrimStart('*').TrimStart('/')
        if ($suffix -and (
                $normalized.EndsWith('/' + $suffix, [StringComparison]::OrdinalIgnoreCase) -or
                $normalized.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase))) {
            return $true
        }
    }
    return $false
}

$sourcesById = @{}
foreach ($source in @($doc.SelectNodes('//source_file'))) {
    $id = [string]$source.id
    $path = [string]$source.path
    if ($id -and $path) {
        $sourcesById[$id] = $path
    }
}

$modules = @($doc.SelectNodes('//module'))
$claimed = New-Object 'System.Collections.Generic.HashSet[string]'

foreach ($prop in $assemblies) {
    $name = $prop.Name
    $floor = [double]$prop.Value
    $candidates = @("$name.dll")
    if ($aliases.ContainsKey($name)) { $candidates += $aliases[$name] }
    $matched = @()
    foreach ($module in $modules) {
        $moduleName = [string]$module.name
        if ([string]::IsNullOrWhiteSpace($moduleName)) { continue }
        if ($claimed.Contains($moduleName)) { continue }
        $hit = $false
        foreach ($dll in $candidates) {
            if ($moduleName -eq $dll -or $moduleName -like $dll) { $hit = $true; break }
        }
        if ($hit) {
            $matched += $module
            [void]$claimed.Add($moduleName)
        }
    }

    if ($matched.Count -eq 0) {
        Write-Host ("{0}: (not in report)" -f $name)
        continue
    }

    $covered = 0.0
    $total = 0.0
    foreach ($module in $matched) {
        foreach ($range in @($module.SelectNodes('.//range'))) {
            $sourceId = [string]$range.source_id
            if ($sourceId -and $sourcesById.ContainsKey($sourceId) -and (Test-ExcludedPath $sourcesById[$sourceId])) {
                continue
            }

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

    if ($total -eq 0) {
        # Fall back to module aggregates when ranges are absent.
        foreach ($module in $matched) {
            if ($module.lines_covered -and $module.lines_not_covered) {
                $linesCovered = [double]$module.lines_covered
                $linesPartial = 0.0
                if ($module.lines_partially_covered) { $linesPartial = [double]$module.lines_partially_covered }
                $linesNot = [double]$module.lines_not_covered
                $covered += $linesCovered + (0.5 * $linesPartial)
                $total += $linesCovered + $linesPartial + $linesNot
            }
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
