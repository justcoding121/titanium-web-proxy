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
Write-Host "install-inspector: msiexec /qn $Msi"
# pwsh passes a single -ArgumentList string as one argv entry, so msiexec never
# sees /qn and waits on a UI that the runner does not have. Pass each switch separately.
# Poll HasExited: WaitForExit(int) did not return before the 50 minute job limit.
$p = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/i', $Msi, '/qn', '/norestart', '/l*v', $log) -PassThru
$deadline = (Get-Date).AddMinutes(8)
while (-not $p.HasExited) {
    if ((Get-Date) -gt $deadline) {
        Write-Host "install-inspector: msiexec still running after 8 minutes"
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 30 | ForEach-Object { Write-Host $_ } }
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        throw "msiexec timed out"
    }
    Start-Sleep -Seconds 10
}
Write-Host "install-inspector: msiexec exit $($p.ExitCode)"
if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) {
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 30 | ForEach-Object { Write-Host $_ } }
    throw "msiexec exited $($p.ExitCode)"
}
$exe = Join-Path ${env:ProgramFiles} 'Titanium Inspector\TitaniumInspector.exe'
if (-not (Test-Path $exe)) { throw "ASSERT: Inspector exe missing after install" }
Write-Output $exe
