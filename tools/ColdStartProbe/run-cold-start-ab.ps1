# Cold-start A/B for Titanium proxy knobs, driven by an h2-ALPN client (see ColdStartProbe).
#
# The proxy is restarted for every trial so its connection pool and HTTP/2 origin-capability cache
# start empty; generated leaf certificates stay on disk, matching a returning user rather than a
# first-ever launch.
#
# Reading the output: compare tls_ms only. total_ms means different things depending on the
# negotiated ALPN (see the header comment in Program.cs), so it is recorded but not summarised.
#
# The "replicate_of_fwd0" configuration is deliberately identical to "fwd0". It is a control: the
# spread between those two is this machine's noise floor, and any difference between real
# configurations smaller than that spread is not a result.
param(
    [string]$Project = "$PSScriptRoot\..\..\examples\Titanium.Web.Proxy.Examples.Basic\Titanium.Web.Proxy.Examples.Basic.csproj",
    [string]$ProbeProject = "$PSScriptRoot\ColdStartProbe.csproj",
    [string]$ResultsDir = "$PSScriptRoot\results",
    [int]$Trials = 3,
    [int]$Port = 8000
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

Write-Host "Building probe..." -ForegroundColor DarkGray
dotnet build $ProbeProject -c Release --nologo -v q | Out-Null
$probeDll = Join-Path $PSScriptRoot "bin\Release\net10.0\ColdStartProbe.dll"
if (-not (Test-Path $probeDll)) { throw "Probe build missing: $probeDll" }

$sites = @(
    "https://example.com/",
    "https://www.google.com/generate_204",
    "https://news.ycombinator.com/",
    "https://www.cloudflare.com/",
    "https://httpbin.org/get"
)

# A host outside $sites, used only to absorb JIT and first-connection costs after each restart.
$warmupSite = "https://www.wikipedia.org/"

$baseEnv = @{
    TWP_FORWARD_UPSTREAM       = "1"
    TWP_PREFETCH               = "1"
    TWP_ENABLE_HTTP2           = "1"
    TWP_ENABLE_CONNECTION_POOL = "1"
    TWP_ENABLE_SVCB_DNS        = "0"
    TWP_ENABLE_HTTP3           = "1"
}

function New-Config([string]$Name, [hashtable]$Overrides) {
    $merged = @{}
    foreach ($k in $baseEnv.Keys) { $merged[$k] = $baseEnv[$k] }
    foreach ($k in $Overrides.Keys) { $merged[$k] = $Overrides[$k] }
    return @{ Name = $Name; Env = $merged }
}

$configs = @(
    (New-Config "baseline"           @{}),
    (New-Config "fwd0"               @{ TWP_FORWARD_UPSTREAM = "0" }),
    (New-Config "replicate_of_fwd0"  @{ TWP_FORWARD_UPSTREAM = "0" }),
    (New-Config "prefetch0"          @{ TWP_PREFETCH = "0" }),
    (New-Config "http2_off"          @{ TWP_ENABLE_HTTP2 = "0" }),
    (New-Config "pool0"              @{ TWP_ENABLE_CONNECTION_POOL = "0" }),
    (New-Config "svcb_on"            @{ TWP_ENABLE_SVCB_DNS = "1" }),
    (New-Config "http3_off"          @{ TWP_ENABLE_HTTP3 = "0" })
)

$allTwpVars = @(
    "TWP_FORWARD_UPSTREAM", "TWP_PREFETCH", "TWP_ENABLE_HTTP2", "TWP_ENABLE_CONNECTION_POOL",
    "TWP_ENABLE_SVCB_DNS", "TWP_ENABLE_HTTP3", "TWP_SET_SYSTEM_PROXY", "TWP_TRUST_ROOT",
    "TWP_CAPTURE_TIMING", "TWP_SAVE_FAKE_CERTS"
)

function Clear-TwpEnv {
    foreach ($k in $allTwpVars) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
}

function Stop-ProxyListeners {
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
}

function Invoke-Probe([string]$Url) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $out = & dotnet $probeDll @("127.0.0.1", "$Port", $Url) 2>&1
    $ErrorActionPreference = $prev

    $line = $out | Where-Object { $_ -match '^code=' } | Select-Object -Last 1
    if (-not $line) { throw "Probe failed for $Url :`n$($out | Out-String)" }

    $map = @{}
    foreach ($part in ([string]$line).Split(" ")) {
        $kv = $part.Split("=", 2)
        if ($kv.Count -eq 2) { $map[$kv[0]] = $kv[1] }
    }
    return [pscustomobject]@{
        Code     = [int]$map["code"]
        TlsMs    = [double]$map["tls_ms"]
        TtfbMs   = [double]$map["ttfb_ms"]
        TotalMs  = [double]$map["total_ms"]
        Alpn     = $map["alpn"]
        Measured = $map["measured"]
    }
}

function Start-Proxy([hashtable]$EnvMap) {
    Clear-TwpEnv
    # Never register as the Windows system proxy: this script force-kills the proxy between trials,
    # which skips its shutdown path, and a half-removed registration would leave the machine's
    # browsers pointed at a dead port.
    $env:TWP_SET_SYSTEM_PROXY = "0"
    $env:TWP_TRUST_ROOT = "1"
    $env:TWP_SAVE_FAKE_CERTS = "1"
    foreach ($k in $EnvMap.Keys) { Set-Item "Env:$k" $EnvMap[$k] }

    Stop-ProxyListeners
    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $Project, "-c", "Release", "--no-build") `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $ResultsDir "proxy-stdout.log") `
        -RedirectStandardError (Join-Path $ResultsDir "proxy-stderr.log")

    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { break }
        if ($proc.HasExited) { throw "Proxy exited early (code $($proc.ExitCode)); see $ResultsDir\proxy-stderr.log" }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)) {
        throw "Proxy did not listen on :$Port"
    }

    Invoke-Probe $warmupSite | Out-Null
    Start-Sleep -Milliseconds 200
    return $proc
}

function Get-Median([double[]]$Values) {
    $sorted = $Values | Sort-Object
    return $sorted[[int]([math]::Floor(($sorted.Count - 1) / 2))]
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$csv = Join-Path $ResultsDir "cold-start-$stamp.csv"
"config,site,trial,code,tls_ms,ttfb_ms,total_ms,alpn,measured" | Set-Content $csv -Encoding utf8
$rows = @()

# Trials are the outer loop so every configuration is sampled once per pass. Running all trials of
# one configuration back to back would let network drift over the length of the run masquerade as a
# difference between configurations.
for ($t = 1; $t -le $Trials; $t++) {
    Write-Host "`n########## PASS $t of $Trials ##########" -ForegroundColor Magenta
    foreach ($cfg in $configs) {
        Write-Host "`n=== $($cfg.Name) ===" -ForegroundColor Cyan
        $proc = Start-Proxy $cfg.Env
        try {
            foreach ($url in $sites) {
                $m = Invoke-Probe $url
                Write-Host ("  {0,-40} tls={1,6:n0}ms alpn={2} ({3})" -f $url, $m.TlsMs, $m.Alpn, $m.Measured)
                $rows += [pscustomobject]@{
                    config = $cfg.Name; site = $url; trial = $t; tls_ms = $m.TlsMs
                }
                Add-Content $csv ("{0},{1},{2},{3},{4},{5},{6},{7},{8}" -f `
                        $cfg.Name, $url, $t, $m.Code, $m.TlsMs, $m.TtfbMs, $m.TotalMs, $m.Alpn, $m.Measured)
            }
        }
        finally {
            if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
            Stop-ProxyListeners
            Clear-TwpEnv
            Start-Sleep -Milliseconds 400
        }
    }
}

Write-Host "`n=== median cold tls_ms by config ===" -ForegroundColor Green
$summary = $rows | Group-Object config | ForEach-Object {
    $vals = [double[]]($_.Group | ForEach-Object { $_.tls_ms })
    [pscustomobject]@{
        Config = $_.Name
        N      = $vals.Count
        Median = Get-Median $vals
        Mean   = ($vals | Measure-Object -Average).Average
    }
}
$summary | ForEach-Object { "{0,-18} n={1,-3} median={2,6:n0}ms mean={3,6:n0}ms" -f $_.Config, $_.N, $_.Median, $_.Mean }

$a = $summary | Where-Object Config -eq "fwd0"
$b = $summary | Where-Object Config -eq "replicate_of_fwd0"
if ($a -and $b) {
    $floor = [math]::Abs($a.Median - $b.Median)
    Write-Host ("`nNoise floor (two identical configs differed by): {0:n0}ms" -f $floor) -ForegroundColor Yellow
    Write-Host "Treat any gap smaller than that as no result." -ForegroundColor Yellow
}

Write-Host "`nResults: $csv"
