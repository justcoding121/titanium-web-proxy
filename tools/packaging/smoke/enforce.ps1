#Requires -Version 5.1
<#
.SYNOPSIS
  Soft-to-hard lifecycle enforcement wrapper.

.DESCRIPTION
  Usage: enforce.ps1 <name> -- <cmd...>
  If LIFECYCLE_ENFORCE is not "true", failures become a warning + step summary row + exit 0.
  If LIFECYCLE_ENFORCE=true, failures exit non-zero.
#>
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $RemainingArgs
)

$ErrorActionPreference = 'Stop'

function Write-LifecycleSummary {
    param([string] $StepName, [int] $ExitCode, [string] $Detail)
    $row = "| ``$StepName`` | lifecycle | exit $ExitCode | $Detail |"
    if ($env:GITHUB_STEP_SUMMARY) {
        $header = '### lifecycle enforcement (advisory)'
        $body = Get-Content -Path $env:GITHUB_STEP_SUMMARY -Raw -ErrorAction SilentlyContinue
        if (-not $body -or $body -notmatch [regex]::Escape($header)) {
            Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value @(
                $header,
                '',
                '| step | class | exit | detail |',
                '| --- | --- | --- | --- |'
            )
        }
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $row
    }
}

function Invoke-Enforce {
    param([string] $StepName, [string[]] $Command)
    if ($Command.Count -lt 1) {
        throw 'usage: enforce.ps1 <name> -- <cmd...>'
    }
    $exe = $Command[0]
    $cmdArgs = @()
    if ($Command.Count -gt 1) { $cmdArgs = $Command[1..($Command.Count - 1)] }

    $log = New-TemporaryFile
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $cmdArgs -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $log.FullName -RedirectStandardError ($log.FullName + '.err')
        $code = $p.ExitCode
        if (Test-Path $log.FullName) { Get-Content -Path $log.FullName | Write-Host }
        $errPath = $log.FullName + '.err'
        if (Test-Path $errPath) { Get-Content -Path $errPath | Write-Host }
        if ($code -eq 0) { return 0 }

        $text = ''
        if (Test-Path $log.FullName) {
            $text += [string](Get-Content -Path $log.FullName -Raw -ErrorAction SilentlyContinue)
        }
        if (Test-Path $errPath) {
            $text += [string](Get-Content -Path $errPath -Raw -ErrorAction SilentlyContinue)
        }
        $detail = ($text -replace '\s+', ' ').Trim()
        if ($detail.Length -gt 400) { $detail = $detail.Substring(0, 400) }

        if ($env:LIFECYCLE_ENFORCE -eq 'true') {
            Write-Host "lifecycle enforce hard-fail: $StepName exit=$code" -ForegroundColor Red
            return $code
        }

        Write-Warning "Lifecycle advisory ($StepName): exit $code — $detail"
        Write-Host "::warning title=Lifecycle advisory ($StepName)::exit $code — $detail"
        Write-LifecycleSummary -StepName $StepName -ExitCode $code -Detail $detail
        return 0
    }
    finally {
        Remove-Item -Path $log.FullName, ($log.FullName + '.err') -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-EnforceSelfTest {
    Remove-Item Env:LIFECYCLE_ENFORCE -ErrorAction SilentlyContinue
    $soft = Invoke-Enforce -StepName 'self-test-soft' -Command @('cmd.exe', '/c', 'exit 1')
    if ($soft -ne 0) { throw 'self-test: soft mode should swallow false exit' }

    $env:LIFECYCLE_ENFORCE = 'true'
    $hard = Invoke-Enforce -StepName 'self-test-hard' -Command @('cmd.exe', '/c', 'exit 1')
    if ($hard -eq 0) { throw 'self-test: hard mode should fail on false exit' }

    $ok = Invoke-Enforce -StepName 'self-test-hard-ok' -Command @('cmd.exe', '/c', 'exit 0')
    if ($ok -ne 0) { throw 'self-test: hard mode should pass a zero exit' }

    Remove-Item Env:LIFECYCLE_ENFORCE -ErrorAction SilentlyContinue
    Write-Host 'enforce self-test ok'
}

$argv = @($RemainingArgs)
if ($argv.Count -eq 0) { $argv = @($args) }

if ($argv.Count -ge 1 -and $argv[0] -eq '--self-test') {
    Invoke-EnforceSelfTest
    exit 0
}

if ($argv.Count -lt 3 -or $argv[1] -ne '--') {
    throw 'usage: enforce.ps1 <name> -- <cmd...>  or  enforce.ps1 --self-test'
}
$name = $argv[0]
$cmd = $argv[2..($argv.Count - 1)]
$code = Invoke-Enforce -StepName $name -Command $cmd
exit $code
