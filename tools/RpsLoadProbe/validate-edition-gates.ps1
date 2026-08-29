# Validate compare-editions medians at c=64 against edition ratio gates.
# Missing arms FAIL (do not silently skip). Thresholds are first-run estimates — lock after clean Win+Linux.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $CliLibraryGate = 0.80,
    [double] $RouteGate = 0.90,
    [double] $PlusBaseGate = 0.90,
    [double] $PlusCacheGate = 0.60,
    [double] $InterceptGate = 0.65,
    [double] $PlusWafGate = 0.70,
    [double] $PlusCidrGate = 0.70,
    [double] $PlusJwtGate = 0.45,
    [double] $PlusRateLimitGate = 0.70,
    [double] $PlusResilienceGate = 0.65,
    [double] $PlusDiscoveryGate = 0.70,
    [double] $PlusMetricsScrapeGate = 0.70,
    [double] $PlusCacheHitGate = 0.90,
    [double] $StaticGate = 0.85,
    [double] $LoggingGate = 0.90,
    [double] $LbLeastTimeGate = 0.85,
    [double] $DialectTwpGate = 0.90
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

function Get-Ratio([string]$Num, [string]$Den) {
    if (-not $sustain.ContainsKey($Num) -or -not $sustain.ContainsKey($Den) -or $sustain[$Den] -le 0) {
        return $null
    }
    return $sustain[$Num] / $sustain[$Den]
}

$pairs = @(
    @{ Label = 'CLI H1 ÷ Library H1'; Num = 'twp-cli-reverse-http1'; Den = 'twp-reverse-http1'; Gate = $CliLibraryGate },
    @{ Label = 'CLI H1 TLS ÷ Library H1 TLS'; Num = 'twp-cli-reverse-http1-tls'; Den = 'twp-reverse-http1-tls'; Gate = $CliLibraryGate },
    @{ Label = 'CLI route ÷ CLI ForwardHost'; Num = 'twp-cli-reverse-http1-route'; Den = 'twp-cli-reverse-http1'; Gate = $RouteGate },
    @{ Label = 'CLI+Plus-base ÷ CLI'; Num = 'twp-cli-plus-base-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusBaseGate },
    @{ Label = 'CLI+Plus-cache ÷ CLI'; Num = 'twp-cli-plus-cache-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusCacheGate },
    @{ Label = 'CLI+Intercept ÷ CLI'; Num = 'twp-cli-intercept-http1'; Den = 'twp-cli-reverse-http1'; Gate = $InterceptGate },
    @{ Label = 'CLI+Plus-waf ÷ CLI'; Num = 'twp-cli-plus-waf-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusWafGate },
    @{ Label = 'CLI+Plus-cidr ÷ CLI'; Num = 'twp-cli-plus-cidr-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusCidrGate },
    @{ Label = 'CLI+Plus-jwt ÷ CLI'; Num = 'twp-cli-plus-jwt-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusJwtGate },
    @{ Label = 'CLI+Plus-ratelimit ÷ CLI'; Num = 'twp-cli-plus-ratelimit-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusRateLimitGate },
    @{ Label = 'CLI+Plus-resilience ÷ CLI'; Num = 'twp-cli-plus-resilience-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusResilienceGate },
    @{ Label = 'CLI+Plus-discovery-file ÷ CLI'; Num = 'twp-cli-plus-discovery-file-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusDiscoveryGate },
    # Metrics-scrape vs CLI (not Plus-base): sequential-arm heat makes Plus-base÷scrape ratios noisy.
    @{ Label = 'CLI+Plus-metrics-scrape ÷ CLI'; Num = 'twp-cli-plus-metrics-scrape-http1'; Den = 'twp-cli-reverse-http1'; Gate = $PlusMetricsScrapeGate },
    @{ Label = 'CLI+Plus-cache-hit ÷ Plus-cache cold'; Num = 'twp-cli-plus-cache-hit-http1'; Den = 'twp-cli-plus-cache-http1'; Gate = $PlusCacheHitGate },
    @{ Label = 'CLI static ÷ CLI'; Num = 'twp-cli-static-http1'; Den = 'twp-cli-reverse-http1'; Gate = $StaticGate },
    @{ Label = 'CLI logging ÷ CLI'; Num = 'twp-cli-logging-http1'; Den = 'twp-cli-reverse-http1'; Gate = $LoggingGate },
    @{ Label = 'CLI lb-leasttime ÷ CLI route'; Num = 'twp-cli-lb-leasttime-http1'; Den = 'twp-cli-reverse-http1-route'; Gate = $LbLeastTimeGate },
    @{ Label = 'CLI dialect .twp ÷ CLI'; Num = 'twp-cli-dialect-twp-http1'; Den = 'twp-cli-reverse-http1'; Gate = $DialectTwpGate }
)

$failed = $false
Write-Host "Edition gates @ c=64 ($([IO.Path]::GetFileName($CsvPath)))" -ForegroundColor Cyan
foreach ($p in $pairs) {
    $ratio = Get-Ratio $p.Num $p.Den
    if ($null -eq $ratio) {
        Write-Host ("FAIL {0}: missing arm data (need {1} and {2})" -f $p.Label, $p.Num, $p.Den) -ForegroundColor Red
        $failed = $true
        continue
    }
    $ok = $ratio -ge $p.Gate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} = {1:N3} (gate {2:N2})" -f $p.Label, $ratio, $p.Gate) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw 'compare-editions gate validation failed' }
Write-Host 'All edition gates passed.' -ForegroundColor Green
