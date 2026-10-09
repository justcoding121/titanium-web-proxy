<#
.SYNOPSIS
    Reproduces how winget installs the portable CLI zip: extract, then create symlinks to
    titanium.exe and twp.exe in a separate "Links" folder, and run the commands through them.

.DESCRIPTION
    The .NET apphost does not follow symlinks when it looks for titanium.dll, so a multi-file
    publish fails through WinGet\Links ("titanium.dll does not exist"). The single-file win-x64
    publish must pass this test. Needs symlink permission (GitHub Windows runners allow it;
    locally use an elevated shell or Developer Mode).

.EXAMPLE
    ./tools/packaging/winget/test-links-symlink.ps1 -Zip Titanium.Cli-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Zip,
    [string] $ExpectedVersionPrefix = ''
)

$ErrorActionPreference = 'Stop'

$work = Join-Path ([IO.Path]::GetTempPath()) ("twp-links-" + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $work 'install'
$links = Join-Path $work 'Links'
New-Item -ItemType Directory -Force -Path $install, $links | Out-Null

try {
    Expand-Archive -LiteralPath $Zip -DestinationPath $install -Force
    $entries = Get-ChildItem -LiteralPath $install -Force | Select-Object -ExpandProperty Name
    Write-Host "Zip contents: $($entries -join ', ')"

    foreach ($name in 'titanium.exe', 'twp.exe') {
        $target = Join-Path $install $name
        if (-not (Test-Path -LiteralPath $target)) { throw "$name missing from the zip." }
        New-Item -ItemType SymbolicLink -Path (Join-Path $links $name) -Target $target | Out-Null
    }

    $failed = $false
    foreach ($name in 'twp.exe', 'titanium.exe') {
        $exe = Join-Path $links $name
        $out = & $exe version 2>&1 | Out-String
        $code = $LASTEXITCODE
        Write-Host "$name version -> exit=$code"
        Write-Host $out
        if ($code -ne 0 -or $out -match 'does not exist' -or $out -match 'titanium\.dll') {
            Write-Host "::error::$name failed through the Links symlink."
            $failed = $true
        }
        elseif ($ExpectedVersionPrefix -and $out -notmatch [regex]::Escape($ExpectedVersionPrefix)) {
            Write-Host "::error::$name version output does not contain '$ExpectedVersionPrefix'."
            $failed = $true
        }
    }

    if ($failed) { exit 1 }
    Write-Host 'Symlink smoke passed.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
