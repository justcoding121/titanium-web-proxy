# Install

Prefer the [Download](/download) page for buttons. This page is a short install guide.

## Inspector (desktop debugger)

1. Open [Download](/download) and pick **Inspector** for your OS (Windows MSI, macOS DMG, Linux AppImage / deb / rpm, or a portable zip).
2. Install or extract, then launch **Titanium Inspector**.
3. Follow [Inspector](/docs/inspector) to capture and decrypt traffic.

**Optional Windows package managers** (use **one** you already have):

```shell
winget install justcoding121.TitaniumInspector
# or
choco install titanium-inspector
```

Beta: `choco install titanium-inspector --pre`, or the beta section on Download.

## CLI (reverse proxy)

1. [Download](/download) the CLI for your OS (or use a package manager below).
2. Extract if needed so `titanium` (and `twp`) are on your PATH or in the current folder.
3. Create a config and run — see [Getting started](/docs/getting-started).

**Windows** (use **one** manager if you already have it):

```shell
winget install justcoding121.TitaniumCli
# or
choco install titanium-cli
```

**macOS** (Homebrew tap, when published):

```shell
brew tap justcoding121/titanium
brew install titanium
```

Stable builds are on [Download](/download). For beta: `choco install titanium-cli --pre`, the Download beta section, or [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases).

**Start at boot:** `titanium service install -c <config>` (Administrator / sudo). Details: [CLI — service](/docs/cli#service).

**Updates:**

```shell
titanium update
titanium version --check
```

Use `--channel beta` when you intentionally follow beta.

## Plus (optional ops add-on)

After the CLI is installed:

```shell
titanium update --plus
titanium version --check --plus
```

There is no separate Plus download. Enable in config (`plus.enabled: true`). See [Plus](/docs/plus).

## Library (.NET)

```shell
dotnet add package Titanium.Web.Proxy
# Newer than stable:
dotnet add package Titanium.Web.Proxy --prerelease
```

## Advanced

### Which Linux package?

- Most Linux desktops and servers: **AppImage**, **`.deb`**, **`.rpm`**, or the regular `linux-x64` / `linux-arm64` zip.
- **Alpine** or other **musl** containers: use the **Alpine / musl** zip (`linux-musl-x64` or `linux-musl-arm64`), not the regular Linux zip.
- HTTP/3 support is bundled in the platform packages — see [HTTP/3](/docs/http3).

### Trust / publisher

- **Windows:** Signed releases show Authenticode publisher **Jehonathan Thomas**. SmartScreen reputation builds over time.
- **macOS:** Prefer a notarized **DMG** for Inspector when published.
- **Linux:** Prefer AppImage / `.deb` / `.rpm`. Verify releases with `SHA256SUMS` and `SHA256SUMS.asc`:

```shell
curl -fsSL https://titaniumproxy.com/titanium-releases.asc | gpg --import
# Fingerprint: A824 E886 0DAB 01FA DA94  4E2D 8E5A B43F 41C8 DE8F
sha256sum -c SHA256SUMS
gpg --verify SHA256SUMS.asc SHA256SUMS
```

### Update channels

`titanium update` uses **stable** by default or **beta** when you pass `--channel beta`. It does not pick “whichever is newer” across channels. Same-version checks treat assembly `7.0.5.0` and feed tag `7.0.5` as equal.

## See also

- [Download](/download)
- [Getting started](/docs/getting-started)
- [Releases](/releases)
- [Packaging notes (for contributors / packagers)](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/packaging/PACKAGING.md)
