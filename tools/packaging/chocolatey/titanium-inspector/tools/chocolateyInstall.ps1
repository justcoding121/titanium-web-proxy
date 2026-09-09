$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  fileType       = 'msi'
  url64bit       = 'https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.5/TitaniumInspector-win-x64.msi'
  checksum64     = '15179C71A50B7724F55F421025086E0DA798B6A18EC9004FE64F22E04002124E'
  checksumType64 = 'sha256'
  silentArgs     = '/qn /norestart'
  validExitCodes = @(0, 3010, 1641)
  softwareName   = 'Titanium Inspector*'
}

Install-ChocolateyPackage @packageArgs
