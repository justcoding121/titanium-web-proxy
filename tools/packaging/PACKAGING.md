# Packaging notes (Titanium Inspector / Cli)

## RID matrix (7.0)

| Product | RIDs | Primary formats | Also attached |
| --- | --- | --- | --- |
| CLI | `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-*`, `osx-*` | win zip; linux glibc AppImage+deb+rpm; musl zip; osx zip (+ Homebrew) | All RIDs keep a **zip** for `titanium update` |
| Inspector | Same RIDs | win **MSI**; osx **DMG**; linux glibc AppImage+deb+rpm; musl zip | Zip for all RIDs |

HTTP/3 natives are bundled into every Linux/macOS publish via [`bundle-http3-native.ps1`](bundle-http3-native.ps1) + [`http3-native.lock.json`](http3-native.lock.json). Windows uses OS MsQuic (Win11 / Server 2022+).

Website [download](../../website/download.md) prefers MSI/DMG/AppImage/deb/rpm and hides redundant zips when those exist.

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

Uses WiX 5 (`dotnet tool` manifest under `tools/packaging/wix/`) plus **WixToolset.UI.wixext** / **Util**.

### Windows Authenticode

Uses [Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) in [`release.yml`](../../.github/workflows/release.yml) (`win-x64` CLI `titanium.exe`/`twp.exe`, `TitaniumInspector.exe`, and the MSI). Publisher is the validated individual identity (currently `CN=Jehonathan Thomas`). Leaf certificates are short-lived; timestamping is `http://timestamp.acs.microsoft.com`.

### macOS codesign + notarize + DMG

Scripts under [`osx/`](osx/):

| Script | Role |
| --- | --- |
| `build-app-bundle.sh` | `.app` from Inspector publish folder |
| `build-dmg.sh` | UDZO DMG with Applications symlink |
| `sign-and-notarize.sh` | Developer ID codesign + `notarytool` + staple |
| `TitaniumInspector.entitlements` / `TitaniumCli.entitlements` | Hardened runtime (JIT + network) |

CI is **gated**: if `APPLE_CERTIFICATE_P12` (and related secrets) are unset, macOS jobs still build an **unsigned** DMG for artifact shape; notarize is skipped. Required GitHub secrets: `APPLE_DEVELOPER_ID`, `APPLE_CERTIFICATE_P12` (base64), `APPLE_CERTIFICATE_PASSWORD`, `NOTARY_KEY`, `NOTARY_KEY_ID`, `NOTARY_ISSUER`.

### Linux AppImage / deb / rpm

| Script | Role |
| --- | --- |
| [`linux/build-appimage.sh`](linux/build-appimage.sh) | AppImage for `cli` or `inspector` (glibc `linux-x64` / `linux-arm64`) |
| [`linux/build-deb-rpm.sh`](linux/build-deb-rpm.sh) | `.deb` + `.rpm` via `fpm` (deb fallback without fpm) |

Release asset names match the website loader: `Titanium.Cli-{rid}.{AppImage,deb,rpm}`, `TitaniumInspector-{rid}.{AppImage,deb,rpm,dmg}`.

### GPG checksums

[`sign-checksums.sh`](sign-checksums.sh) writes `SHA256SUMS` (always) and `SHA256SUMS.asc` / `release-manifest.json.asc` when `GPG_PRIVATE_KEY` (+ optional `GPG_PASSPHRASE`) is set in the release job.

### Homebrew (Mac CLI)

Formula source of truth: [`homebrew/titanium.rb`](homebrew/titanium.rb). After a release, run [`homebrew/bump-formula-shas.sh`](homebrew/bump-formula-shas.sh) and push to the `justcoding121/homebrew-titanium` tap (`Formula/titanium.rb`).

```shell
brew tap justcoding121/titanium
brew install titanium
```

### Flathub (Inspector only)

Inspector Flatpak under [`flatpak/`](flatpak/). CLI is not submitted to Flathub (console apps are rejected). See [`flatpak/README.md`](flatpak/README.md).

### Linux / macOS desktop helpers (Inspector zips)

Release publish copies helpers into each Inspector zip:

| RID | Helpers |
| --- | --- |
| `linux-*` | `install.sh`, `uninstall.sh`, `TitaniumInspector.desktop.in`, `app.ico`, `desktop-icons.sh`, `titanium-inspector*.png` |
| `osx-*` | `install-app.sh`, `uninstall-app.sh`, `app.ico`, `desktop-icons.sh`, `AppIcon.icns` |

Prebuilt icons live in [`icons/`](icons/) (hicolor PNG sizes + macOS `AppIcon.icns`). Deb/rpm/AppImage/DMG packaging installs them into the OS icon locations.

**Linux:** extract the zip, then `./install.sh` (default prefix `~/.local`). **macOS:** extract, then `./install-app.sh` → `~/Applications/Titanium Inspector.app` (prefer DMG when published).

Winget package IDs:

- `justcoding121.TitaniumInspector` (prefer MSI when attached to the Release)
- `justcoding121.TitaniumCli` (portable zip)

Manifest stubs live in `tools/packaging/winget/`. Resubmit to `microsoft/winget-pkgs` only after the **first signed stable** with fresh SHA256s (do not resubmit unsigned `7.0.4`).

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

- Matrix RID × runner (`windows-latest` for `win-x64` CLI/Inspector; `ubuntu-latest` for linux/musl; `macos-latest` for osx).
- Azure Artifact Signing on Windows; optional Apple notarize when secrets exist; optional GPG on checksums.
- Smoke: `linux-x64` on host + `linux-musl-x64` inside `alpine:3.24` — `titanium http3-deps status` + assert natives present.

Native bundling runs on **release** only (not every PR).

### Local macOS Debug / `dotnet run`

`copy-http3-natives-osx.sh` (Inspector + CLI `CopyMacHttp3Natives` target) copies Homebrew MsQuic + OpenSSL beside `$(TargetDir)` with `@loader_path` rewrites. Framework-dependent hosts still need that directory on `DYLD_FALLBACK_LIBRARY_PATH` because `System.Net.Quic` loads MsQuic by leaf name only. Inspector/CLI call `Http3NativeBootstrap.EnsureAppLocalMsQuicVisible` at startup (and regenerate gitignored `Properties/launchSettings.json`) so a normal Debug launch enables HTTP/3 after `brew install libmsquic openssl@3`. Self-contained RID publishes do not need the re-exec path.

## Quarterly lock-file bump (CVE ownership)

Bundling OpenSSL/MsQuic means **this repo owns CVE patching** for those versions in distributed zips.

1. Bump package URLs/versions in `http3-native.lock.json`.
2. Recompute SHA256 for each URL.
3. Run a release (or local publish + smoke) for `linux-x64` and `linux-musl-x64`.
4. Ship a new GitHub Release so operators pick up patched natives.
