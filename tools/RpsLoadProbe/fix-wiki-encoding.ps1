# Repair paste output or wiki/Performance.md, then optionally apply paste to the wiki.
param(
    [string] $PasteFile = 'tools/RpsLoadProbe/results/wiki-paste-out2.txt',
    [string] $WikiFile = 'wiki/Performance.md',
    [switch] $ApplyPaste,
    [string] $HeadSha = 'df172718',
    [string] $PrimaryRunId = '33041445371'
)

$ErrorActionPreference = 'Stop'

if ($ApplyPaste) {
    & "$PSScriptRoot/apply-wiki-paste.ps1" -PasteFile $PasteFile -WikiFile $WikiFile -HeadSha $HeadSha -PrimaryRunId $PrimaryRunId
    & "$PSScriptRoot/repair-wiki-mojibake.ps1" -WikiFile $WikiFile
    exit 0
}

if (Test-Path $PasteFile) {
    & "$PSScriptRoot/repair-wiki-mojibake.ps1" -WikiFile $PasteFile
}

Write-Host "To regenerate paste with UTF-8: pwsh -NoProfile -Command `"& { `$o = & '$PSScriptRoot/paste-compare-product-wiki.ps1' -RunIds '33041445371,33055267086,33055272140'; [IO.File]::WriteAllLines('$PasteFile', @(`$o), (New-Object Text.UTF8Encoding `$false)) }`""
