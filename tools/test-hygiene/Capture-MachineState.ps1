# Snapshot firewall rules, certificate stores, proxy settings, and Titanium services.
# -Compare <before> <after> exits 1 when the stable fields differ.
param(
    [string]$OutDir = '',
    [string]$Compare = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'TwpCertificatePatterns.ps1')

function Get-FirewallSnapshot {
    $rules = @(Get-NetFirewallRule -PolicyStore PersistentStore)
    $apps = @(Get-NetFirewallApplicationFilter -PolicyStore PersistentStore)
    $map = @{}
    foreach ($app in $apps) { $map[$app.InstanceID] = [string]$app.Program }
    $rows = foreach ($rule in $rules) {
        $program = ''
        if ($map.ContainsKey($rule.InstanceID)) { $program = $map[$rule.InstanceID] }
        [pscustomobject]@{
            displayName = [string]$rule.DisplayName
            group       = [string]$rule.Group
            program     = $program
            action      = [string]$rule.Action
            direction   = [string]$rule.Direction
            profile     = [string]$rule.Profile
            enabled     = [string]$rule.Enabled
        }
    }
    $profiles = @(Get-NetFirewallProfile | ForEach-Object {
        [pscustomobject]@{
            name = [string]$_.Name
            enabled = [string]$_.Enabled
            notifyOnListen = [string]$_.NotifyOnListen
            defaultInbound = [string]$_.DefaultInboundAction
        }
    })
    # Windows rewrites a rule's id when an app refreshes the same allow entry. Compare the policy, not the id.
    [pscustomobject]@{
        rules = @($rows | Sort-Object displayName, program, direction, action, profile, enabled)
        profiles = @($profiles | Sort-Object name)
    }
}

function Get-CertificateSnapshot {
    $rows = New-Object System.Collections.Generic.List[object]
    $twp = 0
    foreach ($scopeName in @('CurrentUser', 'LocalMachine')) {
        foreach ($storeName in $script:TwpStoreNames) {
            $path = "Cert:\$scopeName\$storeName"
            if (-not (Test-Path -LiteralPath $path)) { continue }
            foreach ($cert in @(Get-ChildItem -LiteralPath $path -ErrorAction SilentlyContinue)) {
                $isTwp = Test-TwpCertificate -Subject $cert.Subject -Issuer $cert.Issuer
                if ($isTwp) { $twp++ }
                $rows.Add([pscustomobject]@{
                    scope      = $scopeName
                    store      = $storeName
                    thumbprint = [string]$cert.Thumbprint
                    subject    = [string]$cert.Subject
                    issuer     = [string]$cert.Issuer
                    twp        = $isTwp
                })
            }
        }
    }
    [pscustomobject]@{ twpCount = $twp; certificates = @($rows | Sort-Object scope, store, thumbprint) }
}

function Get-ProxySnapshot {
    $regPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $reg = Get-ItemProperty -LiteralPath $regPath
    function Read-Reg([string]$Name) {
        if ($reg.PSObject.Properties.Name -contains $Name) { return [string]$reg.$Name }
        return ''
    }
    $winhttp = & netsh.exe winhttp show proxy | Out-String
    [pscustomobject]@{
        proxyEnable   = Read-Reg 'ProxyEnable'
        proxyServer   = Read-Reg 'ProxyServer'
        proxyOverride = Read-Reg 'ProxyOverride'
        autoConfigUrl = Read-Reg 'AutoConfigURL'
        winhttp       = ($winhttp.Trim())
    }
}

function Get-ServiceSnapshot {
    @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'titanium*' } | ForEach-Object {
        [pscustomobject]@{ name = $_.Name; status = [string]$_.Status; startType = [string]$_.StartType }
    } | Sort-Object name)
}

function Get-ProcessSnapshot {
    $names = @('consent', 'testhost', 'dotnet')
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($name in $names) {
        foreach ($proc in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            $rows.Add([pscustomobject]@{ name = $proc.ProcessName; id = $proc.Id })
        }
    }
    foreach ($proc in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'titanium*' })) {
        $rows.Add([pscustomobject]@{ name = $proc.ProcessName; id = $proc.Id })
    }
    @($rows | Sort-Object name, id)
}

function Get-ThreatSnapshot {
    try {
        @(Get-MpThreatDetection -ErrorAction Stop | Select-Object -First 20 | ForEach-Object {
            [pscustomobject]@{ id = [string]$_.ThreatID; resources = [string]($_.Resources -join ',') }
        })
    }
    catch {
        @([pscustomobject]@{ unavailable = $_.Exception.Message })
    }
}

function Write-Snapshot {
    param([string]$Dir)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    (Get-FirewallSnapshot | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $Dir 'firewall.json') -Encoding UTF8
    $certs = Get-CertificateSnapshot
    ($certs | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath (Join-Path $Dir 'certificates.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'twp-certificate-count.txt') -Value ([string]$certs.twpCount) -Encoding UTF8
    (Get-ProxySnapshot | ConvertTo-Json) | Set-Content -LiteralPath (Join-Path $Dir 'proxy.json') -Encoding UTF8
    $services = @(Get-ServiceSnapshot)
    if ($services.Count -eq 0) { '[]' | Set-Content -LiteralPath (Join-Path $Dir 'services.json') -Encoding UTF8 }
    else { ($services | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $Dir 'services.json') -Encoding UTF8 }
    $processes = @(Get-ProcessSnapshot)
    if ($processes.Count -eq 0) { '[]' | Set-Content -LiteralPath (Join-Path $Dir 'processes.json') -Encoding UTF8 }
    else { ($processes | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $Dir 'processes.json') -Encoding UTF8 }
    $threats = @(Get-ThreatSnapshot)
    if ($threats.Count -eq 0) { '[]' | Set-Content -LiteralPath (Join-Path $Dir 'threats.json') -Encoding UTF8 }
    else { ($threats | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $Dir 'threats.json') -Encoding UTF8 }
    Write-Host "Snapshot: $Dir (TWP certificates: $($certs.twpCount))"
}

function Compare-SnapshotFile {
    param([string]$Left, [string]$Right, [string]$Label)
    $a = Get-Content -LiteralPath $Left -Raw
    $b = Get-Content -LiteralPath $Right -Raw
    if ($a -ne $b) {
        Write-Host "DIFF $Label"
        return $false
    }
    return $true
}

if ($Compare) {
    $parts = $Compare.Split('|')
    if ($parts.Count -ne 2) { Write-Error "Pass -Compare 'dirA|dirB'"; exit 1 }
    $ok = $true
    foreach ($file in @('firewall.json', 'certificates.json', 'proxy.json', 'services.json', 'twp-certificate-count.txt')) {
        if (-not (Compare-SnapshotFile (Join-Path $parts[0] $file) (Join-Path $parts[1] $file) $file)) { $ok = $false }
    }
    $afterCount = Get-Content -LiteralPath (Join-Path $parts[1] 'twp-certificate-count.txt') -Raw
    if ([int]($afterCount.Trim()) -ne 0) {
        Write-Host "DIFF twp-certificate-count is $afterCount (must stay 0)"
        $ok = $false
    }
    if (-not $ok) { exit 1 }
    Write-Host "No difference in firewall, certificates, proxy, or Titanium services."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($OutDir)) { Write-Error "Pass -OutDir or -Compare 'a|b'"; exit 1 }
Write-Snapshot -Dir $OutDir
exit 0
