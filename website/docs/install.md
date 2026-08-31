# Install

Full download buttons live on the [Download](/download) page. This page is the short install guide.

## Library

```shell
dotnet add package Titanium.Web.Proxy
dotnet add package Titanium.Web.Proxy --prerelease
```

## CLI

On Windows, **winget is stable-only**:

```shell
winget install justcoding121.TitaniumCli
```

For **beta** (or any OS), take a self-contained zip from the [Download](/download) page or [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases) when `Titanium.Cli-*.zip` assets are published (e.g. `v7.0.2-beta`).

Pick the **matching RID** (e.g. Alpine/K8s → `linux-musl-x64` or `linux-musl-arm64`, not `linux-x64`). HTTP/3 natives ship inside those zips — see [HTTP/3](/docs/http3).

```shell
titanium update --channel beta
titanium version --check --channel beta
titanium http3-deps status
```

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

Or MSI / portable zip from [Download](/download) / [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases) when those assets are published (beta example: `v7.0.2-beta`). Linux and macOS Inspector builds are zip-only; Windows also ships an MSI. HTTP/3 natives are bundled the same way as the CLI ([HTTP/3](/docs/http3)).

## See also

- [Download](/download)
- [Releases](/releases)
- [Getting started](/docs/getting-started)
