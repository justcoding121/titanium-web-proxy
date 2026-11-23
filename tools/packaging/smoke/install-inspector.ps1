#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string] $Msi,
    [Parameter(Mandatory = $true)][string] $LogDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$sums = Join-Path (Split-Path -Parent $Msi) 'SHA256SUMS'
if (Test-Path $sums) {
    $expected = $null
    $name = Split-Path -Leaf $Msi
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ($line -match '^([A-Fa-f0-9]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $name) {
            $expected = $Matches[1]
        }
    }
    if (-not $expected) { throw "ASSERT: SHA256SUMS has no entry for $name" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Msi).Hash
    if ($actual -ne $expected) { throw "ASSERT: checksum mismatch for $name" }
}
$log = Join-Path $LogDir 'msiexec.log'
$p = Start-Process msiexec -ArgumentList "/i `"$Msi`" /qn /norestart /l*v `"$log`"" -PassThru
if (-not $p.WaitForExit(480000)) { throw "msiexec timed out" }
if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { throw "msiexec exited $($p.ExitCode)" }
$exe = Join-Path ${env:ProgramFiles} 'Titanium Inspector\TitaniumInspector.exe'
if (-not (Test-Path $exe)) { throw "ASSERT: Inspector exe missing after install" }
Write-Output $exe
