# Scripting natives (local development)

Titanium scripting loads two native libraries next to the CLI / Inspector binary:

| Engine | Files | Source |
| --- | --- | --- |
| Lua 5.4.7 + Titanium host shim (ctx + libraries) | `lua54.dll` / `liblua54.so` / `liblua54.dylib` | Built from the pinned lua.org tarball plus `tools/scripting/lua-shim/*.c` |
| Wasmtime C API | `wasmtime.dll` / `libwasmtime.so` / `libwasmtime.dylib` | Prebuilt GitHub release archive |

Versions, URLs, and SHA-256 values are pinned in
[`tools/packaging/scripting-native.lock.json`](../packaging/scripting-native.lock.json).
Licenses: [`THIRD-PARTY-SCRIPTING.txt`](../packaging/THIRD-PARTY-SCRIPTING.txt).

## Fetch / build with the bundle script

Publish (or pick any output folder) and run the same script CI uses:

```powershell
dotnet publish src/Titanium.Cli/Titanium.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/cli/win-x64
./tools/packaging/bundle-scripting-native.ps1 -Rid win-x64 -PublishDir artifacts/cli/win-x64
```

- Wasmtime is always downloaded and SHA-256 verified (`-SkipWasmtime` to omit).
- Lua is built only when the host can build the RID natively (same OS/arch; `linux-musl-*` needs Alpine)
  **and** `tools/scripting/lua-shim/` contains C sources. Otherwise the script warns and skips it;
  add `-Strict` to fail instead, or `-SkipLua` to skip on purpose.
- Downloads are cached under `tools/packaging/.cache/scripting-natives/` (git-ignored; never commit binaries).

## Build the Lua shim for development

```powershell
./tools/scripting/build-lua-shim.ps1 -OutDir src/Titanium.Cli/bin/Debug/net10.0
```

The script downloads and verifies the pinned Lua tarball, then compiles it with the shim into one library in `-OutDir`.
It skips the build when the output is newer than every shim source (`-Force` rebuilds). On Windows it uses `cl` from
`PATH`, then Visual Studio C++ tools found with `vswhere`, then MinGW `gcc`, then Zig. A machine with none of them can
pass `-FetchZig` to download the pinned Zig toolchain (SHA-256 verified) into `tools/packaging/.cache/toolchains/`.

`tests/Titanium.Web.Proxy.Scripting.Tests` runs the script after every build (output in
`artifacts/scripting-natives/<rid>/`, copied beside the tests). Lua tests are inconclusive when it cannot build.
Set `TITANIUM_SCRIPTING_NATIVE_DIR` to load `lua54` from another folder.

## Shim layout

| File | Contents |
| --- | --- |
| `twp_state.c` | State lifecycle, capped allocator, instruction/deadline hook, sandbox, `ctx`, start/resume/cancel. |
| `twp_codec.c` | Tagged binary format shared with `LuaCodec.cs` (snapshots in, command buffers out). |
| `twp_strlib.c` | Step-bounded Lua pattern matcher; `string.rep` / `string.format` caps; no `string.dump`. |
| `twp_platform.c` | Clocks, OS random bytes, locks. |

Exported functions never let a Lua error leave the library: each runs the state through `lua_pcall` or
`lua_resume`, and the panic handler jumps back to the exported function as a last resort and marks the state broken.

## Build the Lua shim by hand

Compile the Lua core (everything in `src/` except `lua.c` and `luac.c`) together with the shim sources.

```bash
# Linux (glibc); use -DLUA_USE_POSIX and drop -ldl on musl/Alpine
curl -LO https://www.lua.org/ftp/lua-5.4.7.tar.gz
echo "9fbf5e28ef86c69858f6d3d34eccc32e911c1a28b4120ff3e84aaa70cfbf1e30  lua-5.4.7.tar.gz" | sha256sum -c
tar xzf lua-5.4.7.tar.gz
SRC=$(ls lua-5.4.7/src/*.c | grep -v -e '/lua\.c$' -e '/luac\.c$')
cc -O2 -shared -fPIC -DLUA_USE_LINUX -Ilua-5.4.7/src -Itools/scripting/lua-shim \
   -o liblua54.so $SRC tools/scripting/lua-shim/*.c -ldl -lm

# macOS
cc -O2 -dynamiclib -fPIC -DLUA_USE_MACOSX -Ilua-5.4.7/src -Itools/scripting/lua-shim \
   -o liblua54.dylib $SRC tools/scripting/lua-shim/*.c -lm
```

```powershell
# Windows (MinGW gcc; or use MSVC: cl /LD /O2 /DLUA_BUILD_AS_DLL ... /Fe:lua54.dll)
gcc -O2 -shared -DLUA_BUILD_AS_DLL -Ilua-5.4.7/src -Itools/scripting/lua-shim -o lua54.dll <lua src *.c> tools/scripting/lua-shim/*.c
```

Copy the result next to `titanium` (or into `bin/<Config>/<tfm>/`).

## Check what loads

```text
titanium scripting-deps status
```

Prints whether the Lua and Wasmtime natives load, plus per-OS fixes. See also `titanium scripting-deps install`.
