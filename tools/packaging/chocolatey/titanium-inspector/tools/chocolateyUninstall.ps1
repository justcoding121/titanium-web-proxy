$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  softwareName   = 'Titanium Inspector*'
  fileType       = 'msi'
  silentArgs     = '/qn /norestart'
  validExitCodes = @(0, 3010, 1605, 1614, 1641)
}

[array]$keys = Get-UninstallRegistryKey -SoftwareName $packageArgs['softwareName']
if ($keys.Count -eq 1) {
  $packageArgs['file'] = "$($keys[0].UninstallString)"
  if ($keys[0].UninstallString -match '\{[0-9A-Fa-f-]+\}') {
    $packageArgs['silentArgs'] = "$($Matches[0]) $($packageArgs['silentArgs'])"
    $packageArgs['file'] = ''
  }
  Uninstall-ChocolateyPackage @packageArgs
} elseif ($keys.Count -eq 0) {
  Write-Warning "$($packageArgs['packageName']) has already been uninstalled by other means."
} else {
  Write-Warning "$($keys.Count) matches found for $($packageArgs['packageName'])!"
  Write-Warning 'Please alert the package maintainer so the following keys can be checked:'
  $keys | ForEach-Object { Write-Warning "- $($_.DisplayName): $($_.UninstallString)" }
}
