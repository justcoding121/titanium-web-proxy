# Validate compare-grpc medians @ c=64:
#   Reverse TWP ÷ YARP >= 0.60 (when YARP SLO-passes)
# Pairs from RampOrchestrator.BuildCompareGrpcArms.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $ReverseYarpGate = 0.60
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

$revPairs = @(
    @{ Label = 'gRPC H2 TLS->H2 TLS'; Twp = 'twp-grpc-http2'; Yarp = 'yarp-grpc-http2' },
    @{ Label = 'gRPC H2 TLS->h2c'; Twp = 'twp-grpc-h2c'; Yarp = 'yarp-grpc-h2c' }
)

$failed = $false
Write-Host "gRPC TWP/YARP gates (>= $ReverseYarpGate @ c=64 median; skip when YARP SLO-fails)" -ForegroundColor Cyan
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
        Write-Host "SKIP $($p.Label) : no YARP SLO-pass peer (TWP present)" -ForegroundColor DarkYellow
        continue
    }
    $ratio = $sustain[$p.Twp] / $sustain[$p.Yarp]
    $ok = $ratio -ge $ReverseYarpGate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} TWP/YARP = {1:N3} (gate {2:N2})" -f $p.Label, $ratio, $ReverseYarpGate) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw 'gRPC YARP gate validation failed' }
Write-Host 'All gRPC YARP gates passed.' -ForegroundColor Green
