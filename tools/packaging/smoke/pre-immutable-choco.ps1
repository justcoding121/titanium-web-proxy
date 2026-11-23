#Requires -Version 5.1
<#
.SYNOPSIS
  Pack both Chocolatey stubs and install them from a local HTTP server.
  The published GitHub URLs are rewritten to that server so the check does not
  depend on a release that has not been published yet.
#>
param(
    [Parameter(Mandatory = $true)][string] $CliZip,
    [Parameter(Mandatory = $true)][string] $InspectorMsi,
    [Parameter(Mandatory = $true)][string] $Tag
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$work = Join-Path ([IO.Path]::GetTempPath()) ("choco-smoke-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $work, (Join-Path $work 'files') | Out-Null
Copy-Item -Recurse (Join-Path $root 'tools\packaging\chocolatey') (Join-Path $work 'chocolatey')
$cliName = 'Titanium.Cli-win-x64.zip'
$msiName = 'TitaniumInspector-win-x64.msi'
Copy-Item $CliZip (Join-Path $work "files\$cliName")
Copy-Item $InspectorMsi (Join-Path $work "files\$msiName")
$sums = Join-Path $work 'SHA256SUMS'
$lines = @(
    "$((Get-FileHash (Join-Path $work "files\$cliName") -Algorithm SHA256).Hash) $cliName"
    "$((Get-FileHash (Join-Path $work "files\$msiName") -Algorithm SHA256).Hash) $msiName"
)
Set-Content -LiteralPath $sums -Value $lines -Encoding ascii
& (Join-Path $work 'chocolatey\bump-packages.ps1') -Tag $Tag -Sha256SumsPath $sums
$port = 8877
$base = "http://127.0.0.1:$port"
foreach ($pair in @(
    @{ Script = (Join-Path $work 'chocolatey\titanium-cli\tools\chocolateyInstall.ps1'); File = $cliName },
    @{ Script = (Join-Path $work 'chocolatey\titanium-inspector\tools\chocolateyInstall.ps1'); File = $msiName }
)) {
    $text = Get-Content -LiteralPath $pair.Script -Raw
    $text = [regex]::Replace($text, "https://github.com/[^']+$([regex]::Escape($pair.File))", "$base/$($pair.File)")
    Set-Content -LiteralPath $pair.Script -Value $text -NoNewline -Encoding utf8
}
$listener = Start-Process python -ArgumentList "-m","http.server",$port,"--bind","127.0.0.1","--directory",(Join-Path $work 'files') -PassThru -WindowStyle Hidden
try {
    Push-Location (Join-Path $work 'chocolatey\titanium-cli')
    choco pack --output-directory $work
    Pop-Location
    Push-Location (Join-Path $work 'chocolatey\titanium-inspector')
    choco pack --output-directory $work
    Pop-Location
    $nupkgs = Get-ChildItem $work -Filter *.nupkg
    if ($nupkgs.Count -lt 2) { throw "ASSERT: choco pack did not produce both packages" }
    # The Inspector proof installed this MSI already. The package install is the check,
    # and msiexec fails that second install. Remove the earlier copy first.
    $removed = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/x', $InspectorMsi, '/qn', '/norestart') -PassThru -Wait
    Write-Host "pre-choco msiexec /x exit $($removed.ExitCode)"
    # 8.0.7-beta is a prerelease. Without --pre, Chocolatey ignores the nupkg it just packed
    # and reports that the package was not found.
    choco install titanium-cli titanium-inspector -y --pre --no-progress --accept-license --source $work
    if ($LASTEXITCODE -ne 0) { throw "ASSERT: local choco install failed $LASTEXITCODE" }
    Write-Host 'chocolatey local install ok'
}
finally {
    if ($listener -and -not $listener.HasExited) { Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue }
    choco uninstall titanium-cli titanium-inspector -y --no-progress 2>$null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
