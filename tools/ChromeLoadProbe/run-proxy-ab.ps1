# Three-arm A/B for real Chrome page loads.
#
#   direct        Chrome straight to the origin (--no-proxy-server), the baseline.
#   proxy_h3on    Chrome through the Basic example with origin HTTP/3 enabled (the shipped default).
#   proxy_h3off   Same, with TWP_ENABLE_HTTP3=0.
#
# proxy_h3off is the control that matters: HTTP/3 to an origin should never be slower than HTTP/2 to
# the same origin, so any gap between those two arms is a defect rather than a tuning question.
#
# Default (cold): the proxy is restarted before every measurement so its connection pool and Alt-Svc
# knowledge start empty. That exercises cold-start routing (including the deferred H3 switch).
#
# -WarmProxy: one proxy process per arm stays up for the whole arm. Each site is primed once
# (unrecorded) so Alt-Svc / QUIC are warm before measured trials. That is what exercises shared
# HTTP/3 stream multiplexing; the cold mode deliberately does not.
#
# Chrome still gets a fresh profile per measurement (see ChromeLoadProbe). Generated leaf
# certificates stay on disk, so this models a returning user rather than a first-ever launch.
#
# The harness never sets TWP_SET_SYSTEM_PROXY=1: a measurement run must not rewrite the machine's
# WinINet configuration. Chrome is pointed at the proxy explicitly instead.

[CmdletBinding()]
param(
    [string[]] $Sites = @(
        'https://news.google.com/',
        'https://www.google.com/',
        'https://en.wikipedia.org/wiki/Main_Page',
        'https://www.msn.com/'
    ),
    [int]    $Trials      = 5,
    [int]    $ProxyPort   = 8000,
    [int]    $TimeoutMs   = 45000,
    [string] $ChromePath  = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [string] $ResultsDir,
    [switch] $SkipBuild,
    [switch] $WarmProxy
)

$ErrorActionPreference = 'Stop'
if (-not $ResultsDir) { $ResultsDir = Join-Path $PSScriptRoot 'results' }
$repoRoot  = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proxyExe  = Join-Path $repoRoot 'examples\Titanium.Web.Proxy.Examples.Basic\bin\Release\net10.0\Titanium.Web.Proxy.Examples.Basic.exe'
$probeDll  = Join-Path $PSScriptRoot 'bin\Release\net10.0\ChromeLoadProbe.dll'

function Test-PortOpen([int] $Port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect('127.0.0.1', $Port)
        return $true
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Start-Proxy([bool] $EnableHttp3) {
    $env:TWP_SET_SYSTEM_PROXY = '0'
    $env:TWP_TRUST_ROOT       = '0'
    $env:TWP_ENABLE_HTTP3     = if ($EnableHttp3) { '1' } else { '0' }
    # Returning-user warm leaf cache (Basic Balanced default is SaveFakeCertificates=false).
    $env:TWP_SAVE_FAKE_CERTS  = '1'

    $stdinFile = Join-Path $env:TEMP 'twp-ab-stdin.txt'
    if (-not (Test-Path $stdinFile)) { New-Item -ItemType File -Path $stdinFile -Force | Out-Null }

    $proc = Start-Process -FilePath $proxyExe -PassThru -WindowStyle Hidden `
        -RedirectStandardInput  $stdinFile `
        -RedirectStandardOutput (Join-Path $ResultsDir 'proxy-stdout.log') `
        -RedirectStandardError  (Join-Path $ResultsDir 'proxy-stderr.log')

    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { throw "Proxy exited early with code $($proc.ExitCode)" }
        if (Test-PortOpen $ProxyPort) { return $proc }
        Start-Sleep -Milliseconds 150
    }

    try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { Write-Verbose $_.Exception.Message }
    throw "Proxy did not start listening on port $ProxyPort"
}

function Stop-Proxy($Proc) {
    if ($null -eq $Proc) { return }
    try { Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue } catch { Write-Verbose $_.Exception.Message }
    try { $Proc.WaitForExit(5000) | Out-Null } catch { Write-Verbose $_.Exception.Message }

    # The next arm restarts the proxy immediately; give the listener time to release the port so the
    # restart does not race an ephemeral bind failure.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Test-PortOpen $ProxyPort)) {
        Start-Sleep -Milliseconds 150
    }
}

function Invoke-Probe([string] $Arm, [string] $Url, [int] $Trial, [string] $Csv, [switch] $UseProxy) {
    $probeArgs = @(
        $probeDll,
        '--url', $Url,
        '--arm', $Arm,
        '--trial', $Trial,
        '--timeout-ms', $TimeoutMs,
        '--chrome', $ChromePath
    )
    if ($Csv)      { $probeArgs += @('--csv', $Csv) }
    if ($UseProxy) { $probeArgs += @('--proxy', "http://127.0.0.1:$ProxyPort") }

    & dotnet @probeArgs
}

# --- preflight -------------------------------------------------------------------------------

if (-not (Test-Path $ChromePath)) { throw "Chrome not found at $ChromePath" }
New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

if (Test-PortOpen $ProxyPort) {
    throw "Something is already listening on port $ProxyPort. Stop the manually-launched proxy first; " +
          "this harness needs to control proxy lifetime."
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

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv   = Join-Path $ResultsDir "proxy-ab-$stamp.csv"
$arms  = @('direct', 'proxy_h3on', 'proxy_h3off')

# Per-arm proxy configuration. 'direct' needs no proxy and is absent from the table.
$armConfig = @{
    proxy_h3on  = @{ Http3 = $true }
    proxy_h3off = @{ Http3 = $false }
}

Write-Host ''
Write-Host "Sites : $($Sites.Count)   Trials: $Trials   Arms: $($arms -join ', ')   Mode: $(if ($WarmProxy) { 'warm-proxy' } else { 'cold-restart' })" -ForegroundColor Cyan
Write-Host "Output: $csv" -ForegroundColor Cyan
Write-Host ''

# --- warm-up (not recorded) ------------------------------------------------------------------
# Populates the OS DNS cache and generates leaf certificates for a host outside the measured set,
# so that one-time costs are not charged to whichever arm happens to run first.

Write-Host 'Warm-up...' -ForegroundColor DarkGray
Invoke-Probe -Arm 'warmup' -Url 'https://example.com/' -Trial 0 -Csv '' | Out-Null
$bootstrapProxy = $null
try {
    $bootstrapProxy = Start-Proxy $true
    Invoke-Probe -Arm 'warmup' -Url 'https://example.com/' -Trial 0 -Csv '' -UseProxy | Out-Null
} finally {
    Stop-Proxy $bootstrapProxy
}

# --- measurement -----------------------------------------------------------------------------

if ($WarmProxy) {
    # One proxy per arm, primed before measured trials. Arms run sequentially so the proxy's
    # capability cache and QUIC pool stay warm for the whole arm.
    foreach ($arm in $arms) {
        Write-Host "--- arm $arm ---" -ForegroundColor Yellow
        $proxy = $null
        try {
            if ($arm -ne 'direct') {
                $proxy = Start-Proxy $armConfig[$arm].Http3
                Write-Host "  priming sites (unrecorded)..." -ForegroundColor DarkGray
                foreach ($site in $Sites) {
                    Invoke-Probe -Arm 'prime' -Url $site -Trial 0 -Csv '' -UseProxy | Out-Null
                }
                # Let background QUIC warmups from Alt-Svc finish before the first measured load.
                Start-Sleep -Seconds 2
            }

            foreach ($trial in 1..$Trials) {
                Write-Host "  trial $trial" -ForegroundColor DarkYellow
                foreach ($site in $Sites) {
                    try {
                        Invoke-Probe -Arm $arm -Url $site -Trial $trial -Csv $csv -UseProxy:($arm -ne 'direct')
                    } catch {
                        Write-Host "  $arm $site FAILED: $_" -ForegroundColor Red
                    }
                }
            }
        } finally {
            Stop-Proxy $proxy
        }
    }
} else {
    foreach ($trial in 1..$Trials) {
        # Rotate arm order per trial so no arm keeps a fixed position in the schedule.
        $offset    = ($trial - 1) % $arms.Count
        $armsOrder = 0..($arms.Count - 1) | ForEach-Object { $arms[($_ + $offset) % $arms.Count] }

        Write-Host "--- trial $trial ---" -ForegroundColor Yellow

        foreach ($arm in $armsOrder) {
            foreach ($site in $Sites) {
                $proxy = $null
                try {
                    if ($arm -ne 'direct') {
                        $proxy = Start-Proxy $armConfig[$arm].Http3
                    }
                    Invoke-Probe -Arm $arm -Url $site -Trial $trial -Csv $csv -UseProxy:($arm -ne 'direct')
                } catch {
                    Write-Host "  $arm $site FAILED: $_" -ForegroundColor Red
                } finally {
                    Stop-Proxy $proxy
                }
            }
        }
    }
}

Remove-Item Env:TWP_SET_SYSTEM_PROXY, Env:TWP_TRUST_ROOT, Env:TWP_ENABLE_HTTP3, Env:TWP_SAVE_FAKE_CERTS `
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
    Write-Host (('{0,-26}' -f 'site') + (($arms | ForEach-Object { '{0,14}' -f $_ }) -join ''))

    foreach ($site in ($rows.site | Select-Object -Unique)) {
        $cells = foreach ($arm in $arms) {
            $values = @($rows |
                Where-Object { $_.site -eq $site -and $_.arm -eq $arm -and $_.ok -eq '1' } |
                ForEach-Object { [double]$_.$Metric })
            if ($values.Count -eq 0) { 'n/a' } else { '{0:F0}' -f (Get-Median $values) }
        }
        Write-Host (('{0,-26}' -f $site) + (($cells | ForEach-Object { '{0,14}' -f $_ }) -join ''))
    }
}

Write-MedianTable 'ttfb_ms' 'Median main-document TTFB in ms (not affected by how much of the page rendered):'
Write-MedianTable 'load_ms' 'Median load_ms (compare with care: resource counts vary between runs):'

Write-Host ''
Write-Host 'Totals across all trials and sites:' -ForegroundColor Cyan
Write-Host ''
Write-Host ('{0,-14} {1,10} {2,10} {3,12} {4,12}' -f 'arm', 'over_1s', 'over_3s', 'median_ttfb', 'median_load')

foreach ($arm in $arms) {
    $armRows = @($rows | Where-Object { $_.arm -eq $arm -and $_.ok -eq '1' })
    if ($armRows.Count -eq 0) { continue }
    $over1 = (@($armRows | ForEach-Object { [int]$_.res_over1s }) | Measure-Object -Sum).Sum
    $over3 = (@($armRows | ForEach-Object { [int]$_.res_over3s }) | Measure-Object -Sum).Sum
    $loads = @($armRows | ForEach-Object { [double]$_.load_ms })
    $ttfbs = @($armRows | ForEach-Object { [double]$_.ttfb_ms })
    Write-Host ('{0,-14} {1,10} {2,10} {3,12:F0} {4,12:F0}' -f `
        $arm, $over1, $over3, (Get-Median $ttfbs), (Get-Median $loads))
}

$failures = @($rows | Where-Object { $_.ok -ne '1' })
if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "$($failures.Count) failed/timed-out load(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $($_.arm) $($_.site) trial=$($_.trial) $($_.error)" }
}

Write-Host ''
Write-Host "Raw rows: $csv" -ForegroundColor DarkGray
