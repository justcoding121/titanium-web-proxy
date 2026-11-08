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
$nextLine = 0
$inHunk = $false
foreach ($line in $diff) {
    if ($line.StartsWith('+++ b/')) {
        $file = ($line.Substring(6) -replace '\\', '/')
        $inHunk = $false
        continue
    }
    if ($line.StartsWith('@@')) {
        # Score every added line in the hunk so method signatures alone cannot sink the gate.
        $match = [regex]::Match($line, '\+(\d+)(?:,(\d+))?')
        if ($match.Success -and $file) {
            $nextLine = [int]$match.Groups[1].Value
            $inHunk = $true
        }
        continue
    }
    if (-not $inHunk -or -not $file) { continue }
    if ($line.StartsWith('+') -and -not $line.StartsWith('+++')) {
        if (-not $changed.ContainsKey($file)) {
            $changed[$file] = New-Object System.Collections.Generic.List[int]
        }
        $changed[$file].Add($nextLine)
        $nextLine++
        continue
    }
    if ($line.StartsWith('-') -and -not $line.StartsWith('---')) {
        continue
    }
    # context line
    $nextLine++
}

if ($changed.Count -eq 0) {
    Write-Host "No changed C# lines."
    exit 0
}

[xml]$doc = Get-Content -LiteralPath $CoverageXml

# VS coverage XML: //source_file[@id]/path and //range[@source_id].
$sourcesById = @{}
foreach ($source in @($doc.SelectNodes('//source_file'))) {
    $id = [string]$source.id
    $path = ([string]$source.path) -replace '\\', '/'
    if ($id -and $path) {
        $sourcesById[$id] = $path
    }
}

$rangesBySourceId = @{}
foreach ($range in @($doc.SelectNodes('//range'))) {
    $sourceId = [string]$range.source_id
    if (-not $sourceId) { continue }
    if (-not $rangesBySourceId.ContainsKey($sourceId)) {
        $rangesBySourceId[$sourceId] = New-Object System.Collections.Generic.List[object]
    }
    $rangesBySourceId[$sourceId].Add($range)
}

function Find-SourceIds([string]$repoPath) {
    $ids = New-Object System.Collections.Generic.List[string]
    $normalized = $repoPath.TrimStart('./')
    foreach ($entry in $sourcesById.GetEnumerator()) {
        $coveragePath = $entry.Value
        if ($coveragePath.EndsWith($normalized, [StringComparison]::OrdinalIgnoreCase) -or
            $coveragePath.EndsWith('/' + $normalized, [StringComparison]::OrdinalIgnoreCase)) {
            $ids.Add($entry.Key)
        }
    }
    return ,$ids.ToArray()
}

$covered = 0
$measured = 0
$misses = New-Object System.Collections.Generic.List[string]
foreach ($path in @($changed.Keys | Sort-Object)) {
    # Diff coverage is a product-code gate; test and tool fixtures are not scored.
    if ($path -match '(^|/)(tests|tools|examples)/') {
        continue
    }

    $sourceIds = Find-SourceIds $path
    if ($sourceIds.Count -eq 0) {
        Write-Host "Coverage XML has no source entry for $path."
        continue
    }

    $fileRanges = New-Object System.Collections.Generic.List[object]
    foreach ($sourceId in $sourceIds) {
        if ($rangesBySourceId.ContainsKey($sourceId)) {
            foreach ($range in $rangesBySourceId[$sourceId]) {
                $fileRanges.Add($range)
            }
        }
    }
    if ($fileRanges.Count -eq 0) {
        Write-Host "Coverage XML has no ranges to map onto $path."
        continue
    }

    foreach ($lineNo in $changed[$path]) {
        $hit = $false
        $known = $false
        foreach ($range in $fileRanges) {
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
            if ($hit) {
                $covered++
            }
            else {
                $misses.Add("${path}:${lineNo}")
            }
        }
    }
}

if ($measured -eq 0) {
    Write-Host "Changed lines were not present in the coverage ranges, so diff coverage is not applied."
    exit 0
}

$pct = [math]::Round(100.0 * $covered / $measured, 2)
Write-Host "Diff coverage $pct% ($covered/$measured mapped lines)."
if ($misses.Count -gt 0 -and $misses.Count -le 40) {
    Write-Host "Uncovered changed lines:"
    foreach ($miss in $misses) { Write-Host "  $miss" }
}
elseif ($misses.Count -gt 40) {
    Write-Host "Uncovered changed lines ($($misses.Count); first 40):"
    foreach ($miss in ($misses | Select-Object -First 40)) { Write-Host "  $miss" }
}
if ($pct + 0.001 -lt $Minimum) {
    Write-Host "::error::Changed-line coverage $pct% is below $Minimum%."
    exit 1
}
exit 0
