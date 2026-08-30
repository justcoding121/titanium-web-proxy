# Packaging notes (Titanium Inspector / Cli)

## RID matrix (7.0)

| Product | RIDs | Notes |
| --- | --- | --- |
| CLI | `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64` | Self-contained zips |
| Inspector | Same RIDs | Zip for all; **MSI only** for `win-x64` |

HTTP/3 natives are bundled into every Linux/macOS zip via [`bundle-http3-native.ps1`](bundle-http3-native.ps1) + [`http3-native.lock.json`](http3-native.lock.json). Windows uses OS MsQuic (Win11 / Server 2022+).

## Publish (local)

```powershell
$rid = "linux-x64"   # or linux-musl-x64, osx-arm64, win-x64, …
dotnet publish src/Titanium.Cli/Titanium.Cli.csproj -c Release -r $rid --self-contained true -p:AssemblyName=titanium -o artifacts/cli/$rid
pwsh ./tools/packaging/bundle-http3-native.ps1 -Rid $rid -PublishDir artifacts/cli/$rid
```

Inspector:

```powershell
dotnet publish src/Titanium.Inspector/Titanium.Inspector.csproj -c Release -r $rid --self-contained true -o artifacts/inspector/$rid
pwsh ./tools/packaging/bundle-http3-native.ps1 -Rid $rid -PublishDir artifacts/inspector/$rid
```

### MSI (Inspector, Windows only)

```powershell
./tools/packaging/build-inspector-msi.ps1 `
  -PayloadDir artifacts/inspector/win-x64 `
  -OutputMsi TitaniumInspector-win-x64.msi `
  -Version 7.0.0
```

Uses WiX 5 (`dotnet tool` manifest under `tools/packaging/wix/`). Authenticode signing is stretch; unsigned MSI is fine for GitHub Releases / early winget.

Winget package IDs:

- `justcoding121.TitaniumInspector` (prefer MSI when attached to the Release)
- `justcoding121.TitaniumCli` (portable zip)

Manifest stubs live in `tools/packaging/winget/`.

## HTTP/3 native lock file

- **Lock:** [`http3-native.lock.json`](http3-native.lock.json) — pinned URLs + SHA256 per RID.
- **Script:** [`bundle-http3-native.ps1`](bundle-http3-native.ps1) — download/verify → extract → copy beside binary → `patchelf --set-rpath '$ORIGIN'` (Linux) or `install_name_tool` `@loader_path` (macOS).
- **Cache:** `tools/packaging/.cache/http3-natives/` (gitignored). CI caches this with `actions/cache` keyed on the lock file.
- **License:** [`THIRD-PARTY-HTTP3.txt`](THIRD-PARTY-HTTP3.txt) is copied into every publish folder (MsQuic MIT + OpenSSL Apache-2.0). Inspector also ships [`THIRD-PARTY-INSPECTOR.txt`](THIRD-PARTY-INSPECTOR.txt) (Inter SIL OFL-1.1).

| RID | Source |
| --- | --- |
| `win-x64` | OS component (no DLL shipped) |
| `linux-x64` / `linux-arm64` | Ubuntu 22.04-class `.deb` (Microsoft `libmsquic` + Ubuntu `libssl3`). Host package: `libnuma1` via `http3-deps` / apt |
| `linux-musl-x64` / `linux-musl-arm64` | Alpine v3.24 `.apk` (`libmsquic` + Alpine OpenSSL). Host packages: `numactl` + `lttng-ust` via `http3-deps` / apk |
| `osx-x64` / `osx-arm64` | Homebrew `libmsquic` + `openssl@3` on `macos-latest` |

Do **not** mix glibc `.so` into musl zips (or the reverse). Do **not** redistribute LGPL/GPL natives (`libnuma`, `lttng-ust`) in the zip — install them on the host.

## CI

See [`.github/workflows/release.yml`](../../.github/workflows/release.yml):

- Matrix RID × runner (`ubuntu-latest` for win/linux/musl; `macos-latest` for osx).
- `setup-dotnet` with `cache: true`.
- Native download cache keyed on lock file hash.
- Smoke: `linux-x64` on host + `linux-musl-x64` inside `alpine:3.24` — `titanium http3-deps status` + assert natives present.

Native bundling runs on **release** only (not every PR).

## Quarterly lock-file bump (CVE ownership)

Bundling OpenSSL/MsQuic means **this repo owns CVE patching** for those versions in distributed zips.

1. Bump package URLs/versions in `http3-native.lock.json`.
2. Recompute SHA256 for each URL.
3. Run a release (or local publish + smoke) for `linux-x64` and `linux-musl-x64`.
4. Ship a new GitHub Release so operators pick up patched natives.

Target cadence: **at least quarterly**, and immediately for critical OpenSSL/MsQuic CVEs.

## Fallback for edge hosts

```bash
titanium http3-deps status
titanium http3-deps install   # apt / dnf / zypper / apk / brew
```

Not run automatically by MSI/winget. NuGet library consumers stay docs-only (system MsQuic).

## Operator docs

- Website: `/docs/http3`, `/docs/install`, download page RID table
- Wiki: `HTTP-3.md` packaging sections
