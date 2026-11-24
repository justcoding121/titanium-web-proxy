#Requires -Version 5.1
param(
    [switch] $SelfTest,
    [string] $CliDir,
    [string] $PlusZip,
    [string] $Titanium
)
$ErrorActionPreference = 'Stop'

$StaleName = 'Titanium.Xds.NOTICE'
$MarkerName = 'smoke-plus-upgrade.marker'
$PlusVersionA = '0.0.0-smoke-plus-a'
$PlusVersionB = '0.0.0-smoke-plus-b'

function Invoke-SelfTest {
    $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
  try {
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        if ($port -le 0) { throw 'ASSERT: self-test port missing' }
        Write-Host "plus-lifecycle self-test ok (port=$port)"
    }
    finally { try { $listener.Stop() } catch { } }
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

if ([string]::IsNullOrWhiteSpace($CliDir) -or [string]::IsNullOrWhiteSpace($PlusZip) -or [string]::IsNullOrWhiteSpace($Titanium)) {
    throw 'CliDir, PlusZip, and Titanium are required unless -SelfTest'
}

$CliDir = (Resolve-Path $CliDir).Path
$PlusZip = (Resolve-Path $PlusZip).Path
$Titanium = (Resolve-Path $Titanium).Path

function Invoke-ApplyPlusFeed {
    param([string] $ZipPath, [string] $Version)
    $feed = Join-Path $env:TEMP ("plus-lifecycle-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $feed | Out-Null
    Copy-Item $ZipPath (Join-Path $feed 'plus.zip')
    $hash = (Get-FileHash (Join-Path $feed 'plus.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item (Join-Path $feed 'plus.zip')).Length
    $port = 8768
    $manifest = @{
        version = $Version
        channel = 'beta'
        products = @{
            plus = @{
                version = $Version
                bundle = @{
                    url = "http://127.0.0.1:$port/plus.zip"
                    sha256 = $hash
                    size = $size
                }
            }
        }
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $feed 'release-manifest.json') -Encoding utf8
    $listener = Start-Process python -ArgumentList '-m','http.server',$port,'--bind','127.0.0.1','--directory',$feed -PassThru -WindowStyle Hidden
    try {
        $ok = $false
        foreach ($i in 1..20) {
            try {
                Invoke-WebRequest -Uri "http://127.0.0.1:$port/release-manifest.json" -UseBasicParsing | Out-Null
                $ok = $true
                break
            } catch { Start-Sleep -Milliseconds 200 }
        }
        if (-not $ok) { throw 'ASSERT: local Plus feed did not start' }
        $env:TITANIUM_UPDATE_FEED = "http://127.0.0.1:$port/release-manifest.json"
        & $Titanium update --plus --channel beta
        if ($LASTEXITCODE -ne 0) { throw "ASSERT: titanium update --plus failed $LASTEXITCODE" }
    }
    finally {
        if ($listener -and -not $listener.HasExited) { Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item -Recurse -Force $feed -ErrorAction SilentlyContinue
    }
}

function New-PlusUpgradeZip {
    param([string] $SourceZip, [string] $DestZip)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = Join-Path $env:TEMP ("plus-b-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $archive = [System.IO.Compression.ZipFile]::OpenRead($SourceZip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $name = [IO.Path]::GetFileName($entry.FullName)
            if ($name -eq $StaleName) { continue }
            $destPath = Join-Path $stage $name
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destPath, $true)
        }
    }
    finally { $archive.Dispose() }
    Set-Content -LiteralPath (Join-Path $stage $MarkerName) -Value 'plus-lifecycle-upgrade' -Encoding utf8NoBOM
    $names = @(Get-ChildItem -LiteralPath $stage -File | ForEach-Object { $_.Name } | Sort-Object -Unique)
    if ($names -notcontains 'Titanium.Plus.files') { $names += 'Titanium.Plus.files' }
    $names = $names | Sort-Object
    [IO.File]::WriteAllLines((Join-Path $stage 'Titanium.Plus.files'), $names)
    if (Test-Path $DestZip) { Remove-Item $DestZip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $DestZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}

$plusA = Join-Path $env:TEMP ("plus-a-" + [guid]::NewGuid().ToString('n') + '.zip')
$plusB = Join-Path $env:TEMP ("plus-b-" + [guid]::NewGuid().ToString('n') + '.zip')
Copy-Item $PlusZip $plusA -Force

if (-not (Test-Path (Join-Path $CliDir 'Titanium.Plus.dll'))) {
    Invoke-ApplyPlusFeed -ZipPath $plusA -Version $PlusVersionA
}

New-PlusUpgradeZip -SourceZip $plusA -DestZip $plusB
Invoke-ApplyPlusFeed -ZipPath $plusB -Version $PlusVersionB

if (Test-Path (Join-Path $CliDir $StaleName)) {
    throw "ASSERT: stale Plus file still present after upgrade: $StaleName"
}
if (-not (Test-Path (Join-Path $CliDir $MarkerName))) {
    throw "ASSERT: upgrade marker missing: $MarkerName"
}

$ver = & $Titanium version
$verText = (@($ver) | ForEach-Object { "$_" }) -join "`n"
Write-Host $verText
if ($verText -notmatch 'Plus: [0-9]') { throw 'ASSERT: version did not report Plus after upgrade' }

& $Titanium update --remove-plus
if ($LASTEXITCODE -ne 0) { throw 'ASSERT: remove-plus failed' }
if (Test-Path (Join-Path $CliDir 'Titanium.Plus.dll')) {
    throw 'ASSERT: Titanium.Plus.dll still present after remove-plus'
}
$ver2 = & $Titanium version
$ver2Text = (@($ver2) | ForEach-Object { "$_" }) -join "`n"
Write-Host $ver2Text
if ($ver2Text -match 'Plus: [0-9]') { throw 'ASSERT: version still reports Plus after remove-plus' }
& $Titanium version
if ($LASTEXITCODE -ne 0) { throw 'ASSERT: titanium version failed after remove-plus' }

Remove-Item $plusA, $plusB -Force -ErrorAction SilentlyContinue
Write-Host 'plus-lifecycle ok'