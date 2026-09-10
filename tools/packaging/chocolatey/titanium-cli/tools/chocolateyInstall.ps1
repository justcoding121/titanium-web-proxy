$ErrorActionPreference = 'Stop'

$toolsDir = "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)"

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  unzipLocation  = $toolsDir
  url64bit       = 'https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.6/Titanium.Cli-win-x64.zip'
  checksum64     = '8C6D09C7293C9A3D205C64FB00D946A10AFA3379F072FA83A87EEFC4FE9425E2'
  checksumType64 = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs

Get-ChildItem -Path $toolsDir -Recurse -Filter '*.exe' | ForEach-Object {
  if ($_.Name -notin @('titanium.exe', 'twp.exe')) {
    New-Item -Path "$($_.FullName).ignore" -ItemType File -Force | Out-Null
  }
}
