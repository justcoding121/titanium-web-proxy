# Validate 7.0 compare-matrix (or compare-cross-version) CSV against committed 6.0 baselines.
# Gate: current_rps / baseline_rps >= RpsGate AND current_rss / baseline_rss <= RssGate per common arm.
param(
    [Parameter(Mandatory)] [string] $BaselineCsv,
    [Parameter(Mandatory)] [string] $CurrentCsv,
    [double] $RpsGate = 0.95,
    [double] $RssGate = 1.10,
    [int] $Concurrency = 64
)

$ErrorActionPreference = 'Stop'

function Get-ArmMetrics([string]$Path, [int]$C) {
    $rows = Import-Csv $Path
    $map = @{}
    foreach ($row in $rows) {
        if ([int]$row.concurrency -ne $C) { continue }
        if ($row.meets_slo -ne '1') { continue }
        # Prefer last qualifying row (later repeats overwrite; median aggregation is in summary —
        # for raw ramp CSVs with repeats, take the median of rps/rss per arm).
        $arm = [string]$row.arm
        if (-not $map.ContainsKey($arm)) {
            $map[$arm] = [System.Collections.Generic.List[object]]::new()
        }
        $rss = 0L
        if ($row.PSObject.Properties.Name -contains 'proxy_rss_peak_bytes' -and $row.proxy_rss_peak_bytes) {
            [void][long]::TryParse([string]$row.proxy_rss_peak_bytes, [ref]$rss)
        }
        $map[$arm].Add([pscustomobject]@{ Rps = [double]$row.rps; Rss = $rss })
    }

    $out = @{}
    foreach ($arm in $map.Keys) {
        $list = $map[$arm]
        $rpsSorted = @($list | ForEach-Object { $_.Rps } | Sort-Object)
        $rssSorted = @($list | ForEach-Object { $_.Rss } | Sort-Object)
        $mid = [int][math]::Floor(($rpsSorted.Count - 1) / 2)
        $rpsMed = if ($rpsSorted.Count % 2 -eq 0 -and $rpsSorted.Count -ge 2) {
            ($rpsSorted[$mid] + $rpsSorted[$mid + 1]) / 2
        } else { $rpsSorted[$mid] }
        $rssMed = if ($rssSorted.Count % 2 -eq 0 -and $rssSorted.Count -ge 2) {
            [long](($rssSorted[$mid] + $rssSorted[$mid + 1]) / 2)
        } else { [long]$rssSorted[$mid] }
        $out[$arm] = [pscustomobject]@{ Rps = $rpsMed; Rss = $rssMed }
    }
    return $out
}

if (-not (Test-Path $BaselineCsv)) { throw "Baseline CSV not found: $BaselineCsv" }
if (-not (Test-Path $CurrentCsv)) { throw "Current CSV not found: $CurrentCsv" }

$baseline = Get-ArmMetrics $BaselineCsv $Concurrency
$current = Get-ArmMetrics $CurrentCsv $Concurrency

$common = @($baseline.Keys | Where-Object { $current.ContainsKey($_) } | Sort-Object)
if ($common.Count -eq 0) {
    throw 'No common arms between baseline and current CSVs at c=' + $Concurrency
}

$failed = $false
Write-Host ("Cross-version gates @ c={0}: RPS >= {1:N2}x, RSS <= {2:N2}x" -f $Concurrency, $RpsGate, $RssGate) -ForegroundColor Cyan
Write-Host ("Baseline: {0}" -f $BaselineCsv)
Write-Host ("Current:  {0}" -f $CurrentCsv)
Write-Host ''

foreach ($arm in $common) {
    $b = $baseline[$arm]
    $c = $current[$arm]
    if ($b.Rps -le 0) {
        Write-Host ("SKIP {0}: baseline RPS is 0" -f $arm) -ForegroundColor DarkYellow
        continue
    }
    $rpsRatio = $c.Rps / $b.Rps
    $rssRatio = if ($b.Rss -gt 0) { $c.Rss / [double]$b.Rss } else { 1.0 }
    $rpsOk = $rpsRatio -ge $RpsGate
    $rssOk = $rssRatio -le $RssGate
    $ok = $rpsOk -and $rssOk
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0}: RPS {1:N0}/{2:N0} = {3:N3}  RSS {4}/{5} = {6:N3}  {7}" -f `
        $arm, $c.Rps, $b.Rps, $rpsRatio, $c.Rss, $b.Rss, $rssRatio, $(if ($ok) { 'PASS' } else { 'FAIL' })) `
        -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

$onlyCurrent = @($current.Keys | Where-Object { -not $baseline.ContainsKey($_) } | Sort-Object)
$onlyBaseline = @($baseline.Keys | Where-Object { -not $current.ContainsKey($_) } | Sort-Object)
if ($onlyCurrent.Count -gt 0) {
    Write-Host ("Note: {0} arm(s) only in current (skipped): {1}" -f $onlyCurrent.Count, ($onlyCurrent -join ', ')) -ForegroundColor DarkYellow
}
if ($onlyBaseline.Count -gt 0) {
    Write-Host ("Note: {0} arm(s) only in baseline (skipped): {1}" -f $onlyBaseline.Count, ($onlyBaseline -join ', ')) -ForegroundColor DarkYellow
}

if ($failed) { throw 'cross-version gate validation failed' }
Write-Host 'All cross-version gates passed.' -ForegroundColor Green
