# PR2 local spot gate: compare-spot @ c=64, validate Full÷Reverse >= 0.70 and reverse TWP÷YARP >= 0.95.
[CmdletBinding()]
param(
    [int] $Concurrency = 64,
    [int] $WarmupSec = 2,
    [int] $DurationSec = 8,
    [double] $MitmRatioGate = 0.70,
    [double] $ReverseYarpGate = 0.95,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
$resultsDir = Join-Path $scriptDir 'results/spot-matrix'

& (Join-Path $scriptDir 'run-rps.ps1') `
    -Mode compare-spot `
    -Concurrency $Concurrency `
    -WarmupSec $WarmupSec `
    -DurationSec $DurationSec `
    -Repeats 1 `
    -ResultsDir $resultsDir `
    -SkipBuild:$SkipBuild

$csv = Get-ChildItem $resultsDir -Filter 'rps-ramp-*.csv' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $csv) { throw "No CSV under $resultsDir" }

$rows = Import-Csv $csv.FullName
$rpsAtC = @{}
foreach ($row in $rows) {
    if ([int]$row.concurrency -ne $Concurrency) { continue }
    if ($row.meets_slo -ne '1') { continue }
    $rpsAtC[$row.arm] = [double]$row.rps
}

function Get-Ratio([string]$NumeratorArm, [string]$DenominatorArm) {
    if (-not $rpsAtC.ContainsKey($NumeratorArm)) {
        return $null
    }
    if (-not $rpsAtC.ContainsKey($DenominatorArm)) {
        return $null
    }
    $den = $rpsAtC[$DenominatorArm]
    if ($den -le 0) { return $null }
    return $rpsAtC[$NumeratorArm] / $den
}

$mitmPairs = @(
    @{ Label = 'H3→H1 plain'; Full = 'twp-mitm-full-http3-cleartext'; Reverse = 'twp-reverse-http3-cleartext' },
    @{ Label = 'H3→H1 TLS'; Full = 'twp-mitm-full-http3-to-http1'; Reverse = 'twp-reverse-http3-to-https-http1' },
    @{ Label = 'H3→H3'; Full = 'twp-mitm-full-http3'; Reverse = 'twp-reverse-http3' },
    @{ Label = 'H1 plain'; Full = 'twp-mitm-full-http1'; Reverse = 'twp-reverse-http1' },
    @{ Label = 'H2 h2c→h2c'; Full = 'twp-mitm-full-h2c-to-h2c'; Reverse = 'twp-reverse-h2c-to-h2c' },
    @{ Label = 'H2 TLS→h2c'; Full = 'twp-mitm-full-http2-to-h2c'; Reverse = 'twp-reverse-http2-to-h2c' },
    @{ Label = 'H2 plain'; Full = 'twp-mitm-full-http2-cleartext'; Reverse = 'twp-reverse-http2-cleartext' },
    @{ Label = 'H2 TLS'; Full = 'twp-mitm-full-http2'; Reverse = 'twp-reverse-http2' }
)

$reversePairs = @(
    @{ Label = 'Reverse H3→H1 TWP÷YARP'; Twp = 'twp-reverse-http3-to-https-http1'; Yarp = 'yarp-reverse-http3-to-https-http1' },
    @{ Label = 'Reverse H3→H3 TWP÷YARP'; Twp = 'twp-reverse-http3'; Yarp = 'yarp-reverse-http3-to-http3' }
)

$failed = $false
Write-Host ''
Write-Host "Spot matrix ($($csv.Name) @ c=$Concurrency)" -ForegroundColor Cyan

foreach ($pair in $mitmPairs) {
    $ratio = Get-Ratio $pair.Full $pair.Reverse
    if ($null -eq $ratio) {
        Write-Host ("FAIL {0}: missing arm data" -f $pair.Label) -ForegroundColor Red
        $failed = $true
        continue
    }
    $ok = $ratio -ge $MitmRatioGate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} Full÷Reverse = {1:N3} (gate {2:N2})" -f $pair.Label, $ratio, $MitmRatioGate) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

foreach ($pair in $reversePairs) {
    $ratio = Get-Ratio $pair.Twp $pair.Yarp
    if ($null -eq $ratio) {
        Write-Host ("FAIL {0}: missing arm data" -f $pair.Label) -ForegroundColor Red
        $failed = $true
        continue
    }
    $ok = $ratio -ge $ReverseYarpGate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} = {1:N3} (gate {2:N2})" -f $pair.Label, $ratio, $ReverseYarpGate) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) {
    throw 'Spot matrix gate failed'
}

Write-Host 'Spot matrix: all gates passed.' -ForegroundColor Green
