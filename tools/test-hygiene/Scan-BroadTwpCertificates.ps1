# Broad leftover scan. Prints any certificate whose subject or issuer mentions
# Titanium, twp-, TWP, or Acme Corp, whether or not it matches the anchored patterns.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'TwpCertificatePatterns.ps1')

$count = 0
foreach ($scopeName in @('CurrentUser', 'LocalMachine')) {
    foreach ($storeName in $script:TwpStoreNames) {
        $path = "Cert:\$scopeName\$storeName"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $all = @(Get-ChildItem -LiteralPath $path -ErrorAction SilentlyContinue)
        $hits = @($all | Where-Object { (($_.Subject + ' ' + $_.Issuer) -match 'Titanium|twp-|TWP|Acme Corp') })
        if ($hits.Count -gt 0) {
            foreach ($hit in $hits) {
                $count++
                '{0}\{1} | {2} | issuer={3} | {4}' -f $scopeName, $storeName, $hit.Subject, $hit.Issuer, $hit.Thumbprint
            }
        }
        '{0}\{1} total={2}' -f $scopeName, $storeName, $all.Count
    }
}
Write-Host "BROAD_HITS=$count"
if ($count -gt 0) { exit 1 }
exit 0
