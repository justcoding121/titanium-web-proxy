# Orchestrates a Release build + saturation ramp for Titanium.Web.Proxy.
# Works on Windows PowerShell and on ubuntu-latest (pwsh).
#
# Examples:
#   pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare
#   pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode reverse-http1 -Concurrency 32,64,128 -DurationSec 10
#   pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare -NginxPath "C:\nginx\nginx.exe"

[CmdletBinding()]
param(
    [ValidateSet(
        'compare', 'compare-http2', 'compare-tls', 'compare-terminate', 'compare-same', 'compare-bridges', 'compare-mitm', 'explicit-pool-sweep',
        'reverse-http1', 'nginx-reverse-http1', 'reverse-http1-tls', 'nginx-reverse-http1-tls',
        'https-mitm', 'mitm-http2-to-http1', 'mitm-http3-to-http1',
        'reverse-http2', 'reverse-http2-cleartext', 'reverse-http2-to-h2c',
        'reverse-h2c', 'reverse-h2c-to-h2c', 'reverse-h2c-to-h1', 'reverse-h2c-to-h3',
        'nginx-reverse-http2',
        'reverse-http3', 'reverse-http3-cleartext',
        'reverse-http11-to-http2', 'reverse-http1-to-http3', 'reverse-http2-to-http3', 'reverse-http3-to-http2',
        'explicit-http1-multi', 'explicit-http2-multi')]
    [string] $Mode = 'compare',

    [string] $NginxPath,
    [string] $Concurrency = '8,16,24,32,48,64,128,256,512',
    [int]    $WarmupSec = 5,
    [int]    $DurationSec = 20,
    [int]    $Repeats = 1,
    [string] $ResultsDir,
    [switch] $SkipBuild,
    [switch] $BombardierCheck
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project  = Join-Path $PSScriptRoot 'RpsLoadProbe.csproj'
$outDir   = Join-Path $PSScriptRoot 'bin/Release/net10.0'
$exeWin   = Join-Path $outDir 'RpsLoadProbe.exe'
$exeUnix  = Join-Path $outDir 'RpsLoadProbe'
if (-not $ResultsDir) {
    $ResultsDir = Join-Path $PSScriptRoot 'results'
}

Write-Host ''
Write-Host 'RpsLoadProbe — close browsers / heavy apps before a publishable run.' -ForegroundColor Yellow
Write-Host "Mode=$Mode  concurrency=$Concurrency  warmup=${WarmupSec}s  duration=${DurationSec}s  repeats=$Repeats" -ForegroundColor Cyan
Write-Host ''

if (-not $SkipBuild) {
    Write-Host 'Building Release...' -ForegroundColor Cyan
    & dotnet build -c Release $project --warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'RpsLoadProbe build failed' }
}

$probeCmd = $null
$probePrefix = @()
if (Test-Path $exeWin) {
    $probeCmd = $exeWin
}
elseif (Test-Path $exeUnix) {
    $probeCmd = $exeUnix
}
else {
    # Fallback: run the built DLL via the host (covers some RID-less publish layouts).
    $dll = Join-Path $outDir 'RpsLoadProbe.dll'
    if (-not (Test-Path $dll)) { throw "RpsLoadProbe binary not found under $outDir" }
    $probeCmd = 'dotnet'
    $probePrefix = @($dll)
}

New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

$probeArgs = $probePrefix + @(
    '--ramp',
    '--mode', $Mode,
    '--concurrency', $Concurrency,
    '--warmup-sec', $WarmupSec,
    '--duration-sec', $DurationSec,
    '--repeats', $Repeats,
    '--results-dir', $ResultsDir
)
if ($NginxPath) {
    $probeArgs += @('--nginx-path', $NginxPath)
}

& $probeCmd @probeArgs
if ($LASTEXITCODE -ne 0) { throw "RpsLoadProbe exited with code $LASTEXITCODE" }

if ($BombardierCheck) {
    $bombardier = Get-Command bombardier -ErrorAction SilentlyContinue
    if (-not $bombardier) {
        Write-Host 'bombardier not on PATH; skipping optional check. Install from https://github.com/codesenberg/bombardier/releases' -ForegroundColor DarkYellow
    }
    else {
        Write-Host ''
        Write-Host 'Optional bombardier check: start --serve in another terminal, then for example:' -ForegroundColor Cyan
        Write-Host '  bombardier -c 256 -d 30s -l http://127.0.0.1:<listen>/'
    }
}

Write-Host ''
Write-Host "Results: $ResultsDir" -ForegroundColor Green
Get-ChildItem $ResultsDir -Filter 'rps-ramp-*.csv' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 3 |
    ForEach-Object { Write-Host "  $($_.FullName)" }
