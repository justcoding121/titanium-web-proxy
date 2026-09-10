# Local asserts: comparison-group shards are exclusive, complete, and keep gate pairs atomic.
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$probeDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $probeDir
try {
    dotnet build -c $Configuration --nologo -v q | Out-Host
    $dll = Join-Path $probeDir "bin\$Configuration\net10.0\RpsLoadProbe.dll"
    if (-not (Test-Path $dll)) { throw "Build output missing: $dll" }

    function Get-Arms([string] $Mode, [string] $Shard = 'all') {
        $args = @('run', '--no-build', '-c', $Configuration, '--', '--ramp', '--mode', $Mode, '--print-arms')
        if ($Shard -ne 'all') {
            $args += @('--arm-shard', $Shard)
        }
        & dotnet @args 2>$null | Where-Object { $_ -and $_ -notmatch '^\[' }
    }

    Write-Host 'Validating compare-product 3-way comparison-group shards...' -ForegroundColor Cyan
    $all = @(Get-Arms 'compare-product')
    if ($all.Count -lt 10) { throw "Expected many product arms; got $($all.Count)" }

    $s1 = @(Get-Arms 'compare-product' '1/3')
    $s2 = @(Get-Arms 'compare-product' '2/3')
    $s3 = @(Get-Arms 'compare-product' '3/3')
    $union = @($s1 + $s2 + $s3 | Select-Object -Unique)
    if ($union.Count -ne $all.Count) {
        throw "Union size $($union.Count) != all $($all.Count)"
    }
    $missing = $all | Where-Object { $union -notcontains $_ }
    if ($missing) { throw "Missing from union: $($missing -join ', ')" }

    $inter12 = $s1 | Where-Object { $s2 -contains $_ }
    $inter13 = $s1 | Where-Object { $s3 -contains $_ }
    $inter23 = $s2 | Where-Object { $s3 -contains $_ }
    if ($inter12 -or $inter13 -or $inter23) {
        throw "Shard intersection not empty"
    }

    # Gate pairs (Lite/Full/Reverse) and TWP+YARP must share a shard when both present.
    $pairs = @(
        @('twp-reverse-http1', 'twp-mitm-http1', 'twp-mitm-full-http1'),
        @('twp-reverse-http2', 'twp-mitm-http2', 'twp-mitm-full-http2', 'yarp-reverse-http2-to-https'),
        @('twp-reverse-http2-cleartext', 'yarp-reverse-http2', 'nginx-reverse-http2'),
        @('twp-mitm-https-connect', 'twp-mitm-full-https-connect')
    )
    $shards = @(@{ Name = '1/3'; Arms = $s1 }, @{ Name = '2/3'; Arms = $s2 }, @{ Name = '3/3'; Arms = $s3 })
    foreach ($pair in $pairs) {
        $present = @($pair | Where-Object { $all -contains $_ })
        if ($present.Count -lt 2) { continue }
        $homes = @()
        foreach ($arm in $present) {
            $shardHome = ($shards | Where-Object { $_.Arms -contains $arm } | Select-Object -First 1).Name
            if (-not $shardHome) { throw "Arm $arm missing from shards" }
            $homes += $shardHome
        }
        $distinct = $homes | Select-Object -Unique
        if ($distinct.Count -ne 1) {
            throw "Pair split across shards: $($present -join ', ') -> $($homes -join ', ')"
        }
    }

    Write-Host "OK: product $($all.Count) arms -> $($s1.Count)/$($s2.Count)/$($s3.Count) by comparison group" -ForegroundColor Green

    Write-Host 'Validating compare-product-smoke 2-way shards...' -ForegroundColor Cyan
    $smokeAll = @(Get-Arms 'compare-product-smoke')
    $sm1 = @(Get-Arms 'compare-product-smoke' '1/2')
    $sm2 = @(Get-Arms 'compare-product-smoke' '2/2')
    if ((@($sm1 + $sm2 | Select-Object -Unique).Count) -ne $smokeAll.Count) {
        throw 'Smoke shard union incomplete'
    }
    Write-Host "OK: smoke $($smokeAll.Count) arms -> $($sm1.Count)/$($sm2.Count)" -ForegroundColor Green

    Write-Host 'Validating compare-grpc single group...' -ForegroundColor Cyan
    $grpc = @(Get-Arms 'compare-grpc')
    if ($grpc.Count -lt 2) { throw "Expected grpc arms; got $($grpc.Count)" }
    $g1 = @(Get-Arms 'compare-grpc' '1/2')
    $g2 = @(Get-Arms 'compare-grpc' '2/2')
    # One comparison group → all arms on shard 1, none on shard 2
    if ($g1.Count -ne $grpc.Count -or $g2.Count -ne 0) {
        throw "gRPC should be one group (shard1=$($g1.Count) shard2=$($g2.Count) all=$($grpc.Count))"
    }
    Write-Host "OK: grpc $($grpc.Count) arms stay on one shard" -ForegroundColor Green
}
finally {
    Pop-Location
}
