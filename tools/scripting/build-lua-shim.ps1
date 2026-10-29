<#
.SYNOPSIS
  Build the Lua 5.4 core plus the Titanium Lua shim into one native library for this host.

.DESCRIPTION
  Downloads (or reuses from tools/packaging/.cache/scripting-natives/lua) the Lua tarball pinned in
  tools/packaging/scripting-native.lock.json, verifies its SHA-256, and compiles every Lua core source except
  lua.c / luac.c together with tools/scripting/lua-shim/*.c.

  Windows: uses cl from PATH, else finds Visual Studio C++ tools with vswhere and runs vcvars64, else gcc, else
  Zig (on PATH, in the cache, or downloaded and SHA-256 verified with -FetchZig).
  Linux / macOS: uses cc, gcc, or clang.

  The output is lua54.dll, liblua54.so, or liblua54.dylib in -OutDir. Skips the build when the output is newer
  than every shim source unless -Force is given.

.EXAMPLE
  ./tools/scripting/build-lua-shim.ps1 -OutDir src/Titanium.Cli/bin/Debug/net10.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [switch] $Force,
    [switch] $FetchZig
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$lockFile = Join-Path $repoRoot "tools/packaging/scripting-native.lock.json"
$shimDir = Join-Path $repoRoot "tools/scripting/lua-shim"
$cacheDir = Join-Path $repoRoot "tools/packaging/.cache/scripting-natives/lua"

function Write-Info([string] $msg) { Write-Host "[lua-shim] $msg" }

# Zig bundles clang and MinGW headers, so it builds the Windows DLL on a machine with no Visual Studio C++ SDK.
$zigVersion = "0.14.1"
$zigUrl = "https://ziglang.org/download/$zigVersion/zig-x86_64-windows-$zigVersion.zip"
$zigSha256 = "554f5378228923ffd558eac35e21af020c73789d87afeabf4bfd16f2e6feed2c"
$toolchainDir = Join-Path $repoRoot "tools/packaging/.cache/toolchains"

function Find-Zig {
    $onPath = (Get-Command zig -ErrorAction SilentlyContinue).Source
    if ($onPath) { return $onPath }
    $cached = Join-Path $toolchainDir "zig-x86_64-windows-$zigVersion/zig.exe"
    if (Test-Path $cached) { return $cached }
    if (-not $FetchZig) { return $null }
    New-Item -ItemType Directory -Force -Path $toolchainDir | Out-Null
    $zip = Join-Path $toolchainDir "zig-$zigVersion.zip"
    Write-Info "download $zigUrl"
    Invoke-WebRequest -Uri $zigUrl -OutFile $zip -UseBasicParsing
    $got = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($got -ne $zigSha256) {
        Remove-Item -Force $zip
        throw "SHA256 mismatch for $zigUrl`: expected $zigSha256, got $got"
    }
    Expand-Archive -Path $zip -DestinationPath $toolchainDir -Force
    Remove-Item -Force $zip
    return $cached
}

$isWin = $env:OS -eq "Windows_NT"
$isMac = (Get-Variable -Name IsMacOS -ValueOnly -ErrorAction SilentlyContinue) -eq $true
$output = if ($isWin) { "lua54.dll" } elseif ($isMac) { "liblua54.dylib" } else { "liblua54.so" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$outPath = Join-Path $OutDir $output

$shimSources = @(Get-ChildItem -Path $shimDir -File -Include "*.c", "*.h" -Recurse)
if (-not $Force -and (Test-Path $outPath)) {
    $built = (Get-Item $outPath).LastWriteTimeUtc
    $newest = ($shimSources | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
    if ($built -ge $newest) {
        Write-Info "up to date: $outPath"
        return
    }
}

$lock = Get-Content -Raw -Path $lockFile | ConvertFrom-Json
$rid = if ($isWin) { "win-x64" } elseif ($isMac) { "osx-arm64" } else { "linux-x64" }
$lua = $lock.rids.$rid.lua
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
$tarball = Join-Path $cacheDir (Split-Path -Leaf $lua.sourceUrl)
$want = $lua.sha256.ToLowerInvariant()
if (-not (Test-Path $tarball) -or (Get-FileHash -Algorithm SHA256 $tarball).Hash.ToLowerInvariant() -ne $want) {
    Write-Info "download $($lua.sourceUrl)"
    $tmp = "$tarball.partial"
    Invoke-WebRequest -Uri $lua.sourceUrl -OutFile $tmp -UseBasicParsing
    $got = (Get-FileHash -Algorithm SHA256 $tmp).Hash.ToLowerInvariant()
    if ($got -ne $want) {
        Remove-Item -Force $tmp
        throw "SHA256 mismatch for $($lua.sourceUrl): expected $want, got $got"
    }
    Move-Item -Force $tmp $tarball
}

$srcRoot = Join-Path $cacheDir $lua.sourceDir
if (-not (Test-Path (Join-Path $srcRoot "src/lua.h"))) {
    Push-Location $cacheDir
    try { tar -xzf $tarball; if ($LASTEXITCODE -ne 0) { throw "tar failed" } }
    finally { Pop-Location }
}
$srcDir = Join-Path $srcRoot "src"
$luaSources = @(Get-ChildItem -Path $srcDir -File -Filter "*.c" | Where-Object { $_.Name -notin @("lua.c", "luac.c") } |
        ForEach-Object { $_.FullName })
$cSources = @($shimSources | Where-Object { $_.Extension -eq ".c" } | ForEach-Object { $_.FullName })
$all = $luaSources + $cSources

$objDir = Join-Path ([IO.Path]::GetTempPath()) ("twp-lua-obj-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $objDir | Out-Null
try {
    if ($isWin) {
        $cl = (Get-Command cl -ErrorAction SilentlyContinue).Source
        # Trailing '/' (not '\') so quoting the /Fo arg in an .rsp file cannot escape the closing '"'.
        $foDir = $objDir.TrimEnd('\', '/') + '/'
        $clArgs = @("/nologo", "/O2", "/MT", "/W3", "/LD", "/DLUA_BUILD_AS_DLL", "/D_CRT_SECURE_NO_WARNINGS",
            "/I$srcDir", "/I$shimDir", "/Fo$foDir") + $all + @("/Fe:$outPath", "/link", "/NOLOGO")
        if ($cl) {
            & $cl @clArgs
            if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }
        }
        else {
            $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
            $vs = if (Test-Path $vswhere) {
                & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            }
            $vcvarsall = if ($vs) { Join-Path $vs "VC/Auxiliary/Build/vcvarsall.bat" }
            $vcvars = if ($vs) { Join-Path $vs "VC/Auxiliary/Build/vcvars64.bat" }
            $zig = Find-Zig
            if ($vcvarsall -and (Test-Path $vcvarsall) -and (Test-Path $vcvars)) {
                $rsp = Join-Path $objDir "cl.rsp"
                Set-Content -Path $rsp -Value (($clArgs | ForEach-Object { '"' + $_ + '"' }) -join "`n")
                cmd /c "call `"$vcvars`" >nul && cl @`"$rsp`""
                if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }
            }
            elseif (Get-Command gcc -ErrorAction SilentlyContinue) {
                & gcc -O2 -shared -DLUA_BUILD_AS_DLL "-I$srcDir" "-I$shimDir" -o $outPath @all -static-libgcc
                if ($LASTEXITCODE -ne 0) { throw "gcc failed ($LASTEXITCODE)" }
            }
            elseif ($zig) {
                Write-Info "using $zig"
                & $zig cc -target x86_64-windows-gnu -O2 -shared -DLUA_BUILD_AS_DLL "-I$srcDir" "-I$shimDir" -o $outPath @all
                if ($LASTEXITCODE -ne 0) { throw "zig cc failed ($LASTEXITCODE)" }
                # Zig names the import library after the first object (lapi.lib); nothing links against it.
                Remove-Item -Force (Join-Path $OutDir "lapi.lib") -ErrorAction SilentlyContinue
            }
            else {
                throw "No C compiler: install the Visual Studio C++ build tools (with the Windows SDK) or MinGW gcc, " +
                    "or run again with -FetchZig to download the pinned Zig toolchain into the cache."
            }
        }
        Get-ChildItem -Path $OutDir -Filter "lua54.*" | Where-Object { $_.Extension -in ".lib", ".exp", ".pdb" } | Remove-Item -Force
    }
    else {
        $cc = @("cc", "gcc", "clang") | ForEach-Object { (Get-Command $_ -ErrorAction SilentlyContinue).Source } |
            Select-Object -First 1
        if (-not $cc) { throw "No C compiler (cc / gcc / clang) found." }
        if ($isMac) {
            & $cc -O2 -dynamiclib -fPIC -DLUA_USE_MACOSX "-I$srcDir" "-I$shimDir" -install_name "@loader_path/$output" -o $outPath @all -lm
        }
        else {
            & $cc -O2 -shared -fPIC -DLUA_USE_LINUX -fvisibility=hidden "-I$srcDir" "-I$shimDir" "-Wl,-soname,$output" -o $outPath @all -ldl -lm -lpthread
        }
        if ($LASTEXITCODE -ne 0) { throw "$cc failed ($LASTEXITCODE)" }
    }
}
finally {
    Remove-Item -Recurse -Force $objDir -ErrorAction SilentlyContinue
}
Write-Info "built $outPath"
