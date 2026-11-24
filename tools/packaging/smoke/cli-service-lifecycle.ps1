#Requires -Version 5.1
<#
.SYNOPSIS
  Windows OS service install → start → stop → uninstall smoke.
.PARAMETER SelfTest
  Parse check plus ephemeral TcpListener ports (no titanium binary).
.PARAMETER Titanium
  Path to titanium.exe (required unless -SelfTest).
.PARAMETER CliZip
  Optional CLI zip for an update-while-running probe.
#>
param(
    [switch] $SelfTest,
    [string] $Titanium,
    [string] $CliZip
)

$ErrorActionPreference = 'Stop'

function Invoke-SelfTest {
    $a = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    $b = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    try {
        $a.Start(); $b.Start()
        $pa = ([System.Net.IPEndPoint]$a.LocalEndpoint).Port
        $pb = ([System.Net.IPEndPoint]$b.LocalEndpoint).Port
        if ($pa -le 0 -or $pb -le 0) { throw 'ASSERT: self-test ports missing' }
        Write-Host "cli-service-lifecycle self-test ok (ports=$pa,$pb)"
    }
    finally {
        try { $a.Stop() } catch { }
        try { $b.Stop() } catch { }
    }
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Titanium)) {
    throw 'Titanium is required unless -SelfTest'
}
$Titanium = (Resolve-Path $Titanium).Path
$work = Join-Path $env:TEMP ("twp-svc-smoke-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$name = 'twp-smk-' + ([guid]::NewGuid().ToString('n').Substring(0, 12))

$originListener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
$proxyListener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
$originListener.Start()
$proxyListener.Start()
$originPort = ([System.Net.IPEndPoint]$originListener.LocalEndpoint).Port
$listenPort = ([System.Net.IPEndPoint]$proxyListener.LocalEndpoint).Port
$originListener.Stop()
$proxyListener.Stop()

$cfg = Join-Path $work 'twp-service-smoke.yaml'
@"
schemaVersion: "7.0"
listeners:
  - host: "127.0.0.1"
    port: $listenPort
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: $originPort
"@ | Set-Content -LiteralPath $cfg -Encoding utf8

$originJob = Start-Job -ScriptBlock {
    param($Port)
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$Port/")
    $listener.Start()
    while ($listener.IsListening) {
        try {
            $ctx = $listener.GetContext()
            $buf = [Text.Encoding]::UTF8.GetBytes('ok')
            $ctx.Response.ContentLength64 = $buf.Length
            $ctx.Response.OutputStream.Write($buf, 0, $buf.Length)
            $ctx.Response.Close()
        } catch { break }
    }
} -ArgumentList $originPort

try {
    & $Titanium service uninstall --name $name 2>$null | Out-Null
    & $Titanium service install -c $cfg --name $name --no-start
    if ($LASTEXITCODE -ne 0) { throw "ASSERT: service install failed $LASTEXITCODE" }

    $stOut = & $Titanium service status --name $name 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ASSERT: service status failed after install: $stOut" }
    Write-Host ($stOut -join "`n")
    $stText = ($stOut | ForEach-Object { "$_" }) -join "`n"
    if ($stText -notmatch 'stopped|installed') { throw "ASSERT: expected stopped/installed after --no-start" }

    & $Titanium service start --name $name
    if ($LASTEXITCODE -ne 0) { throw "ASSERT: service start failed" }

    $httpOk = $false
    foreach ($i in 1..40) {
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:$listenPort/svc" -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { $httpOk = $true; break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $httpOk) { throw "ASSERT: port $listenPort not listening after start" }

    $sc = sc.exe query $name 2>&1
    Write-Host ($sc | ForEach-Object { "$_" }) -join "`n"
    if (($sc | Out-String) -notmatch 'RUNNING') { throw 'ASSERT: sc query did not report RUNNING' }

    if ($CliZip -and (Test-Path $CliZip)) {
        $feed = Join-Path $env:TEMP ("twp-svc-feed-" + [guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Force -Path $feed | Out-Null
        Copy-Item $CliZip (Join-Path $feed 'cli.zip')
        $hash = (Get-FileHash (Join-Path $feed 'cli.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item (Join-Path $feed 'cli.zip')).Length
        $feedPort = 8878
        $rids = @('linux-x64','linux-arm64','osx-arm64','osx-x64','win-x64','linux-musl-x64','linux-musl-arm64')
        $assets = @{}
        foreach ($rid in $rids) { $assets[$rid] = @{ url = "http://127.0.0.1:$feedPort/cli.zip"; sha256 = $hash; size = $size } }
        $manifest = @{ version = '9.9.9-smoke'; channel = 'beta'; products = @{ cli = @{ assets = $assets } } }
        $manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $feed 'release-manifest.json') -Encoding utf8
        $listener = Start-Process python -ArgumentList '-m','http.server',$feedPort,'--bind','127.0.0.1','--directory',$feed -PassThru -WindowStyle Hidden
        try {
            $env:TITANIUM_UPDATE_FEED = "http://127.0.0.1:$feedPort/release-manifest.json"
            $upd = & $Titanium update --channel beta 2>&1
            Write-Host ($upd | ForEach-Object { "$_" }) -join "`n"
            $updText = ($upd | ForEach-Object { "$_" }) -join "`n"
            if ($updText -notmatch 'service|Warning|stop') {
                Write-Host "NOTE: update while service running had no service warning"
            }
        }
        finally {
            if ($listener -and -not $listener.HasExited) { Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue }
            Remove-Item -Recurse -Force $feed -ErrorAction SilentlyContinue
        }
    }

    & $Titanium version
    if ($LASTEXITCODE -ne 0) { throw 'ASSERT: titanium version failed after update probe' }

    & $Titanium service stop --name $name
    if ($LASTEXITCODE -ne 0) { throw 'ASSERT: service stop failed' }

    & $Titanium service uninstall --name $name
    if ($LASTEXITCODE -ne 0) { throw 'ASSERT: service uninstall failed' }

    sc.exe query $name 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'ASSERT: sc query still succeeds after uninstall' }

    & $Titanium service status --name $name 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'ASSERT: status still reports installed after uninstall' }

    Write-Host "cli-service-lifecycle ok ($name)"
}
finally {
    if ($originJob) {
        Stop-Job $originJob -ErrorAction SilentlyContinue
        Remove-Job $originJob -Force -ErrorAction SilentlyContinue
    }
    try { & $Titanium service stop --name $name 2>$null } catch { }
    try { & $Titanium service uninstall --name $name 2>$null } catch { }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}