# Validate compare-lossy / compare-arch against the hard bar:
#   - Every TWP arm present in the CSV must have sustain RPS > 0 (best SLO-pass step, else peak)
#   - When YARP has an SLO-pass sustain > 0 at the same concurrency band, TWP÷YARP >= 0.50
#   - SKIP ratio when YARP sustain is 0 / absent (still FAIL if TWP is 0)
# Pair list mirrors RampOrchestrator.HeavierReverseArms + BuildArchArms duplex.
param(
    [Parameter(Mandatory)] [string] $CsvPath,
    [Parameter(Mandatory)]
    [ValidateSet('lossy', 'arch')]
    [string] $Suite,
    [double] $ReverseYarpGate = 0.50
)

$ErrorActionPreference = 'Stop'

function Get-Median([double[]] $values) {
    if ($values.Count -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    $mid = [int][math]::Floor(($sorted.Count - 1) / 2)
    if ($sorted.Count % 2 -eq 0 -and $sorted.Count -ge 2) {
        return ($sorted[$mid] + $sorted[$mid + 1]) / 2
    }
    return $sorted[$mid]
}

$rows = @(Import-Csv $CsvPath)

# Prefer c=64 SLO-pass medians; fall back to best SLO-pass concurrency, then any-row peak.
function Get-ArmSustain([string] $arm) {
    $slo64 = @($rows | Where-Object { [string]$_.arm -eq $arm -and [string]$_.concurrency -eq '64' -and $_.meets_slo -eq '1' } | ForEach-Object { [double]$_.rps })
    $m64 = Get-Median $slo64
    if ($null -ne $m64) { return @{ Value = $m64; Band = 'c64-slo' } }

    $sloAny = @($rows | Where-Object { [string]$_.arm -eq $arm -and $_.meets_slo -eq '1' })
    if ($sloAny.Count -gt 0) {
        $bestC = ($sloAny | ForEach-Object { [int]$_.concurrency } | Measure-Object -Maximum).Maximum
        $atBest = @($sloAny | Where-Object { [int]$_.concurrency -eq $bestC } | ForEach-Object { [double]$_.rps })
        return @{ Value = (Get-Median $atBest); Band = "c$bestC-slo" }
    }

    $any = @($rows | Where-Object { [string]$_.arm -eq $arm } | ForEach-Object { [double]$_.rps })
    if ($any.Count -eq 0) { return $null }
    return @{ Value = (Get-Median $any); Band = 'peak-any' }
}

function New-Pairs([string[]] $suffixes) {
    $list = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($suffix in $suffixes) {
        $list.Add(@{ Label = "H1 TLS->H1 plain ($suffix)"; Twp = "twp-reverse-http1-tls-$suffix"; Yarp = "yarp-reverse-http1-tls-$suffix" })
        $list.Add(@{ Label = "H2 TLS->H1 plain ($suffix)"; Twp = "twp-reverse-http2-cleartext-$suffix"; Yarp = "yarp-reverse-http2-$suffix" })
        $list.Add(@{ Label = "H3->H1 plain ($suffix)"; Twp = "twp-reverse-http3-cleartext-$suffix"; Yarp = "yarp-reverse-http3-cleartext-$suffix" })
        $list.Add(@{ Label = "H2c->H1 plain ($suffix)"; Twp = "twp-reverse-h2c-to-h1-$suffix"; Yarp = "yarp-reverse-h2c-to-h1-$suffix" })
        $list.Add(@{ Label = "H2 TLS->h2c ($suffix)"; Twp = "twp-reverse-http2-to-h2c-$suffix"; Yarp = "yarp-reverse-http2-to-h2c-$suffix" })
        $list.Add(@{ Label = "H2 TLS->H2 TLS ($suffix)"; Twp = "twp-reverse-http2-to-https-$suffix"; Yarp = "yarp-reverse-http2-to-https-$suffix" })
        $list.Add(@{ Label = "H3->H2 TLS ($suffix)"; Twp = "twp-reverse-http3-to-http2-$suffix"; Yarp = "yarp-reverse-http3-to-http2-$suffix" })
        $list.Add(@{ Label = "H3->H1 TLS ($suffix)"; Twp = "twp-reverse-http3-to-https-http1-$suffix"; Yarp = "yarp-reverse-http3-to-https-http1-$suffix" })
    }
    # Unary comma: PowerShell unwraps IEnumerable on return otherwise (fixed-size Object[]).
    return ,$list
}

$pairs = [System.Collections.Generic.List[hashtable]]::new()
switch ($Suite) {
    'lossy' {
        foreach ($item in (New-Pairs @('lossy'))) { $pairs.Add($item) }
    }
    'arch' {
        foreach ($item in (New-Pairs @('slow256k', 'early64k'))) { $pairs.Add($item) }
        $pairs.Add(@{ Label = 'Duplex H2 TLS->H2 TLS'; Twp = 'twp-reverse-http2-duplex-h2'; Yarp = 'yarp-reverse-http2-to-https-duplex-h2' })
        $pairs.Add(@{ Label = 'Duplex WebSocket H1 TLS'; Twp = 'twp-reverse-http1-tls-duplex-ws'; Yarp = 'yarp-reverse-http1-tls-duplex-ws' })
    }
}

$failed = $false
Write-Host "Lossy/arch hard-bar gates ($Suite; TWP>0; TWP/YARP >= $ReverseYarpGate when YARP>0)" -ForegroundColor Cyan

foreach ($p in $pairs) {
    $twp = Get-ArmSustain $p.Twp
    $yarp = Get-ArmSustain $p.Yarp

    if ($null -eq $twp -and $null -eq $yarp) {
        Write-Host "SKIP $($p.Label) : not in this shard/CSV" -ForegroundColor DarkYellow
        continue
    }

    if ($null -eq $twp -or $twp.Value -le 0) {
        Write-Host "FAIL $($p.Label) : TWP sustain/peak <= 0 ($($p.Twp))" -ForegroundColor Red
        $failed = $true
        continue
    }

    Write-Host ("OK   {0} : TWP={1:N1} ({2})" -f $p.Label, $twp.Value, $twp.Band) -ForegroundColor Green

    if ($null -eq $yarp -or $yarp.Value -le 0) {
        Write-Host "SKIP $($p.Label) : YARP sustain/peak <= 0 (ratio N/A; TWP>0 holds)" -ForegroundColor DarkYellow
        continue
    }

    $ratio = $twp.Value / $yarp.Value
    $ok = $ratio -ge $ReverseYarpGate
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0} TWP/YARP = {1:N3} (gate {2:N2}; YARP={3:N1} {4})" -f $(if ($ok) { 'OK  ' } else { 'FAIL' }), $ratio, $ReverseYarpGate, $yarp.Value, $yarp.Band) -ForegroundColor $color
    if (-not $ok) { $failed = $true }
}

if ($failed) { throw "lossy/arch hard-bar gate validation failed ($Suite)" }
Write-Host "All lossy/arch hard-bar gates passed ($Suite)." -ForegroundColor Green
