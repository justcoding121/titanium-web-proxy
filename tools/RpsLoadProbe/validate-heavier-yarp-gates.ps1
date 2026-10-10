# Validate heavier-reverse (bodies/post) medians @ c=64:
#   Reverse TWP ÷ closest peer >= 0.40 (YARP, nginx, HAProxy, Envoy)
# Pair list mirrors RampOrchestrator.HeavierReverseArms TWP/YARP stems + -NameSuffix.
# Sharded CSVs skip pairs whose arms are not in this artifact.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [Parameter(Mandatory)]
    [ValidateSet('body64k', 'body256k', 'post64k')]
    [string] $NameSuffix,
    [double] $ReverseYarpGate = 0.40
)

. (Join-Path $PSScriptRoot 'rps-peer-gate.ps1')

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

# TWP / YARP arm name pairs for HeavierReverseArms (note cleartext YARP stem is http2, not http2-cleartext).
$revPairs = @(
    @{ Label = 'H1 TLS->H1 plain'; Twp = "twp-reverse-http1-tls-$NameSuffix"; Yarp = "yarp-reverse-http1-tls-$NameSuffix" },
    @{ Label = 'H2 TLS->H1 plain'; Twp = "twp-reverse-http2-cleartext-$NameSuffix"; Yarp = "yarp-reverse-http2-$NameSuffix" },
    @{ Label = 'H3->H1 plain'; Twp = "twp-reverse-http3-cleartext-$NameSuffix"; Yarp = "yarp-reverse-http3-cleartext-$NameSuffix" },
    @{ Label = 'H2c->H1 plain'; Twp = "twp-reverse-h2c-to-h1-$NameSuffix"; Yarp = "yarp-reverse-h2c-to-h1-$NameSuffix" },
    @{ Label = 'H2 TLS->h2c'; Twp = "twp-reverse-http2-to-h2c-$NameSuffix"; Yarp = "yarp-reverse-http2-to-h2c-$NameSuffix" },
    @{ Label = 'H2 TLS->H2 TLS'; Twp = "twp-reverse-http2-to-https-$NameSuffix"; Yarp = "yarp-reverse-http2-to-https-$NameSuffix" },
    @{ Label = 'H3->H2 TLS'; Twp = "twp-reverse-http3-to-http2-$NameSuffix"; Yarp = "yarp-reverse-http3-to-http2-$NameSuffix" },
    @{ Label = 'H3->H1 TLS'; Twp = "twp-reverse-http3-to-https-http1-$NameSuffix"; Yarp = "yarp-reverse-http3-to-https-http1-$NameSuffix" }
)

$failed = $false
Write-Host "Heavier reverse TWP/closest-peer gates ($NameSuffix; >= $ReverseYarpGate @ c=64 median; skip when no peer SLO-passes)" -ForegroundColor Cyan
foreach ($p in $revPairs) {
    $peerNames = @(Get-PeerArmCandidates $p.Yarp)
    $anyPeer = @($peerNames | Where-Object { $sustain.ContainsKey($_) }).Count -gt 0
    if (-not $sustain.ContainsKey($p.Twp) -and -not $anyPeer) {
        Write-Host "SKIP $($p.Label) : not in this shard/CSV" -ForegroundColor DarkYellow
        continue
    }
    if (-not $sustain.ContainsKey($p.Twp)) {
        Write-Host "SKIP $($p.Label) : no TWP SLO-pass at c=64 (peer present)" -ForegroundColor DarkYellow
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

if ($failed) { throw "heavier peer gate validation failed ($NameSuffix)" }
Write-Host "All heavier peer gates passed ($NameSuffix)." -ForegroundColor Green
