#Requires -Version 5.1
param(
  [Parameter(Mandatory = $true)]
  [string]$Tag,

  [string]$Repo = 'justcoding121/titanium-web-proxy',

  [string]$Sha256SumsPath
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v') {
  $Tag = "v$Tag"
}
$Version = $Tag.TrimStart('v')
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-Sha256Map {
  param([string[]]$Lines)
  $map = @{}
  foreach ($line in $Lines) {
    $t = $line.Trim()
    if ($t -eq '' -or $t.StartsWith('#')) { continue }
    if ($t -match '^([A-Fa-f0-9]{64})\s+\*?(.+)$') {
      $map[$Matches[2].Trim()] = $Matches[1].ToUpperInvariant()
    }
  }
  return $map
}

if ($Sha256SumsPath) {
  $sumsText = Get-Content -LiteralPath $Sha256SumsPath -Raw -Encoding utf8
} else {
  $url = "https://github.com/$Repo/releases/download/$Tag/SHA256SUMS"
  Write-Host "Fetching $url"
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("twp-SHA256SUMS-$Tag.txt")
  Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
  $sumsText = Get-Content -LiteralPath $tmp -Raw -Encoding utf8
}

$sha = Get-Sha256Map -Lines ($sumsText -split "`r?`n")
$cliZip = 'Titanium.Cli-win-x64.zip'
$msi = 'TitaniumInspector-win-x64.msi'
foreach ($name in @($cliZip, $msi)) {
  if (-not $sha.ContainsKey($name)) {
    throw "SHA256SUMS missing entry for $name (tag $Tag)"
  }
}

$cliUrl = "https://github.com/$Repo/releases/download/$Tag/$cliZip"
$msiUrl = "https://github.com/$Repo/releases/download/$Tag/$msi"
$releaseNotes = "https://github.com/$Repo/releases/tag/$Tag"

function Update-Nuspec {
  param(
    [string]$Path,
    [string]$Version,
    [string]$ReleaseNotes
  )
  [xml]$xml = Get-Content -LiteralPath $Path -Raw
  $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
  $ns.AddNamespace('n', 'http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd')
  $meta = $xml.SelectSingleNode('//n:metadata', $ns)
  if (-not $meta) { throw "metadata missing in $Path" }
  $meta.SelectSingleNode('n:version', $ns).InnerText = $Version
  $rn = $meta.SelectSingleNode('n:releaseNotes', $ns)
  if ($rn) { $rn.InnerText = $ReleaseNotes }
  $xml.Save($Path)
}

function Update-InstallScript {
  param(
    [string]$Path,
    [string]$UrlProperty,
    [string]$Url,
    [string]$ChecksumProperty,
    [string]$Checksum
  )
  $text = Get-Content -LiteralPath $Path -Raw
  $text = [regex]::Replace(
    $text,
    "(?m)(\s*$UrlProperty\s*=\s*)'[^']*'",
    "`$1'$Url'"
  )
  $text = [regex]::Replace(
    $text,
    "(?m)(\s*$ChecksumProperty\s*=\s*)'[^']*'",
    "`$1'$Checksum'"
  )
  if (-not $text.EndsWith("`n")) { $text += "`n" }
  Set-Content -LiteralPath $Path -Value $text -NoNewline -Encoding utf8NoBOM
}

$cliNuspec = Join-Path $Root 'titanium-cli\titanium-cli.nuspec'
$cliInstall = Join-Path $Root 'titanium-cli\tools\chocolateyInstall.ps1'
$insNuspec = Join-Path $Root 'titanium-inspector\titanium-inspector.nuspec'
$insInstall = Join-Path $Root 'titanium-inspector\tools\chocolateyInstall.ps1'

Update-Nuspec -Path $cliNuspec -Version $Version -ReleaseNotes $releaseNotes
Update-Nuspec -Path $insNuspec -Version $Version -ReleaseNotes $releaseNotes
Update-InstallScript -Path $cliInstall -UrlProperty 'url64bit' -Url $cliUrl -ChecksumProperty 'checksum64' -Checksum $sha[$cliZip]
Update-InstallScript -Path $insInstall -UrlProperty 'url64bit' -Url $msiUrl -ChecksumProperty 'checksum64' -Checksum $sha[$msi]

Write-Host "Updated chocolatey stubs for $Tag ($Version)"
Write-Host "  $cliZip = $($sha[$cliZip])"
Write-Host "  $msi = $($sha[$msi])"
