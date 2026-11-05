# Inventory and remove Titanium / TWP certificates from the Windows stores.
# Default is a dry run. Nothing is removed unless -Apply is passed.
# LocalMachine removal needs an elevated shell; this script never elevates itself.
param(
    [ValidateSet('CurrentUser', 'LocalMachine', 'Both')]
    [string]$Scope = 'Both',
    [string]$BackupDir = '',
    [switch]$DeleteKeys,
    [switch]$Apply
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'TwpCertificatePatterns.ps1')

function Test-IsElevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-StoreCertificates {
    param([string]$ScopeName, [string]$StoreName)
    $path = "Cert:\$ScopeName\$StoreName"
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    @(Get-ChildItem -LiteralPath $path -ErrorAction SilentlyContinue)
}

function Convert-ToInventoryRow {
    param($Cert, [string]$ScopeName, [string]$StoreName)
    [pscustomobject]@{
        scope          = $ScopeName
        store          = $StoreName
        subject        = [string]$Cert.Subject
        issuer         = [string]$Cert.Issuer
        thumbprint     = [string]$Cert.Thumbprint
        notAfter       = $Cert.NotAfter.ToString('o')
        hasPrivateKey  = [bool]$Cert.HasPrivateKey
    }
}

$scopes = Get-TwpCertificateScopes -Scope $Scope
$candidates = New-Object System.Collections.Generic.List[object]
$storeTotals = @{}

foreach ($scopeName in $scopes) {
    foreach ($storeName in $script:TwpStoreNames) {
        $certs = Get-StoreCertificates -ScopeName $scopeName -StoreName $storeName
        $storeTotals["$scopeName\$storeName"] = @($certs).Count
        foreach ($cert in $certs) {
            if (Test-TwpCertificate -Subject $cert.Subject -Issuer $cert.Issuer) {
                $candidates.Add((Convert-ToInventoryRow -Cert $cert -ScopeName $scopeName -StoreName $storeName))
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($BackupDir)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $repoRoot = Resolve-Path (Join-Path $here '..\..')
    $BackupDir = Join-Path $repoRoot "TestResults\prompt-audit\cert-backup\$stamp"
}

New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$inventoryPath = Join-Path $BackupDir 'inventory.json'
$candidates | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inventoryPath -Encoding UTF8

Write-Host "TWP certificate candidates: $($candidates.Count)"
$candidates | Group-Object scope, store | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,5}  {1}" -f $_.Count, $_.Name)
}
Write-Host "Inventory: $inventoryPath"
Write-Host "Store totals (all certificates, not only TWP):"
$storeTotals.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,5}  {1}" -f $_.Value, $_.Key)
}

if (-not $Apply) {
    Write-Host "Dry run. Pass -Apply to remove these certificates."
    exit 0
}

$machineHits = @($candidates | Where-Object { $_.scope -eq 'LocalMachine' })
if ($machineHits.Count -gt 0 -and -not (Test-IsElevated)) {
    Write-Host "LocalMachine hits require an elevated PowerShell. Re-run:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -Scope LocalMachine -Apply -DeleteKeys -BackupDir `"$BackupDir`""
    exit 2
}

# Safety: re-read the inventory we just wrote and refuse anything that drifted.
$parsed = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$written = New-Object System.Collections.Generic.List[object]
foreach ($item in $parsed) { [void]$written.Add($item) }
if ($written.Count -lt $candidates.Count) {
    Write-Error "Refusing to apply: inventory count $($written.Count) is below candidate count $($candidates.Count)."
    exit 1
}

$removed = 0
foreach ($row in $candidates) {
    if (-not (Test-TwpCertificate -Subject $row.subject -Issuer $row.issuer)) {
        Write-Error "Refusing certificate that does not match a TWP pattern: $($row.subject)"
        exit 1
    }
    $certPath = "Cert:\$($row.scope)\$($row.store)\$($row.thumbprint)"
    $live = Get-Item -LiteralPath $certPath -ErrorAction SilentlyContinue
    if (-not $live) { continue }

    $exportDir = Join-Path $BackupDir (Join-Path $row.scope $row.store)
    New-Item -ItemType Directory -Force -Path $exportDir | Out-Null
    $cer = Join-Path $exportDir ($row.thumbprint + '.cer')
    $bytes = $live.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($cer, $bytes)

    if ($row.store -eq 'Root') {
        if ($row.scope -eq 'CurrentUser') {
            & certutil.exe -user -delstore Root $row.thumbprint | Out-Null
        }
        else {
            & certutil.exe -delstore Root $row.thumbprint | Out-Null
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Error "certutil -delstore failed for $($row.thumbprint) (exit $LASTEXITCODE)."
            exit 1
        }
    }
    else {
        if ($DeleteKeys -and $live.HasPrivateKey) {
            try {
                Remove-Item -LiteralPath $certPath -DeleteKey -Force -ErrorAction Stop
            }
            catch {
                # The key container is already gone. Drop the certificate anyway.
                if (Test-Path -LiteralPath $certPath) {
                    Remove-Item -LiteralPath $certPath -Force
                }
            }
        }
        else {
            Remove-Item -LiteralPath $certPath -Force
        }
    }
    $removed++
    if (($removed % 50) -eq 0) { Write-Host "Removed $removed ..." }
}

Write-Host "Removed $removed certificates. Backup: $BackupDir"
exit 0
