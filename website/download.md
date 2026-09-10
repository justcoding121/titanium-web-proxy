# Download

<script setup>
import { data as links } from './download.data.ts'
import inspectorScreenshot from '../wiki/images/inspector-screenshot.jpg'

const channels = [
  { id: 'stable', label: 'Stable', hint: 'Recommended for production', data: links.stable },
  { id: 'beta', label: 'Beta', hint: 'Prerelease — newest features', data: links.beta },
]
</script>

Download **Inspector** (desktop debugger) or the **CLI** (reverse proxy) for your OS. Prefer the primary installer when available: **Windows MSI**, **macOS DMG**, **Linux AppImage / deb / rpm**. Portable zips work everywhere and are what `titanium update` uses.

Pick **Stable** for production or **Beta** for the newest builds. Then choose Inspector or CLI below.

::: tip Quick picks
- **Windows:** MSI (Inspector) or zip (CLI); or `winget` / Chocolatey under [Optional Windows package managers](#optional-windows-package-managers).
- **macOS:** DMG when published, otherwise zip; CLI also via Homebrew when the tap is live.
- **Linux:** AppImage / `.deb` / `.rpm` for normal desktops; Alpine containers need the **musl** zip — see [Advanced](#advanced).
:::

<div v-for="ch in channels" :key="ch.id" class="download-channel">
  <h2 :id="ch.id">
    {{ ch.label }}
    <span v-if="ch.data.tag" class="badge-pre">{{ ch.data.tag }}</span>
  </h2>
  <p class="vp-muted">
    {{ ch.hint }}
    <template v-if="!ch.data.tag">
      — no product release on this channel yet; see
      <a :href="links.releasesUrl">GitHub Releases</a>.
    </template>
  </p>

  <h3 :id="ch.id + '-inspector'">Inspector</h3>
  <p>
    Desktop HTTPS / HTTP debugger (decrypt on machines you control).
    <a href="/docs/inspector">Inspector guide</a>.
  </p>
  <figure v-if="ch.id === 'stable'" class="inspector-preview">
    <img :src="inspectorScreenshot" alt="Titanium Inspector session grid with HTTPS decrypt and headers pane" width="1400" height="888" />
    <figcaption>
      Session grid, HTTPS decrypt, and inspectors.
    </figcaption>
  </figure>
  <div class="download-grid">
    <div class="download-row">
      <strong>Windows MSI</strong>
      <a v-if="ch.data.inspector.msi" :href="ch.data.inspector.msi.url">{{ ch.data.inspector.msi.name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64 AppImage</strong>
      <a v-if="ch.data.inspector.appimage && ch.data.inspector.appimage['linux-x64']" :href="ch.data.inspector.appimage['linux-x64'].url">{{ ch.data.inspector.appimage['linux-x64'].name }}</a>
      <a v-else-if="ch.data.inspector['linux-x64']" :href="ch.data.inspector['linux-x64'].url">{{ ch.data.inspector['linux-x64'].name }} (zip)</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64 .deb</strong>
      <a v-if="ch.data.inspector.deb && ch.data.inspector.deb['linux-x64']" :href="ch.data.inspector.deb['linux-x64'].url">{{ ch.data.inspector.deb['linux-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64 .rpm</strong>
      <a v-if="ch.data.inspector.rpm && ch.data.inspector.rpm['linux-x64']" :href="ch.data.inspector.rpm['linux-x64'].url">{{ ch.data.inspector.rpm['linux-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64 AppImage</strong>
      <a v-if="ch.data.inspector.appimage && ch.data.inspector.appimage['linux-arm64']" :href="ch.data.inspector.appimage['linux-arm64'].url">{{ ch.data.inspector.appimage['linux-arm64'].name }}</a>
      <a v-else-if="ch.data.inspector['linux-arm64']" :href="ch.data.inspector['linux-arm64'].url">{{ ch.data.inspector['linux-arm64'].name }} (zip)</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64 .deb</strong>
      <a v-if="ch.data.inspector.deb && ch.data.inspector.deb['linux-arm64']" :href="ch.data.inspector.deb['linux-arm64'].url">{{ ch.data.inspector.deb['linux-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64 .rpm</strong>
      <a v-if="ch.data.inspector.rpm && ch.data.inspector.rpm['linux-arm64']" :href="ch.data.inspector.rpm['linux-arm64'].url">{{ ch.data.inspector.rpm['linux-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Alpine / musl x64</strong>
      <a v-if="ch.data.inspector['linux-musl-x64']" :href="ch.data.inspector['linux-musl-x64'].url">{{ ch.data.inspector['linux-musl-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Alpine / musl arm64</strong>
      <a v-if="ch.data.inspector['linux-musl-arm64']" :href="ch.data.inspector['linux-musl-arm64'].url">{{ ch.data.inspector['linux-musl-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>macOS arm64 DMG</strong>
      <a v-if="ch.data.inspector.dmg && ch.data.inspector.dmg['osx-arm64']" :href="ch.data.inspector.dmg['osx-arm64'].url">{{ ch.data.inspector.dmg['osx-arm64'].name }}</a>
      <a v-else-if="ch.data.inspector['osx-arm64']" :href="ch.data.inspector['osx-arm64'].url">{{ ch.data.inspector['osx-arm64'].name }} (zip)</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>macOS x64 DMG</strong>
      <a v-if="ch.data.inspector.dmg && ch.data.inspector.dmg['osx-x64']" :href="ch.data.inspector.dmg['osx-x64'].url">{{ ch.data.inspector.dmg['osx-x64'].name }}</a>
      <a v-else-if="ch.data.inspector['osx-x64']" :href="ch.data.inspector['osx-x64'].url">{{ ch.data.inspector['osx-x64'].name }} (zip)</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
  </div>

  <h3 :id="ch.id + '-cli'">CLI</h3>
  <p>
    Command-line reverse / edge proxy (<code>titanium</code> / <code>twp</code>).
    <a href="/docs/cli">CLI guide</a>.
  </p>
  <div class="download-grid">
    <div class="download-row">
      <strong>Windows x64</strong>
      <a v-if="ch.data.cli['win-x64']" :href="ch.data.cli['win-x64'].url">{{ ch.data.cli['win-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64 AppImage</strong>
      <a v-if="ch.data.cli.appimage && ch.data.cli.appimage['linux-x64']" :href="ch.data.cli.appimage['linux-x64'].url">{{ ch.data.cli.appimage['linux-x64'].name }}</a>
      <a v-else-if="ch.data.cli['linux-x64']" :href="ch.data.cli['linux-x64'].url">{{ ch.data.cli['linux-x64'].name }} (zip)</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64 .deb</strong>
      <a v-if="ch.data.cli.deb && ch.data.cli.deb['linux-x64']" :href="ch.data.cli.deb['linux-x64'].url">{{ ch.data.cli.deb['linux-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64 .rpm</strong>
      <a v-if="ch.data.cli.rpm && ch.data.cli.rpm['linux-x64']" :href="ch.data.cli.rpm['linux-x64'].url">{{ ch.data.cli.rpm['linux-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64 AppImage</strong>
      <a v-if="ch.data.cli.appimage && ch.data.cli.appimage['linux-arm64']" :href="ch.data.cli.appimage['linux-arm64'].url">{{ ch.data.cli.appimage['linux-arm64'].name }}</a>
      <a v-else-if="ch.data.cli['linux-arm64']" :href="ch.data.cli['linux-arm64'].url">{{ ch.data.cli['linux-arm64'].name }} (zip)</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64 .deb</strong>
      <a v-if="ch.data.cli.deb && ch.data.cli.deb['linux-arm64']" :href="ch.data.cli.deb['linux-arm64'].url">{{ ch.data.cli.deb['linux-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64 .rpm</strong>
      <a v-if="ch.data.cli.rpm && ch.data.cli.rpm['linux-arm64']" :href="ch.data.cli.rpm['linux-arm64'].url">{{ ch.data.cli.rpm['linux-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Alpine / musl x64</strong>
      <a v-if="ch.data.cli['linux-musl-x64']" :href="ch.data.cli['linux-musl-x64'].url">{{ ch.data.cli['linux-musl-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Alpine / musl arm64</strong>
      <a v-if="ch.data.cli['linux-musl-arm64']" :href="ch.data.cli['linux-musl-arm64'].url">{{ ch.data.cli['linux-musl-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>macOS x64</strong>
      <a v-if="ch.data.cli['osx-x64']" :href="ch.data.cli['osx-x64'].url">{{ ch.data.cli['osx-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>macOS arm64</strong>
      <a v-if="ch.data.cli['osx-arm64']" :href="ch.data.cli['osx-arm64'].url">{{ ch.data.cli['osx-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
  </div>
</div>

## Optional Windows package managers

Use **one** of winget or Chocolatey if you already have it — not both. Otherwise use the buttons above.

### winget (stable only)

```shell
winget install justcoding121.TitaniumCli
winget install justcoding121.TitaniumInspector
```

### Chocolatey

```shell
choco install titanium-cli
choco install titanium-inspector
```

Beta: `choco install titanium-cli --pre` or `choco install titanium-inspector --pre`.

### Homebrew (macOS CLI)

```shell
brew tap justcoding121/titanium
brew install titanium
```

## Plus

Plus is **not** a separate download. After the CLI:

```shell
titanium update --plus
```

Then enable it in config — see [Plus](/docs/plus).

## Library (NuGet)

```shell
dotnet add package Titanium.Web.Proxy
# Newer than stable:
dotnet add package Titanium.Web.Proxy --prerelease
```

## Advanced

- **Alpine / musl containers:** use the Alpine / musl zip rows above — not the regular Linux zip. Details: [HTTP/3](/docs/http3) and [Install](/docs/install#advanced).
- **Windows signing:** Authenticode publisher **Jehonathan Thomas**.
- **Some GitHub tags are NuGet-only** (no CLI/Inspector assets). Prefer this page or a release that lists the installers you need.

## Release notes

See [Releases](/releases) or [all assets on GitHub](https://github.com/justcoding121/titanium-web-proxy/releases).

## See also

- [Install](/docs/install)
- [Getting started](/docs/getting-started)
- [HTTP/3](/docs/http3)
