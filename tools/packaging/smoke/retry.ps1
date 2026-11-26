#Requires -Version 5.1
<#
.SYNOPSIS
  Windows twin of retry.sh. Infra failures retry twice. Assertion failures do
  not retry, except -Leg gui or kind, which get one retry. A pass on retry is flaky.
#>
param(
    [switch] $SelfTest,
    [ValidateSet('assert', 'gui', 'kind')]
    [string] $Leg = 'assert',
    [Parameter(Position = 0)]
    [scriptblock] $Command
)

if ($SelfTest) {
    $runner = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    $retryCmd = @"
& '$PSCommandPath' -Leg assert -Command { cmd.exe /c exit 6 }
exit `$LASTEXITCODE
"@
    & $runner -NoProfile -Command $retryCmd | Out-Host
    if ($LASTEXITCODE -eq 0) { throw 'self-test: retry.ps1 failing assert leg left the caller green' }
    if ($LASTEXITCODE -ne 6) { throw "self-test: expected retry exit 6, got $LASTEXITCODE" }
    . (Join-Path $PSScriptRoot 'invoke-leg.ps1')
    $threw = $false
    try {
        Invoke-SmokeLeg { & $runner -NoProfile -Command $retryCmd }
    } catch {
        $threw = $true
    }
    if (-not $threw) { throw 'self-test: Invoke-SmokeLeg swallowed a failing retry.ps1 leg' }
    Write-Host 'retry self-test ok'
    exit 0
}

if (-not $Command) { throw 'usage: retry.ps1 -Leg assert -Command { ... }  or  retry.ps1 -SelfTest' }

$ErrorActionPreference = 'Continue'
$maxTries = 1
if ($Leg -eq 'gui' -or $Leg -eq 'kind') { $maxTries = 2 }

function Get-SmokeClass([string] $Text) {
    $lower = $Text.ToLowerInvariant()
    if ($lower.Contains('assert:')) { return 'assertion' }
    $infra = @(
        'hdiutil: ',
        'resource temporarily unavailable',
        'could not resolve host',
        'temporary failure in name resolution',
        'connection reset',
        'connection timed out',
        'tls handshake timeout',
        'failed to download',
        'failed to pull',
        'econnreset',
        'dial tcp',
        'no space left on device'
    )
    foreach ($needle in $infra) {
        if ($lower.Contains($needle)) { return 'infra' }
    }
    if ($lower -match 'hdiutil: .*busy') { return 'infra' }
    return 'assertion'
}

$attempt = 1
while ($true) {
    $lines = New-Object System.Collections.Generic.List[string]
    try {
        # Stream each line. Out-String holds the whole proof until the driver exits, so a
        # hang looks like fifty minutes of silence. Inspector's own stdout is redirected
        # in the driver and does not keep this pipeline open.
        & $Command *>&1 | ForEach-Object {
            $text = "$_"
            Write-Host $text
            $lines.Add($text)
        }
        $code = $LASTEXITCODE
        if (-not $code) { $code = 0 }
        $log = ($lines -join "`n")
    } catch {
        $log = "$_"
        Write-Host $log
        $code = 1
    }
    if ($code -eq 0) {
        if ($attempt -gt 1) {
            Write-Host "flaky: $Leg passed on attempt $attempt"
            if ($env:GITHUB_STEP_SUMMARY) {
                Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "### flaky`nLeg $Leg passed on attempt $attempt."
            }
        }
        exit 0
    }
    $class = Get-SmokeClass $log
    Write-Host "smoke-classify attempt=$attempt class=$class exit=$code leg=$Leg"
    $retry = $false
    if ($class -eq 'infra' -and $attempt -lt 3) { $retry = $true }
    if ($class -eq 'assertion' -and $Leg -ne 'assert' -and $attempt -lt $maxTries) { $retry = $true }
    if (-not $retry) { exit $code }
    $attempt++
    Start-Sleep -Seconds (5 * $attempt)
}
