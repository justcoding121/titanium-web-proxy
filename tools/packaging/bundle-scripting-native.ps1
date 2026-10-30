#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Bundle scripting natives (Lua 5.4 + Titanium Lua shim, Wasmtime C API) into a self-contained publish folder.

.DESCRIPTION
  Mirrors bundle-http3-native.ps1. Wasmtime is downloaded from the pinned GitHub release and verified
  against the sha256 in scripting-native.lock.json (always strict). Lua is built from the pinned
  lua.org source tarball (also sha256-verified) together with tools/scripting/lua-shim/*.c when a native
  compiler for the RID is available on the host. Missing compiler, missing shim sources, or a host that
  cannot build the RID produce a warning and skip Lua unless -Strict is set.

  Downloaded archives and build trees live under tools/packaging/.cache/scripting-natives (never commit).

.PARAMETER Rid
  Runtime identifier (win-x64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64, osx-x64, osx-arm64).

.PARAMETER PublishDir
  Output directory from `dotnet publish`.

.PARAMETER LockFile
  Path to scripting-native.lock.json (defaults next to this script).

.PARAMETER CacheDir
  Download cache directory (defaults to tools/packaging/.cache/scripting-natives).

.PARAMETER SkipLua
  Do not build Lua / the shim (Wasmtime only).

.PARAMETER SkipWasmtime
  Do not fetch Wasmtime (Lua only).

.PARAMETER Strict
  Fail instead of warning when Lua cannot be built (no compiler, no shim sources, host/RID mismatch).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [string] $LockFile = "",
    [string] $CacheDir = "",
    [switch] $SkipLua,
    [switch] $SkipWasmtime,
    [switch] $Strict
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "../.."))
if (-not $LockFile) { $LockFile = Join-Path $scriptDir "scripting-native.lock.json" }
if (-not $CacheDir) { $CacheDir = Join-Path $scriptDir ".cache/scripting-natives" }

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

function Write-Info([string] $msg) { Write-Host "[scripting-bundle] $msg" }
function Write-Warn([string] $msg) { Write-Host "[scripting-bundle] WARNING: $msg" }

$licenseSrc = Join-Path $scriptDir "THIRD-PARTY-SCRIPTING.txt"
$licenseDst = Join-Path $PublishDir "THIRD-PARTY-SCRIPTING.txt"
if (Test-Path $licenseSrc) {
    Copy-Item -Force $licenseSrc $licenseDst
}

function Get-Sha256([string] $path) {
    return (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

function Ensure-CacheDir([string] $name) {
    $dir = Join-Path $CacheDir $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Download-Verified([string] $url, [string] $sha256, [string] $destPath) {
    if ($sha256 -match "REPLACE_ON_FETCH") {
        throw "Lock file has a placeholder sha256 for $url — fill it in before bundling."
    }
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

function Test-IsWindowsHost { return ($env:OS -eq "Windows_NT") }
function Test-IsMacHost { return ((Get-Variable -Name IsMacOS -ValueOnly -ErrorAction SilentlyContinue) -eq $true) }
function Test-IsLinuxHost { return ((Get-Variable -Name IsLinux -ValueOnly -ErrorAction SilentlyContinue) -eq $true) }

function Assert-RequiredFiles([string[]] $names) {
    foreach ($n in $names) {
        if (-not (Test-Path (Join-Path $PublishDir $n))) {
            throw "Required native file missing in publish dir after bundle: $n (RID=$Rid)"
        }
    }
}

# --- Wasmtime ----------------------------------------------------------------------------------

function Bundle-Wasmtime {
    $wt = $ridEntry.wasmtime
    $cache = Ensure-CacheDir "wasmtime/$Rid"
    $leaf = Split-Path -Leaf $wt.url
    $archive = Join-Path $cache $leaf
    Download-Verified $wt.url $wt.sha256 $archive

    $extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("twp-wasmtime-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    try {
        if ($wt.archive -eq "zip") {
            Expand-Archive -Path $archive -DestinationPath $extractRoot -Force
        }
        else {
            Push-Location $extractRoot
            try { Invoke-Native "tar" @("-xf", $archive) }
            finally { Pop-Location }
        }

        $lib = Join-Path (Join-Path $extractRoot $wt.archiveRoot) $wt.libPath
        if (-not (Test-Path $lib)) {
            throw "Wasmtime archive layout changed: $($wt.libPath) not found under $($wt.archiveRoot)"
        }
        $dest = Join-Path $PublishDir $wt.output
        Copy-Item -Force $lib $dest

        $upstreamLicense = Join-Path (Join-Path $extractRoot $wt.archiveRoot) "LICENSE"
        if (Test-Path $upstreamLicense) {
            Copy-Item -Force $upstreamLicense (Join-Path $PublishDir "LICENSE-WASMTIME.txt")
        }

        if ($Rid -like "osx-*" -and (Find-Tool "install_name_tool")) {
            try { Invoke-Native "install_name_tool" @("-id", "@loader_path/$($wt.output)", $dest) }
            catch { Write-Info "install_name_tool skipped for $($wt.output): $_" }
        }
        Write-Info "wasmtime $($wt.version): $($wt.output)"
    }
    finally {
        Remove-Item -Recurse -Force $extractRoot -ErrorAction SilentlyContinue
    }
}

# --- Lua + shim --------------------------------------------------------------------------------

function Skip-Lua([string] $reason) {
    if ($Strict) { throw "Lua build required (-Strict) but unavailable: $reason" }
    Write-Warn "skipping Lua ($reason). Scripting will report lua as missing until built; see tools/scripting/README.md."
}

function Test-HostCanBuildRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $wantArm = $Rid -like "*arm64"
    $hostArm = $arch -eq "arm64"
    if ($wantArm -ne $hostArm) { return "host arch $arch cannot build $Rid natively" }

    if ($Rid -like "win-*") {
        if (-not (Test-IsWindowsHost)) { return "$Rid must be built on Windows" }
    }
    elseif ($Rid -like "osx-*") {
        if (-not (Test-IsMacHost)) { return "$Rid must be built on macOS" }
    }
    elseif ($Rid -like "linux-musl-*") {
        if (-not (Test-IsLinuxHost) -or -not (Test-Path "/etc/alpine-release")) {
            return "$Rid must be built on an Alpine (musl) host/container"
        }
    }
    elseif ($Rid -like "linux-*") {
        if (-not (Test-IsLinuxHost)) { return "$Rid must be built on Linux" }
        if (Test-Path "/etc/alpine-release") { return "$Rid (glibc) must not be built on a musl host" }
    }
    return $null
}

function Bundle-Lua {
    $lua = $ridEntry.lua
    if ($lua.mode -ne "build-from-source") {
        throw "Unsupported lua mode '$($lua.mode)' for RID $Rid"
    }

    $mismatch = Test-HostCanBuildRid
    if ($mismatch) { Skip-Lua $mismatch; return }

    $shimDir = Join-Path $repoRoot $lua.shim.source
    $shimSources = @()
    if (Test-Path $shimDir) {
        $shimSources = @(Get-ChildItem -Path $shimDir -File -Filter "*.c" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    }
    if ($shimSources.Count -eq 0) {
        Skip-Lua "no C sources under $($lua.shim.source) yet (Titanium Lua shim not landed)"
        return
    }

    $cache = Ensure-CacheDir "lua"
    $tarball = Join-Path $cache (Split-Path -Leaf $lua.sourceUrl)
    Download-Verified $lua.sourceUrl $lua.sha256 $tarball

    $buildRoot = Join-Path ([IO.Path]::GetTempPath()) ("twp-lua-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
    try {
        Push-Location $buildRoot
        try { Invoke-Native "tar" @("-xzf", $tarball) }
        finally { Pop-Location }

        $srcDir = Join-Path (Join-Path $buildRoot $lua.sourceDir) "src"
        if (-not (Test-Path $srcDir)) { throw "Lua source layout changed: $srcDir not found" }

        $luaSources = @(Get-ChildItem -Path $srcDir -File -Filter "*.c" |
            Where-Object { $_.Name -notin @("lua.c", "luac.c") } |
            ForEach-Object { $_.FullName })
        $allSources = $luaSources + $shimSources
        $out = Join-Path $PublishDir $lua.output

        if ($Rid -like "win-*") {
            $cl = Find-Tool "cl"
            $gcc = Find-Tool "gcc"
            if ($cl) {
                Push-Location $buildRoot
                try {
                    Invoke-Native $cl (@("/nologo", "/O2", "/LD", "/DLUA_BUILD_AS_DLL", "/I$srcDir", "/I$shimDir") + $allSources + @("/Fe:$out"))
                }
                finally { Pop-Location }
            }
            elseif ($gcc) {
                Invoke-Native $gcc (@("-O2", "-shared", "-DLUA_BUILD_AS_DLL", "-I$srcDir", "-I$shimDir", "-o", $out) + $allSources)
            }
            else { Skip-Lua "neither cl nor gcc found on PATH"; return }
        }
        else {
            $cc = Find-Tool "cc"
            if (-not $cc) { $cc = Find-Tool "gcc" }
            if (-not $cc) { $cc = Find-Tool "clang" }
            if (-not $cc) { Skip-Lua "no C compiler (cc/gcc/clang) found"; return }

            if ($Rid -like "osx-*") {
                Invoke-Native $cc (@("-O2", "-dynamiclib", "-fPIC", "-DLUA_USE_MACOSX", "-I$srcDir", "-I$shimDir",
                        "-install_name", "@loader_path/$($lua.output)", "-o", $out) + $allSources + @("-lm"))
            }
            else {
                $def = if ($Rid -like "linux-musl-*") { "-DLUA_USE_POSIX" } else { "-DLUA_USE_LINUX" }
                $libs = if ($Rid -like "linux-musl-*") { @("-lm") } else { @("-ldl", "-lm") }
                Invoke-Native $cc (@("-O2", "-shared", "-fPIC", $def, "-I$srcDir", "-I$shimDir",
                        "-Wl,-soname,$($lua.output)", "-o", $out) + $allSources + $libs)
            }
        }
        Write-Info "lua $($lua.version) + shim: $($lua.output)"
    }
    finally {
        Remove-Item -Recurse -Force $buildRoot -ErrorAction SilentlyContinue
    }
}

Write-Info "RID=$Rid PublishDir=$PublishDir"

if (-not $SkipWasmtime) { Bundle-Wasmtime }
if (-not $SkipLua) { Bundle-Lua }

# Assert only what was requested and what can exist; a skipped Lua build is a warning, not a failure.
$required = @()
if (-not $SkipWasmtime) { $required += $ridEntry.wasmtime.output }
if (-not $SkipLua -and (Test-Path (Join-Path $PublishDir $ridEntry.lua.output))) { $required += $ridEntry.lua.output }
Assert-RequiredFiles $required

Write-Info "done"
# Keep GitHub Actions pwsh steps green after soft-skipped native tool failures.
$global:LASTEXITCODE = 0
