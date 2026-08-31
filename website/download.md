# Download

<script setup>
import { data as links } from './download.data.ts'

const channels = [
  { id: 'stable', label: 'Stable', hint: 'Recommended for production', data: links.stable },
  { id: 'beta', label: 'Beta', hint: 'Prerelease — newest features', data: links.beta },
]
</script>

Get CLI and Inspector builds from GitHub Releases. This page lists the **latest stable** and **latest beta** product releases that include zip/MSI assets (NuGet-only tags are skipped).

**winget** installs the last published **stable** community package only — use the buttons below or GitHub for beta.

HTTP/3 natives ship inside each RID zip (except Windows, which uses OS MsQuic on Win11 / Server 2022+). Alpine/K8s: use **`linux-musl-*`**, not `linux-x64`. Details: [HTTP/3](/docs/http3).

<div v-for="ch in channels" :key="ch.id" class="download-channel">
  <h2 :id="ch.id">
    {{ ch.label }}
    <span v-if="ch.data.tag" class="badge-pre">{{ ch.data.tag }}</span>
  </h2>
  <p class="vp-muted">
    {{ ch.hint }}
    <template v-if="!ch.data.tag">
      — no product zip/MSI release on this channel yet; see
      <a :href="links.releasesUrl">GitHub Releases</a>.
    </template>
  </p>

  <h3 :id="ch.id + '-cli'">CLI (<code>titanium</code> / <code>twp</code>)</h3>
  <p>Self-contained zip. Extract and run. Each zip includes both <code>titanium</code> and <code>twp</code> binaries.</p>
  <div class="download-grid">
    <div class="download-row">
      <strong>Windows x64</strong>
      <a v-if="ch.data.cli['win-x64']" :href="ch.data.cli['win-x64'].url">{{ ch.data.cli['win-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64 (glibc)</strong>
      <a v-if="ch.data.cli['linux-x64']" :href="ch.data.cli['linux-x64'].url">{{ ch.data.cli['linux-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64 (glibc)</strong>
      <a v-if="ch.data.cli['linux-arm64']" :href="ch.data.cli['linux-arm64'].url">{{ ch.data.cli['linux-arm64'].name }}</a>
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

  <h3 :id="ch.id + '-inspector'">Titanium Inspector</h3>
  <p>
    Desktop MITM debugger.
    <strong>Windows:</strong> MSI wizard (choose install folder, Finished + Launch) or portable zip.
    Uninstall from Settings → Apps (branded icon).
    <strong>Linux / macOS:</strong> zip — run portable, or use
    <code>install.sh</code> / <code>install-app.sh</code> in the zip
    (<code>uninstall.sh</code> / <code>uninstall-app.sh</code> to remove).
  </p>
  <div class="download-grid">
    <div class="download-row">
      <strong>Windows MSI</strong>
      <a v-if="ch.data.inspector.msi" :href="ch.data.inspector.msi.url">{{ ch.data.inspector.msi.name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Windows zip</strong>
      <a v-if="ch.data.inspector.zip || ch.data.inspector['win-x64']" :href="(ch.data.inspector.zip || ch.data.inspector['win-x64']).url">{{ (ch.data.inspector.zip || ch.data.inspector['win-x64']).name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux x64</strong>
      <a v-if="ch.data.inspector['linux-x64']" :href="ch.data.inspector['linux-x64'].url">{{ ch.data.inspector['linux-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Linux arm64</strong>
      <a v-if="ch.data.inspector['linux-arm64']" :href="ch.data.inspector['linux-arm64'].url">{{ ch.data.inspector['linux-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>Alpine / musl x64</strong>
      <a v-if="ch.data.inspector['linux-musl-x64']" :href="ch.data.inspector['linux-musl-x64'].url">{{ ch.data.inspector['linux-musl-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>macOS arm64</strong>
      <a v-if="ch.data.inspector['osx-arm64']" :href="ch.data.inspector['osx-arm64'].url">{{ ch.data.inspector['osx-arm64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
    <div class="download-row">
      <strong>macOS x64</strong>
      <a v-if="ch.data.inspector['osx-x64']" :href="ch.data.inspector['osx-x64'].url">{{ ch.data.inspector['osx-x64'].name }}</a>
      <span v-else class="vp-muted">Not published yet</span>
    </div>
  </div>
</div>

## winget (Windows, stable only)

```shell
winget install justcoding121.TitaniumCli
winget install justcoding121.TitaniumInspector
```

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
