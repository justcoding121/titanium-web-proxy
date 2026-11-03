# One-time test-machine setup. Default does nothing to the firewall.
# Loopback allow rules and turning off listen notifications are separate opt-in switches.
# The script never elevates itself. An elevated PowerShell is required for firewall changes.
param(
    [switch]$Ci,
    [switch]$LoopbackRules,
    [switch]$NotifyOnListenOff,
    [switch]$AllowWildcardBindTests,
    [switch]$Remove,
    [switch]$DefenderExclusions
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host "Loopback traffic is not filtered. Default inbound stays Block."
Write-Host "Option A: program rules that allow inbound only from 127.0.0.1 and ::1 for dotnet.exe."
Write-Host "Option B: NotifyOnListen False, so unlisted apps are blocked with no dialog. That also affects other apps."
Write-Host "Default is neither. Pass -LoopbackRules or -NotifyOnListenOff. -Ci selects option A for a hosted runner."

if ($AllowWildcardBindTests) {
    [Environment]::SetEnvironmentVariable('TITANIUM_ALLOW_WILDCARD_BIND_TESTS', '1', 'User')
    Write-Host "Set user environment TITANIUM_ALLOW_WILDCARD_BIND_TESTS=1"
}

$wantsFirewall = $Ci -or $LoopbackRules -or $NotifyOnListenOff -or $Remove -or $DefenderExclusions
if (-not $wantsFirewall) {
    Write-Host "No firewall change requested."
    exit 0
}

if (-not (Test-Elevated)) {
    Write-Host "This command needs an elevated PowerShell window:"
    Write-Host "powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Ci:$Ci -LoopbackRules:$LoopbackRules -NotifyOnListenOff:$NotifyOnListenOff -Remove:$Remove"
    exit 2
}

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$ruleName = 'Titanium test loopback dotnet'

if ($Remove) {
    Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction SilentlyContinue |
        Where-Object {
            $_.DisplayName -eq $ruleName -or
            $_.DisplayName -eq "$ruleName (IPv4)" -or
            $_.DisplayName -eq "$ruleName (IPv6)"
        } |
        Remove-NetFirewallRule -PolicyStore PersistentStore
    Write-Host "Removed $ruleName rules"
}

if ($Ci -or $LoopbackRules) {
    # New-NetFirewallRule rejects ::1 as RemoteAddress ("loopback IPv6 address was specified").
    # IPv4 loopback is enough for hosted runners; tests bind 127.0.0.1.
    $display = "$ruleName (IPv4)"
    $existing = Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq $display -or $_.DisplayName -eq $ruleName }
    if (-not $existing) {
        New-NetFirewallRule -PolicyStore PersistentStore -DisplayName $display -Direction Inbound -Action Allow `
            -Program $dotnet -RemoteAddress '127.0.0.1' -Profile Any | Out-Null
        Write-Host "Added $display for $dotnet"
    } else {
        Write-Host "$display already exists"
    }
}

if ($NotifyOnListenOff) {
    Set-NetFirewallProfile -Profile Domain, Private, Public -NotifyOnListen False
    Write-Host "NotifyOnListen is now False. Default inbound is still Block."
}

if ($DefenderExclusions) {
    Write-Host "Defender exclusions are opt-in and were requested."
    Add-MpPreference -ExclusionPath (Join-Path (Get-Location) 'tests')
}

exit 0
