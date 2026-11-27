#Requires -Version 5.1
<#
.SYNOPSIS
  Run a smoke leg and fail the caller when the leg exits non-zero.

.DESCRIPTION
  PowerShell does not treat `exit $code` from a called script as a terminating
  error. $ErrorActionPreference = 'Stop' only sees exceptions, so a Windows
  product-smoke step can stay green after retry.ps1 or enforce.ps1 fails.
  Dot-source this file and call Invoke-SmokeLeg. Run --self-test to prove a
  failing leg does not leave the caller green.
#>
$ErrorActionPreference = 'Stop'

function Invoke-SmokeLeg {
    param([Parameter(Mandatory = $true)][scriptblock] $Command)
    $global:LASTEXITCODE = 0
    & $Command
    $code = 0
    if ($null -ne $global:LASTEXITCODE) { $code = [int]$global:LASTEXITCODE }
    if ($code -ne 0) {
        throw "ASSERT: smoke leg failed exit=$code"
    }
}

if ($MyInvocation.InvocationName -eq '.') {
    return
}

if (@($args).Count -lt 1 -or $args[0] -ne '--self-test') {
    throw 'usage: dot-source invoke-leg.ps1, or invoke-leg.ps1 --self-test'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('smoke-leg-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
try {
    $failScript = Join-Path $root 'fail.ps1'
    $passScript = Join-Path $root 'pass.ps1'
    Set-Content -Path $failScript -Value "exit 4"
    Set-Content -Path $passScript -Value "exit 0"

    $threw = $false
    try {
        Invoke-SmokeLeg { & $failScript }
    } catch {
        if ($_.Exception.Message -notmatch 'exit=4') { throw }
        $threw = $true
    }
    if (-not $threw) { throw 'self-test: failing script leg left the caller green' }

    Invoke-SmokeLeg { & $passScript }

    $env:LIFECYCLE_ENFORCE = 'true'
    $enforce = Join-Path $PSScriptRoot 'enforce.ps1'
    $threw = $false
    try {
        Invoke-SmokeLeg { & $enforce 'caller-guard' -- cmd.exe /c exit 1 }
    } catch {
        if ($_.Exception.Message -notmatch 'exit=') { throw }
        $threw = $true
    }
    if (-not $threw) { throw 'self-test: enforce.ps1 hard-fail left the step green' }

    $retry = Join-Path $PSScriptRoot 'retry.ps1'
    $runner = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    $retryCmd = @"
& '$retry' -Leg assert -Command { cmd.exe /c exit 6 }
exit `$LASTEXITCODE
"@
    $threw = $false
    try {
        Invoke-SmokeLeg { & $runner -NoProfile -Command $retryCmd }
    } catch {
        if ($_.Exception.Message -notmatch 'exit=') { throw }
        $threw = $true
    }
    if (-not $threw) { throw 'self-test: retry.ps1 assertion failure left the step green' }

    Write-Host 'invoke-leg self-test ok'
} finally {
    Remove-Item Env:LIFECYCLE_ENFORCE -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
