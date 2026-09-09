# Download

<script setup>
import { data as links } from './download.data.ts'
import inspectorScreenshot from '../wiki/images/inspector-screenshot.jpg'

const channels = [
  { id: 'stable', label: 'Stable', hint: 'Recommended for production', data: links.stable },
  { id: 'beta', label: 'Beta', hint: 'Prerelease — newest features', data: links.beta },
]
</script>

Get CLI and Inspector builds from GitHub Releases. This page lists the **latest stable** and **latest beta** product releases (NuGet-only tags are skipped). Prefer the primary format per OS (MSI / DMG / AppImage / deb / rpm); portable zips remain on GitHub for `titanium update` and Alpine (musl) images.

**Windows:** Authenticode-signed assets show publisher **Jehonathan Thomas**. **winget** is stable-only. **Chocolatey** packages `titanium-cli` / `titanium-inspector` support stable and `--pre` beta. **macOS CLI:** `brew tap justcoding121/titanium && brew install titanium` when the tap is published. **Linux desktop:** use AppImage / `.deb` / `.rpm` from GitHub Releases.

HTTP/3 (QUIC) native libraries ship inside each platform zip / package (except Windows OS MsQuic). Alpine / Kubernetes: use **`linux-musl-*`**, not `linux-x64`. Details: [HTTP/3](/docs/http3).

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

  <h3 :id="ch.id + '-inspector'">Titanium Inspector</h3>
  <p>
    Desktop man-in-the-middle (MITM) debugger.
    <strong>Windows:</strong> MSI (signed).
    <strong>macOS:</strong> DMG when published; otherwise zip + <code>install-app.sh</code>.
    <strong>Linux glibc:</strong> AppImage / <code>.deb</code> / <code>.rpm</code> when published; otherwise zip.
    <strong>Alpine musl:</strong> zip only.
  </p>
  <figure v-if="ch.id === 'stable'" class="inspector-preview">
    <img :src="inspectorScreenshot" alt="Titanium Inspector session grid with HTTPS decrypt and headers pane" width="1400" height="888" />
    <figcaption>
      Session grid, HTTPS decrypt, and inspectors.
      <a href="/docs/inspector">Inspector guide</a>
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

  <h3 :id="ch.id + '-cli'">CLI (<code>titanium</code> / <code>twp</code>)</h3>
  <p>
    Each package includes <code>titanium</code> and <code>twp</code>.
    Prefer AppImage / deb / rpm on Linux glibc; zip on Windows / musl / macOS.
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

Use **one** of winget or Chocolatey if you already have it — not both. If you have neither, use the download buttons above. Each command below is a separate product; run only the one you want.

## winget (Windows, stable only)

Built into Windows 10/11.

CLI:

```shell
winget install justcoding121.TitaniumCli
```

Inspector:

```shell
winget install justcoding121.TitaniumInspector
```

## Chocolatey (Windows)

If you already use [Chocolatey](https://chocolatey.org). Packages appear on chocolatey.org after moderation.

CLI (stable):

```shell
choco install titanium-cli
```

Inspector (stable):

```shell
choco install titanium-inspector
```

Beta uses the same ids with `--pre` (do not also run the stable install): `choco install titanium-cli --pre` or `choco install titanium-inspector --pre`. Uninstall: `choco uninstall titanium-cli` or `choco uninstall titanium-inspector`.

## Homebrew (macOS CLI)

```shell
brew tap justcoding121/titanium
brew install titanium
```

Requires the public tap repo (`homebrew-titanium`) with formula SHA256s matching the release zip.

```shell
titanium run -c twp.yaml
titanium version --check
titanium update
titanium update --channel beta
titanium http3-deps status
```

`titanium update` self-updates the CLI from the release feed for the selected channel (stable by default).
## Titanium.Plus

Plus is **not** a separate download on this page. After the CLI is installed:

```shell
titanium update --plus --channel beta
titanium version --check --plus --channel beta
```

For stable Plus updates, omit `--channel beta` (default channel is `stable`).

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
Some tags publish **NuGet only**. CLI / Inspector zip assets appear above only when a full product release is cut (`v*` tag via the release workflow). Prefer this page or GitHub Releases for binaries; **winget** remains stable-only; **Chocolatey** also ships beta via `--pre`.
:::

## See also

- [Install](/docs/install)
- [HTTP/3](/docs/http3)
- [Releases](/releases)
