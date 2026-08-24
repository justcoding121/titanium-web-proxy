# Profile one RpsLoadProbe arm: gcdump mid-measure by polling run.log for proxy pid + measure.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Mode,
    [string] $Concurrency = '64',
    [int] $WarmupSec = 3,
    [int] $DurationSec = 20,
    [string] $OutDir,
    [switch] $SkipBuild,
    [int] $DumpAfterMeasureSec = 4
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutDir) {
    $OutDir = Join-Path $PSScriptRoot ("results/mem-audit-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$armDir = Join-Path $OutDir $Mode
New-Item -ItemType Directory -Path $armDir -Force | Out-Null
$logPath = Join-Path $armDir 'run.log'
if (Test-Path $logPath) { Remove-Item $logPath -Force }

$runScript = Join-Path $PSScriptRoot 'run-rps.ps1'
$argList = @(
    '-NoProfile', '-File', $runScript,
    '-Mode', $Mode,
    '-Concurrency', $Concurrency,
    '-WarmupSec', "$WarmupSec",
    '-DurationSec', "$DurationSec",
    '-Repeats', '1',
    '-ResultsDir', $armDir
)
if ($SkipBuild) { $argList += '-SkipBuild' }

Write-Host "Profiling $Mode → $armDir" -ForegroundColor Cyan

$p = Start-Process -FilePath 'pwsh' -ArgumentList $argList -WorkingDirectory $repoRoot `
    -RedirectStandardOutput $logPath -RedirectStandardError (Join-Path $armDir 'run.err.log') `
    -PassThru -NoNewWindow

$proxyPid = $null
$dumped = $false
$pos = 0
$metaPath = Join-Path $armDir 'profile-meta.log'
# Do not write into $logPath — Start-Process -RedirectStandardOutput locks it on Windows.

while (-not $p.HasExited -or (Test-Path $logPath)) {
    Start-Sleep -Milliseconds 200
    if (-not (Test-Path $logPath)) { continue }
    $content = Get-Content -Path $logPath -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    if ($content.Length -gt $pos) {
        $chunk = $content.Substring($pos)
        $pos = $content.Length
        foreach ($line in ($chunk -split "`r?`n")) {
            if ($line) { Write-Host $line }
        }
    }

    if ($null -eq $proxyPid -and $content -match 'proxy pid=(\d+)') {
        $proxyPid = [int]$Matches[1]
        Write-Host "CAPTURED_PROXY_PID=$proxyPid" -ForegroundColor Yellow
        Add-Content $metaPath "CAPTURED_PROXY_PID=$proxyPid"
    }

    if (-not $dumped -and $null -ne $proxyPid -and $content -match 'measure c=') {
        $dumped = $true
        Write-Host "Waiting ${DumpAfterMeasureSec}s then dumping pid=$proxyPid..." -ForegroundColor Yellow
        Start-Sleep -Seconds $DumpAfterMeasureSec
        if (Get-Process -Id $proxyPid -ErrorAction SilentlyContinue) {
            $gcdump = Join-Path $armDir "proxy-$proxyPid.gcdump"
            Write-Host "GCDUMP $gcdump" -ForegroundColor Cyan
            & dotnet-gcdump collect -p $proxyPid -o $gcdump
            Add-Content $metaPath "GCDUMP_DONE $gcdump"
            $dumpOut = Join-Path $armDir "proxy-$proxyPid.dump"
            Write-Host "HEAP DUMP $dumpOut" -ForegroundColor Cyan
            & dotnet-dump collect -p $proxyPid -o $dumpOut --type Heap
            Add-Content $metaPath "DUMP_DONE $dumpOut"
        }
        else {
            Write-Host "WARN: proxy exited before dump" -ForegroundColor Red
        }
    }

    if ($p.HasExited -and $dumped) { break }
    if ($p.HasExited -and (Get-Date) -gt $p.StartTime.AddSeconds($WarmupSec + $DurationSec + 90)) { break }
}

Wait-Process -Id $p.Id -ErrorAction SilentlyContinue
# Start-Process ExitCode can stay null after Wait-Process on some hosts; treat null as 0 when CSV exists.
$exitCode = if ($null -eq $p.ExitCode) { 0 } else { $p.ExitCode }
Write-Host "EXIT_CODE=$exitCode"

$gc = Get-ChildItem $armDir -Filter '*.gcdump' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gc) {
    $report = Join-Path $armDir 'gcdump-top.txt'
    Write-Host "Report → $report" -ForegroundColor Cyan
    & dotnet-gcdump report $gc.FullName 2>&1 | Select-Object -First 120 | Tee-Object -FilePath $report
}
else {
    Write-Host "WARN: no gcdump" -ForegroundColor Red
}

$csvOk = @(Get-ChildItem $armDir -Filter 'rps-ramp-*.csv' -ErrorAction SilentlyContinue).Count -gt 0
if ($exitCode -ne 0 -and -not $csvOk) { throw "exit $exitCode" }
Write-Host "Done: $armDir" -ForegroundColor Green
