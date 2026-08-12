# Focused Cloudflare H3-on repro for TTFB buffering investigation.
# Primes once, then runs 3 measured loads through Basic with TWP_ENABLE_HTTP3=1.
[CmdletBinding()]
param(
    [int] $ProxyPort = 8000,
    [string] $ChromePath = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proxyExe = Join-Path $repoRoot 'examples\Titanium.Web.Proxy.Examples.Basic\bin\Release\net10.0\Titanium.Web.Proxy.Examples.Basic.exe'
$probeDll = Join-Path $PSScriptRoot 'bin\Release\net10.0\ChromeLoadProbe.dll'
$resultsDir = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

function Test-PortOpen([int] $Port) {
    $c = [System.Net.Sockets.TcpClient]::new()
    try { $c.Connect('127.0.0.1', $Port); return $true }
    catch { return $false }
    finally { $c.Dispose() }
}

if (-not (Test-Path $proxyExe)) { throw "Build Basic Release first: $proxyExe" }
if (-not (Test-Path $probeDll)) { throw "Build ChromeLoadProbe Release first: $probeDll" }
if (Test-PortOpen $ProxyPort) { throw "Port $ProxyPort already in use" }

$env:TWP_SET_SYSTEM_PROXY = '0'
$env:TWP_TRUST_ROOT = '0'
$env:TWP_ENABLE_HTTP3 = '1'
$env:TWP_SAVE_FAKE_CERTS = '1'
$env:TWP_FORWARD_UPSTREAM = '0'
$env:TWP_ENABLE_SVCB_DNS = '0'

$stdin = Join-Path $env:TEMP 'twp-cf-h3-repro-stdin.txt'
if (-not (Test-Path $stdin)) { New-Item -ItemType File -Path $stdin -Force | Out-Null }
$proc = Start-Process -FilePath $proxyExe -PassThru -WindowStyle Hidden `
    -RedirectStandardInput $stdin `
    -RedirectStandardOutput (Join-Path $resultsDir 'cf-h3-repro-out.log') `
    -RedirectStandardError (Join-Path $resultsDir 'cf-h3-repro-err.log')

try {
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { throw "Proxy exited early: $($proc.ExitCode)" }
        if (Test-PortOpen $ProxyPort) { break }
        Start-Sleep -Milliseconds 150
    }
    if (-not (Test-PortOpen $ProxyPort)) { throw 'Proxy start timeout' }

    Write-Host 'Prime (unrecorded)...'
    & dotnet $probeDll --url 'https://www.cloudflare.com/' --arm prime --trial 0 `
        --timeout-ms 60000 --chrome $ChromePath --proxy "http://127.0.0.1:$ProxyPort" | Out-Host
    Start-Sleep -Seconds 1

    for ($t = 1; $t -le 3; $t++) {
        Write-Host "Measured trial $t"
        & dotnet $probeDll --url 'https://www.cloudflare.com/' --arm proxy_h3on --trial $t `
            --timeout-ms 60000 --chrome $ChromePath --proxy "http://127.0.0.1:$ProxyPort" | Out-Host
    }
}
finally {
    try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
    Remove-Item Env:TWP_SET_SYSTEM_PROXY, Env:TWP_TRUST_ROOT, Env:TWP_ENABLE_HTTP3, Env:TWP_SAVE_FAKE_CERTS, Env:TWP_FORWARD_UPSTREAM, Env:TWP_ENABLE_SVCB_DNS -ErrorAction SilentlyContinue
}

Write-Host "Done."
