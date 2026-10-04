# Validate compare-product medians @ c=64:
#   MITM Lite ÷ Reverse >= 0.25, Full ÷ Reverse >= 0.25 (all OS, every Client×Origin wire)
#   Reverse TWP ÷ closest peer >= 0.50 (YARP, nginx, HAProxy, Envoy; nearest sustain)
# When Repeats>1, each arm contributes multiple c=64 SLO-pass rows — use the median RPS.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $MitmLiteGate = 0.25,
    [double] $MitmFullGate = 0.25,
    # Backward-compatible alias: if set, applies to both Lite and Full (overrides the pair above).
    [double] $MitmGate = -1,
    [double] $ReverseYarpGate = 0.50,
    [string] $BaselineCsvPath = ""
)

. (Join-Path $PSScriptRoot 'rps-peer-gate.ps1')

$ErrorActionPreference = 'Stop'
if ($MitmGate -ge 0) {
    $MitmLiteGate = $MitmGate
    $MitmFullGate = $MitmGate
}

$rows = Import-Csv $CsvPath
$byArm = @{}
foreach ($row in $rows) {
    if ([string]$row.concurrency -ne '64') { continue }
    if ($row.meets_slo -ne '1') { continue }
    $arm = [string]$row.arm
    if (-not $byArm.ContainsKey($arm)) {
        $byArm[$arm] = [System.Collections.Generic.List[double]]::new()
    }
    $byArm[$arm].Add([double]$row.rps)
}

$sustain = @{}
foreach ($arm in $byArm.Keys) {
    $sorted = @($byArm[$arm] | Sort-Object)
    $mid = [int][math]::Floor(($sorted.Count - 1) / 2)
    $sustain[$arm] = if ($sorted.Count % 2 -eq 0 -and $sorted.Count -ge 2) {
        ($sorted[$mid] + $sorted[$mid + 1]) / 2
    } else {
        $sorted[$mid]
    }
}

$mitmPairs = @(
    @{ Label = 'H1 plain->H1 plain'; Lite = 'twp-mitm-http1'; Full = 'twp-mitm-full-http1'; Reverse = 'twp-reverse-http1' },
    @{ Label = 'H1 plain->H1 TLS'; Lite = 'twp-mitm-http1-to-https'; Full = 'twp-mitm-full-http1-to-https'; Reverse = 'twp-reverse-http1-to-https' },
    @{ Label = 'H1 plain->H2 plain'; Lite = 'twp-mitm-http1-plain-to-h2c'; Full = 'twp-mitm-full-http1-plain-to-h2c'; Reverse = 'twp-reverse-http1-plain-to-h2c' },
    @{ Label = 'H1 plain->H2 TLS'; Lite = 'twp-mitm-http1-plain-to-http2'; Full = 'twp-mitm-full-http1-plain-to-http2'; Reverse = 'twp-reverse-http1-plain-to-http2' },
    @{ Label = 'H1 plain->H3'; Lite = 'twp-mitm-http1-plain-to-http3'; Full = 'twp-mitm-full-http1-plain-to-http3'; Reverse = 'twp-reverse-http1-plain-to-http3' },
    @{ Label = 'H1 TLS->H1 plain'; Lite = 'twp-mitm-http1-tls'; Full = 'twp-mitm-full-http1-tls'; Reverse = 'twp-reverse-http1-tls' },
    @{ Label = 'H1 TLS->H1 TLS'; Lite = 'twp-mitm-http1-tls-to-https'; Full = 'twp-mitm-full-http1-tls-to-https'; Reverse = 'twp-reverse-http1-mitm' },
    @{ Label = 'H1 TLS->H2 plain'; Lite = 'twp-mitm-http1-to-h2c'; Full = 'twp-mitm-full-http1-to-h2c'; Reverse = 'twp-reverse-http1-to-h2c' },
    @{ Label = 'H1 TLS->H2 TLS'; Lite = 'twp-mitm-http11-to-http2'; Full = 'twp-mitm-full-http11-to-http2'; Reverse = 'twp-reverse-http11-to-http2' },
    @{ Label = 'H1 TLS->H3'; Lite = 'twp-mitm-http1-to-http3'; Full = 'twp-mitm-full-http1-to-http3'; Reverse = 'twp-reverse-http1-to-http3' },
    @{ Label = 'H2c->H1 plain'; Lite = 'twp-mitm-h2c-to-h1'; Full = 'twp-mitm-full-h2c-to-h1'; Reverse = 'twp-reverse-h2c-to-h1' },
    @{ Label = 'H2c->H1 TLS'; Lite = 'twp-mitm-h2c-to-https'; Full = 'twp-mitm-full-h2c-to-https'; Reverse = 'twp-reverse-h2c-to-https' },
    @{ Label = 'H2c->h2c'; Lite = 'twp-mitm-h2c-to-h2c'; Full = 'twp-mitm-full-h2c-to-h2c'; Reverse = 'twp-reverse-h2c-to-h2c' },
    @{ Label = 'H2c->H2 TLS'; Lite = 'twp-mitm-h2c'; Full = 'twp-mitm-full-h2c'; Reverse = 'twp-reverse-h2c' },
    @{ Label = 'H2c->H3'; Lite = 'twp-mitm-h2c-to-h3'; Full = 'twp-mitm-full-h2c-to-h3'; Reverse = 'twp-reverse-h2c-to-h3' },
    @{ Label = 'H2 TLS->H1 plain'; Lite = 'twp-mitm-http2-cleartext'; Full = 'twp-mitm-full-http2-cleartext'; Reverse = 'twp-reverse-http2-cleartext' },
    @{ Label = 'H2 TLS->H1 TLS'; Lite = 'twp-mitm-http2-to-http1'; Full = 'twp-mitm-full-http2-to-http1'; Reverse = 'twp-reverse-http2-to-https-http1' },
    @{ Label = 'H2 TLS->h2c'; Lite = 'twp-mitm-http2-to-h2c'; Full = 'twp-mitm-full-http2-to-h2c'; Reverse = 'twp-reverse-http2-to-h2c' },
    @{ Label = 'H2 TLS->H2 TLS'; Lite = 'twp-mitm-http2'; Full = 'twp-mitm-full-http2'; Reverse = 'twp-reverse-http2' },
    @{ Label = 'H2 TLS->H3'; Lite = 'twp-mitm-http2-to-http3'; Full = 'twp-mitm-full-http2-to-http3'; Reverse = 'twp-reverse-http2-to-http3' },
    @{ Label = 'H3->H1 plain'; Lite = 'twp-mitm-http3-cleartext'; Full = 'twp-mitm-full-http3-cleartext'; Reverse = 'twp-reverse-http3-cleartext' },
    @{ Label = 'H3->H1 TLS'; Lite = 'twp-mitm-http3-to-http1'; Full = 'twp-mitm-full-http3-to-http1'; Reverse = 'twp-reverse-http3-to-https-http1' },
    @{ Label = 'H3->h2c'; Lite = 'twp-mitm-http3-to-h2c'; Full = 'twp-mitm-full-http3-to-h2c'; Reverse = 'twp-reverse-http3-to-h2c' },
    @{ Label = 'H3->H2 TLS'; Lite = 'twp-mitm-http3-to-http2'; Full = 'twp-mitm-full-http3-to-http2'; Reverse = 'twp-reverse-http3-to-http2' },
    @{ Label = 'H3->H3'; Lite = 'twp-mitm-http3'; Full = 'twp-mitm-full-http3'; Reverse = 'twp-reverse-http3' }
)

$failed = $false
Write-Host "MITM gates (Lite >= $MitmLiteGate / Full >= $MitmFullGate x Reverse @ c=64 median; all OS)" -ForegroundColor Cyan
foreach ($p in $mitmPairs) {
    foreach ($kind in @('Lite', 'Full')) {
        $num = $p.$kind
        $den = $p.Reverse
        $gate = if ($kind -eq 'Lite') { $MitmLiteGate } else { $MitmFullGate }
        # Sharded CSVs only contain a subset of arms — skip pairs not present in this artifact.
        if (-not $sustain.ContainsKey($num) -and -not $sustain.ContainsKey($den)) {
            Write-Host "SKIP $($p.Label) $kind : not in this shard/CSV" -ForegroundColor DarkYellow
            continue
        }
        if (-not $sustain.ContainsKey($num) -or -not $sustain.ContainsKey($den)) {
            Write-Host "FAIL $($p.Label) $kind : missing data (partial pair in CSV)" -ForegroundColor Red
            $failed = $true
            continue
        }
        $ratio = $sustain[$num] / $sustain[$den]
        $ok = $ratio -ge $gate
        $color = if ($ok) { 'Green' } else { 'Red' }
        Write-Host ("{0} {1} = {2:N3} (gate {3:N2})" -f $p.Label, $kind, $ratio, $gate) -ForegroundColor $color
        if (-not $ok) { $failed = $true }
    }
}

Write-Host ""
Write-Host "Reverse TWP/closest-peer gates (>= $ReverseYarpGate @ c=64 median; skip when no peer SLO-passes)" -ForegroundColor Cyan
# All reverse Client×Origin wires with a YARP peer (same set as validate-all-compare-product-arms.ps1).
$revPairs = @(
    @{ Label = 'H1 plain->H1 plain'; Twp = 'twp-reverse-http1'; Yarp = 'yarp-reverse-http1' },
    @{ Label = 'H1 plain->H1 TLS'; Twp = 'twp-reverse-http1-to-https'; Yarp = 'yarp-reverse-http1-to-https' },
    @{ Label = 'H1 plain->H2 plain'; Twp = 'twp-reverse-http1-plain-to-h2c'; Yarp = 'yarp-reverse-http1-plain-to-h2c' },
    @{ Label = 'H1 plain->H2 TLS'; Twp = 'twp-reverse-http1-plain-to-http2'; Yarp = 'yarp-reverse-http1-plain-to-http2' },
    @{ Label = 'H1 plain->H3'; Twp = 'twp-reverse-http1-plain-to-http3'; Yarp = 'yarp-reverse-http1-plain-to-http3' },
    @{ Label = 'H1 TLS->H1 plain'; Twp = 'twp-reverse-http1-tls'; Yarp = 'yarp-reverse-http1-tls' },
    @{ Label = 'H1 TLS->H1 TLS'; Twp = 'twp-reverse-http1-mitm'; Yarp = 'yarp-reverse-http1-tls-to-https' },
    @{ Label = 'H1 TLS->H2 plain'; Twp = 'twp-reverse-http1-to-h2c'; Yarp = 'yarp-reverse-http1-to-h2c' },
    @{ Label = 'H1 TLS->H2 TLS'; Twp = 'twp-reverse-http11-to-http2'; Yarp = 'yarp-reverse-http11-to-http2' },
    @{ Label = 'H1 TLS->H3'; Twp = 'twp-reverse-http1-to-http3'; Yarp = 'yarp-reverse-http1-to-http3' },
    @{ Label = 'H2c->H1 plain'; Twp = 'twp-reverse-h2c-to-h1'; Yarp = 'yarp-reverse-h2c-to-h1' },
    @{ Label = 'H2c->H1 TLS'; Twp = 'twp-reverse-h2c-to-https'; Yarp = 'yarp-reverse-h2c-to-https' },
    @{ Label = 'H2c->h2c'; Twp = 'twp-reverse-h2c-to-h2c'; Yarp = 'yarp-reverse-h2c-to-h2c' },
    @{ Label = 'H2c->H2 TLS'; Twp = 'twp-reverse-h2c'; Yarp = 'yarp-reverse-h2c' },
    @{ Label = 'H2c->H3'; Twp = 'twp-reverse-h2c-to-h3'; Yarp = 'yarp-reverse-h2c-to-h3' },
    @{ Label = 'H2 TLS->H1 plain'; Twp = 'twp-reverse-http2-cleartext'; Yarp = 'yarp-reverse-http2' },
    @{ Label = 'H2 TLS->H1 TLS'; Twp = 'twp-reverse-http2-to-https-http1'; Yarp = 'yarp-reverse-http2-to-https-http1' },
    @{ Label = 'H2 TLS->h2c'; Twp = 'twp-reverse-http2-to-h2c'; Yarp = 'yarp-reverse-http2-to-h2c' },
    @{ Label = 'H2 TLS->H2 TLS'; Twp = 'twp-reverse-http2'; Yarp = 'yarp-reverse-http2-to-https' },
    @{ Label = 'H2 TLS->H3'; Twp = 'twp-reverse-http2-to-http3'; Yarp = 'yarp-reverse-http2-to-http3' },
    @{ Label = 'H3->H1 plain'; Twp = 'twp-reverse-http3-cleartext'; Yarp = 'yarp-reverse-http3-cleartext' },
    @{ Label = 'H3->H1 TLS'; Twp = 'twp-reverse-http3-to-https-http1'; Yarp = 'yarp-reverse-http3-to-https-http1' },
    @{ Label = 'H3->h2c'; Twp = 'twp-reverse-http3-to-h2c'; Yarp = 'yarp-reverse-http3-to-h2c' },
    @{ Label = 'H3->H2 TLS'; Twp = 'twp-reverse-http3-to-http2'; Yarp = 'yarp-reverse-http3-to-http2' },
    @{ Label = 'H3->H3'; Twp = 'twp-reverse-http3'; Yarp = 'yarp-reverse-http3-to-http3' }
)
foreach ($p in $revPairs) {
    $peerNames = @(Get-PeerArmCandidates $p.Yarp)
    $anyPeer = @($peerNames | Where-Object { $sustain.ContainsKey($_) }).Count -gt 0
    if (-not $sustain.ContainsKey($p.Twp) -and -not $anyPeer) {
        Write-Host "SKIP $($p.Label) : not in this shard/CSV" -ForegroundColor DarkYellow
        continue
    }
    if (-not $sustain.ContainsKey($p.Twp)) {
        Write-Host "FAIL $($p.Label) : missing TWP data" -ForegroundColor Red
        $failed = $true
        continue
    }
    $peer = Get-ClosestPeer $sustain $sustain[$p.Twp] $p.Yarp
    if ($null -eq $peer) {
        Write-Host "SKIP $($p.Label) : no SLO-pass peer (TWP present)" -ForegroundColor DarkYellow
        continue
    }
    $ok = $peer.Ratio -ge $ReverseYarpGate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} TWP/{1} = {2:N3} (gate {3:N2})" -f $p.Label, $peer.Arm, $peer.Ratio, $ReverseYarpGate) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw 'compare-product gate validation failed' }
Write-Host 'All gates passed.' -ForegroundColor Green
