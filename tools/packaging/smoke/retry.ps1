#Requires -Version 5.1
<#
.SYNOPSIS
  Windows twin of retry.sh. Infra failures retry twice. Assertion failures do
  not retry, except -Leg gui or kind, which get one retry. A pass on retry is flaky.
#>
param(
    [ValidateSet('assert', 'gui', 'kind')]
    [string] $Leg = 'assert',
    [Parameter(Mandatory = $true, Position = 0)]
    [scriptblock] $Command
)

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
    $logFile = Join-Path ([System.IO.Path]::GetTempPath()) ("smoke-retry-" + [guid]::NewGuid().ToString('n') + ".log")
    try {
        # A pipe (`*>&1 | Out-String`) leaves Inspector holding the driver's stdout.
        # The reader waits for that handle to close, so the job sits until it is cancelled.
        # A file does not. Same pattern as retry.sh.
        & $Command *> $logFile
        $code = $LASTEXITCODE
        if (-not $code) { $code = 0 }
        $log = if (Test-Path -LiteralPath $logFile) { Get-Content -LiteralPath $logFile -Raw } else { '' }
        if (-not $log) { $log = '' }
    } catch {
        $log = "$_"
        $code = 1
    } finally {
        Remove-Item -LiteralPath $logFile -Force -ErrorAction SilentlyContinue
    }
    Write-Host $log
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
