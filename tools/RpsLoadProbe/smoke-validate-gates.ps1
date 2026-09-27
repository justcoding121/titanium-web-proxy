# Smoke-test post-ramp gate scripts with synthetic CSVs (seconds, not hours).
# Catches PowerShell/runtime bugs (e.g. List unwrap → fixed-size .Add) before GHA burns a full ramp.
# Usage: pwsh tools/RpsLoadProbe/smoke-validate-gates.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("twp-gate-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

$header = 'timestamp_utc,arm,generator,concurrency,duration_s,ok,errors,rps,error_rate_pct,p50_ms,p99_ms,max_ms,meets_slo,nginx_version,haproxy_version,envoy_version,yarp_version,http_versions,max_cached_connections,method,response_bytes,request_bytes,delay_ms,loss_percent,keepalive,proxy_rss_peak_bytes,proxy_cpu_avg_pct'

function New-Row([string] $arm, [int] $concurrency, [double] $rps, [int] $meetsSlo = 1) {
    $ts = [DateTime]::UtcNow.ToString('o')
    return "$ts,$arm,dotnet-httpclient,$concurrency,8.0,100,0,$rps,0.0000,1.0,2.0,3.0,$meetsSlo,,,,,,2.0,,GET,65536,0,0,0.00,1,,"
}

function Write-Fixture([string] $path, [string[]] $rows) {
    @(,$header) + $rows | Set-Content -Path $path -Encoding utf8
}

try {
    Write-Host '=== smoke: validate-lossy-arch-gates (lossy + arch) ===' -ForegroundColor Cyan
    $lossyCsv = Join-Path $tmp 'lossy.csv'
    Write-Fixture $lossyCsv @(
        (New-Row 'twp-reverse-http3-cleartext-lossy' 64 400),
        (New-Row 'yarp-reverse-http3-cleartext-lossy' 64 500),
        (New-Row 'twp-reverse-http2-to-https-lossy' 64 100),
        (New-Row 'yarp-reverse-http2-to-https-lossy' 64 120)
    )
    & pwsh -NoProfile -File (Join-Path $root 'validate-lossy-arch-gates.ps1') -CsvPath $lossyCsv -Suite lossy
    if ($LASTEXITCODE -ne 0) { throw "lossy gate smoke failed (exit $LASTEXITCODE)" }

    $archCsv = Join-Path $tmp 'arch.csv'
    Write-Fixture $archCsv @(
        (New-Row 'twp-reverse-http2-to-https-slow256k' 64 50),
        (New-Row 'yarp-reverse-http2-to-https-slow256k' 64 55),
        (New-Row 'twp-reverse-http2-to-https-early64k' 64 200),
        (New-Row 'yarp-reverse-http2-to-https-early64k' 64 180),
        (New-Row 'twp-reverse-http2-duplex-h2' 32 10),
        (New-Row 'yarp-reverse-http2-to-https-duplex-h2' 32 0 0),
        (New-Row 'twp-reverse-http1-tls-duplex-ws' 64 1000),
        (New-Row 'yarp-reverse-http1-tls-duplex-ws' 64 900)
    )
    & pwsh -NoProfile -File (Join-Path $root 'validate-lossy-arch-gates.ps1') -CsvPath $archCsv -Suite arch
    if ($LASTEXITCODE -ne 0) { throw "arch gate smoke failed (exit $LASTEXITCODE)" }

    Write-Host '=== smoke: validate-heavier-yarp-gates ===' -ForegroundColor Cyan
    $bodiesCsv = Join-Path $tmp 'bodies.csv'
    Write-Fixture $bodiesCsv @(
        (New-Row 'twp-reverse-http1-tls-body64k' 64 9000),
        (New-Row 'yarp-reverse-http1-tls-body64k' 64 8000),
        (New-Row 'twp-reverse-http2-to-https-body256k' 64 1000),
        (New-Row 'yarp-reverse-http2-to-https-body256k' 64 1100)
    )
    & pwsh -NoProfile -File (Join-Path $root 'validate-heavier-yarp-gates.ps1') -CsvPath $bodiesCsv -NameSuffix body64k
    if ($LASTEXITCODE -ne 0) { throw "heavier body64k smoke failed (exit $LASTEXITCODE)" }
    & pwsh -NoProfile -File (Join-Path $root 'validate-heavier-yarp-gates.ps1') -CsvPath $bodiesCsv -NameSuffix body256k
    if ($LASTEXITCODE -ne 0) { throw "heavier body256k smoke failed (exit $LASTEXITCODE)" }

    Write-Host '=== smoke: validate-grpc-yarp-gates (parse-only / skip-ok) ===' -ForegroundColor Cyan
    $grpcCsv = Join-Path $tmp 'grpc.csv'
    # Empty-ish: script should SKIP pairs not present, not crash.
    Write-Fixture $grpcCsv @((New-Row 'twp-grpc-placeholder' 64 1))
    & pwsh -NoProfile -File (Join-Path $root 'validate-grpc-yarp-gates.ps1') -CsvPath $grpcCsv
    if ($LASTEXITCODE -ne 0) { throw "grpc gate smoke failed (exit $LASTEXITCODE)" }

    Write-Host 'All post-ramp gate smokes passed.' -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
