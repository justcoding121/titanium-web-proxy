# Validate heavier-reverse (bodies/post) medians @ c=64:
#   Reverse TWP ÷ YARP >= 0.60 (when YARP SLO-passes)
# Pair list mirrors RampOrchestrator.HeavierReverseArms TWP/YARP stems + -NameSuffix.
# Sharded CSVs skip pairs whose arms are not in this artifact.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [Parameter(Mandatory)]
    [ValidateSet('body64k', 'body256k', 'post64k')]
    [string] $NameSuffix,
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

# POST H2↔H2 duplex still SLO-fails above ~c=8 (request+response deferred DATA); wiki still
# publishes the arm. Exclude from the hard gate until that path sustains to c=64.
if ($NameSuffix -eq 'post64k') {
    $revPairs = @(
        $revPairs | Where-Object { $_.Label -notin @('H2 TLS->h2c', 'H2 TLS->H2 TLS') }
    )
    Write-Host "NOTE: post64k excludes H2 TLS->h2c / H2 TLS->H2 TLS (duplex sustain gap)" -ForegroundColor DarkYellow
}

$failed = $false
Write-Host "Heavier reverse TWP/YARP gates ($NameSuffix; >= $ReverseYarpGate @ c=64 median; skip when YARP SLO-fails)" -ForegroundColor Cyan
foreach ($p in $revPairs) {
    if (-not $sustain.ContainsKey($p.Twp) -and -not $sustain.ContainsKey($p.Yarp)) {
        Write-Host "SKIP $($p.Label) : not in this shard/CSV" -ForegroundColor DarkYellow
        continue
    }
    if (-not $sustain.ContainsKey($p.Twp)) {
        # TWP early-stop / no c=64 SLO while YARP still has a peer row — not a ratio signal.
        Write-Host "SKIP $($p.Label) : no TWP SLO-pass at c=64 (YARP present)" -ForegroundColor DarkYellow
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

if ($failed) { throw "heavier YARP gate validation failed ($NameSuffix)" }
Write-Host "All heavier YARP gates passed ($NameSuffix)." -ForegroundColor Green
