$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  fileType       = 'msi'
  url64bit       = 'https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.6/TitaniumInspector-win-x64.msi'
  checksum64     = '71A1A4CE095540EC5AF2765D6663EFC8540AC04A9B02B98F25CF3775FE18B5B7'
  checksumType64 = 'sha256'
  silentArgs     = '/qn /norestart'
  validExitCodes = @(0, 3010, 1641)
  softwareName   = 'Titanium Inspector*'
}

Install-ChocolateyPackage @packageArgs
