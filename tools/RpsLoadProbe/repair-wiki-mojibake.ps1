# Repair common UTF-8 mojibake sequences in wiki/Performance.md without touching valid Unicode.
param(
    [string] $WikiFile = 'wiki/Performance.md'
)

$ErrorActionPreference = 'Stop'

$middleDot = [char]0x00B7
$middleDotM = [string][char]0x00C2 + [char]0x00B7
$times = [char]0x00D7
$timesM = [string][char]0x00C3 + [char]0x2014
$div = [char]0x00F7
$divM = [string][char]0x00C3 + [char]0x00B7
$medal = [char]::ConvertFromUtf32(0x1F947)
$medalM = [string][char]0x00F0 + [char]0x0178 + [char]0x00A5 + [char]0x2021

$wikiPath = Resolve-Path $WikiFile
$wiki = [System.IO.File]::ReadAllText($wikiPath, [System.Text.Encoding]::UTF8)

function Count-Mojibake([string]$text) {
    return @{
        Dot = ([regex]::Matches($text, [regex]::Escape($middleDotM))).Count
        Times = ([regex]::Matches($text, [regex]::Escape($timesM))).Count
        Div = ([regex]::Matches($text, [regex]::Escape($divM))).Count
        Medal = ([regex]::Matches($text, [regex]::Escape($medalM))).Count
        Bad = ([regex]::Matches($text, [char]0xFFFD)).Count
    }
}

$before = Count-Mojibake $wiki
$wiki = $wiki.Replace($middleDotM, $middleDot)
$wiki = $wiki.Replace($timesM, $times)
$wiki = $wiki.Replace($divM, $div)
$wiki = $wiki.Replace($medalM, $medal)
$after = Count-Mojibake $wiki

$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($wikiPath, $wiki, $utf8)
Write-Host ('Repaired {0}: dot {1}->{2} times {3}->{4} div {5}->{6} medal {7}->{8} bad {9}' -f `
    $WikiFile, $before.Dot, $after.Dot, $before.Times, $after.Times, $before.Div, $after.Div, $before.Medal, $after.Medal, $after.Bad)
