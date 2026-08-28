# Download

Get the latest stable builds from [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases/latest). Links below always resolve to the newest non-prerelease assets.

## CLI (`titanium` / `twp`)

Self-contained zip. Extract and run. Each zip includes both `titanium` and `twp` binaries.

<div class="download-grid">
  <div class="download-row">
    <strong>Windows x64</strong>
    <a href="https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/Titanium.Cli-win-x64.zip">Titanium.Cli-win-x64.zip</a>
  </div>
  <div class="download-row">
    <strong>Linux x64</strong>
    <a href="https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/Titanium.Cli-linux-x64.zip">Titanium.Cli-linux-x64.zip</a>
  </div>
  <div class="download-row">
    <strong>macOS x64</strong>
    <a href="https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/Titanium.Cli-osx-x64.zip">Titanium.Cli-osx-x64.zip</a>
  </div>
</div>

**winget** (Windows):

```shell
winget install justcoding121.TitaniumCli
```

```shell
titanium run -c twp.yaml
titanium version --check
titanium update
```

## Titanium Inspector

Desktop MITM debugger for Windows.

<div class="download-grid">
  <div class="download-row">
    <strong>Windows MSI</strong>
    <a href="https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/TitaniumInspector-win-x64.msi">TitaniumInspector-win-x64.msi</a>
  </div>
  <div class="download-row">
    <strong>Windows zip</strong>
    <a href="https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/TitaniumInspector-win-x64.zip">TitaniumInspector-win-x64.zip</a>
  </div>
</div>

**winget:**

```shell
winget install justcoding121.TitaniumInspector
```

## Titanium.Plus

Plus is **not** a separate download on this page. After the CLI is installed:

```shell
titanium update --plus
titanium version --check --plus
```

Place the DLL beside the CLI (the updater does this), then enable Plus in config:

```yaml
plus:
  enabled: true
  controlPlane:
    host: "127.0.0.1"
    port: 9080
    sharedSecret: "<shared-secret>"
```

Plus is licensed under [PolyForm Noncommercial](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt) — not for commercial use without a separate license agreement.

## Library (NuGet)

```shell
dotnet add package Titanium.Web.Proxy
```

Prerelease:

```shell
dotnet add package Titanium.Web.Proxy --prerelease
```

## Release notes

See [Releases](/releases) or [all assets on GitHub](https://github.com/justcoding121/titanium-web-proxy/releases/latest).
