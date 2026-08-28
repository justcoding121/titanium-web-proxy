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
        'compare', 'compare-http2', 'compare-tls', 'compare-terminate', 'compare-same', 'compare-bridges',
        'compare-http3-cleartext', 'compare-mitm', 'compare-matrix', 'compare-product', 'compare-spot', 'compare-ceiling',
        'compare-bodies', 'compare-post', 'compare-lossy', 'compare-tls-cost', 'compare-arch', 'compare-saturation',
        'compare-editions', 'compare-cross-version',
        'origin-direct', 'explicit-pool-sweep',
        'reverse-http1', 'bare-reverse-http1', 'nginx-reverse-http1', 'yarp-reverse-http1',
        'reverse-http1-tls', 'bare-reverse-http1-tls', 'nginx-reverse-http1-tls', 'yarp-reverse-http1-tls',
        'reverse-http1-to-https', 'yarp-reverse-http1-to-https',
        'https-mitm', 'http-mitm', 'reverse-http1-mitm', 'mitm-http2-to-http1', 'mitm-http3-to-http1',
        'reverse-http2', 'reverse-http2-cleartext', 'reverse-http2-to-h2c', 'yarp-reverse-http2-to-h2c',
        'reverse-h2c', 'yarp-reverse-h2c', 'reverse-h2c-to-h2c', 'yarp-reverse-h2c-to-h2c',
        'reverse-h2c-to-h1', 'yarp-reverse-h2c-to-h1', 'reverse-h2c-to-https', 'yarp-reverse-h2c-to-https',
        'reverse-h2c-to-h3', 'yarp-reverse-h2c-to-h3',
        'nginx-reverse-http2', 'nginx-reverse-http3-cleartext', 'yarp-reverse-http2', 'yarp-reverse-http2-to-https',
        'yarp-reverse-http2-to-https-http1', 'yarp-reverse-http1-tls-to-https', 'yarp-reverse-http3-to-https-http1',
        'reverse-http3', 'reverse-http3-cleartext', 'yarp-reverse-http3-cleartext',
        'reverse-http11-to-http2', 'yarp-reverse-http11-to-http2',
        'reverse-http1-to-h2c', 'yarp-reverse-http1-to-h2c',
        'reverse-http1-plain-to-h2c', 'yarp-reverse-http1-plain-to-h2c',
        'reverse-http1-plain-to-http2', 'yarp-reverse-http1-plain-to-http2',
        'reverse-http1-plain-to-http3', 'yarp-reverse-http1-plain-to-http3',
        'reverse-http1-to-http3', 'yarp-reverse-http1-to-http3',
        'reverse-http2-to-http3', 'yarp-reverse-http2-to-http3',
        'reverse-http3-to-http2', 'yarp-reverse-http3-to-http2',
        'reverse-http3-to-h2c', 'yarp-reverse-http3-to-h2c', 'yarp-reverse-http3-to-http3',
        'twp-cli-reverse-http1', 'twp-cli-reverse-http1-tls', 'twp-cli-reverse-http1-route',
        'twp-cli-plus-base-http1', 'twp-cli-plus-cache-http1', 'twp-cli-intercept-http1',
        'explicit-http1-multi', 'explicit-http2-multi')]
    [string] $Mode = 'compare',

    [string] $NginxPath,
    [string] $Concurrency = '8,16,24,32,48,64,128,256,512',
    [int]    $WarmupSec = 5,
    [int]    $DurationSec = 20,
    [int]    $Repeats = 1,
    [string] $ResultsDir,
    [ValidateSet('GET', 'POST')]
    [string] $Method = 'GET',
    [int]    $ResponseBytes = 0,
    [int]    $RequestBytes = 0,
    [switch] $NoKeepAlive,
    [int]    $DelayMs = 0,
    [double] $LossPercent = 0,
    [switch] $SkipBuild,
    [switch] $BombardierCheck,
    [switch] $NoStopOnSloFail
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

    $needsCli = $Mode -match '^(compare-editions|compare-cross-version|twp-cli-)'
    if ($needsCli -or $Mode -eq 'compare-editions') {
        $cliProj = Join-Path $repoRoot 'src/Titanium.Cli/Titanium.Cli.csproj'
        Write-Host 'Building Titanium.Cli Release...' -ForegroundColor Cyan
        & dotnet build -c Release $cliProj --warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'Titanium.Cli build failed' }
    }

    $needsPlus = $Mode -match '^(compare-editions|twp-cli-plus-)'
    if ($needsPlus) {
        $plusProj = Join-Path $repoRoot 'src/Titanium.Plus/Titanium.Plus.csproj'
        if (Test-Path $plusProj) {
            Write-Host 'Building Titanium.Plus Release...' -ForegroundColor Cyan
            & dotnet build -c Release $plusProj --warnaserror
            if ($LASTEXITCODE -ne 0) { throw 'Titanium.Plus build failed' }
        }
    }
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
    '--results-dir', $ResultsDir,
    '--method', $Method,
    '--delay-ms', $DelayMs,
    '--loss-percent', $LossPercent
)
if ($ResponseBytes -gt 0) {
    $probeArgs += @('--response-bytes', $ResponseBytes)
}
if ($RequestBytes -gt 0) {
    $probeArgs += @('--request-bytes', $RequestBytes)
}
if ($NoKeepAlive) {
    $probeArgs += '--no-keepalive'
}
if ($NginxPath) {
    $probeArgs += @('--nginx-path', $NginxPath)
}
if ($NoStopOnSloFail) {
    $probeArgs += '--no-stop-on-slo-fail'
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
