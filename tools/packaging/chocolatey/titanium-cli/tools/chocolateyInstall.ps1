$ErrorActionPreference = 'Stop'

$toolsDir = "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)"

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  unzipLocation  = $toolsDir
  url64bit       = 'https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.9/Titanium.Cli-win-x64.zip'
  checksum64     = 'EF79522355392ABB805C27F98426DB22DDB73BFE759500FFF4B41320F9B48DAB'
  checksumType64 = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs

Get-ChildItem -Path $toolsDir -Recurse -Filter '*.exe' | ForEach-Object {
  if ($_.Name -notin @('titanium.exe', 'twp.exe')) {
    New-Item -Path "$($_.FullName).ignore" -ItemType File -Force | Out-Null
  }
}
