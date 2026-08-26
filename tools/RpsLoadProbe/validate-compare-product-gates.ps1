# Validate compare-product medians: MITM Lite/Full >= 0.80, reverse TWP/YARP >= 0.95 vs baseline run.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $MitmGate = 0.80,
    [double] $ReverseYarpGate = 0.95,
    [string] $BaselineCsvPath = ""
)

$ErrorActionPreference = 'Stop'
$rows = Import-Csv $CsvPath
$sustain = @{}
foreach ($row in $rows) {
    if ($row.concurrency -ne '64') { continue }
    if ($row.meets_slo -ne '1') { continue }
    $sustain[$row.arm] = [double]$row.rps
}

$mitmPairs = @(
    @{ Label = 'H3->H1 plain'; Full = 'twp-mitm-full-http3-cleartext'; Reverse = 'twp-reverse-http3-cleartext'; Lite = 'twp-mitm-http3-cleartext' },
    @{ Label = 'H3->H1 TLS'; Full = 'twp-mitm-full-http3-to-http1'; Reverse = 'twp-reverse-http3-to-https-http1'; Lite = 'twp-mitm-http3-to-http1' },
    @{ Label = 'H3->H3'; Full = 'twp-mitm-full-http3'; Reverse = 'twp-reverse-http3'; Lite = 'twp-mitm-http3' },
    @{ Label = 'H1 plain'; Full = 'twp-mitm-full-http1'; Reverse = 'twp-reverse-http1'; Lite = 'twp-mitm-http1' },
    @{ Label = 'H2 plain'; Full = 'twp-mitm-full-http2-cleartext'; Reverse = 'twp-reverse-http2-cleartext'; Lite = 'twp-mitm-http2-cleartext' },
    @{ Label = 'H2 TLS'; Full = 'twp-mitm-full-http2'; Reverse = 'twp-reverse-http2'; Lite = 'twp-mitm-http2' }
)

$failed = $false
Write-Host "MITM gates (Full/Lite >= $MitmGate x Reverse @ c=64)" -ForegroundColor Cyan
foreach ($p in $mitmPairs) {
    foreach ($kind in @('Lite', 'Full')) {
        $num = $p.$kind
        $den = $p.Reverse
        if (-not $sustain.ContainsKey($num) -or -not $sustain.ContainsKey($den)) {
            Write-Host "FAIL $($p.Label) $kind : missing data" -ForegroundColor Red
            $failed = $true
            continue
        }
        $ratio = $sustain[$num] / $sustain[$den]
        $ok = $ratio -ge $MitmGate
        $color = if ($ok) { 'Green' } else { 'Red' }
        Write-Host ("{0} {1} = {2:N3}" -f $p.Label, $kind, $ratio) -ForegroundColor $color
        if (-not $ok) { $failed = $true }
    }
}

Write-Host ""
Write-Host "Reverse TWP/YARP gates (>= $ReverseYarpGate @ c=64)" -ForegroundColor Cyan
$revPairs = @(
    @{ Label = 'H3->H1'; Twp = 'twp-reverse-http3-to-https-http1'; Yarp = 'yarp-reverse-http3-to-https-http1' },
    @{ Label = 'H3->H3'; Twp = 'twp-reverse-http3'; Yarp = 'yarp-reverse-http3-to-http3' }
)
foreach ($p in $revPairs) {
    if (-not $sustain.ContainsKey($p.Twp) -or -not $sustain.ContainsKey($p.Yarp)) {
        Write-Host "FAIL $($p.Label) : missing data" -ForegroundColor Red
        $failed = $true
        continue
    }
    $ratio = $sustain[$p.Twp] / $sustain[$p.Yarp]
    $ok = $ratio -ge $ReverseYarpGate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} TWP/YARP = {1:N3}" -f $p.Label, $ratio) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw 'compare-product gate validation failed' }
Write-Host 'All gates passed.' -ForegroundColor Green
