# Emit wiki markdown for compare-product Reverse + MITM tables (median of 3 GHA runs).
param(
    [Parameter(Mandatory)] [string[]] $RunIds,
    [string] $ResultsRoot = 'tools/RpsLoadProbe/results/gha-dl',
    [string] $HeadSha = 'df172718',
    [string] $PrimaryRunId = '33041445371',
    [string] $OutFile = ''
)

$ErrorActionPreference = 'Stop'
if ($RunIds.Count -eq 1 -and $RunIds[0] -match ',') {
    $RunIds = $RunIds[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}
$Steps = 4
$mul = [string][char]0x00D7          # ×
$goldMedal = [char]::ConvertFromUtf32(0x1F947)  # 🥇

function Median([double[]]$vals) {
    if ($vals.Count -eq 0) { return $null }
    $s = $vals | Sort-Object
    return $s[[math]::Floor(($s.Count - 1) / 2)]
}

function Get-ArmMetrics([string]$CsvPath, [string]$Arm) {
    $rows = @(Import-Csv $CsvPath | Where-Object { $_.arm -eq $Arm })
    if ($rows.Count -eq 0) { return $null }
    $sustains = @(); $peaks = @(); $rss = @(); $cpu = @()
    for ($i = 0; $i + $Steps -le $rows.Count; $i += $Steps) {
        $chunk = $rows[$i..($i + $Steps - 1)]
        $c64Ok = $chunk | Where-Object { $_.concurrency -eq '64' -and $_.meets_slo -eq '1' } | Select-Object -Last 1
        if ($c64Ok) {
            $sustains += [double]$c64Ok.rps
            $peaks += [double]$c64Ok.rps
            $rss += [double]$c64Ok.proxy_rss_peak_bytes
            $cpu += [double]$c64Ok.proxy_cpu_avg_pct
            continue
        }
        # SLO miss (e.g. Linux nginx H3): still publish peak @ c=64 with sustain 0.
        $c64Any = $chunk | Where-Object { $_.concurrency -eq '64' } | Select-Object -Last 1
        if ($c64Any) {
            $sustains += 0
            $peaks += [double]$c64Any.rps
            $rss += [double]$c64Any.proxy_rss_peak_bytes
            $cpu += [double]$c64Any.proxy_cpu_avg_pct
        }
    }
    if ($peaks.Count -eq 0) { return $null }
    return @{
        Sustain = Median $sustains
        Peak = Median $peaks
        Rss = Median $rss
        Cpu = Median $cpu
    }
}

function Get-MedianMetrics([string]$OsFolder, [string]$Arm) {
    $s = @(); $p = @(); $r = @(); $c = @()
    foreach ($runId in $RunIds) {
        $dir = Join-Path $ResultsRoot $runId
        $csv = Get-ChildItem "$dir/rps-csv-$OsFolder/*.csv", "$dir/$OsFolder/*.csv" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $csv) { continue }
        $m = Get-ArmMetrics $csv.FullName $Arm
        if ($m) {
            $s += $m.Sustain; $p += $m.Peak; $r += $m.Rss; $c += $m.Cpu
        }
    }
    if ($s.Count -eq 0) { return $null }
    return @{
        Sustain = Median $s; Peak = Median $p; Rss = Median $r; Cpu = Median $c
    }
}

function Format-RpsCell($metrics, [switch]$Medal, [switch]$Peak) {
    if (-not $metrics) { return '*Not measured*' }
    $r = [math]::Round($(if ($Peak) { $metrics.Peak } else { $metrics.Sustain }), 0)
    $mb = [math]::Round($metrics.Rss / 1MB, 0)
    $cpu = [math]::Round($metrics.Cpu, 1)
    $prefix = if ($Medal) { "$goldMedal " } else { '' }
    return ("{0}**{1}**<br><sub>({2} MiB / {3}% CPU)</sub>" -f $prefix, $r, $mb, $cpu)
}

function Format-Impossible([string]$Reason = 'Not possible') {
    return "*$Reason*"
}

$wires = @(
    @{ C='HTTP/1 · plain'; O='HTTP/1 · plain'; Rev='twp-reverse-http1'; Yarp='yarp-reverse-http1'; Nginx='nginx-reverse-http1'; Lite='twp-mitm-http1'; Full='twp-mitm-full-http1' },
    @{ C='HTTP/1 · plain'; O='HTTP/1 · TLS'; Rev='twp-reverse-http1-to-https'; Yarp='yarp-reverse-http1-to-https'; Nginx=$null; Lite='twp-mitm-http1-to-https'; Full='twp-mitm-full-http1-to-https' },
    @{ C='HTTP/1 · plain'; O='HTTP/2 · plain'; Rev='twp-reverse-http1-plain-to-h2c'; Yarp='yarp-reverse-http1-plain-to-h2c'; Nginx=$null; Lite='twp-mitm-http1-plain-to-h2c'; Full='twp-mitm-full-http1-plain-to-h2c' },
    @{ C='HTTP/1 · plain'; O='HTTP/2 · TLS'; Rev='twp-reverse-http1-plain-to-http2'; Yarp='yarp-reverse-http1-plain-to-http2'; Nginx=$null; Lite='twp-mitm-http1-plain-to-http2'; Full='twp-mitm-full-http1-plain-to-http2' },
    @{ C='HTTP/1 · plain'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http1-plain-to-http3'; Yarp='yarp-reverse-http1-plain-to-http3'; Nginx=$null; Lite='twp-mitm-http1-plain-to-http3'; Full='twp-mitm-full-http1-plain-to-http3' },
    @{ C='HTTP/1 · TLS'; O='HTTP/1 · plain'; Rev='twp-reverse-http1-tls'; Yarp='yarp-reverse-http1-tls'; Nginx='nginx-reverse-http1-tls'; Lite='twp-mitm-http1-tls'; Full='twp-mitm-full-http1-tls' },
    @{ C='HTTP/1 · TLS'; O='HTTP/1 · TLS'; Rev='twp-reverse-http1-mitm'; Yarp='yarp-reverse-http1-tls-to-https'; Nginx=$null; Lite='twp-mitm-http1-tls-to-https'; Full='twp-mitm-full-http1-tls-to-https' },
    @{ C='HTTP/1 · TLS'; O='HTTP/2 · plain'; Rev='twp-reverse-http1-to-h2c'; Yarp='yarp-reverse-http1-to-h2c'; Nginx=$null; Lite='twp-mitm-http1-to-h2c'; Full='twp-mitm-full-http1-to-h2c' },
    @{ C='HTTP/1 · TLS'; O='HTTP/2 · TLS'; Rev='twp-reverse-http11-to-http2'; Yarp='yarp-reverse-http11-to-http2'; Nginx=$null; Lite='twp-mitm-http11-to-http2'; Full='twp-mitm-full-http11-to-http2' },
    @{ C='HTTP/1 · TLS'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http1-to-http3'; Yarp='yarp-reverse-http1-to-http3'; Nginx=$null; Lite='twp-mitm-http1-to-http3'; Full='twp-mitm-full-http1-to-http3' },
    @{ C='HTTP/2 · plain'; O='HTTP/1 · plain'; Rev='twp-reverse-h2c-to-h1'; Yarp='yarp-reverse-h2c-to-h1'; Nginx=$null; Lite='twp-mitm-h2c-to-h1'; Full='twp-mitm-full-h2c-to-h1' },
    @{ C='HTTP/2 · plain'; O='HTTP/1 · TLS'; Rev='twp-reverse-h2c-to-https'; Yarp='yarp-reverse-h2c-to-https'; Nginx=$null; Lite='twp-mitm-h2c-to-https'; Full='twp-mitm-full-h2c-to-https' },
    @{ C='HTTP/2 · plain'; O='HTTP/2 · plain'; Rev='twp-reverse-h2c-to-h2c'; Yarp='yarp-reverse-h2c-to-h2c'; Nginx=$null; Lite='twp-mitm-h2c-to-h2c'; Full='twp-mitm-full-h2c-to-h2c' },
    @{ C='HTTP/2 · plain'; O='HTTP/2 · TLS'; Rev='twp-reverse-h2c'; Yarp='yarp-reverse-h2c'; Nginx=$null; Lite='twp-mitm-h2c'; Full='twp-mitm-full-h2c' },
    @{ C='HTTP/2 · plain'; O='HTTP/3 · QUIC'; Rev='twp-reverse-h2c-to-h3'; Yarp='yarp-reverse-h2c-to-h3'; Nginx=$null; Lite='twp-mitm-h2c-to-h3'; Full='twp-mitm-full-h2c-to-h3' },
    @{ C='HTTP/2 · TLS'; O='HTTP/1 · plain'; Rev='twp-reverse-http2-cleartext'; Yarp='yarp-reverse-http2'; Nginx='nginx-reverse-http2'; Lite='twp-mitm-http2-cleartext'; Full='twp-mitm-full-http2-cleartext' },
    @{ C='HTTP/2 · TLS'; O='HTTP/1 · TLS'; Rev='twp-reverse-http2-to-https-http1'; Yarp='yarp-reverse-http2-to-https-http1'; Nginx=$null; Lite='twp-mitm-http2-to-http1'; Full='twp-mitm-full-http2-to-http1' },
    @{ C='HTTP/2 · TLS'; O='HTTP/2 · plain'; Rev='twp-reverse-http2-to-h2c'; Yarp='yarp-reverse-http2-to-h2c'; Nginx=$null; Lite='twp-mitm-http2-to-h2c'; Full='twp-mitm-full-http2-to-h2c' },
    @{ C='HTTP/2 · TLS'; O='HTTP/2 · TLS'; Rev='twp-reverse-http2'; Yarp='yarp-reverse-http2-to-https'; Nginx=$null; Lite='twp-mitm-http2'; Full='twp-mitm-full-http2' },
    @{ C='HTTP/2 · TLS'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http2-to-http3'; Yarp='yarp-reverse-http2-to-http3'; Nginx=$null; Lite='twp-mitm-http2-to-http3'; Full='twp-mitm-full-http2-to-http3' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/1 · plain'; Rev='twp-reverse-http3-cleartext'; Yarp='yarp-reverse-http3-cleartext'; Nginx='nginx-reverse-http3-cleartext'; Lite='twp-mitm-http3-cleartext'; Full='twp-mitm-full-http3-cleartext' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/1 · TLS'; Rev='twp-reverse-http3-to-https-http1'; Yarp='yarp-reverse-http3-to-https-http1'; Nginx=$null; Lite='twp-mitm-http3-to-http1'; Full='twp-mitm-full-http3-to-http1' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/2 · plain'; Rev='twp-reverse-http3-to-h2c'; Yarp='yarp-reverse-http3-to-h2c'; Nginx=$null; Lite='twp-mitm-http3-to-h2c'; Full='twp-mitm-full-http3-to-h2c' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/2 · TLS'; Rev='twp-reverse-http3-to-http2'; Yarp='yarp-reverse-http3-to-http2'; Nginx=$null; Lite='twp-mitm-http3-to-http2'; Full='twp-mitm-full-http3-to-http2' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http3'; Yarp='yarp-reverse-http3-to-http3'; Nginx=$null; Lite='twp-mitm-http3'; Full='twp-mitm-full-http3' }
)

function Emit-ReverseTable([string]$OsFolder) {
    Write-Output '| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |'
    Write-Output '|---|---|---:|---:|---:|---:|---:|---:|'
    foreach ($w in $wires) {
        $twp = Get-MedianMetrics $OsFolder $w.Rev
        $yarp = Get-MedianMetrics $OsFolder $w.Yarp
        $nginx = if ($w.Nginx) { Get-MedianMetrics $OsFolder $w.Nginx } else { $null }
        $candidates = @(@{ M = $twp; K = 'twp' }, @{ M = $yarp; K = 'yarp' })
        if ($nginx -and $nginx.Sustain -gt 0) { $candidates += @{ M = $nginx; K = 'nginx' } }
        $best = ($candidates | Where-Object { $_.M } | Sort-Object { $_.M.Sustain } -Descending | Select-Object -First 1).K
        if ($nginx) {
            $nS = Format-RpsCell $nginx -Medal:($best -eq 'nginx')
            $nP = Format-RpsCell $nginx -Medal:($best -eq 'nginx') -Peak
        }
        elseif ($w.Nginx -eq 'nginx-reverse-http3-cleartext' -or ($w.O -match 'QUIC' -and -not $w.Nginx)) {
            $nS = Format-Impossible 'Not possible (no QUIC)'
            $nP = $nS
        }
        elseif ($w.C -match 'HTTP/3' -and $w.O -match 'HTTP/2') {
            $nS = Format-Impossible 'Not possible (no H3 to H2)'
            $nP = $nS
        }
        else {
            $nS = Format-Impossible
            $nP = $nS
        }
        Write-Output ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |" -f $w.C, $w.O,
            (Format-RpsCell $twp -Medal:($best -eq 'twp')), (Format-RpsCell $twp -Medal:($best -eq 'twp') -Peak),
            $nS, $nP,
            (Format-RpsCell $yarp -Medal:($best -eq 'yarp')), (Format-RpsCell $yarp -Medal:($best -eq 'yarp') -Peak))
    }
}

function Emit-MitmTable([string]$OsFolder) {
    Write-Output '| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |'
    Write-Output '|---|---|---:|---:|---:|---:|'
    foreach ($w in $wires) {
        $rev = Get-MedianMetrics $OsFolder $w.Rev
        $lite = Get-MedianMetrics $OsFolder $w.Lite
        $full = Get-MedianMetrics $OsFolder $w.Full
        if (-not $rev) { continue }
        $lr = if ($lite) { [math]::Round($lite.Sustain / $rev.Sustain, 2) } else { 0 }
        $fr = if ($full) { [math]::Round($full.Sustain / $rev.Sustain, 2) } else { 0 }
        Write-Output ("| {0} | {1} | {2} | {3} | **{4}{5}** | **{6}{5}** |" -f $w.C, $w.O,
            (Format-RpsCell $lite), (Format-RpsCell $full), $lr, $mul, $fr)
    }
}

$lines = [System.Collections.Generic.List[string]]::new()
function Out([string]$s) { [void]$lines.Add($s) }

Out "HEAD_SHA=$HeadSha PRIMARY_RUN=$PrimaryRunId"
Out '---WIN_REVERSE---'
# Emit helpers still Write-Output — capture via scriptblock redirection below.
$script:EmitSink = $lines
function Emit-SinkRedirect {
    param([scriptblock]$Block)
    foreach ($line in (& $Block)) { [void]$script:EmitSink.Add([string]$line) }
}

Emit-SinkRedirect { Emit-ReverseTable 'windows-latest' }
Out '---WIN_MITM---'
Emit-SinkRedirect { Emit-MitmTable 'windows-latest' }
Out '---LIN_REVERSE---'
Emit-SinkRedirect { Emit-ReverseTable 'ubuntu-latest' }
Out '---LIN_MITM---'
Emit-SinkRedirect { Emit-MitmTable 'ubuntu-latest' }

$text = ($lines -join "`n") + "`n"
if ($OutFile) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $full = if ([IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location) $OutFile }
    [IO.File]::WriteAllText($full, $text, $utf8)
    Write-Host "Wrote $full"
}
else {
    Write-Output $text
}
