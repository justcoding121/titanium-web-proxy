#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build the Plus bundle zip and the legacy single-DLL asset.

.DESCRIPTION
  Publishes PlusBundle (framework-dependent, no RID) and a framework-dependent CLI,
  then keeps only top-level files whose names are absent from the CLI publish.
  Writes Titanium.Plus.files (the exact entry list) into Titanium.Plus-<tag>.zip.
  Also copies Titanium.Plus.dll to Titanium.Plus-<tag>.dll for feeds that still
  publish products.plus.asset.

.PARAMETER Tag
  Release tag used in the artifact file name (for example 8.0.0 or v8.0.0).

.PARAMETER Version
  Assembly version stamped onto the publish (InformationalVersion). Defaults to Tag without a leading v.

.PARAMETER Configuration
  Build configuration. Default Release.

.PARAMETER OutputDirectory
  Directory for Titanium.Plus-<tag>.zip and Titanium.Plus-<tag>.dll. Default: current directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    [string] $Version = "",
    [string] $Configuration = "Release",
    [string] $OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "Tag is required."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $Tag.Trim()
    if ($Version.StartsWith("v")) { $Version = $Version.Substring(1) }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\..\.."))
if (-not $OutputDirectory) { $OutputDirectory = (Get-Location).Path }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("twp-plus-bundle-" + [guid]::NewGuid().ToString("n"))
$cliOut = Join-Path $work "cli"
$plusOut = Join-Path $work "plus"
$stage = Join-Path $work "stage"
New-Item -ItemType Directory -Force -Path $cliOut, $plusOut, $stage | Out-Null

function Invoke-Publish([string] $Project, [string] $OutDir) {
    Write-Host "Publishing $Project -> $OutDir"
    & dotnet publish $Project -c $Configuration -f net10.0 --self-contained false -o $OutDir "-p:Version=$Version" --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE): $Project" }
}

try {
    Invoke-Publish (Join-Path $repo "src\Titanium.Cli\Titanium.Cli.csproj") $cliOut
    Invoke-Publish (Join-Path $scriptDir "PlusBundle.csproj") $plusOut

    $cliNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -LiteralPath $cliOut -Recurse -File | ForEach-Object { [void]$cliNames.Add($_.Name) }

    $payload = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    Get-ChildItem -LiteralPath $plusOut -File | ForEach-Object {
        if ($_.Name.StartsWith("PlusBundle.", [StringComparison]::OrdinalIgnoreCase)) { return }
        if ($cliNames.Contains($_.Name)) { return }
        $payload.Add($_)
    }

    foreach ($extra in @(
            @{ Source = (Join-Path $repo "tools\packaging\THIRD-PARTY-PLUS.txt"); Name = "THIRD-PARTY-PLUS.txt" },
            @{ Source = (Join-Path $repo "src\Titanium.Xds\NOTICE"); Name = "Titanium.Xds.NOTICE" }
        )) {
        if (-not (Test-Path -LiteralPath $extra.Source)) { throw "Missing notice: $($extra.Source)" }
        Copy-Item -LiteralPath $extra.Source -Destination (Join-Path $stage $extra.Name) -Force
    }

    foreach ($file in $payload) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stage $file.Name) -Force
    }

    $names = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $stage -File | ForEach-Object { $names.Add($_.Name) }
    $names.Add("Titanium.Plus.files")
    $sorted = [string[]]($names | Sort-Object)
    $listPath = Join-Path $stage "Titanium.Plus.files"
    [System.IO.File]::WriteAllLines($listPath, $sorted)

    $zipName = "Titanium.Plus-$Tag.zip"
    $zipPath = Join-Path $OutputDirectory $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $_.FullName, $_.Name, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $zip.Dispose()
    }

    $legacy = Join-Path $plusOut "Titanium.Plus.dll"
    if (-not (Test-Path -LiteralPath $legacy)) { throw "Plus publish did not produce Titanium.Plus.dll." }
    Copy-Item -LiteralPath $legacy -Destination (Join-Path $OutputDirectory "Titanium.Plus-$Tag.dll") -Force

    $check = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($check.Entries | ForEach-Object { $_.FullName })
        $listEntry = $check.GetEntry("Titanium.Plus.files")
        if (-not $listEntry) { throw "Zip is missing Titanium.Plus.files." }
        $reader = New-Object System.IO.StreamReader($listEntry.Open())
        try { $listed = @($reader.ReadToEnd().Split(@("`r`n", "`n", "`r"), [StringSplitOptions]::RemoveEmptyEntries)) }
        finally { $reader.Dispose() }

        $entrySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($e in $entries) {
            if ($e.Contains("/") -or $e.Contains("\") -or $e.Contains("..")) {
                throw "Bundle entry is not a plain file name: $e"
            }
            [void]$entrySet.Add($e)
        }
        if ($listed.Count -ne $entrySet.Count) {
            throw "Titanium.Plus.files lists $($listed.Count) names but the zip has $($entrySet.Count) entries."
        }
        foreach ($name in $listed) {
            if (-not $entrySet.Contains($name)) { throw "Titanium.Plus.files names '$name', which is not in the zip." }
        }

        $required = @(
            "Titanium.Plus.dll",
            "Titanium.Xds.dll",
            "Titanium.Cli.GeoIp.dll",
            "MaxMind.Db.dll",
            "StackExchange.Redis.dll",
            "Google.Protobuf.dll",
            "BCrypt.Net-Next.dll",
            "DnsClient.dll",
            "Grpc.Net.Client.dll",
            "Titanium.Plus.files",
            "THIRD-PARTY-PLUS.txt",
            "Titanium.Xds.NOTICE"
        )
        $forbidden = @(
            "titanium.dll",
            "titanium.exe",
            "titanium",
            "Titanium.Web.Proxy.dll",
            "Titanium.Web.Proxy.Abstractions.dll",
            "Titanium.Web.Proxy.Configuration.dll",
            "PlusBundle.dll"
        )
        foreach ($name in $required) {
            if (-not $entrySet.Contains($name)) {
                throw "Plus bundle is missing $name. Entries: $($entries -join ', ')"
            }
        }
        foreach ($name in $forbidden) {
            if ($entrySet.Contains($name)) { throw "Plus bundle must not contain $name (it belongs in the CLI zip)." }
        }
    }
    finally {
        $check.Dispose()
    }

    Write-Host "Wrote $zipPath ($($sorted.Count) files) and Titanium.Plus-$Tag.dll"
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
