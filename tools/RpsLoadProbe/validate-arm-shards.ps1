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

    Write-Host 'Validating compare-grpc groups (H2 TLS + h2c)...' -ForegroundColor Cyan
    $grpc = @(Get-Arms 'compare-grpc')
    if ($grpc.Count -lt 4) { throw "Expected grpc arms (http2+h2c); got $($grpc.Count)" }
    $g1 = @(Get-Arms 'compare-grpc' '1/2')
    $g2 = @(Get-Arms 'compare-grpc' '2/2')
    if (($g1.Count + $g2.Count) -ne $grpc.Count) {
        throw "gRPC shard union incomplete (shard1=$($g1.Count) shard2=$($g2.Count) all=$($grpc.Count))"
    }
    if ($g1.Count -eq 0 -or $g2.Count -eq 0) {
        throw "gRPC should split into two groups (shard1=$($g1.Count) shard2=$($g2.Count))"
    }
    Write-Host "OK: grpc $($grpc.Count) arms -> $($g1.Count)/$($g2.Count)" -ForegroundColor Green

    Write-Host 'Validating compare-ws-h1tls / compare-ws-h2 single groups...' -ForegroundColor Cyan
    foreach ($mode in @('compare-ws-h1tls', 'compare-ws-h2')) {
        $ws = @(Get-Arms $mode)
        if ($ws.Count -lt 2) { throw "Expected $mode arms; got $($ws.Count)" }
        $w1 = @(Get-Arms $mode '1/2')
        $w2 = @(Get-Arms $mode '2/2')
        if ($w1.Count -ne $ws.Count -or $w2.Count -ne 0) {
            throw "$mode should be one group (shard1=$($w1.Count) shard2=$($w2.Count) all=$($ws.Count))"
        }
    Write-Host "OK: $mode $($ws.Count) arms stay on one shard" -ForegroundColor Green
    }

    function Assert-ShardUnion([string] $Mode, [int] $N, [string] $Label) {
        Write-Host "Validating $Mode $N-way shards ($Label)..." -ForegroundColor Cyan
        $all = @(Get-Arms $Mode)
        if ($all.Count -lt 2) { throw "Expected $Mode arms; got $($all.Count)" }
        $parts = @()
        for ($i = 1; $i -le $N; $i++) {
            $parts += ,@(Get-Arms $Mode "$i/$N")
        }
        $union = @($parts | ForEach-Object { $_ } | Select-Object -Unique)
        if ($union.Count -ne $all.Count) {
            throw "$Mode ${N}-way union size $($union.Count) != all $($all.Count)"
        }
        for ($a = 0; $a -lt $N; $a++) {
            for ($b = $a + 1; $b -lt $N; $b++) {
                $inter = $parts[$a] | Where-Object { $parts[$b] -contains $_ }
                if ($inter) {
                    throw ("{0} shard {1}/{2} intersects {3}/{2}: {4}" -f $Mode, ($a + 1), $N, ($b + 1), ($inter -join ', '))
                }
            }
        }
        $counts = ($parts | ForEach-Object { $_.Count }) -join '/'
        Write-Host "OK: $Mode $($all.Count) arms -> $counts ($Label)" -ForegroundColor Green
    }

    # Mac Apple Silicon wiki refresh uses finer shards (PERF-GATES.md).
    Assert-ShardUnion 'compare-product' 9 'Mac product'
    Assert-ShardUnion 'compare-bodies' 4 'Mac bodies'
    Assert-ShardUnion 'compare-arch' 6 'Mac arch'
    Assert-ShardUnion 'compare-grpc' 4 'Mac grpc'
    Assert-ShardUnion 'compare-post' 2 'Mac post'
    Assert-ShardUnion 'compare-lossy' 2 'Mac lossy'
    Assert-ShardUnion 'compare-tls-cost' 2 'Mac tls-cost'

    # Row-level suite (rps-suite.yml): one job per comparison-group key.
    function Get-Groups([string] $Mode) {
        & dotnet run --no-build -c $Configuration -- --ramp --mode $Mode --print-groups 2>$null |
            Where-Object { $_ -and $_ -notmatch '^\[' } |
            ForEach-Object {
                $p = $_ -split "`t"
                [pscustomobject]@{ Key = $p[0]; Arms = [int]$p[1] }
            }
    }

    function Assert-GroupKeys([string] $Mode) {
        Write-Host "Validating $Mode row-level group keys..." -ForegroundColor Cyan
        $all = @(Get-Arms $Mode)
        $groups = @(Get-Groups $Mode)
        if ($groups.Count -lt 1) { throw "$Mode printed no groups" }
        if (@($groups.Key | Select-Object -Unique).Count -ne $groups.Count) { throw "$Mode duplicate group keys" }
        $seen = @()
        $armHome = @{}
        foreach ($g in $groups) {
            $armsForKey = @(Get-Arms $Mode $g.Key)
            if ($armsForKey.Count -ne $g.Arms) {
                throw "$Mode group $($g.Key): --print-groups says $($g.Arms) arms, --arm-shard says $($armsForKey.Count)"
            }
            $dup = $armsForKey | Where-Object { $seen -contains $_ }
            if ($dup) { throw "$Mode group $($g.Key) overlaps earlier group: $($dup -join ', ')" }
            foreach ($arm in $armsForKey) { $armHome[$arm] = $g.Key }
            $seen += $armsForKey
        }
        if (@($seen | Select-Object -Unique).Count -ne $all.Count) {
            throw "$Mode groups cover $(@($seen | Select-Object -Unique).Count) arms; --print-arms lists $($all.Count)"
        }
        # Gate pairs must stay in one group.
        foreach ($pair in @(
                @('twp-reverse-http1', 'twp-mitm-http1', 'twp-mitm-full-http1'),
                @('twp-mitm-https-connect', 'twp-mitm-full-https-connect'))) {
            $present = @($pair | Where-Object { $all -contains $_ })
            if ($present.Count -lt 2) { continue }
            $homes = @($present | ForEach-Object { $armHome[$_] } | Select-Object -Unique)
            if ($homes.Count -ne 1) { throw "$Mode gate pair split across groups: $($present -join ', ') -> $($homes -join ', ')" }
        }
        if ($groups.Count -gt 256) { throw "$Mode has $($groups.Count) groups (> 256 matrix cap)" }
        Write-Host "OK: $Mode $($all.Count) arms -> $($groups.Count) groups" -ForegroundColor Green
    }

    foreach ($mode in @('compare-product', 'compare-bodies', 'compare-post', 'compare-lossy', 'compare-tls-cost',
            'compare-arch', 'compare-saturation', 'compare-grpc', 'compare-ws-h1tls', 'compare-ws-h2')) {
        Assert-GroupKeys $mode
    }

    $bad = & dotnet run --no-build -c $Configuration -- --ramp --mode compare-product --print-arms --arm-shard no-such-group 2>$null |
        Where-Object { $_ -and $_ -notmatch '^\[' }
    if ($bad) { throw 'Unknown group key must resolve to zero arms' }
}
finally {
    Pop-Location
}
