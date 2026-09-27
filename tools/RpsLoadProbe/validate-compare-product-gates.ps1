# Validate compare-product medians @ c=64:
#   MITM Lite ÷ Reverse >= 0.50, Full ÷ Reverse >= 0.50 (all OS, all gated pairs)
#   Reverse TWP ÷ YARP >= 0.60 (when YARP SLO-passes)
# No nginx gate — nginx is wiki/charts only.
# When Repeats>1, each arm contributes multiple c=64 SLO-pass rows — use the median RPS.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $MitmLiteGate = 0.50,
    [double] $MitmFullGate = 0.50,
    # Backward-compatible alias: if set, applies to both Lite and Full (overrides the pair above).
    [double] $MitmGate = -1,
    [double] $ReverseYarpGate = 0.60,
    [string] $BaselineCsvPath = ""
)

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
Write-Host "Reverse TWP/YARP gates (>= $ReverseYarpGate @ c=64 median; skip when YARP SLO-fails)" -ForegroundColor Cyan
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
    if (-not $sustain.ContainsKey($p.Twp) -and -not $sustain.ContainsKey($p.Yarp)) {
        Write-Host "SKIP $($p.Label) : not in this shard/CSV" -ForegroundColor DarkYellow
        continue
    }
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
    $ok = $ratio -ge $ReverseYarpGate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} TWP/YARP = {1:N3} (gate {2:N2})" -f $p.Label, $ratio, $ReverseYarpGate) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw 'compare-product gate validation failed' }
Write-Host 'All gates passed.' -ForegroundColor Green
