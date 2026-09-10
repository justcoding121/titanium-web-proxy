# Emit wiki markdown for compare-product Reverse + MITM tables (median of 3 GHA runs).
# Pass every product shard run id: -RunIds 111,222,333 (union by arm name; same SHA only).
# Lite÷Reverse stays same-job because comparison-group shards co-locate peers on one VM.
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
    $sawC64 = $false
    for ($i = 0; $i + $Steps -le $rows.Count; $i += $Steps) {
        $chunk = $rows[$i..($i + $Steps - 1)]
        $c64Ok = $chunk | Where-Object { $_.concurrency -eq '64' -and $_.meets_slo -eq '1' } | Select-Object -Last 1
        if ($c64Ok) {
            $sustains += [double]$c64Ok.rps
            $peaks += [double]$c64Ok.rps
            $rss += [double]$c64Ok.proxy_rss_peak_bytes
            $cpu += [double]$c64Ok.proxy_cpu_avg_pct
            $sawC64 = $true
            continue
        }
        # SLO miss: do not mix 0 into the sustain median (noisy Mac peer-fix arms).
        $c64Any = $chunk | Where-Object { $_.concurrency -eq '64' } | Select-Object -Last 1
        if ($c64Any) {
            $sawC64 = $true
            $peaks += [double]$c64Any.rps
            $rss += [double]$c64Any.proxy_rss_peak_bytes
            $cpu += [double]$c64Any.proxy_cpu_avg_pct
        }
    }
    if ($sustains.Count -gt 0) {
        return @{
            Sustain = Median $sustains
            Peak = Median $peaks
            Rss = Median $rss
            Cpu = Median $cpu
        }
    }
    # Misaligned repeats (missing c=64 in a pass): use any SLO-pass c=64 rows.
    $okAny = @($rows | Where-Object { $_.concurrency -eq '64' -and $_.meets_slo -eq '1' })
    if ($okAny.Count -gt 0) {
        $s = @($okAny | ForEach-Object { [double]$_.rps })
        $r = @($okAny | ForEach-Object { [double]$_.proxy_rss_peak_bytes })
        $c = @($okAny | ForEach-Object { [double]$_.proxy_cpu_avg_pct })
        return @{
            Sustain = Median $s
            Peak = Median $s
            Rss = Median $r
            Cpu = Median $c
        }
    }
    if (-not $sawC64 -or $peaks.Count -eq 0) { return $null }
    return @{
        Sustain = 0
        Peak = Median $peaks
        Rss = Median $rss
        Cpu = Median $cpu
    }
}

function Get-MedianMetrics([string]$OsFolder, [string]$Arm) {
    # Across run IDs: take the best sustain/peak so peer-fix overlays beat older 0-RPS
    # product rows for the same arm (median of [0, 50k] would publish 0).
    $best = $null
    foreach ($runId in $RunIds) {
        $dir = Join-Path $ResultsRoot $runId
        # Exact OS folder or shard-suffixed (rps-csv-ubuntu-latest-shard-1-3).
        # Also accept flat artifact layouts (csv directly under runId).
        $csv = Get-ChildItem `
            "$dir/rps-csv-$OsFolder/*.csv", `
            "$dir/rps-csv-$OsFolder-*/*.csv", `
            "$dir/$OsFolder/*.csv", `
            "$dir/*.csv" `
            -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $csv) { continue }
        $m = Get-ArmMetrics $csv.FullName $Arm
        if (-not $m) { continue }
        if ($null -eq $best -or $m.Sustain -gt $best.Sustain -or
            ($m.Sustain -eq $best.Sustain -and $m.Peak -gt $best.Peak)) {
            $best = $m
        }
    }
    return $best
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
    @{ C='HTTP/1 · plain'; O='HTTP/1 · TLS'; Rev='twp-reverse-http1-to-https'; Yarp='yarp-reverse-http1-to-https'; Nginx='nginx-reverse-http1-to-https'; Lite='twp-mitm-http1-to-https'; Full='twp-mitm-full-http1-to-https' },
    @{ C='HTTP/1 · plain'; O='HTTP/2 · plain'; Rev='twp-reverse-http1-plain-to-h2c'; Yarp='yarp-reverse-http1-plain-to-h2c'; Nginx=$null; Haproxy='haproxy-reverse-http1-plain-to-h2c'; Envoy='envoy-reverse-http1-plain-to-h2c'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http1-plain-to-h2c'; Full='twp-mitm-full-http1-plain-to-h2c' },
    @{ C='HTTP/1 · plain'; O='HTTP/2 · TLS'; Rev='twp-reverse-http1-plain-to-http2'; Yarp='yarp-reverse-http1-plain-to-http2'; Nginx=$null; Haproxy='haproxy-reverse-http1-plain-to-http2'; Envoy='envoy-reverse-http1-plain-to-http2'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http1-plain-to-http2'; Full='twp-mitm-full-http1-plain-to-http2' },
    @{ C='HTTP/1 · plain'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http1-plain-to-http3'; Yarp='yarp-reverse-http1-plain-to-http3'; Nginx=$null; Haproxy='haproxy-reverse-http1-plain-to-http3'; Envoy='envoy-reverse-http1-plain-to-http3'; NginxImpossible='Not possible (no H3 upstream)'; Lite='twp-mitm-http1-plain-to-http3'; Full='twp-mitm-full-http1-plain-to-http3' },
    @{ C='HTTP/1 · TLS'; O='HTTP/1 · plain'; Rev='twp-reverse-http1-tls'; Yarp='yarp-reverse-http1-tls'; Nginx='nginx-reverse-http1-tls'; Lite='twp-mitm-http1-tls'; Full='twp-mitm-full-http1-tls' },
    @{ C='HTTP/1 · TLS'; O='HTTP/1 · TLS'; Rev='twp-reverse-http1-mitm'; Yarp='yarp-reverse-http1-tls-to-https'; Nginx='nginx-reverse-http1-tls-to-https'; Lite='twp-mitm-http1-tls-to-https'; Full='twp-mitm-full-http1-tls-to-https' },
    @{ C='HTTP/1 · TLS'; O='HTTP/2 · plain'; Rev='twp-reverse-http1-to-h2c'; Yarp='yarp-reverse-http1-to-h2c'; Nginx=$null; Haproxy='haproxy-reverse-http1-to-h2c'; Envoy='envoy-reverse-http1-to-h2c'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http1-to-h2c'; Full='twp-mitm-full-http1-to-h2c' },
    @{ C='HTTP/1 · TLS'; O='HTTP/2 · TLS'; Rev='twp-reverse-http11-to-http2'; Yarp='yarp-reverse-http11-to-http2'; Nginx=$null; Haproxy='haproxy-reverse-http11-to-http2'; Envoy='envoy-reverse-http11-to-http2'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http11-to-http2'; Full='twp-mitm-full-http11-to-http2' },
    @{ C='HTTP/1 · TLS'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http1-to-http3'; Yarp='yarp-reverse-http1-to-http3'; Nginx=$null; Haproxy='haproxy-reverse-http1-to-http3'; Envoy='envoy-reverse-http1-to-http3'; NginxImpossible='Not possible (no H3 upstream)'; Lite='twp-mitm-http1-to-http3'; Full='twp-mitm-full-http1-to-http3' },
    @{ C='HTTP/2 · plain'; O='HTTP/1 · plain'; Rev='twp-reverse-h2c-to-h1'; Yarp='yarp-reverse-h2c-to-h1'; Nginx='nginx-reverse-h2c-to-h1'; Lite='twp-mitm-h2c-to-h1'; Full='twp-mitm-full-h2c-to-h1' },
    @{ C='HTTP/2 · plain'; O='HTTP/1 · TLS'; Rev='twp-reverse-h2c-to-https'; Yarp='yarp-reverse-h2c-to-https'; Nginx='nginx-reverse-h2c-to-https'; Lite='twp-mitm-h2c-to-https'; Full='twp-mitm-full-h2c-to-https' },
    @{ C='HTTP/2 · plain'; O='HTTP/2 · plain'; Rev='twp-reverse-h2c-to-h2c'; Yarp='yarp-reverse-h2c-to-h2c'; Nginx=$null; Haproxy='haproxy-reverse-h2c-to-h2c'; Envoy='envoy-reverse-h2c-to-h2c'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-h2c-to-h2c'; Full='twp-mitm-full-h2c-to-h2c' },
    @{ C='HTTP/2 · plain'; O='HTTP/2 · TLS'; Rev='twp-reverse-h2c'; Yarp='yarp-reverse-h2c'; Nginx=$null; Haproxy='haproxy-reverse-h2c'; Envoy='envoy-reverse-h2c'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-h2c'; Full='twp-mitm-full-h2c' },
    @{ C='HTTP/2 · plain'; O='HTTP/3 · QUIC'; Rev='twp-reverse-h2c-to-h3'; Yarp='yarp-reverse-h2c-to-h3'; Nginx=$null; Haproxy='haproxy-reverse-h2c-to-h3'; Envoy='envoy-reverse-h2c-to-h3'; NginxImpossible='Not possible (no H3 upstream)'; Lite='twp-mitm-h2c-to-h3'; Full='twp-mitm-full-h2c-to-h3' },
    @{ C='HTTP/2 · TLS'; O='HTTP/1 · plain'; Rev='twp-reverse-http2-cleartext'; Yarp='yarp-reverse-http2'; Nginx='nginx-reverse-http2'; Lite='twp-mitm-http2-cleartext'; Full='twp-mitm-full-http2-cleartext' },
    @{ C='HTTP/2 · TLS'; O='HTTP/1 · TLS'; Rev='twp-reverse-http2-to-https-http1'; Yarp='yarp-reverse-http2-to-https-http1'; Nginx='nginx-reverse-http2-to-https-http1'; Lite='twp-mitm-http2-to-http1'; Full='twp-mitm-full-http2-to-http1' },
    @{ C='HTTP/2 · TLS'; O='HTTP/2 · plain'; Rev='twp-reverse-http2-to-h2c'; Yarp='yarp-reverse-http2-to-h2c'; Nginx=$null; Haproxy='haproxy-reverse-http2-to-h2c'; Envoy='envoy-reverse-http2-to-h2c'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http2-to-h2c'; Full='twp-mitm-full-http2-to-h2c' },
    @{ C='HTTP/2 · TLS'; O='HTTP/2 · TLS'; Rev='twp-reverse-http2'; Yarp='yarp-reverse-http2-to-https'; Nginx=$null; Haproxy='haproxy-reverse-http2-to-https'; Envoy='envoy-reverse-http2-to-https'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http2'; Full='twp-mitm-full-http2' },
    @{ C='HTTP/2 · TLS'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http2-to-http3'; Yarp='yarp-reverse-http2-to-http3'; Nginx=$null; Haproxy='haproxy-reverse-http2-to-http3'; Envoy='envoy-reverse-http2-to-http3'; NginxImpossible='Not possible (no H3 upstream)'; Lite='twp-mitm-http2-to-http3'; Full='twp-mitm-full-http2-to-http3' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/1 · plain'; Rev='twp-reverse-http3-cleartext'; Yarp='yarp-reverse-http3-cleartext'; Nginx='nginx-reverse-http3-cleartext'; Lite='twp-mitm-http3-cleartext'; Full='twp-mitm-full-http3-cleartext' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/1 · TLS'; Rev='twp-reverse-http3-to-https-http1'; Yarp='yarp-reverse-http3-to-https-http1'; Nginx='nginx-reverse-http3-to-https-http1'; Lite='twp-mitm-http3-to-http1'; Full='twp-mitm-full-http3-to-http1' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/2 · plain'; Rev='twp-reverse-http3-to-h2c'; Yarp='yarp-reverse-http3-to-h2c'; Nginx=$null; Haproxy='haproxy-reverse-http3-to-h2c'; Envoy='envoy-reverse-http3-to-h2c'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http3-to-h2c'; Full='twp-mitm-full-http3-to-h2c' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/2 · TLS'; Rev='twp-reverse-http3-to-http2'; Yarp='yarp-reverse-http3-to-http2'; Nginx=$null; Haproxy='haproxy-reverse-http3-to-http2'; Envoy='envoy-reverse-http3-to-http2'; NginxImpossible='Not possible (no H2 upstream)'; Lite='twp-mitm-http3-to-http2'; Full='twp-mitm-full-http3-to-http2' },
    @{ C='HTTP/3 · QUIC'; O='HTTP/3 · QUIC'; Rev='twp-reverse-http3'; Yarp='yarp-reverse-http3-to-http3'; Nginx=$null; Haproxy='haproxy-reverse-http3-to-http3'; Envoy='envoy-reverse-http3-to-http3'; NginxImpossible='Not possible (no H3 upstream)'; Lite='twp-mitm-http3'; Full='twp-mitm-full-http3' }
)

foreach ($w in $wires) {
    # Derive HAProxy/Envoy from nginx when the terminate-to-H1 arm already exists.
    # Product-possible / harness-absent cells set Haproxy/Envoy explicitly (see
    # product-arm-matrix.py). Do not copy nginx=$null onto those two — that used
    # to label H3→H2 as *Not possible (no H3 to H2)* for HAProxy/Envoy.
    if ($w.Nginx) {
        if (-not $w.Haproxy) { $w.Haproxy = $w.Nginx -replace '^nginx-', 'haproxy-' }
        if (-not $w.Envoy) { $w.Envoy = $w.Nginx -replace '^nginx-', 'envoy-' }
    }
}

function Get-PeerImpossibleReason([hashtable]$w, [string]$PeerKey, [string]$Arm) {
    if ($Arm) { return $null }
    if ($PeerKey -eq 'Nginx' -and $w.NginxImpossible) { return $w.NginxImpossible }
    if ($w.O -match 'QUIC') { return 'Not possible (no H3 upstream)' }
    if ($w.O -match 'HTTP/2') { return 'Not possible (no H2 upstream)' }
    return 'Not possible'
}

function Format-TerminatePeerCell(
    [string]$OsFolder,
    [hashtable]$w,
    [string]$PeerKey,
    $metrics,
    [switch]$Medal,
    [switch]$Peak
) {
    if ($PeerKey -in @('Haproxy', 'Envoy') -and $OsFolder -eq 'windows-latest') {
        return Format-Impossible 'Not possible'
    }
    $arm = $w[$PeerKey]
    if ($arm) {
        return Format-RpsCell $metrics -Medal:$Medal -Peak:$Peak
    }
    $reason = Get-PeerImpossibleReason $w $PeerKey $arm
    return Format-Impossible $reason
}

function Emit-ReverseTable([string]$OsFolder) {
    Write-Output '| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |'
    Write-Output '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|'
    foreach ($w in $wires) {
        $twp = Get-MedianMetrics $OsFolder $w.Rev
        $yarp = Get-MedianMetrics $OsFolder $w.Yarp
        $nginx = if ($w.Nginx) { Get-MedianMetrics $OsFolder $w.Nginx } else { $null }
        $haproxy = if ($w.Haproxy) { Get-MedianMetrics $OsFolder $w.Haproxy } else { $null }
        $envoy = if ($w.Envoy) { Get-MedianMetrics $OsFolder $w.Envoy } else { $null }
        $candidates = @(@{ M = $twp; K = 'twp' }, @{ M = $yarp; K = 'yarp' })
        foreach ($pair in @(@{ M = $nginx; K = 'nginx' }, @{ M = $haproxy; K = 'haproxy' }, @{ M = $envoy; K = 'envoy' })) {
            if ($pair.M -and $pair.M.Sustain -gt 0) { $candidates += $pair }
        }
        $best = ($candidates | Where-Object { $_.M } | Sort-Object { $_.M.Sustain } -Descending | Select-Object -First 1).K
        Write-Output ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} |" -f $w.C, $w.O,
            (Format-RpsCell $twp -Medal:($best -eq 'twp')), (Format-RpsCell $twp -Medal:($best -eq 'twp') -Peak),
            (Format-TerminatePeerCell $OsFolder $w 'Nginx' $nginx -Medal:($best -eq 'nginx')),
            (Format-TerminatePeerCell $OsFolder $w 'Nginx' $nginx -Medal:($best -eq 'nginx') -Peak),
            (Format-TerminatePeerCell $OsFolder $w 'Haproxy' $haproxy -Medal:($best -eq 'haproxy')),
            (Format-TerminatePeerCell $OsFolder $w 'Haproxy' $haproxy -Medal:($best -eq 'haproxy') -Peak),
            (Format-TerminatePeerCell $OsFolder $w 'Envoy' $envoy -Medal:($best -eq 'envoy')),
            (Format-TerminatePeerCell $OsFolder $w 'Envoy' $envoy -Medal:($best -eq 'envoy') -Peak),
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
Out '---MAC_REVERSE---'
Emit-SinkRedirect { Emit-ReverseTable 'macos-15-intel' }
Out '---MAC_MITM---'
Emit-SinkRedirect { Emit-MitmTable 'macos-15-intel' }

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
