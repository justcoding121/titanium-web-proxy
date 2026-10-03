# Fail the leg unless every arm this host resolved for the mode and shard produced at least
# one CSV row in every repeat. Gate scripts skip pairs whose arms are absent, so without this
# a wedged arm would leave a one-row-per-job leg green.
param(
    [Parameter(Mandatory)][string] $ResultsDir,
    [Parameter(Mandatory)][string] $Mode,
    [string] $ArmShard = 'all',
    [int] $Repeats = 1
)

$ErrorActionPreference = 'Stop'
$probeDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$probeArgs = @('run', '--no-build', '-c', 'Release', '--', '--ramp', '--mode', $Mode, '--print-arms')
if ($ArmShard -and $ArmShard -ne 'all') {
    $probeArgs += @('--arm-shard', $ArmShard)
}

Push-Location $probeDir
try {
    $expected = @(& dotnet @probeArgs 2>$null | Where-Object { $_ -and $_ -notmatch '^\[' })
}
finally {
    Pop-Location
}

if ($expected.Count -eq 0) {
    throw "No arms resolved for mode $Mode shard '$ArmShard'. A row with no runnable arms is a failure."
}

$csv = Get-ChildItem -Path $ResultsDir -Filter 'rps-ramp-*.csv' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $csv) { throw "No ramp CSV found in $ResultsDir" }

$byArm = Import-Csv $csv.FullName | Group-Object arm -AsHashTable -AsString
$missing = @()
foreach ($arm in $expected) {
    $rows = if ($byArm.ContainsKey($arm)) { $byArm[$arm].Count } else { 0 }
    # Early-stop cuts concurrency steps, never whole repeats, so each repeat leaves rows.
    if ($rows -lt $Repeats) { $missing += "$arm ($rows rows, need >= $Repeats)" }
}

if ($missing) {
    throw ("Ramp incomplete: {0} of {1} arms are short: {2}" -f $missing.Count, $expected.Count, ($missing -join ', '))
}

Write-Host ("Ramp complete: {0} arms, each with >= {1} rows ({2})" -f $expected.Count, $Repeats, $csv.Name)
