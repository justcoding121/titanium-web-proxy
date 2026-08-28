# Download

<script setup>
import { data as links } from './download.data.ts'
</script>

Get CLI and Inspector builds from GitHub Releases, or install with winget on Windows.

HTTP/3 natives ship inside each RID zip (except Windows, which uses OS MsQuic on Win11 / Server 2022+). Alpine/K8s: use **`linux-musl-*`**, not `linux-x64`. Details: [HTTP/3](/docs/http3).

## CLI (`titanium` / `twp`)

Self-contained zip. Extract and run. Each zip includes both `titanium` and `twp` binaries.

<div class="download-grid">
  <div class="download-row">
    <strong>Windows x64</strong>
    <a v-if="links.cli['win-x64']" :href="links.cli['win-x64'].url">{{ links.cli['win-x64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — use winget or <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.cli['win-x64']" class="badge-pre">{{ links.cli['win-x64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>Linux x64 (glibc)</strong>
    <a v-if="links.cli['linux-x64']" :href="links.cli['linux-x64'].url">{{ links.cli['linux-x64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.cli['linux-x64']" class="badge-pre">{{ links.cli['linux-x64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>Linux arm64 (glibc)</strong>
    <a v-if="links.cli['linux-arm64']" :href="links.cli['linux-arm64'].url">{{ links.cli['linux-arm64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.cli['linux-arm64']" class="badge-pre">{{ links.cli['linux-arm64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>Alpine / musl x64</strong>
    <a v-if="links.cli['linux-musl-x64']" :href="links.cli['linux-musl-x64'].url">{{ links.cli['linux-musl-x64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.cli['linux-musl-x64']" class="badge-pre">{{ links.cli['linux-musl-x64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>Alpine / musl arm64</strong>
    <a v-if="links.cli['linux-musl-arm64']" :href="links.cli['linux-musl-arm64'].url">{{ links.cli['linux-musl-arm64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.cli['linux-musl-arm64']" class="badge-pre">{{ links.cli['linux-musl-arm64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>macOS x64</strong>
    <a v-if="links.cli['osx-x64']" :href="links.cli['osx-x64'].url">{{ links.cli['osx-x64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.cli['osx-x64']" class="badge-pre">{{ links.cli['osx-x64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>macOS arm64</strong>
    <a v-if="links.cli['osx-arm64']" :href="links.cli['osx-arm64'].url">{{ links.cli['osx-arm64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.cli['osx-arm64']" class="badge-pre">{{ links.cli['osx-arm64'].tag }}</span>
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
titanium http3-deps status
```

## Titanium Inspector

Desktop MITM debugger. Windows: MSI + zip. Linux / macOS: zip only (same RID set as CLI).

<div class="download-grid">
  <div class="download-row">
    <strong>Windows MSI</strong>
    <a v-if="links.inspector.msi" :href="links.inspector.msi.url">{{ links.inspector.msi.name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — use winget or <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.inspector.msi" class="badge-pre">{{ links.inspector.msi.tag }}</span>
  </div>
  <div class="download-row">
    <strong>Windows zip</strong>
    <a v-if="links.inspector.zip || links.inspector['win-x64']" :href="(links.inspector.zip || links.inspector['win-x64']).url">{{ (links.inspector.zip || links.inspector['win-x64']).name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.inspector.zip || links.inspector['win-x64']" class="badge-pre">{{ (links.inspector.zip || links.inspector['win-x64']).tag }}</span>
  </div>
  <div class="download-row">
    <strong>Linux x64</strong>
    <a v-if="links.inspector['linux-x64']" :href="links.inspector['linux-x64'].url">{{ links.inspector['linux-x64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.inspector['linux-x64']" class="badge-pre">{{ links.inspector['linux-x64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>Linux arm64</strong>
    <a v-if="links.inspector['linux-arm64']" :href="links.inspector['linux-arm64'].url">{{ links.inspector['linux-arm64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.inspector['linux-arm64']" class="badge-pre">{{ links.inspector['linux-arm64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>Alpine / musl x64</strong>
    <a v-if="links.inspector['linux-musl-x64']" :href="links.inspector['linux-musl-x64'].url">{{ links.inspector['linux-musl-x64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.inspector['linux-musl-x64']" class="badge-pre">{{ links.inspector['linux-musl-x64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>macOS arm64</strong>
    <a v-if="links.inspector['osx-arm64']" :href="links.inspector['osx-arm64'].url">{{ links.inspector['osx-arm64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.inspector['osx-arm64']" class="badge-pre">{{ links.inspector['osx-arm64'].tag }}</span>
  </div>
  <div class="download-row">
    <strong>macOS x64</strong>
    <a v-if="links.inspector['osx-x64']" :href="links.inspector['osx-x64'].url">{{ links.inspector['osx-x64'].name }}</a>
    <span v-else class="vp-muted">Not in current releases yet — see <a :href="links.releasesUrl">GitHub Releases</a></span>
    <span v-if="links.inspector['osx-x64']" class="badge-pre">{{ links.inspector['osx-x64'].tag }}</span>
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

See [Releases](/releases) or [all assets on GitHub](https://github.com/justcoding121/titanium-web-proxy/releases).

::: tip Product zips vs NuGet tags
Recent tags such as `6.0.2` may publish only the NuGet package. CLI / Inspector / Plus zip assets are attached when a full product release is cut with the release workflow. Until then, prefer **winget** or the GitHub Releases page for available binaries.
:::

## See also

- [Install](/docs/install)
- [HTTP/3](/docs/http3)
- [Releases](/releases)
