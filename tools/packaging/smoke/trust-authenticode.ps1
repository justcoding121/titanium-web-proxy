#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string[]] $Paths
)
$ErrorActionPreference = 'Stop'
foreach ($path in $Paths) {
    if (-not (Test-Path $path)) { throw "ASSERT: missing $path" }
    $sig = Get-AuthenticodeSignature -LiteralPath $path
    Write-Host "$path $($sig.Status)"
    if ($sig.Status -ne 'Valid') { throw "ASSERT: Authenticode status $($sig.Status) for $path" }
}
