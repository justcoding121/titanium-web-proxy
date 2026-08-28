# Install

Full download buttons live on the [Download](/download) page. This page is the short install guide.

## Library

```shell
dotnet add package Titanium.Web.Proxy
dotnet add package Titanium.Web.Proxy --prerelease
```

## CLI

On Windows:

```shell
winget install justcoding121.TitaniumCli
```

Or take a self-contained zip from [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases) / the [Download](/download) page when `Titanium.Cli-*.zip` assets are published.

Pick the **matching RID** (e.g. Alpine/K8s → `linux-musl-x64` or `linux-musl-arm64`, not `linux-x64`). HTTP/3 natives ship inside those zips — see [HTTP/3](/docs/http3).

```shell
titanium update
titanium version --check
titanium http3-deps status
```

## Plus

```shell
titanium update --plus
```

See [Plus](/docs/plus). There is no separate Plus download link.

## Inspector

```shell
winget install justcoding121.TitaniumInspector
```

Or MSI / portable zip from [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases) / [Download](/download) when those assets are published. Linux and macOS Inspector builds are zip-only; Windows also ships an MSI. HTTP/3 natives are bundled the same way as the CLI ([HTTP/3](/docs/http3)).

## See also

- [Download](/download)
- [Releases](/releases)
- [Getting started](/docs/getting-started)
