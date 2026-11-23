#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string] $CliDir,
    [Parameter(Mandatory = $true)][string] $PlusZip,
    [Parameter(Mandatory = $true)][string] $Titanium
)
$ErrorActionPreference = 'Stop'
$CliDir = (Resolve-Path $CliDir).Path
$PlusZip = (Resolve-Path $PlusZip).Path
$feed = Join-Path ([IO.Path]::GetTempPath()) ("plus-feed-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $feed | Out-Null
Copy-Item $PlusZip (Join-Path $feed 'plus.zip')
$hash = (Get-FileHash -Algorithm SHA256 (Join-Path $feed 'plus.zip')).Hash.ToLowerInvariant()
$size = (Get-Item (Join-Path $feed 'plus.zip')).Length
$port = 8766
$manifest = @{
    version = '0.0.0-smoke'
    channel = 'beta'
    products = @{
        plus = @{
            version = '0.0.0-smoke'
            bundle = @{
                url = "http://127.0.0.1:$port/plus.zip"
                sha256 = $hash
                size = $size
            }
        }
    }
}
$manifestPath = Join-Path $feed 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
$listener = Start-Process python -ArgumentList "-m","http.server",$port,"--bind","127.0.0.1","--directory",$feed -PassThru -WindowStyle Hidden
try {
    $ok = $false
    foreach ($i in 1..20) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:$port/release-manifest.json" -UseBasicParsing | Out-Null
            $ok = $true
            break
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ok) { throw "ASSERT: local Plus feed did not start" }
    $env:TITANIUM_UPDATE_FEED = "http://127.0.0.1:$port/release-manifest.json"
    & $Titanium update --plus --channel beta
    if ($LASTEXITCODE -ne 0) { throw "ASSERT: titanium update --plus failed $LASTEXITCODE" }
    if (-not (Test-Path (Join-Path $CliDir 'Titanium.Plus.dll'))) {
        throw "ASSERT: Titanium.Plus.dll was not applied beside the CLI"
    }
    $list = Join-Path $CliDir 'Titanium.Plus.files'
    if (Test-Path $list) {
        foreach ($name in Get-Content $list) {
            $name = $name.Trim()
            if (-not $name) { continue }
            if ($name -in @('titanium', 'titanium.exe', 'twp', 'twp.exe')) {
                throw "ASSERT: Plus bundle collides with MIT CLI file $name"
            }
            if (-not (Test-Path (Join-Path $CliDir $name))) {
                throw "ASSERT: Plus file list entry missing after apply: $name"
            }
        }
    }
    $ver = & $Titanium version
    if ($LASTEXITCODE -ne 0) { throw "ASSERT: titanium version failed" }
    Write-Host $ver
    if ($ver -notmatch 'Plus:') { throw "ASSERT: titanium version did not report Plus" }
    $hashOut = & $Titanium plus hash-password --user alice --role operator smoke-secret
    if ($LASTEXITCODE -ne 0) { throw "ASSERT: plus hash-password failed" }
    Write-Host $hashOut
    if ($hashOut -notmatch '(?m)^alice:operator:') { throw "ASSERT: plus hash-password did not print a user line" }
    Write-Host 'plus apply ok'
}
finally {
    if ($listener -and -not $listener.HasExited) { Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $feed -ErrorAction SilentlyContinue
}
