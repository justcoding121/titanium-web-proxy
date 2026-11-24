# Windows companion to cli-update-ignores-proxy.sh
param(
    [Parameter(Mandatory = $true)][string] $CliDir,
    [Parameter(Mandatory = $true)][string] $CliZip,
    [Parameter(Mandatory = $true)][string] $Titanium,
    [string] $Version = '8.0.9-smoke'
)
$ErrorActionPreference = 'Stop'
$CliZip = (Resolve-Path $CliZip).Path
$Titanium = (Resolve-Path $Titanium).Path

$feed = Join-Path $env:TEMP ("twp-update-feed-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $feed | Out-Null
Copy-Item $CliZip (Join-Path $feed 'cli.zip') -Force
$sha = (Get-FileHash (Join-Path $feed 'cli.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item (Join-Path $feed 'cli.zip')).Length
$rids = @('linux-x64','linux-arm64','osx-arm64','osx-x64','win-x64','linux-musl-x64','linux-musl-arm64')

$hitsFile = Join-Path $env:TEMP ("twp-hits-" + [guid]::NewGuid().ToString('n') + '.txt')
'0' | Set-Content $hitsFile -Encoding ascii

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

$feedPort = 8766
$feedJob = Start-Job -ScriptBlock {
    param($dir, $port)
    Set-Location $dir
    & python -m http.server $port --bind 127.0.0.1
} -ArgumentList $feed, $feedPort

try {
    $portFile = Join-Path (Split-Path $hitsFile) 'proxy-port.txt'
    for ($i = 0; $i -lt 50; $i++) {
        if (Test-Path $portFile) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path $portFile)) { throw 'proxy port file missing' }
    $proxyPort = (Get-Content $portFile -Raw).Trim()

    for ($i = 0; $i -lt 50; $i++) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:$feedPort/" -UseBasicParsing -TimeoutSec 1 | Out-Null
            break
        } catch { Start-Sleep -Milliseconds 100 }
    }

    $assets = [ordered]@{}
    foreach ($rid in $rids) {
        $assets[$rid] = @{ url = "http://127.0.0.1:$feedPort/cli.zip"; sha256 = $sha; size = $size }
    }
    $manifest = @{ version = $Version; channel = 'beta'; products = @{ cli = @{ assets = $assets } } }
    ($manifest | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $feed 'release-manifest.json') -Encoding utf8

    $idDir = Join-Path $env:LOCALAPPDATA 'TitaniumCli'
    New-Item -ItemType Directory -Force -Path $idDir | Out-Null
    '{"tag":"0.0.1","channel":"Beta"}' | Set-Content (Join-Path $idDir 'installed-release.json') -Encoding utf8

    $env:HTTP_PROXY = "http://127.0.0.1:$proxyPort"
    $env:HTTPS_PROXY = $env:HTTP_PROXY
    $env:http_proxy = $env:HTTP_PROXY
    $env:https_proxy = $env:HTTP_PROXY
    $env:NO_PROXY = ''
    $env:no_proxy = ''
    $env:TITANIUM_UPDATE_FEED = "http://127.0.0.1:$feedPort/release-manifest.json"

    & $Titanium update --channel beta
    if ($LASTEXITCODE -ne 0) { throw "titanium update exited $LASTEXITCODE" }

    $count = [int]((Get-Content $hitsFile -Raw).Trim())
    if ($count -ne 0) {
        throw "ASSERT: update used loopback HTTPS_PROXY ($count hits)"
    }
    Write-Host "cli update ignored loopback proxy ok (hits=0 version=$Version)"
}
finally {
    Stop-Job $proxyJob, $feedJob -ErrorAction SilentlyContinue
    Remove-Job $proxyJob, $feedJob -Force -ErrorAction SilentlyContinue
    Remove-Item $feed, $hitsFile -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:HTTP_PROXY, Env:HTTPS_PROXY, Env:http_proxy, Env:https_proxy, Env:TITANIUM_UPDATE_FEED -ErrorAction SilentlyContinue
}
