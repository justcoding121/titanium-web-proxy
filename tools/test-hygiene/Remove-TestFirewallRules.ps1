# Remove prompt-created Windows Defender Firewall rules for test hosts and
# Titanium-built executables. Default is a dry run. -Apply needs an elevated shell.
# This script never elevates itself and never removes stock rule groups or
# third-party application rules.
param(
    [switch]$Apply,
    [string]$BackupDir = '',
    [string]$Restore = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-IsElevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$builtNames = @(
    'titanium.exe',
    'titaniuminspector.exe',
    'rpsloadprobe.exe',
    'rpsallocticks.exe',
    'inspectormemoryprobe.exe',
    'inspectordesktopprobe.exe',
    'chromeloadprobe.exe',
    'coldstartprobe.exe',
    'h2externalrepro.exe',
    'cliqaprobe.exe',
    'quic-bind-repro.exe',
    '_wafdiag.exe'
)

function Test-NeverRemoveProgram {
    param([string]$Program)
    if ([string]::IsNullOrWhiteSpace($Program)) { return $true }
    $name = [IO.Path]::GetFileName($Program).ToLowerInvariant()
    $path = $Program.ToLowerInvariant()
    if ($name -in @('node.exe', 'adb.exe', 'firefox.exe', 'zoom.exe', 'msedge.exe', 'outlook.exe', 'cursor.exe', 'devenv.exe', 'svchost.exe', 'teamviewer.exe', 'teamviewer_service.exe', 'com.docker.backend.exe')) {
        return $true
    }
    if ($path -match 'teamviewer|\\zoom\\|docker\\docker|mozilla firefox|microsoft\\edge|office16\\outlook|android|\\cursor\\|visual studio') {
        return $true
    }
    return $false
}

function Test-CandidateProgram {
    param([string]$Program)
    if ([string]::IsNullOrWhiteSpace($Program) -or $Program -eq 'Any' -or $Program -eq 'System') { return $false }
    if (Test-NeverRemoveProgram $Program) { return $false }
    $name = [IO.Path]::GetFileName($Program).ToLowerInvariant()
    $path = $Program.ToLowerInvariant()
    if ($name -eq 'testhost.exe') { return $true }
    if ($name -eq 'dotnet.exe' -and $path -like 'c:\program files\dotnet\*') { return $true }
    if ($builtNames -contains $name) { return $true }
    if ($name -like 'titanium.web.proxy.examples.*.exe') { return $true }
    $temp = $env:TEMP.ToLowerInvariant()
    if ($path -like 'c:\work\*' -or $path -like ($temp + '\*') -or $path -like 'c:\users\jehon\appdata\local\temp\*') {
        return $true
    }
    return $false
}

if ($Restore) {
    if (-not (Test-IsElevated)) {
        Write-Host "Restore needs an elevated PowerShell."
        exit 2
    }
    $rows = Get-Content -LiteralPath (Join-Path $Restore 'firewall-candidates.json') -Raw | ConvertFrom-Json
    foreach ($row in $rows) {
        $existing = Get-NetFirewallRule -PolicyStore PersistentStore -Name $row.name -ErrorAction SilentlyContinue
        if ($existing) { continue }
        New-NetFirewallRule -PolicyStore PersistentStore -Name $row.name -DisplayName $row.displayName -Direction $row.direction -Action $row.action -Program $row.program -Profile $row.profile -Enabled True | Out-Null
        Write-Host "Restored $($row.displayName) $($row.program)"
    }
    Write-Host "Restored from JSON. Full policy backup, if present: $(Join-Path $Restore 'firewall-before.wfw')"
    Write-Host "Import it with: netsh advfirewall import `"$(Join-Path $Restore 'firewall-before.wfw')`""
    exit 0
}

$rules = @(Get-NetFirewallRule -PolicyStore PersistentStore)
$apps = @(Get-NetFirewallApplicationFilter -PolicyStore PersistentStore)
$appMap = @{}
foreach ($app in $apps) { $appMap[$app.InstanceID] = [string]$app.Program }

$candidates = New-Object System.Collections.Generic.List[object]
$review = New-Object System.Collections.Generic.List[object]
foreach ($rule in $rules) {
    $program = ''
    if ($appMap.ContainsKey($rule.InstanceID)) { $program = $appMap[$rule.InstanceID] }
    $grouped = -not [string]::IsNullOrWhiteSpace([string]$rule.Group)
    if ($grouped) { continue }
    if ([string]::IsNullOrWhiteSpace($program) -or $program -eq 'Any') { continue }
    $row = [pscustomobject]@{
        name        = [string]$rule.Name
        displayName = [string]$rule.DisplayName
        program     = $program
        action      = [string]$rule.Action
        direction   = [string]$rule.Direction
        profile     = [string]$rule.Profile
        enabled     = [string]$rule.Enabled
    }
    if (Test-NeverRemoveProgram $program) { continue }
    if (Test-CandidateProgram $program) {
        $candidates.Add($row)
    }
    elseif (-not $grouped) {
        $review.Add($row)
    }
}

if ([string]::IsNullOrWhiteSpace($BackupDir)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $BackupDir = Join-Path 'C:\Work\qa-prompt-audit' "fw-backup-$stamp"
}
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$jsonPath = Join-Path $BackupDir 'firewall-candidates.json'
$candidates | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
if ($review.Count -gt 0) {
    $review | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $BackupDir 'firewall-needs-review.json') -Encoding UTF8
}

Write-Host "Candidate rules: $($candidates.Count)"
$candidates | Group-Object { [IO.Path]::GetFileName($_.program).ToLowerInvariant() } | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("  {0,5}  {1}" -f $_.Count, $_.Name)
}
Write-Host "Needs review (left in place): $($review.Count)"
$review | Select-Object -First 20 | ForEach-Object { Write-Host ("  REVIEW  {0}  {1}" -f $_.displayName, $_.program) }
Write-Host "Candidate list: $jsonPath"

if (-not $Apply) {
    Write-Host "Dry run. Pass -Apply from an elevated PowerShell to remove the candidates."
    exit 0
}

if (-not (Test-IsElevated)) {
    Write-Host "Not elevated. Re-run:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -Apply -BackupDir `"$BackupDir`""
    exit 2
}

$again = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
$againList = New-Object System.Collections.Generic.List[object]
foreach ($item in $again) { [void]$againList.Add($item) }
if ($againList.Count -lt $candidates.Count) {
    Write-Error "Refusing to apply: saved candidate count is below the live count."
    exit 1
}

& netsh.exe advfirewall export (Join-Path $BackupDir 'firewall-before.wfw') | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Error "Firewall export failed."
    exit 1
}

$removed = 0
foreach ($row in $candidates) {
    if (-not [string]::IsNullOrWhiteSpace((Get-NetFirewallRule -PolicyStore PersistentStore -Name $row.name).Group)) {
        Write-Error "Refusing grouped rule $($row.name)"
        exit 1
    }
    if (Test-NeverRemoveProgram $row.program) {
        Write-Error "Refusing third-party rule $($row.program)"
        exit 1
    }
    Remove-NetFirewallRule -PolicyStore PersistentStore -Name $row.name
    Write-Host "Removed $($row.displayName) $($row.program)"
    $removed++
}
Write-Host "Removed $removed rules. Backup: $BackupDir"
exit 0
