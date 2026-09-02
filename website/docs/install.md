# Install

Full download buttons live on the [Download](/download) page. This page is the short install guide.

## Trust / publisher

- **Windows:** Signed releases show Authenticode publisher **Jehonathan Thomas** (Azure Artifact Signing). SmartScreen reputation still builds over downloads/time.
- **macOS:** Prefer the notarized **DMG** for Inspector when published. CLI zips are signed/notarized when Apple Developer secrets are configured in CI.
- **Linux:** Prefer AppImage / `.deb` / `.rpm` (glibc) or Flathub when listed. Verify GitHub assets with `SHA256SUMS` (+ `SHA256SUMS.asc` when GPG signing is enabled):

```shell
# From a release asset directory
sha256sum -c SHA256SUMS
# Optional, when SHA256SUMS.asc is attached:
gpg --verify SHA256SUMS.asc SHA256SUMS
```

## Library

```shell
dotnet add package Titanium.Web.Proxy
# Prerelease when newer than stable:
dotnet add package Titanium.Web.Proxy --prerelease
```

## CLI

On Windows, **winget is stable-only**:

```shell
winget install justcoding121.TitaniumCli
```

**macOS (Homebrew tap, when published):**

```shell
brew tap justcoding121/titanium
brew install titanium
```

Stable CLI packages are on the [Download](/download) page (`v7.0.4`). For **beta** (or any OS), use the beta section or [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases) when `Titanium.Cli-*` assets are published (e.g. `v7.0.4-beta`).

Pick the **matching RID** (e.g. Alpine/K8s → `linux-musl-x64` or `linux-musl-arm64`, not `linux-x64`). Prefer AppImage / deb / rpm on glibc Linux; musl stays zip-only. HTTP/3 natives ship inside those packages — see [HTTP/3](/docs/http3).

```shell
titanium update --channel beta
titanium version --check --channel beta
titanium http3-deps status
```

`titanium update` checks the selected channel, downloads the RID **zip**, verifies SHA256, replaces the install directory after the process exits, and prints the new version. Use `--channel stable` (default) or `--channel beta` — it does not pick “whichever is newer” across channels.

## Plus

```shell
titanium update --plus --channel beta
```

See [Plus](/docs/plus). There is no separate Plus download link.

## Inspector

Prefer [Download](/download). **winget is stable-only**:

```shell
winget install justcoding121.TitaniumInspector
```

Or MSI / DMG / AppImage / deb / rpm / portable zip from [Download](/download) / [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases) (stable: `v7.0.4`; beta example: `v7.0.4-beta`). **Windows:** MSI (signed). **macOS:** DMG when published (else zip + `install-app.sh`). **Linux glibc:** AppImage / `.deb` / `.rpm` when published. **Alpine musl:** zip only. HTTP/3 natives are bundled the same way as the CLI ([HTTP/3](/docs/http3)).

## See also

- [Download](/download)
- [Releases](/releases)
- [Getting started](/docs/getting-started)
- [Packaging notes](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/packaging/PACKAGING.md)
