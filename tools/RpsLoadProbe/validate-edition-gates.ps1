# Validate compare-editions medians at c=64 against edition ratio gates.
# Prefer SLO-passing c=64 rows. When an arm ran but missed p99 SLO at c=64, still compute the
# ratio from that c=64 row so failures are real ratio misses — not "missing arm data".
# Truly absent CSV arms still FAIL as missing.
# Non-YARP edition floors are 0.50 (runner noise on Plus/CLI feature arms).
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $CliLibraryGate = 0.50,
    [double] $RouteGate = 0.50,
    [double] $PlusBaseGate = 0.50,
    [double] $PlusCacheGate = 0.50,
    [double] $InterceptGate = 0.50,
    [double] $PlusWafGate = 0.50,
    [double] $PlusCidrGate = 0.50,
    [double] $PlusJwtGate = 0.50,
    [double] $PlusRateLimitGate = 0.50,
    [double] $PlusResilienceGate = 0.50,
    [double] $PlusDiscoveryGate = 0.50,
    [double] $PlusMetricsScrapeGate = 0.50,
    [double] $PlusCacheHitGate = 0.50,
    [double] $StaticGate = 0.50,
    [double] $LoggingGate = 0.50,
    [double] $LbLeastTimeGate = 0.50,
    [double] $DialectTwpGate = 0.50
)

$ErrorActionPreference = 'Stop'
$rows = Import-Csv $CsvPath

$present = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$byArmSlo = @{}
$byArmAny = @{}
$c64Meta = @{} # arm -> @{ p99; meets_slo; rps } last c=64 row
$bestAny = @{} # arm -> @{ concurrency; rps; p99; meets_slo } highest concurrency row
foreach ($row in $rows) {
    $arm = [string]$row.arm
    if ([string]::IsNullOrWhiteSpace($arm)) { continue }
    [void]$present.Add($arm)

    $conc = 0
    [void][int]::TryParse([string]$row.concurrency, [ref]$conc)
    $rps = [double]$row.rps
    $p99 = 0.0
    [void][double]::TryParse([string]$row.p99_ms, [ref]$p99)
    if (-not $bestAny.ContainsKey($arm) -or $conc -ge [int]$bestAny[$arm].concurrency) {
        $bestAny[$arm] = @{
            concurrency = $conc
            rps = $rps
            p99 = $p99
            meets_slo = [string]$row.meets_slo
        }
    }

    if ($conc -ne 64) { continue }

    $c64Meta[$arm] = @{
        p99 = $p99
        meets_slo = [string]$row.meets_slo
        rps = $rps
    }

    if (-not $byArmAny.ContainsKey($arm)) {
        $byArmAny[$arm] = [System.Collections.Generic.List[double]]::new()
    }
    $byArmAny[$arm].Add($rps)

    if ($row.meets_slo -ne '1') { continue }
    if (-not $byArmSlo.ContainsKey($arm)) {
        $byArmSlo[$arm] = [System.Collections.Generic.List[double]]::new()
    }
    $byArmSlo[$arm].Add($rps)
}

function Get-Median([System.Collections.Generic.List[double]]$Values) {
    $sorted = @($Values | Sort-Object)
    $mid = [int][math]::Floor(($sorted.Count - 1) / 2)
    if ($sorted.Count % 2 -eq 0 -and $sorted.Count -ge 2) {
        return ($sorted[$mid] + $sorted[$mid + 1]) / 2
    }
    return $sorted[$mid]
}

$sustainSlo = @{}
foreach ($arm in $byArmSlo.Keys) {
    $sustainSlo[$arm] = Get-Median $byArmSlo[$arm]
}
$sustainAny = @{}
foreach ($arm in $byArmAny.Keys) {
    $sustainAny[$arm] = Get-Median $byArmAny[$arm]
}

function Get-ArmRps([string]$Arm) {
    # Returns @{ Ok; Rps; UsedSloFail; Detail }
    if ($sustainSlo.ContainsKey($Arm)) {
        return @{ Ok = $true; Rps = [double]$sustainSlo[$Arm]; UsedSloFail = $false; Detail = $null }
    }
    if ($sustainAny.ContainsKey($Arm)) {
        $meta = $c64Meta[$Arm]
        $detail = ("c=64 SLO-fail p99={0:N1}ms rps={1:N0}" -f $meta.p99, $meta.rps)
        return @{ Ok = $true; Rps = [double]$sustainAny[$Arm]; UsedSloFail = $true; Detail = $detail }
    }
    if ($bestAny.ContainsKey($Arm)) {
        $b = $bestAny[$Arm]
        $detail = ("no c=64 row; using c={0} rps={1:N0} p99={2:N1}ms meets_slo={3}" -f `
            $b.concurrency, $b.rps, $b.p99, $b.meets_slo)
        return @{ Ok = $true; Rps = [double]$b.rps; UsedSloFail = $true; Detail = $detail }
    }
    return @{ Ok = $false; Rps = 0; UsedSloFail = $false; Detail = 'arm absent from CSV' }
}

function Get-RatioInfo([string]$Num, [string]$Den) {
    $n = Get-ArmRps $Num
    $d = Get-ArmRps $Den
    if (-not $n.Ok -or -not $d.Ok -or $d.Rps -le 0) {
        $parts = @()
        if (-not $n.Ok) { $parts += ("{0}: {1}" -f $Num, $n.Detail) }
        if (-not $d.Ok) { $parts += ("{0}: {1}" -f $Den, $d.Detail) }
        if ($d.Ok -and $d.Rps -le 0) { $parts += ("{0}: rps<=0" -f $Den) }
        return @{ Ratio = $null; Note = ($parts -join '; ') }
    }
    $notes = @()
    if ($n.UsedSloFail) { $notes += ("num {0}" -f $n.Detail) }
    if ($d.UsedSloFail) { $notes += ("den {0}" -f $d.Detail) }
    return @{
        Ratio = $n.Rps / $d.Rps
        Note = if ($notes.Count -gt 0) { ($notes -join '; ') } else { $null }
    }
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
    $info = Get-RatioInfo $p.Num $p.Den
    if ($null -eq $info.Ratio) {
        Write-Host ("FAIL {0}: cannot compute ratio ({1})" -f $p.Label, $info.Note) -ForegroundColor Red
        $failed = $true
        continue
    }
    $ok = $info.Ratio -ge $p.Gate
    $color = if ($ok) { 'Green' } else { 'Red' }
    $suffix = if ($info.Note) { " [{0}]" -f $info.Note } else { '' }
    $prefix = if ($ok) { '' } else { 'FAIL ' }
    Write-Host ("{0}{1} = {2:N3} (gate {3:N2}){4}" -f $prefix, $p.Label, $info.Ratio, $p.Gate, $suffix) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw 'compare-editions gate validation failed' }
Write-Host 'All edition gates passed.' -ForegroundColor Green
