#Requires -Version 5.1
<#
.SYNOPSIS
  Compare a lifecycle-proof JSON file against expected-lifecycle.json.

.DESCRIPTION
  Proof rows: product, rid, format, phase, from_version, to_version,
  installed_version_after, state_restored.
  Every required tuple (entry with exception null) must appear in the proof.
  Named exceptions skip proof requirements for that entry entirely.
#>
param(
    [string] $ProofPath = '',
    [string] $ExpectedPath = '',
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$ciRoot = $PSScriptRoot
if (-not $ExpectedPath) { $ExpectedPath = Join-Path $ciRoot 'expected-lifecycle.json' }

function Get-RequiredTuples {
    param([string] $Path)
    $doc = Get-Content -Path $Path -Raw | ConvertFrom-Json
    $required = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $doc.entries) {
        if ($entry.exception) { continue }
        foreach ($phase in $entry.phases) {
            $key = "$($entry.product)|$($entry.rid)|$($entry.format)|$phase"
            if ($required -notcontains $key) { [void]$required.Add($key) }
        }
    }
    return ,$required
}

function Test-LifecycleProof {
    param([string] $Proof, [string] $Expected)
    if (-not (Test-Path $Expected)) { throw "missing expected file: $Expected" }
    if (-not (Test-Path $Proof)) { throw "missing proof file: $Proof" }

    $required = Get-RequiredTuples -Path $Expected
    $parsed = Get-Content -Path $Proof -Raw | ConvertFrom-Json
    $proofRows = if ($null -eq $parsed) { @() }
        elseif ($parsed -is [System.Array]) { $parsed }
        else { @($parsed) }
  $present = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in $proofRows) {
        $key = "$($row.product)|$($row.rid)|$($row.format)|$($row.phase)"
        [void]$present.Add($key)
    }

    $missing = @($required | Where-Object { -not $present.Contains($_) })
    if ($missing.Count -gt 0) {
        Write-Host 'missing lifecycle proof tuples:'
        foreach ($m in $missing) { Write-Host "  $m" }
        return $false
    }
    Write-Host "lifecycle proof ok ($($required.Count) tuples)"
    return $true
}

if ($SelfTest) {
    $tmpdir = Join-Path ([System.IO.Path]::GetTempPath()) ("twp-life-proof-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmpdir -Force | Out-Null
    try {
        $miniExpected = Join-Path $tmpdir 'expected-mini.json'
        $miniExpectedDoc = @{
            version = 1
            entries = @(
                @{
                    product   = 'cli'
                    rid       = 'win-x64'
                    format    = 'zip'
                    phases    = @('upgrade', 'uninstall')
                    exception = $null
                },
                @{
                    product   = 'cli'
                    rid       = 'linux-musl-x64'
                    format    = 'systemd'
                    phases    = @('upgrade')
                    exception = 'musl-no-systemd'
                }
            )
        }
        ($miniExpectedDoc | ConvertTo-Json -Depth 6) | Set-Content -Path $miniExpected -Encoding UTF8

        $proofRow = {
            param($Product, $Rid, $Format, $Phase)
            [pscustomobject]@{
                product                 = $Product
                rid                     = $Rid
                format                  = $Format
                phase                   = $Phase
                from_version            = '8.0.7'
                to_version              = '8.0.8'
                installed_version_after = '8.0.8'
                state_restored          = $true
            }
        }

        $complete = Join-Path $tmpdir 'complete.json'
        $incomplete = Join-Path $tmpdir 'incomplete.json'
        $completeRows = @(
            & $proofRow 'cli' 'win-x64' 'zip' 'upgrade'
            & $proofRow 'cli' 'win-x64' 'zip' 'uninstall'
        )
        $incompleteRows = @(& $proofRow 'cli' 'win-x64' 'zip' 'upgrade')
        [System.IO.File]::WriteAllText($complete, (ConvertTo-Json -InputObject $completeRows -Depth 6) + "`n")
        [System.IO.File]::WriteAllText($incomplete, (ConvertTo-Json -InputObject $incompleteRows -Depth 6) + "`n")

        if (Test-LifecycleProof -Proof $incomplete -Expected $miniExpected) {
            throw 'self-test: incomplete proof should fail'
        }
        if (-not (Test-LifecycleProof -Proof $complete -Expected $miniExpected)) {
            throw 'self-test: complete proof should pass'
        }

        $required = Get-RequiredTuples -Path $ExpectedPath
        if ($required.Count -lt 2) { throw 'expected-lifecycle.json has too few tuples' }

        Write-Host 'assert-lifecycle-proof self-test ok'
        exit 0
    }
    finally {
        Remove-Item -Path $tmpdir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $ProofPath) {
    throw 'Pass -ProofPath or -SelfTest'
}

if (Test-LifecycleProof -Proof $ProofPath -Expected $ExpectedPath) {
    exit 0
}
exit 1