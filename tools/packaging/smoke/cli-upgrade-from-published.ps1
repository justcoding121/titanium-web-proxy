#Requires -Version 5.1
<#
.SYNOPSIS
  Windows twin of cli-upgrade-from-published.sh
#>
param(
    [switch] $SelfTest,
    [string] $PublishedZip,
    [string] $CandidateZip,
    [string] $WorkDir,
    [string] $Version = '99.0.0-smoke'
)

$ErrorActionPreference = 'Stop'

function Invoke-SelfTest {
    $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        if ($port -le 0) { throw 'ASSERT: self-test feed port missing' }
        Write-Host "cli-upgrade-from-published self-test ok (feed=$port)"
    }
    finally {
        try { $listener.Stop() } catch { }
    }
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

if ([string]::IsNullOrWhiteSpace($PublishedZip) -or [string]::IsNullOrWhiteSpace($CandidateZip) -or [string]::IsNullOrWhiteSpace($WorkDir)) {
    throw 'PublishedZip, CandidateZip, and WorkDir are required unless -SelfTest'
}

$PublishedZip = (Resolve-Path $PublishedZip).Path
$CandidateZip = (Resolve-Path $CandidateZip).Path
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$WorkDir = (Resolve-Path $WorkDir).Path

$old = Join-Path $WorkDir 'old'
if (Test-Path $old) { Remove-Item $old -Recurse -Force }
New-Item -ItemType Directory -Force -Path $old | Out-Null
Expand-Archive $PublishedZip $old -Force
$bin = Join-Path $old 'titanium.exe'
if (-not (Test-Path $bin)) { $bin = Join-Path $old 'titanium' }
if (-not (Test-Path $bin)) { throw 'ASSERT: published zip has no titanium binary' }

$feed = Join-Path $env:TEMP ("twp-upgrade-feed-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $feed | Out-Null
Copy-Item $CandidateZip (Join-Path $feed 'cli.zip') -Force
$sha = (Get-FileHash (Join-Path $feed 'cli.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item (Join-Path $feed 'cli.zip')).Length
$rids = @('linux-x64','linux-arm64','osx-arm64','osx-x64','win-x64','linux-musl-x64','linux-musl-arm64')

$feedJob = Start-Job -ScriptBlock {
    param($dir)
    $listener = New-Object System.Net.HttpListener
    for ($p = 19000; $p -lt 20000; $p++) {
        try {
            $listener.Prefixes.Clear()
            $listener.Prefixes.Add("http://127.0.0.1:$p/")
            $listener.Start()
            Set-Content (Join-Path $dir 'feed-port.txt') $p -Encoding ascii
            break
        } catch { }
    }
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
            } else { $ctx.Response.StatusCode = 404 }
            $ctx.Response.Close()
        } catch { break }
    }
} -ArgumentList $feed

try {
    for ($i = 0; $i -lt 50; $i++) {
        if (Test-Path (Join-Path $feed 'feed-port.txt')) { break }
        Start-Sleep -Milliseconds 100
    }
    $feedPort = (Get-Content (Join-Path $feed 'feed-port.txt') -Raw).Trim()
    $assets = [ordered]@{}
    foreach ($rid in $rids) {
        $assets[$rid] = @{ url = "http://127.0.0.1:$feedPort/cli.zip"; sha256 = $sha; size = $size }
    }
    $manifest = @{ version = $Version; channel = 'stable'; products = @{ cli = @{ assets = $assets } } }
    ($manifest | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $feed 'release-manifest.json') -Encoding utf8

    $idDir = Join-Path $env:LOCALAPPDATA 'TitaniumCli'
    New-Item -ItemType Directory -Force -Path $idDir | Out-Null
    '{"tag":"0.0.1","channel":"Stable"}' | Set-Content (Join-Path $idDir 'installed-release.json') -Encoding utf8

    $env:TITANIUM_UPDATE_FEED = "http://127.0.0.1:$feedPort/release-manifest.json"
    $proc = Start-Process -FilePath $bin -ArgumentList @('update', '--channel', 'stable') -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -ne 0) { throw "ASSERT: published CLI update exited $($proc.ExitCode)" }

    $id = Get-Content (Join-Path $idDir 'installed-release.json') -Raw -ErrorAction SilentlyContinue
    if ($id -and ($id -notmatch [regex]::Escape($Version)) -and ($id -notmatch 'smoke')) {
        throw "ASSERT: installed-release.json was not updated to $Version"
    }
    Write-Host "cli upgrade from published ok (version=$Version)"
}
finally {
    Stop-Job $feedJob -ErrorAction SilentlyContinue
    Remove-Job $feedJob -Force -ErrorAction SilentlyContinue
    Remove-Item $feed -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:TITANIUM_UPDATE_FEED -ErrorAction SilentlyContinue
}
