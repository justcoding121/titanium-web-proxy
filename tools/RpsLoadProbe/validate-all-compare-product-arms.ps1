# Full compare-product gate validation (all WIRES rows, Win+Lin+Mac, median of 3 GHA runs).
param(
    [Parameter(Mandatory)] [string[]] $RunIds,
    [double] $MitmLiteGate = 0.25,
    [double] $MitmFullGate = 0.25,
    # Backward-compatible alias: if set, applies to both Lite and Full.
    [double] $MitmGate = -1,
    [double] $ReverseYarpGate = 0.50,
    [string] $BaselineRunId = '32960766249'
)
if ($MitmGate -ge 0) {
    $MitmLiteGate = $MitmGate
    $MitmFullGate = $MitmGate
}

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'rps-peer-gate.ps1')
if ($RunIds.Count -eq 1 -and $RunIds[0] -match ',') {
    $RunIds = $RunIds[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}
$Steps = 4  # concurrency 8,16,32,64 per repeat

function Get-ArmSustainRps([string]$CsvPath, [string]$Arm) {
    $rows = @(Import-Csv $CsvPath | Where-Object { $_.arm -eq $Arm })
    if ($rows.Count -eq 0) { return $null }
    $sustains = @()
    for ($i = 0; $i + $Steps -le $rows.Count; $i += $Steps) {
        $chunk = $rows[$i..($i + $Steps - 1)]
        $c64 = $chunk | Where-Object { $_.concurrency -eq '64' -and $_.meets_slo -eq '1' } | Select-Object -Last 1
        if ($c64) { $sustains += [double]$c64.rps }
    }
    if ($sustains.Count -eq 0) { return $null }
    ($sustains | Sort-Object)[([math]::Floor(($sustains.Count - 1) / 2))]
}

function Median([double[]]$vals) {
    if ($vals.Count -eq 0) { return $null }
    $s = $vals | Sort-Object
    return $s[[math]::Floor(($s.Count - 1) / 2)]
}

$wires = @(
    @{ C='HTTP/1 plain'; O='HTTP/1 plain'; Rev='twp-reverse-http1'; Yarp='yarp-reverse-http1'; Lite='twp-mitm-http1'; Full='twp-mitm-full-http1' },
    @{ C='HTTP/1 plain'; O='HTTP/1 TLS'; Rev='twp-reverse-http1-to-https'; Yarp='yarp-reverse-http1-to-https'; Lite='twp-mitm-http1-to-https'; Full='twp-mitm-full-http1-to-https' },
    @{ C='HTTP/1 plain'; O='HTTP/2 plain'; Rev='twp-reverse-http1-plain-to-h2c'; Yarp='yarp-reverse-http1-plain-to-h2c'; Lite='twp-mitm-http1-plain-to-h2c'; Full='twp-mitm-full-http1-plain-to-h2c' },
    @{ C='HTTP/1 plain'; O='HTTP/2 TLS'; Rev='twp-reverse-http1-plain-to-http2'; Yarp='yarp-reverse-http1-plain-to-http2'; Lite='twp-mitm-http1-plain-to-http2'; Full='twp-mitm-full-http1-plain-to-http2' },
    @{ C='HTTP/1 plain'; O='HTTP/3 QUIC'; Rev='twp-reverse-http1-plain-to-http3'; Yarp='yarp-reverse-http1-plain-to-http3'; Lite='twp-mitm-http1-plain-to-http3'; Full='twp-mitm-full-http1-plain-to-http3' },
    @{ C='HTTP/1 TLS'; O='HTTP/1 plain'; Rev='twp-reverse-http1-tls'; Yarp='yarp-reverse-http1-tls'; Lite='twp-mitm-http1-tls'; Full='twp-mitm-full-http1-tls' },
    @{ C='HTTP/1 TLS'; O='HTTP/1 TLS'; Rev='twp-reverse-http1-mitm'; Yarp='yarp-reverse-http1-tls-to-https'; Lite='twp-mitm-http1-tls-to-https'; Full='twp-mitm-full-http1-tls-to-https' },
    @{ C='HTTP/1 TLS'; O='HTTP/2 plain'; Rev='twp-reverse-http1-to-h2c'; Yarp='yarp-reverse-http1-to-h2c'; Lite='twp-mitm-http1-to-h2c'; Full='twp-mitm-full-http1-to-h2c' },
    @{ C='HTTP/1 TLS'; O='HTTP/2 TLS'; Rev='twp-reverse-http11-to-http2'; Yarp='yarp-reverse-http11-to-http2'; Lite='twp-mitm-http11-to-http2'; Full='twp-mitm-full-http11-to-http2' },
    @{ C='HTTP/1 TLS'; O='HTTP/3 QUIC'; Rev='twp-reverse-http1-to-http3'; Yarp='yarp-reverse-http1-to-http3'; Lite='twp-mitm-http1-to-http3'; Full='twp-mitm-full-http1-to-http3' },
    @{ C='HTTP/2 plain'; O='HTTP/1 plain'; Rev='twp-reverse-h2c-to-h1'; Yarp='yarp-reverse-h2c-to-h1'; Lite='twp-mitm-h2c-to-h1'; Full='twp-mitm-full-h2c-to-h1' },
    @{ C='HTTP/2 plain'; O='HTTP/1 TLS'; Rev='twp-reverse-h2c-to-https'; Yarp='yarp-reverse-h2c-to-https'; Lite='twp-mitm-h2c-to-https'; Full='twp-mitm-full-h2c-to-https' },
    @{ C='HTTP/2 plain'; O='HTTP/2 plain'; Rev='twp-reverse-h2c-to-h2c'; Yarp='yarp-reverse-h2c-to-h2c'; Lite='twp-mitm-h2c-to-h2c'; Full='twp-mitm-full-h2c-to-h2c' },
    @{ C='HTTP/2 plain'; O='HTTP/2 TLS'; Rev='twp-reverse-h2c'; Yarp='yarp-reverse-h2c'; Lite='twp-mitm-h2c'; Full='twp-mitm-full-h2c' },
    @{ C='HTTP/2 plain'; O='HTTP/3 QUIC'; Rev='twp-reverse-h2c-to-h3'; Yarp='yarp-reverse-h2c-to-h3'; Lite='twp-mitm-h2c-to-h3'; Full='twp-mitm-full-h2c-to-h3' },
    @{ C='HTTP/2 TLS'; O='HTTP/1 plain'; Rev='twp-reverse-http2-cleartext'; Yarp='yarp-reverse-http2'; Lite='twp-mitm-http2-cleartext'; Full='twp-mitm-full-http2-cleartext' },
    @{ C='HTTP/2 TLS'; O='HTTP/1 TLS'; Rev='twp-reverse-http2-to-https-http1'; Yarp='yarp-reverse-http2-to-https-http1'; Lite='twp-mitm-http2-to-http1'; Full='twp-mitm-full-http2-to-http1' },
    @{ C='HTTP/2 TLS'; O='HTTP/2 plain'; Rev='twp-reverse-http2-to-h2c'; Yarp='yarp-reverse-http2-to-h2c'; Lite='twp-mitm-http2-to-h2c'; Full='twp-mitm-full-http2-to-h2c' },
    @{ C='HTTP/2 TLS'; O='HTTP/2 TLS'; Rev='twp-reverse-http2'; Yarp='yarp-reverse-http2-to-https'; Lite='twp-mitm-http2'; Full='twp-mitm-full-http2' },
    @{ C='HTTP/2 TLS'; O='HTTP/3 QUIC'; Rev='twp-reverse-http2-to-http3'; Yarp='yarp-reverse-http2-to-http3'; Lite='twp-mitm-http2-to-http3'; Full='twp-mitm-full-http2-to-http3' },
    @{ C='HTTP/3 QUIC'; O='HTTP/1 plain'; Rev='twp-reverse-http3-cleartext'; Yarp='yarp-reverse-http3-cleartext'; Lite='twp-mitm-http3-cleartext'; Full='twp-mitm-full-http3-cleartext' },
    @{ C='HTTP/3 QUIC'; O='HTTP/1 TLS'; Rev='twp-reverse-http3-to-https-http1'; Yarp='yarp-reverse-http3-to-https-http1'; Lite='twp-mitm-http3-to-http1'; Full='twp-mitm-full-http3-to-http1' },
    @{ C='HTTP/3 QUIC'; O='HTTP/2 plain'; Rev='twp-reverse-http3-to-h2c'; Yarp='yarp-reverse-http3-to-h2c'; Lite='twp-mitm-http3-to-h2c'; Full='twp-mitm-full-http3-to-h2c' },
    @{ C='HTTP/3 QUIC'; O='HTTP/2 TLS'; Rev='twp-reverse-http3-to-http2'; Yarp='yarp-reverse-http3-to-http2'; Lite='twp-mitm-http3-to-http2'; Full='twp-mitm-full-http3-to-http2' },
    @{ C='HTTP/3 QUIC'; O='HTTP/3 QUIC'; Rev='twp-reverse-http3'; Yarp='yarp-reverse-http3-to-http3'; Lite='twp-mitm-http3'; Full='twp-mitm-full-http3' }
)

function Get-MedianSustain([string]$OsFolder, [string]$Arm) {
    $vals = @()
    foreach ($runId in $RunIds) {
        $csv = Get-ChildItem "tools/RpsLoadProbe/results/gha-$runId/$OsFolder/*.csv" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $csv) {
            $csv = Get-ChildItem "tools/RpsLoadProbe/results/gha-$runId/rps-csv-$OsFolder/*.csv" -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        if (-not $csv) { continue }
        $r = Get-ArmSustainRps $csv.FullName $Arm
        if ($null -ne $r) { $vals += $r }
    }
    return Median $vals
}

$failed = @()
foreach ($os in @('windows-latest', 'ubuntu-latest', 'macos-15')) {
    Write-Host "`n=== $os ===" -ForegroundColor Cyan
    foreach ($w in $wires) {
        $rev = Get-MedianSustain $os $w.Rev
        $lite = Get-MedianSustain $os $w.Lite
        $full = Get-MedianSustain $os $w.Full
        if ($null -eq $rev -or $rev -le 0) { continue }

        $peerSustain = @{}
        foreach ($arm in (Get-PeerArmCandidates $w.Yarp)) {
            $got = Get-MedianSustain $os $arm
            if ($null -ne $got -and $got -gt 0) { $peerSustain[$arm] = [double]$got }
        }

        # Windows and macOS product jobs do not run twp-mitm-* (Linux-only MITM).
        # A null Lite/Full is that omission, not a missing arm.
        if ($null -ne $lite) {
            $lr = $lite / $rev
            $ok = $lr -ge $MitmLiteGate
            if (-not $ok) { $failed += "$os $($w.C)->$($w.O) Lite=$([math]::Round($lr,3))" }
            Write-Host ("MITM Lite {0}->{1}: {2:N3} {3}" -f $w.C, $w.O, $lr, $(if($ok){'OK'}else{'FAIL'}))
        }
        if ($null -ne $full) {
            $fr = $full / $rev
            $ok = $fr -ge $MitmFullGate
            if (-not $ok) { $failed += "$os $($w.C)->$($w.O) Full=$([math]::Round($fr,3))" }
            Write-Host ("MITM Full {0}->{1}: {2:N3} {3}" -f $w.C, $w.O, $fr, $(if($ok){'OK'}else{'FAIL'}))
        }
        $peer = Get-ClosestPeer $peerSustain $rev $w.Yarp
        if ($null -ne $peer) {
            $ok = $peer.Ratio -ge $ReverseYarpGate
            if (-not $ok) { $failed += "$os $($w.C)->$($w.O) TWP/$($peer.Arm)=$([math]::Round($peer.Ratio,3))" }
            Write-Host ("Reverse {0}->{1} TWP/{2}: {3:N3} {4}" -f $w.C, $w.O, $peer.Arm, $peer.Ratio, $(if($ok){'OK'}else{'FAIL'}))
        }
    }
}

Write-Host "`n--- FAILURES ($($failed.Count)) ---" -ForegroundColor $(if($failed.Count){'Red'}else{'Green'})
$failed | ForEach-Object { Write-Host $_ }
if ($failed.Count -gt 0) { exit 1 }
