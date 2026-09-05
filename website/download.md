# Download

<script setup>
import { data as links } from './download.data.ts'

const channels = [
  { id: 'stable', label: 'Stable', hint: 'Recommended for production', data: links.stable },
  { id: 'beta', label: 'Beta', hint: 'Prerelease — newest features', data: links.beta },
]
</script>

Get CLI and Inspector builds from GitHub Releases. This page lists the **latest stable** and **latest beta** product releases (NuGet-only tags are skipped). Prefer the primary format per OS (MSI / DMG / AppImage / deb / rpm); portable zips remain on GitHub for `titanium update` and Alpine/musl.

**Windows:** Authenticode-signed assets show publisher **Jehonathan Thomas**. **winget** is stable-only. **macOS CLI:** `brew tap justcoding121/titanium && brew install titanium` when the tap is published. **Linux desktop:** use AppImage / `.deb` / `.rpm` from GitHub Releases (Inspector needs host access for system proxy and CA trust; it is not published on Flathub).

HTTP/3 natives ship inside each RID zip / package (except Windows OS MsQuic). Alpine/K8s: use **`linux-musl-*`**, not `linux-x64`. Details: [HTTP/3](/docs/http3).

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
    Desktop MITM debugger.
    <strong>Windows:</strong> MSI (signed).
    <strong>macOS:</strong> DMG when published; otherwise zip + <code>install-app.sh</code>.
    <strong>Linux glibc:</strong> AppImage / <code>.deb</code> / <code>.rpm</code> when published; otherwise zip.
    <strong>Alpine musl:</strong> zip only.
  </p>
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

## winget (Windows, stable only)

```shell
winget install justcoding121.TitaniumCli
winget install justcoding121.TitaniumInspector
```

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
Some tags publish **NuGet only**. CLI / Inspector zip assets appear above only when a full product release is cut (`v*` tag via the release workflow). Prefer this page or GitHub Releases for binaries; **winget** remains stable-only.
:::

## See also

- [Install](/docs/install)
- [HTTP/3](/docs/http3)
- [Releases](/releases)
