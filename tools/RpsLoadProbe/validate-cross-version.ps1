# Validate 7.0 compare-cross-version CSV against committed 6.0 baselines.
# Gates product (twp-*) arms only — nginx/YARP absolute RPS tracks runner heat, not TWP.
# Prefer peer-normalized RPS when a yarp-* peer exists: (TWP÷YARP)_7 / (TWP÷YARP)_6 >= PeerGate.
# Absolute RPS gate is a noise floor for arms without a peer.
param(
    [Parameter(Mandatory)] [string] $BaselineCsv,
    [Parameter(Mandatory)] [string] $CurrentCsv,
    [double] $RpsGate = 0.70,
    [double] $PeerGate = 0.90,
    # Hosted Windows runners spike RSS ~15–20% on H1→h2c without RPS/peer-norm change (see 33253789356 / 33263428508).
    [double] $RssGate = 1.20,
    [int] $Concurrency = 64,
    [switch] $IncludePeers
)

$ErrorActionPreference = 'Stop'

function Get-ArmMetrics([string]$Path, [int]$C) {
    $rows = Import-Csv $Path
    $map = @{}
    foreach ($row in $rows) {
        if ([int]$row.concurrency -ne $C) { continue }
        if ($row.meets_slo -ne '1') { continue }
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

function Get-YarpPeer([string]$TwpArm) {
    if ($TwpArm -notlike 'twp-*') { return $null }
    return ($TwpArm -replace '^twp-', 'yarp-')
}

if (-not (Test-Path $BaselineCsv)) { throw "Baseline CSV not found: $BaselineCsv" }
if (-not (Test-Path $CurrentCsv)) { throw "Current CSV not found: $CurrentCsv" }

$baseline = Get-ArmMetrics $BaselineCsv $Concurrency
$current = Get-ArmMetrics $CurrentCsv $Concurrency

$common = @($baseline.Keys | Where-Object { $current.ContainsKey($_) } | Sort-Object)
if (-not $IncludePeers) {
    $common = @($common | Where-Object { $_ -like 'twp-*' })
}
if ($common.Count -eq 0) {
    throw 'No common TWP arms between baseline and current CSVs at c=' + $Concurrency
}

$failed = $false
Write-Host ("Cross-version gates @ c={0}: TWP absolute RPS >= {1:N2}x (no-peer floor), peer-norm >= {2:N2}x, RSS <= {3:N2}x" -f `
    $Concurrency, $RpsGate, $PeerGate, $RssGate) -ForegroundColor Cyan
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
    $rssOk = $rssRatio -le $RssGate

    $peer = Get-YarpPeer $arm
    $peerOk = $true
    $peerRatio = $null
    $usedPeer = $false
    if ($peer -and $baseline.ContainsKey($peer) -and $current.ContainsKey($peer) `
        -and $baseline[$peer].Rps -gt 0 -and $current[$peer].Rps -gt 0) {
        $baseTy = $b.Rps / $baseline[$peer].Rps
        $curTy = $c.Rps / $current[$peer].Rps
        if ($baseTy -gt 0) {
            $peerRatio = $curTy / $baseTy
            $peerOk = $peerRatio -ge $PeerGate
            $usedPeer = $true
        }
    }

    # Absolute floor always applies; peer-norm is the primary product gate when available.
    $rpsOk = $rpsRatio -ge $RpsGate
    $ok = $rpsOk -and $rssOk -and $peerOk
    $color = if ($ok) { 'Green' } else { 'Red' }
    $peerTxt = if ($usedPeer) { ("  peer-norm {0:N3}" -f $peerRatio) } else { '  peer-norm n/a' }
    Write-Host ("{0}: RPS {1:N0}/{2:N0} = {3:N3}  RSS {4}/{5} = {6:N3}{7}  {8}" -f `
        $arm, $c.Rps, $b.Rps, $rpsRatio, $c.Rss, $b.Rss, $rssRatio, $peerTxt, $(if ($ok) { 'PASS' } else { 'FAIL' })) `
        -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

$onlyCurrent = @($current.Keys | Where-Object { $_ -like 'twp-*' -and -not $baseline.ContainsKey($_) } | Sort-Object)
$onlyBaseline = @($baseline.Keys | Where-Object { $_ -like 'twp-*' -and -not $current.ContainsKey($_) } | Sort-Object)
if ($onlyCurrent.Count -gt 0) {
    Write-Host ("Note: {0} TWP arm(s) only in current (skipped): {1}" -f $onlyCurrent.Count, ($onlyCurrent -join ', ')) -ForegroundColor DarkYellow
}
if ($onlyBaseline.Count -gt 0) {
    Write-Host ("Note: {0} TWP arm(s) only in baseline (skipped): {1}" -f $onlyBaseline.Count, ($onlyBaseline -join ', ')) -ForegroundColor DarkYellow
}

if ($failed) { throw 'cross-version gate validation failed' }
Write-Host 'All cross-version gates passed.' -ForegroundColor Green
