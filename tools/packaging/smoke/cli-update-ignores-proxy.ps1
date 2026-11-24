#Requires -Version 5.1
<#
.SYNOPSIS
  Windows companion to cli-update-ignores-proxy.sh
.PARAMETER SelfTest
  Start/stop recorder and feed on ephemeral ports; no titanium binary required.
.PARAMETER Plus
  Run titanium update --plus against the asset zip (Plus bundle) under a recording proxy.
#>
param(
    [switch] $SelfTest,
    [switch] $Plus,
    [string] $CliDir,
    [string] $CliZip,
    [string] $PlusZip,
    [string] $Titanium,
    [string] $Version = '8.0.9-smoke'
)

$ErrorActionPreference = 'Stop'

function Invoke-SelfTest {
    # Dry parse + ephemeral TcpListener bind/unbind (no HttpListener / no network deps).
    $proxy = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    $feed = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    try {
        $proxy.Start()
        $feed.Start()
        $proxyPort = ([System.Net.IPEndPoint]$proxy.LocalEndpoint).Port
        $feedPort = ([System.Net.IPEndPoint]$feed.LocalEndpoint).Port
        if ($proxyPort -le 0 -or $feedPort -le 0) { throw 'ASSERT: self-test ports missing' }
        Write-Host "cli-update-ignores-proxy self-test ok (feed=$feedPort proxy=$proxyPort)"
    }
    finally {
        try { $proxy.Stop() } catch { }
        try { $feed.Stop() } catch { }
    }
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

if ([string]::IsNullOrWhiteSpace($CliDir) -or [string]::IsNullOrWhiteSpace($Titanium)) {
    throw 'CliDir and Titanium are required unless -SelfTest'
}
$assetZip = if ($Plus) { $PlusZip } else { $CliZip }
if ([string]::IsNullOrWhiteSpace($assetZip)) {
    throw $(if ($Plus) { 'PlusZip is required with -Plus' } else { 'CliZip is required' })
}

$CliDir = (Resolve-Path $CliDir).Path
$assetZip = (Resolve-Path $assetZip).Path
$Titanium = (Resolve-Path $Titanium).Path

$feed = Join-Path $env:TEMP ("twp-update-feed-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $feed | Out-Null
$assetName = if ($Plus) { 'plus.zip' } else { 'cli.zip' }
Copy-Item $assetZip (Join-Path $feed $assetName) -Force
$sha = (Get-FileHash (Join-Path $feed $assetName) -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item (Join-Path $feed $assetName)).Length
$rids = @('linux-x64','linux-arm64','osx-arm64','osx-x64','win-x64','linux-musl-x64','linux-musl-arm64')

$hitsFile = Join-Path $env:TEMP ("twp-hits-" + [guid]::NewGuid().ToString('n') + '.txt')
'0' | Set-Content $hitsFile -Encoding ascii
$portDir = Split-Path $hitsFile

$proxyJob = Start-Job -ScriptBlock {
    param($hitsPath)
    $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    Set-Content -Path (Join-Path (Split-Path $hitsPath) 'proxy-port.txt') -Value $port -Encoding ascii
    while ($true) {
        try {
            $c = $listener.AcceptTcpClient()
            $n = [int](Get-Content $hitsPath -ErrorAction SilentlyContinue)
            Set-Content $hitsPath -Value ($n + 1) -Encoding ascii
            $c.Close()
        } catch { break }
    }
} -ArgumentList $hitsFile

$feedJob = Start-Job -ScriptBlock {
    param($dir)
    Set-Location $dir
    $listener = New-Object System.Net.HttpListener
    $bound = $false
    $port = 0
    for ($p = 18000; $p -lt 19000; $p++) {
        try {
            $listener.Prefixes.Clear()
            $listener.Prefixes.Add("http://127.0.0.1:$p/")
            $listener.Start()
            $port = $p
            $bound = $true
            break
        } catch { }
    }
    if (-not $bound) { throw 'no feed port' }
    Set-Content -Path (Join-Path $dir 'feed-port.txt') -Value $port -Encoding ascii
    while ($listener.IsListening) {
        try {
            $ctx = $listener.GetContext()
            $path = $ctx.Request.Url.AbsolutePath.TrimStart('/')
            if ([string]::IsNullOrEmpty($path)) { $path = 'index.html' }
            $file = Join-Path $dir $path
            if (Test-Path $file) {
                $bytes = [IO.File]::ReadAllBytes($file)
                $ctx.Response.StatusCode = 200
                $ctx.Response.ContentLength64 = $bytes.Length
                $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            } else {
                $ctx.Response.StatusCode = 404
            }
            $ctx.Response.Close()
        } catch { break }
    }
} -ArgumentList $feed

try {
    $portFile = Join-Path $portDir 'proxy-port.txt'
    $feedPortFile = Join-Path $feed 'feed-port.txt'
    for ($i = 0; $i -lt 50; $i++) {
        if ((Test-Path $portFile) -and (Test-Path $feedPortFile)) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path $portFile)) { throw 'ASSERT: proxy port file missing' }
    if (-not (Test-Path $feedPortFile)) { throw 'ASSERT: feed port file missing' }
    $proxyPort = (Get-Content $portFile -Raw).Trim()
    $feedPort = (Get-Content $feedPortFile -Raw).Trim()

    for ($i = 0; $i -lt 50; $i++) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:$feedPort/" -UseBasicParsing -TimeoutSec 1 | Out-Null
            break
        } catch { Start-Sleep -Milliseconds 100 }
    }

    if ($Plus) {
        $manifest = @{
            version = $Version
            channel = 'beta'
            products = @{
                plus = @{
                    version = $Version
                    bundle = @{
                        url = "http://127.0.0.1:$feedPort/plus.zip"
                        sha256 = $sha
                        size = $size
                    }
                }
            }
        }
    } else {
        $assets = [ordered]@{}
        foreach ($rid in $rids) {
            $assets[$rid] = @{ url = "http://127.0.0.1:$feedPort/cli.zip"; sha256 = $sha; size = $size }
        }
        $manifest = @{ version = $Version; channel = 'beta'; products = @{ cli = @{ assets = $assets } } }
        $idDir = Join-Path $env:LOCALAPPDATA 'TitaniumCli'
        New-Item -ItemType Directory -Force -Path $idDir | Out-Null
        '{"tag":"0.0.1","channel":"Beta"}' | Set-Content (Join-Path $idDir 'installed-release.json') -Encoding utf8
    }
    ($manifest | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $feed 'release-manifest.json') -Encoding utf8

    $env:HTTP_PROXY = "http://127.0.0.1:$proxyPort"
    $env:HTTPS_PROXY = $env:HTTP_PROXY
    $env:http_proxy = $env:HTTP_PROXY
    $env:https_proxy = $env:HTTP_PROXY
    $env:NO_PROXY = ''
    $env:no_proxy = ''
    $env:TITANIUM_UPDATE_FEED = "http://127.0.0.1:$feedPort/release-manifest.json"

    $updateArgs = @('update', '--channel', 'beta')
    if ($Plus) { $updateArgs = @('update', '--plus', '--channel', 'beta') }

    $proc = Start-Process -FilePath $Titanium -ArgumentList $updateArgs -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -ne 0) { throw "ASSERT: titanium update exited $($proc.ExitCode)" }

    $count = [int]((Get-Content $hitsFile -Raw).Trim())
    if ($count -ne 0) {
        throw "ASSERT: update used loopback HTTPS_PROXY ($count hits)"
    }
    if ($Plus) {
        Write-Host "plus update ignored loopback proxy ok (hits=0 version=$Version)"
    } else {
        Write-Host "cli update ignored loopback proxy ok (hits=0 version=$Version)"
    }
}
finally {
    Stop-Job $proxyJob, $feedJob -ErrorAction SilentlyContinue
    Remove-Job $proxyJob, $feedJob -Force -ErrorAction SilentlyContinue
    Remove-Item $feed, $hitsFile -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $portDir 'proxy-port.txt') -Force -ErrorAction SilentlyContinue
    Remove-Item Env:HTTP_PROXY, Env:HTTPS_PROXY, Env:http_proxy, Env:https_proxy, Env:TITANIUM_UPDATE_FEED -ErrorAction SilentlyContinue
}
