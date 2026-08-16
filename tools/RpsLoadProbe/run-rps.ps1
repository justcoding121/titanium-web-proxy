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
        'compare', 'compare-http2', 'compare-tls', 'compare-terminate', 'explicit-pool-sweep',
        'reverse-http1', 'nginx-reverse-http1', 'reverse-http1-tls', 'nginx-reverse-http1-tls',
        'https-mitm',
        'reverse-http2', 'reverse-http2-cleartext', 'nginx-reverse-http2',
        'reverse-http3', 'reverse-http3-cleartext',
        'explicit-http1-multi', 'explicit-http2-multi')]
    [string] $Mode = 'compare',

    [string] $NginxPath,
    [string] $Concurrency = '8,16,24,32,48,64,128,256,512',
    [int]    $WarmupSec = 5,
    [int]    $DurationSec = 20,
    [string] $ResultsDir,
    [switch] $SkipBuild,
    [switch] $BombardierCheck
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project  = Join-Path $PSScriptRoot 'RpsLoadProbe.csproj'
$exe      = Join-Path $PSScriptRoot 'bin\Release\net10.0\RpsLoadProbe.exe'
if (-not $ResultsDir) {
    $ResultsDir = Join-Path $PSScriptRoot 'results'
}

Write-Host ''
Write-Host 'RpsLoadProbe — close browsers / heavy apps before a publishable run.' -ForegroundColor Yellow
Write-Host "Mode=$Mode  concurrency=$Concurrency  warmup=${WarmupSec}s  duration=${DurationSec}s" -ForegroundColor Cyan
Write-Host ''

if (-not $SkipBuild) {
    Write-Host 'Building Release...' -ForegroundColor Cyan
    & dotnet build -c Release $project --warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'RpsLoadProbe build failed' }
}

if (-not (Test-Path $exe)) { throw "Executable not found at $exe" }

New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

$probeArgs = @(
    '--ramp',
    '--mode', $Mode,
    '--concurrency', $Concurrency,
    '--warmup-sec', $WarmupSec,
    '--duration-sec', $DurationSec,
    '--results-dir', $ResultsDir
)
if ($NginxPath) {
    $probeArgs += @('--nginx-path', $NginxPath)
}

& $exe @probeArgs
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
