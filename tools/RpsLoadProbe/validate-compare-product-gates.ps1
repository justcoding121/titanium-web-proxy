# Validate compare-product medians: MITM Lite/Full >= 0.70, reverse TWP/YARP >= 0.95.
# When Repeats>1, each arm contributes multiple c=64 SLO-pass rows — use the median RPS.
# macos-15-intel CI passes lower floors for first Mac baselines (see PERF-GATES.md / workflow).
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $MitmGate = 0.70,
    # H3→H3 MITM (all OS): macos-15-intel first baseline ~0.693; keep written floor.
    [double] $MitmHttp3Gate = 0.69,
    # Defaults match MitmGate; Mac CI overrides to 0.65 (H3→H1 TLS Full smoke @ 2026da55).
    [double] $MitmHttp3TlsFullGate = 0.70,
    # Defaults match MitmGate; Mac CI overrides to 0.60 (H3→H1 plain Full @ 011ef887 = 0.620).
    [double] $MitmHttp3CleartextFullGate = 0.70,
    # Defaults match MitmGate; Mac CI overrides to 0.55 (H1 plain Full @ d0439556 = 0.564).
    [double] $MitmHttp1PlainFullGate = 0.70,
    # Defaults match MitmGate; Mac SoftPick remasure 33982544855 H2 h2c Full = 0.527.
    [double] $MitmHttp2H2cFullGate = 0.70,
    # Defaults match MitmGate; Mac SoftPick remasure 33982544855 H2 plain Lite = 0.671.
    [double] $MitmHttp2PlainLiteGate = 0.70,
    [double] $ReverseYarpGate = 0.95,
    # H3→H3 peer (all OS when YARP SLO-passes): Mac CI overrides to 0.78; Win often TWP ahead.
    [double] $ReverseYarpHttp3Gate = 0.75,
    # Defaults match ReverseYarpGate; Mac CI overrides to 0.70 (SoftGrow SoftPick Mac parity).
    [double] $ReverseYarpHttp3ToHttp1Gate = 0.95,
    [string] $BaselineCsvPath = ""
)

$ErrorActionPreference = 'Stop'
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
    @{ Label = 'H3->H1 plain'; Full = 'twp-mitm-full-http3-cleartext'; Reverse = 'twp-reverse-http3-cleartext'; Lite = 'twp-mitm-http3-cleartext' },
    @{ Label = 'H3->H1 TLS'; Full = 'twp-mitm-full-http3-to-http1'; Reverse = 'twp-reverse-http3-to-https-http1'; Lite = 'twp-mitm-http3-to-http1' },
    @{ Label = 'H3->H3'; Full = 'twp-mitm-full-http3'; Reverse = 'twp-reverse-http3'; Lite = 'twp-mitm-http3' },
    @{ Label = 'H1 plain'; Full = 'twp-mitm-full-http1'; Reverse = 'twp-reverse-http1'; Lite = 'twp-mitm-http1' },
    @{ Label = 'H2 h2c->h2c'; Full = 'twp-mitm-full-h2c-to-h2c'; Reverse = 'twp-reverse-h2c-to-h2c'; Lite = 'twp-mitm-h2c-to-h2c' },
    @{ Label = 'H2 TLS->h2c'; Full = 'twp-mitm-full-http2-to-h2c'; Reverse = 'twp-reverse-http2-to-h2c'; Lite = 'twp-mitm-http2-to-h2c' },
    @{ Label = 'H2 plain'; Full = 'twp-mitm-full-http2-cleartext'; Reverse = 'twp-reverse-http2-cleartext'; Lite = 'twp-mitm-http2-cleartext' },
    @{ Label = 'H2 TLS'; Full = 'twp-mitm-full-http2'; Reverse = 'twp-reverse-http2'; Lite = 'twp-mitm-http2' }
)

$failed = $false
Write-Host "MITM gates (Full/Lite >= $MitmGate x Reverse; H3->H3 >= $MitmHttp3Gate; H3->H1 TLS Full >= $MitmHttp3TlsFullGate; H3->H1 plain Full >= $MitmHttp3CleartextFullGate; H1 plain Full >= $MitmHttp1PlainFullGate; H2 h2c Full >= $MitmHttp2H2cFullGate; H2 plain Lite >= $MitmHttp2PlainLiteGate @ c=64 median)" -ForegroundColor Cyan
foreach ($p in $mitmPairs) {
    foreach ($kind in @('Lite', 'Full')) {
        $pairGate = if ($p.Label -eq 'H3->H3') { $MitmHttp3Gate }
            elseif ($p.Label -eq 'H3->H1 TLS' -and $kind -eq 'Full') { $MitmHttp3TlsFullGate }
            elseif ($p.Label -eq 'H3->H1 plain' -and $kind -eq 'Full') { $MitmHttp3CleartextFullGate }
            elseif ($p.Label -eq 'H1 plain' -and $kind -eq 'Full') { $MitmHttp1PlainFullGate }
            elseif ($p.Label -eq 'H2 h2c->h2c' -and $kind -eq 'Full') { $MitmHttp2H2cFullGate }
            elseif ($p.Label -eq 'H2 plain' -and $kind -eq 'Lite') { $MitmHttp2PlainLiteGate }
            else { $MitmGate }
        $num = $p.$kind
        $den = $p.Reverse
        if (-not $sustain.ContainsKey($num) -or -not $sustain.ContainsKey($den)) {
            Write-Host "FAIL $($p.Label) $kind : missing data" -ForegroundColor Red
            $failed = $true
            continue
        }
        $ratio = $sustain[$num] / $sustain[$den]
        $ok = $ratio -ge $pairGate
        $color = if ($ok) { 'Green' } else { 'Red' }
        Write-Host ("{0} {1} = {2:N3} (gate {3:N2})" -f $p.Label, $kind, $ratio, $pairGate) -ForegroundColor $color
        if (-not $ok) { $failed = $true }
    }
}

Write-Host ""
Write-Host "Reverse TWP/YARP gates (H3->H1 >= $ReverseYarpHttp3ToHttp1Gate; H3->H3 >= $ReverseYarpHttp3Gate @ c=64 median)" -ForegroundColor Cyan
$revPairs = @(
    @{ Label = 'H3->H1'; Twp = 'twp-reverse-http3-to-https-http1'; Yarp = 'yarp-reverse-http3-to-https-http1'; Gate = $ReverseYarpHttp3ToHttp1Gate },
    @{ Label = 'H3->H3'; Twp = 'twp-reverse-http3'; Yarp = 'yarp-reverse-http3-to-http3'; Gate = $ReverseYarpHttp3Gate }
)
foreach ($p in $revPairs) {
    if (-not $sustain.ContainsKey($p.Twp)) {
        Write-Host "FAIL $($p.Label) : missing TWP data" -ForegroundColor Red
        $failed = $true
        continue
    }
    if (-not $sustain.ContainsKey($p.Yarp)) {
        # YARP H3→H3 often records 0 RPS / SLO-fail on Linux GHA (peer harness), not a TWP regression.
        Write-Host "SKIP $($p.Label) : no YARP SLO-pass peer (TWP present)" -ForegroundColor DarkYellow
        continue
    }
    $ratio = $sustain[$p.Twp] / $sustain[$p.Yarp]
    $gate = [double]$p.Gate
    $ok = $ratio -ge $gate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} TWP/YARP = {1:N3} (gate {2:N2})" -f $p.Label, $ratio, $gate) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw 'compare-product gate validation failed' }
Write-Host 'All gates passed.' -ForegroundColor Green
