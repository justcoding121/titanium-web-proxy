# First-visit A/B: what the proxy costs when it has never seen the hosts a page pulls from, which is
# where certificate generation lands on the critical path.
#
#   direct      Chrome straight to the origin, the baseline.
#   proxy_rsa   Through the Basic example issuing RSA-2048 leaves.
#   proxy_ec    Through the Basic example issuing P-256 leaves (TWP_LEAF_KEY unset / 'ec').
#
# Every proxy measurement starts from an empty leaf-certificate cache and a freshly started proxy, so
# each load pays for a certificate per distinct host exactly as a first-ever visit would. The root
# certificate lives outside that cache and is left alone - deleting it would change what the browser
# trusts, not what the measurement is about.
#
# For the returning-user case (certificates already on disk, pool warm), use run-proxy-ab.ps1 instead.

[CmdletBinding()]
param(
    [string[]] $Sites = @(
        'https://www.msn.com/',
        'https://news.google.com/',
        'https://www.jw.org/en/'
    ),
    [int]    $Trials     = 3,
    [int]    $ProxyPort  = 8000,
    [int]    $TimeoutMs  = 60000,
    [string] $ChromePath = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proxyExe   = Join-Path $repoRoot 'examples\Titanium.Web.Proxy.Examples.Basic\bin\Release\net10.0\Titanium.Web.Proxy.Examples.Basic.exe'
$probeDll   = Join-Path $PSScriptRoot 'bin\Release\net10.0\ChromeLoadProbe.dll'
$resultsDir = Join-Path $PSScriptRoot 'results'
$certDir    = Join-Path $env:LOCALAPPDATA 'Titanium.Web.Proxy\crts'

function Test-PortOpen([int] $Port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try { $client.Connect('127.0.0.1', $Port); return $true }
    catch { return $false }
    finally { $client.Dispose() }
}

function Clear-LeafCertificateCache {
    if (Test-Path $certDir) {
        Get-ChildItem $certDir -File -Filter *.pfx | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

function Start-Proxy([string] $LeafKey) {
    $env:TWP_SET_SYSTEM_PROXY = '0'
    $env:TWP_TRUST_ROOT       = '0'
    $env:TWP_ENABLE_HTTP3     = '0'
    $env:TWP_LEAF_KEY         = $LeafKey

    $stdinFile = Join-Path $env:TEMP 'twp-firstvisit-stdin.txt'
    if (-not (Test-Path $stdinFile)) { New-Item -ItemType File -Path $stdinFile -Force | Out-Null }

    $proc = Start-Process -FilePath $proxyExe -PassThru -WindowStyle Hidden `
        -RedirectStandardInput  $stdinFile `
        -RedirectStandardOutput (Join-Path $resultsDir 'firstvisit-proxy-out.log') `
        -RedirectStandardError  (Join-Path $resultsDir 'firstvisit-proxy-err.log')

    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { throw "Proxy exited early with code $($proc.ExitCode)" }
        if (Test-PortOpen $ProxyPort) { return $proc }
        Start-Sleep -Milliseconds 150
    }

    try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    throw "Proxy did not start listening on port $ProxyPort"
}

function Stop-Proxy($Proc) {
    if ($null -eq $Proc) { return }
    try { Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    try { $Proc.WaitForExit(5000) | Out-Null } catch { }

    # The next measurement restarts the proxy immediately; wait for the listener to release the port
    # so the restart does not race an ephemeral bind failure.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Test-PortOpen $ProxyPort)) { Start-Sleep -Milliseconds 150 }
}

# --- preflight -------------------------------------------------------------------------------

if (-not (Test-Path $ChromePath)) { throw "Chrome not found at $ChromePath" }
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

if (Test-PortOpen $ProxyPort) {
    throw "Something is already listening on port $ProxyPort. Stop it first; this harness needs to " +
          'control proxy lifetime.'
}

if (-not $SkipBuild) {
    Write-Host 'Building proxy example and probe...' -ForegroundColor Cyan
    & dotnet build -c Release (Join-Path $repoRoot 'examples\Titanium.Web.Proxy.Examples.Basic\Titanium.Web.Proxy.Examples.Basic.csproj') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Proxy example build failed' }
    & dotnet build -c Release (Join-Path $PSScriptRoot 'ChromeLoadProbe.csproj') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
}

if (-not (Test-Path $proxyExe)) { throw "Proxy executable not found at $proxyExe" }
if (-not (Test-Path $probeDll)) { throw "Probe not found at $probeDll" }

$arms = @('direct', 'proxy_rsa', 'proxy_ec')
$leafKeyForArm = @{ proxy_rsa = 'rsa'; proxy_ec = 'ec' }
$csv = Join-Path $resultsDir ('firstvisit-ab-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

Write-Host ''
Write-Host "Sites: $($Sites.Count)   Trials: $Trials   Arms: $($arms -join ', ')" -ForegroundColor Cyan
Write-Host "Output: $csv" -ForegroundColor Cyan

# --- measurement -----------------------------------------------------------------------------

foreach ($trial in 1..$Trials) {
    # Rotate arm order per trial so no arm keeps a fixed position in the schedule.
    $offset    = ($trial - 1) % $arms.Count
    $armsOrder = 0..($arms.Count - 1) | ForEach-Object { $arms[($_ + $offset) % $arms.Count] }

    Write-Host "--- trial $trial ---" -ForegroundColor Yellow

    foreach ($arm in $armsOrder) {
        foreach ($site in $Sites) {
            $proxy = $null
            try {
                $probeArgs = @($probeDll, '--url', $site, '--arm', $arm, '--trial', $trial,
                    '--timeout-ms', $TimeoutMs, '--chrome', $ChromePath, '--csv', $csv)

                if ($arm -ne 'direct') {
                    Clear-LeafCertificateCache
                    $proxy = Start-Proxy $leafKeyForArm[$arm]
                    $probeArgs += @('--proxy', "http://127.0.0.1:$ProxyPort")
                }

                & dotnet @probeArgs
            } catch {
                Write-Host "  $arm $site FAILED: $_" -ForegroundColor Red
            } finally {
                Stop-Proxy $proxy
            }
        }
    }
}

Remove-Item Env:TWP_SET_SYSTEM_PROXY, Env:TWP_TRUST_ROOT, Env:TWP_ENABLE_HTTP3, Env:TWP_LEAF_KEY `
    -ErrorAction SilentlyContinue

# --- summary ---------------------------------------------------------------------------------

function Get-Median([double[]] $Values) {
    if ($Values.Count -eq 0) { return [double]::NaN }
    $sorted = @($Values | Sort-Object)
    $mid = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

$rows = @(Import-Csv $csv)

function Write-MedianTable([string] $Metric, [string] $Title) {
    Write-Host ''
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ''
    Write-Host (('{0,-30}' -f 'site') + (($arms | ForEach-Object { '{0,12}' -f $_ }) -join ''))

    foreach ($site in ($rows.site | Select-Object -Unique)) {
        $cells = foreach ($arm in $arms) {
            $values = @($rows |
                Where-Object { $_.site -eq $site -and $_.arm -eq $arm -and $_.ok -eq '1' } |
                ForEach-Object { [double]$_.$Metric })
            if ($values.Count -eq 0) { 'n/a' } else { '{0:F0}' -f (Get-Median $values) }
        }
        Write-Host (('{0,-30}' -f $site) + (($cells | ForEach-Object { '{0,12}' -f $_ }) -join ''))
    }
}

Write-MedianTable 'ttfb_ms' 'Median main-document TTFB in ms:'
Write-MedianTable 'load_ms' 'Median load_ms (compare with care: resource counts vary between runs):'

Write-Host ''
Write-Host 'Totals across all trials and sites:' -ForegroundColor Cyan
Write-Host ''
Write-Host ('{0,-12} {1,12} {2,12} {3,10} {4,10}' -f 'arm', 'median_ttfb', 'median_load', 'over_1s', 'over_3s')

foreach ($arm in $arms) {
    $armRows = @($rows | Where-Object { $_.arm -eq $arm -and $_.ok -eq '1' })
    if ($armRows.Count -eq 0) { continue }
    $over1 = (@($armRows | ForEach-Object { [int]$_.res_over1s }) | Measure-Object -Sum).Sum
    $over3 = (@($armRows | ForEach-Object { [int]$_.res_over3s }) | Measure-Object -Sum).Sum
    Write-Host ('{0,-12} {1,12:F0} {2,12:F0} {3,10} {4,10}' -f $arm,
        (Get-Median @($armRows | ForEach-Object { [double]$_.ttfb_ms })),
        (Get-Median @($armRows | ForEach-Object { [double]$_.load_ms })), $over1, $over3)
}

$failures = @($rows | Where-Object { $_.ok -ne '1' })
if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "$($failures.Count) failed/timed-out load(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $($_.arm) $($_.site) trial=$($_.trial) $($_.error)" }
}

Write-Host ''
Write-Host "Raw rows: $csv" -ForegroundColor DarkGray
