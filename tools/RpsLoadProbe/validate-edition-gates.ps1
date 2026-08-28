# Validate compare-editions medians at c=64 against edition ratio gates.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [double] $CliLibraryGate = 0.80,
    [double] $RouteGate = 0.98,
    [double] $PlusBaseGate = 0.95,
    [double] $PlusCacheGate = 0.70,
    [double] $InterceptGate = 0.65
)

$ErrorActionPreference = 'Stop'
$rows = Import-Csv $CsvPath
$sustain = @{}
foreach ($row in $rows) {
    if ([string]$row.concurrency -ne '64') { continue }
    if ($row.meets_slo -ne '1') { continue }
    $sustain[$row.arm] = [double]$row.rps
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
    @{ Label = 'CLI+Intercept ÷ CLI'; Num = 'twp-cli-intercept-http1'; Den = 'twp-cli-reverse-http1'; Gate = $InterceptGate }
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
