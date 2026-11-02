# Fail a pull request when changed lines in a measured file are under 80% covered.
# A qa-waiver label skips the gate. A push that is not a pull request exits 0.
param(
    [string]$CoverageXml = 'coverage/coverage.xml',
    [string]$BaseRef = '',
    [double]$Minimum = 80
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($env:GITHUB_EVENT_NAME -and $env:GITHUB_EVENT_NAME -ne 'pull_request') {
    Write-Host "Diff coverage runs on pull requests."
    exit 0
}
if ($env:QA_WAIVER -eq 'true') {
    Write-Host "qa-waiver label is set. Diff coverage is not a failure."
    exit 0
}
if (-not (Test-Path -LiteralPath $CoverageXml)) {
    Write-Host "No coverage file. Diff coverage has nothing to measure."
    exit 0
}
if ([string]::IsNullOrWhiteSpace($BaseRef)) {
    Write-Host "No base ref. Diff coverage has nothing to compare."
    exit 0
}

$diff = git diff --unified=0 "origin/$BaseRef...HEAD" -- '*.cs'
if ($LASTEXITCODE -ne 0) {
    Write-Host "git diff could not see origin/$BaseRef. Fetch the base branch before this script."
    exit 0
}

$changed = @{}
$file = ''
foreach ($line in $diff) {
    if ($line.StartsWith('+++ b/')) {
        $file = $line.Substring(6)
        continue
    }
    if ($line.StartsWith('@@')) {
        $match = [regex]::Match($line, '\+(\d+)')
        if ($match.Success -and $file) {
            $start = [int]$match.Groups[1].Value
            if (-not $changed.ContainsKey($file)) { $changed[$file] = New-Object System.Collections.Generic.List[int] }
            $changed[$file].Add($start)
        }
    }
}

if ($changed.Count -eq 0) {
    Write-Host "No changed C# lines."
    exit 0
}

[xml]$doc = Get-Content -LiteralPath $CoverageXml
$covered = 0
$measured = 0
foreach ($path in @($changed.Keys)) {
    $name = [IO.Path]::GetFileName($path)
    $ranges = @($doc.SelectNodes('//range'))
    if ($ranges.Count -eq 0) {
        Write-Host "Coverage XML has no ranges to map onto $name."
        continue
    }
    foreach ($lineNo in $changed[$path]) {
        $hit = $false
        $known = $false
        foreach ($range in $ranges) {
            if (-not $range.start_line -or -not $range.end_line) { continue }
            $startLine = [int]$range.start_line
            $endLine = [int]$range.end_line
            if ($lineNo -ge $startLine -and $lineNo -le $endLine) {
                $known = $true
                $flag = [string]$range.covered
                if ($flag -eq 'yes' -or $flag -eq 'true' -or $flag -eq 'partial') { $hit = $true }
                break
            }
        }
        if ($known) {
            $measured++
            if ($hit) { $covered++ }
        }
    }
}

if ($measured -eq 0) {
    Write-Host "Changed lines were not present in the coverage ranges, so diff coverage is not applied."
    exit 0
}

$pct = [math]::Round(100.0 * $covered / $measured, 2)
Write-Host "Diff coverage $pct% ($covered/$measured mapped lines)."
if ($pct + 0.001 -lt $Minimum) {
    Write-Host "::error::Changed-line coverage $pct% is below $Minimum%."
    exit 1
}
exit 0
