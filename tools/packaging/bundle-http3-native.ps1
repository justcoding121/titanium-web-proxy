#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Bundle HTTP/3 (MsQuic) native libraries into a self-contained publish folder.

.PARAMETER Rid
  Runtime identifier (win-x64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64, osx-x64, osx-arm64).

.PARAMETER PublishDir
  Output directory from `dotnet publish`.

.PARAMETER LockFile
  Path to http3-native.lock.json (defaults next to this script).

.PARAMETER CacheDir
  Download cache directory (defaults to tools/packaging/.cache/http3-natives).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [string] $LockFile = "",
    [string] $CacheDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $LockFile) { $LockFile = Join-Path $scriptDir "http3-native.lock.json" }
if (-not $CacheDir) { $CacheDir = Join-Path $scriptDir ".cache/http3-natives" }

$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)
if (-not (Test-Path $PublishDir)) {
    throw "PublishDir does not exist: $PublishDir"
}
if (-not (Test-Path $LockFile)) {
    throw "Lock file not found: $LockFile"
}

$lock = Get-Content -Raw -Path $LockFile | ConvertFrom-Json
$ridEntry = $lock.rids.$Rid
if (-not $ridEntry) {
    throw "RID '$Rid' is not defined in $LockFile"
}

$licenseSrc = Join-Path $scriptDir "THIRD-PARTY-HTTP3.txt"
$licenseDst = Join-Path $PublishDir "THIRD-PARTY-HTTP3.txt"
if (Test-Path $licenseSrc) {
    Copy-Item -Force $licenseSrc $licenseDst
}

function Write-Info([string] $msg) { Write-Host "[http3-bundle] $msg" }

function Get-Sha256([string] $path) {
    return (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

function Ensure-CacheDir([string] $rid) {
    $dir = Join-Path $CacheDir $rid
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Download-Verified([string] $url, [string] $sha256, [string] $destPath) {
    if ((Test-Path $destPath) -and ((Get-Sha256 $destPath) -eq $sha256.ToLowerInvariant())) {
        Write-Info "cache hit $(Split-Path -Leaf $destPath)"
        return
    }
    Write-Info "download $url"
    $tmp = "$destPath.partial"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    $actual = Get-Sha256 $tmp
    if ($actual -ne $sha256.ToLowerInvariant()) {
        Remove-Item -Force $tmp -ErrorAction SilentlyContinue
        throw "SHA256 mismatch for $url`n expected $sha256`n actual   $actual"
    }
    Move-Item -Force $tmp $destPath
}

function Invoke-Native([string] $file, [string[]] $argList) {
    & $file @argList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $file $($argList -join ' ')"
    }
}

function Find-Tool([string] $name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Ensure-Patchelf {
    $p = Find-Tool "patchelf"
    if ($p) { return $p }
    if ($IsLinux -or ($PSVersionTable.Platform -eq "Unix" -and -not $IsMacOS)) {
        Write-Info "installing patchelf"
        if (Find-Tool "apt-get") {
            Invoke-Native "sudo" @("apt-get", "update", "-qq")
            Invoke-Native "sudo" @("apt-get", "install", "-y", "-qq", "patchelf")
        }
        elseif (Find-Tool "apk") {
            Invoke-Native "sudo" @("apk", "add", "--no-cache", "patchelf")
        }
        $p = Find-Tool "patchelf"
    }
    if (-not $p) { throw "patchelf is required for Linux RIDs but was not found." }
    return $p
}

function Expand-Deb([string] $debPath, [string] $outDir) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $bytes = [System.IO.File]::ReadAllBytes($debPath)
    $ascii = [System.Text.Encoding]::ASCII
    if ($ascii.GetString($bytes, 0, 8) -ne "!<arch>`n") {
        throw "Not a deb/ar archive: $debPath"
    }
    $pos = 8
    while ($pos + 60 -le $bytes.Length) {
        $name = $ascii.GetString($bytes, $pos, 16).Trim().TrimEnd("/")
        $size = [int]$ascii.GetString($bytes, $pos + 48, 10).Trim()
        $pos += 60
        $body = New-Object byte[] $size
        [Array]::Copy($bytes, $pos, $body, 0, $size)
        $pos += $size
        if (($pos % 2) -eq 1) { $pos += 1 }

        if ($name -like "data.tar*") {
            $tarPath = Join-Path $outDir $name
            [System.IO.File]::WriteAllBytes($tarPath, $body)
            Push-Location $outDir
            try {
                if ($name.EndsWith(".xz")) {
                    Invoke-Native "tar" @("-xJf", $name)
                }
                elseif ($name.EndsWith(".gz")) {
                    Invoke-Native "tar" @("-xzf", $name)
                }
                elseif ($name.EndsWith(".zst")) {
                    Invoke-Native "tar" @("--zstd", "-xf", $name)
                }
                else {
                    Invoke-Native "tar" @("-xf", $name)
                }
            }
            finally { Pop-Location }
            return
        }
    }
    throw "No data.tar* member in $debPath"
}

function Expand-Apk([string] $apkPath, [string] $outDir) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    # Alpine .apk = concatenated gzip members (signature tar + data tar).
    $bytes = [System.IO.File]::ReadAllBytes($apkPath)
    $pos = 0
    $part = 0
    while ($pos -lt ($bytes.Length - 1) -and $bytes[$pos] -eq 0x1f -and $bytes[$pos + 1] -eq 0x8b) {
        $remaining = New-Object byte[] ($bytes.Length - $pos)
        [Array]::Copy($bytes, $pos, $remaining, 0, $remaining.Length)
        $ms = New-Object System.IO.MemoryStream(,$remaining)
        $gz = New-Object System.IO.Compression.GzipStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
        $outMs = New-Object System.IO.MemoryStream
        try {
            $gz.CopyTo($outMs)
        }
        finally {
            $gz.Dispose()
        }
        $consumed = [int]$ms.Position
        $content = $outMs.ToArray()
        $ms.Dispose(); $outMs.Dispose()
        $pos += $consumed

        $tarPath = Join-Path $outDir ("part{0}.tar" -f $part)
        [System.IO.File]::WriteAllBytes($tarPath, $content)
        Push-Location $outDir
        try {
            & tar -xf (Split-Path -Leaf $tarPath) 2>$null
        }
        finally { Pop-Location }
        $part++
    }
    if ($part -eq 0) {
        Push-Location $outDir
        try { Invoke-Native "tar" @("-xzf", $apkPath) }
        finally { Pop-Location }
    }
}

function Copy-SharedLibs([string] $extractRoot, [string] $dest) {
    # MIT-clean zip: MsQuic + OpenSSL only. Never ship LGPL/GPL natives
    # (libnuma, liblttng-ust*, libmsquic.lttng) — those stay host packages via http3-deps.
    $patterns = @("libmsquic*.so*", "libssl.so*", "libcrypto.so*")
    $copied = @()
    foreach ($pat in $patterns) {
        Get-ChildItem -Path $extractRoot -Recurse -File -Filter $pat -ErrorAction SilentlyContinue | ForEach-Object {
            # Skip OpenSSL engines/modules; only ship the main shared libs.
            if ($_.DirectoryName -match "engines|ossl-modules") { return }
            # Skip MsQuic LTTng plugin (GPL) even if present in the deb/apk.
            if ($_.Name -match "lttng") { return }
            Copy-Item -Force $_.FullName (Join-Path $dest $_.Name)
            $copied += $_.Name
        }
    }
    return $copied
}

function Ensure-SonameLinks([string] $dir) {
    # Create libmsquic.so / libmsquic.so.2 if only versioned files were packaged.
    $versioned = Get-ChildItem -Path $dir -File -Filter "libmsquic.so.*" |
        Where-Object { $_.Name -notmatch "lttng" } |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($versioned) {
        $so2 = Join-Path $dir "libmsquic.so.2"
        $so = Join-Path $dir "libmsquic.so"
        if (-not (Test-Path $so2)) {
            Push-Location $dir
            try { Invoke-Native "ln" @("-sfn", $versioned.Name, "libmsquic.so.2") }
            finally { Pop-Location }
        }
        if (-not (Test-Path $so)) {
            Push-Location $dir
            try { Invoke-Native "ln" @("-sfn", "libmsquic.so.2", "libmsquic.so") }
            finally { Pop-Location }
        }
    }
}

function Set-LinuxRpath([string] $dir) {
    $patchelf = Ensure-Patchelf
    Get-ChildItem -Path $dir -File -Filter "*.so*" | ForEach-Object {
        $soFile = $_
        # Skip pure symlinks
        if ($soFile.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
        try {
            Invoke-Native $patchelf @("--set-rpath", "`$ORIGIN", $soFile.FullName)
        }
        catch {
            # Catch uses $_ as the error; keep $soFile for the name.
            Write-Info "patchelf skipped $($soFile.Name): $_"
        }
    }
}

function Assert-RequiredFiles([string[]] $globs) {
    foreach ($g in $globs) {
        $hits = @(Get-ChildItem -Path $PublishDir -File -Filter $g -ErrorAction SilentlyContinue)
        if ($hits.Count -eq 0) {
            throw "Required native file missing in publish dir after bundle: $g (RID=$Rid)"
        }
    }
}

function Bundle-Deb {
    $cache = Ensure-CacheDir $Rid
    $extractRoot = Join-Path $cache "extract"
    if (Test-Path $extractRoot) { Remove-Item -Recurse -Force $extractRoot }
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

    foreach ($pkg in $ridEntry.packages) {
        $leaf = Split-Path -Leaf $pkg.url
        $dest = Join-Path $cache $leaf
        Download-Verified $pkg.url $pkg.sha256 $dest
        $pkgExtract = Join-Path $extractRoot ([IO.Path]::GetFileNameWithoutExtension($leaf))
        Expand-Deb $dest $pkgExtract
    }

    $copied = Copy-SharedLibs $extractRoot $PublishDir
    Write-Info "copied: $($copied -join ', ')"
    Ensure-SonameLinks $PublishDir
    Set-LinuxRpath $PublishDir
    Assert-RequiredFiles @("libmsquic.so*", "libssl.so.3", "libcrypto.so.3")
}

function Bundle-Apk {
    $cache = Ensure-CacheDir $Rid
    $extractRoot = Join-Path $cache "extract"
    if (Test-Path $extractRoot) { Remove-Item -Recurse -Force $extractRoot }
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

    foreach ($pkg in $ridEntry.packages) {
        $leaf = Split-Path -Leaf $pkg.url
        $dest = Join-Path $cache $leaf
        Download-Verified $pkg.url $pkg.sha256 $dest
        $pkgExtract = Join-Path $extractRoot ([IO.Path]::GetFileNameWithoutExtension($leaf))
        Expand-Apk $dest $pkgExtract
    }

    $copied = Copy-SharedLibs $extractRoot $PublishDir
    Write-Info "copied: $($copied -join ', ')"
    Ensure-SonameLinks $PublishDir
    Set-LinuxRpath $PublishDir
    Assert-RequiredFiles @("libmsquic.so*", "libssl.so.3", "libcrypto.so.3")
}

function Resolve-BrewPrefix {
    $brew = Find-Tool "brew"
    if (-not $brew) { throw "Homebrew (brew) is required to bundle macOS HTTP/3 natives." }
    $prefix = (& $brew --prefix).Trim()
    return @{ Brew = $brew; Prefix = $prefix }
}

function Bundle-Homebrew {
    if (-not $IsMacOS -and $env:OS -eq "Windows_NT") {
        throw "osx RID bundling must run on macOS (install_name_tool / Homebrew)."
    }
    $b = Resolve-BrewPrefix
    foreach ($formula in $ridEntry.formulas) {
        Write-Info "brew install $formula"
        & $b.Brew install $formula
        if ($LASTEXITCODE -ne 0) { throw "brew install $formula failed" }
    }

    $candidates = @(
        (Join-Path $b.Prefix "opt/libmsquic/lib"),
        (Join-Path $b.Prefix "lib"),
        (Join-Path $b.Prefix "opt/openssl@3/lib"),
        (Join-Path $b.Prefix "opt/openssl/lib")
    )

    $needed = @("libmsquic.dylib", "libssl.3.dylib", "libcrypto.3.dylib")
    foreach ($name in $needed) {
        $found = $null
        foreach ($dir in $candidates) {
            $p = Join-Path $dir $name
            if (Test-Path $p) { $found = $p; break }
            # Also accept versioned names for msquic
            if ($name -eq "libmsquic.dylib") {
                $alt = Get-ChildItem -Path $dir -Filter "libmsquic*.dylib" -ErrorAction SilentlyContinue |
                    Select-Object -First 1
                if ($alt) { $found = $alt.FullName; break }
            }
        }
        if (-not $found) { throw "Could not find $name under Homebrew prefix $($b.Prefix)" }
        $destName = if ($name -eq "libmsquic.dylib" -and ([IO.Path]::GetFileName($found) -ne "libmsquic.dylib")) {
            # Prefer stable name
            "libmsquic.dylib"
        } else { $name }
        # If source is already libmsquic.X.dylib, copy as both real name and libmsquic.dylib
        Copy-Item -Force $found (Join-Path $PublishDir ([IO.Path]::GetFileName($found)))
        if ($destName -eq "libmsquic.dylib" -and ([IO.Path]::GetFileName($found) -ne "libmsquic.dylib")) {
            Copy-Item -Force $found (Join-Path $PublishDir "libmsquic.dylib")
        }
        elseif ($name -ne ([IO.Path]::GetFileName($found))) {
            Copy-Item -Force $found (Join-Path $PublishDir $name)
        }
    }

    # Rewrite install names to @loader_path
    $dylibs = @(Get-ChildItem -Path $PublishDir -File -Filter "*.dylib")
    foreach ($lib in $dylibs) {
        Invoke-Native "install_name_tool" @("-id", "@loader_path/$($lib.Name)", $lib.FullName)
    }
    foreach ($lib in $dylibs) {
        $deps = & otool -L $lib.FullName
        foreach ($line in $deps) {
            if ($line -notmatch "^\s+(\S+)") { continue }
            $dep = $Matches[1]
            $depLeaf = Split-Path -Leaf $dep
            if ($depLeaf -match "lib(msquic|ssl|crypto)") {
                $local = Join-Path $PublishDir $depLeaf
                if (-not (Test-Path $local)) {
                    # map libssl.3.dylib style
                    $alt = $dylibs | Where-Object { $_.Name -like (($depLeaf -replace '\..*$','') + "*") } | Select-Object -First 1
                    if ($alt) { $depLeaf = $alt.Name; $local = $alt.FullName }
                }
                if ((Test-Path $local) -or ($dylibs.Name -contains $depLeaf)) {
                    if ($dep -ne "@loader_path/$depLeaf") {
                        try {
                            Invoke-Native "install_name_tool" @("-change", $dep, "@loader_path/$depLeaf", $lib.FullName)
                        }
                        catch {
                            Write-Info "install_name_tool change skipped $($lib.Name) $dep : $_"
                        }
                    }
                }
            }
        }
    }

    Assert-RequiredFiles @("libmsquic.dylib", "libssl.3.dylib", "libcrypto.3.dylib")
}

function Bundle-WindowsOsComponent {
    Write-Info "win-x64: MsQuic is an OS component (Windows 11 / Server 2022+). No DLL bundled."
    # Write a marker so release artifacts document the requirement.
    $marker = Join-Path $PublishDir "HTTP3-WINDOWS.txt"
    @"
HTTP/3 on Windows uses the OS-provided MsQuic (Schannel).
Minimum OS: Windows 11 or Windows Server 2022 or later.
QuicListener.IsSupported reports availability at runtime.
"@ | Set-Content -Path $marker -Encoding utf8
    if (-not (Test-Path $licenseDst) -and (Test-Path $licenseSrc)) {
        Copy-Item -Force $licenseSrc $licenseDst
    }
}

Write-Info "RID=$Rid PublishDir=$PublishDir mode=$($ridEntry.mode)"

switch ($ridEntry.mode) {
    "os-component" { Bundle-WindowsOsComponent }
    "deb" { Bundle-Deb }
    "apk" { Bundle-Apk }
    "homebrew" { Bundle-Homebrew }
    default { throw "Unknown mode '$($ridEntry.mode)' for RID $Rid" }
}

Write-Info "done"
