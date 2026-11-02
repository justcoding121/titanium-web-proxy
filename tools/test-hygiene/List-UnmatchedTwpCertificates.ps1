Set-StrictMode -Version 2.0
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'TwpCertificatePatterns.ps1')

foreach ($scopeName in @('CurrentUser', 'LocalMachine')) {
    foreach ($storeName in $script:TwpStoreNames) {
        $path = "Cert:\$scopeName\$storeName"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        Get-ChildItem -LiteralPath $path | Where-Object {
            (($_.Subject + ' ' + $_.Issuer) -match 'Titanium|twp-|TWP|Acme Corp') -and
            -not (Test-TwpCertificate -Subject $_.Subject -Issuer $_.Issuer)
        } | ForEach-Object {
            '{0}\{1} | {2} | issuer={3}' -f $scopeName, $storeName, $_.Subject, $_.Issuer
        }
    }
}
