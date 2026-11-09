# Print line coverage for one or more product source files from a VS coverage XML.
# Used locally while writing behavior tests: uncovered spans tell you which branches to hit next.
param(
    [string]$CoverageXml = 'coverage/coverage.xml',
    [string]$File = '',
    [int]$Top = 0
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CoverageXml)) {
    Write-Error "Coverage XML not found: $CoverageXml"
    exit 1
}

[xml]$doc = Get-Content -LiteralPath $CoverageXml

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

function Merge-Spans([System.Collections.Generic.List[int]]$lines) {
    $spans = New-Object System.Collections.Generic.List[string]
    if ($lines.Count -eq 0) { return ,$spans.ToArray() }
    $sorted = $lines | Sort-Object -Unique
    $start = [int]$sorted[0]
    $prev = $start
    for ($i = 1; $i -lt $sorted.Count; $i++) {
        $n = [int]$sorted[$i]
        if ($n -eq $prev + 1) {
            $prev = $n
            continue
        }
        if ($start -eq $prev) { $spans.Add("$start") } else { $spans.Add("${start}-${prev}") }
        $start = $n
        $prev = $n
    }
    if ($start -eq $prev) { $spans.Add("$start") } else { $spans.Add("${start}-${prev}") }
    return ,$spans.ToArray()
}

function Get-FileStats([string]$path, [object[]]$ranges) {
    $coveredLines = New-Object 'System.Collections.Generic.HashSet[int]'
    $uncoveredLines = New-Object System.Collections.Generic.List[int]
    $known = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($range in $ranges) {
        if (-not $range.start_line -or -not $range.end_line) { continue }
        $startLine = [int]$range.start_line
        $endLine = [int]$range.end_line
        if ($endLine -lt $startLine) { continue }
        $flag = [string]$range.covered
        $hit = $flag -eq 'yes' -or $flag -eq 'true' -or $flag -eq 'partial'
        for ($line = $startLine; $line -le $endLine; $line++) {
            [void]$known.Add($line)
            if ($hit) {
                [void]$coveredLines.Add($line)
            }
        }
    }
    foreach ($line in ($known | Sort-Object)) {
        if (-not $coveredLines.Contains($line)) {
            $uncoveredLines.Add($line)
        }
    }
    $total = $known.Count
    $covered = $coveredLines.Count
    $pct = if ($total -eq 0) { 0.0 } else { [math]::Round(100.0 * $covered / $total, 2) }
    return [pscustomobject]@{
        Path = $path
        Covered = $covered
        Total = $total
        Uncovered = $uncoveredLines.Count
        Percent = $pct
        UncoveredSpans = (Merge-Spans $uncoveredLines)
    }
}

$needle = if ([string]::IsNullOrWhiteSpace($File)) { '' } else { ($File -replace '\\', '/').Trim() }
$stats = New-Object System.Collections.Generic.List[object]

foreach ($entry in $sourcesById.GetEnumerator()) {
    $path = $entry.Value
    if ($needle) {
        if (-not (
                $path.EndsWith($needle, [StringComparison]::OrdinalIgnoreCase) -or
                $path.EndsWith('/' + $needle, [StringComparison]::OrdinalIgnoreCase) -or
                ([System.IO.Path]::GetFileName($path).Equals([System.IO.Path]::GetFileName($needle), [StringComparison]::OrdinalIgnoreCase)))) {
            continue
        }
    }
    elseif ($path -notmatch '(^|/)src/') {
        continue
    }

    if (-not $rangesBySourceId.ContainsKey($entry.Key)) { continue }
    $fileRanges = @($rangesBySourceId[$entry.Key])
    if ($fileRanges.Count -eq 0) { continue }
    $stats.Add((Get-FileStats $path $fileRanges))
}

if ($stats.Count -eq 0) {
    if ($needle) {
        Write-Host "No coverage ranges matched File='$File'."
    }
    else {
        Write-Host "No product source files found under src/ in $CoverageXml."
    }
    exit 0
}

$ordered = if ($Top -gt 0) {
    @($stats | Sort-Object Uncovered -Descending | Select-Object -First $Top)
}
elseif ($needle) {
    @($stats | Sort-Object Path)
}
else {
    @($stats | Sort-Object Uncovered -Descending)
}

foreach ($item in $ordered) {
    Write-Host ("{0}: {1}% ({2}/{3} lines; {4} uncovered)" -f $item.Path, $item.Percent, $item.Covered, $item.Total, $item.Uncovered)
    if ($item.Uncovered -gt 0) {
        $spanText = ($item.UncoveredSpans -join ', ')
        if ($spanText.Length -gt 400) {
            $spanText = $spanText.Substring(0, 400) + '...'
        }
        Write-Host ("  uncovered: {0}" -f $spanText)
    }
}

exit 0
