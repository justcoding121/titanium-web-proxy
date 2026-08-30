# Releases

<script setup>
import { data as releases } from './releases.data.ts'
</script>

Latest product releases from [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases). Notes are generated when each tag is published.

<div v-if="!releases?.length" class="download-row">
  <p>Could not load release notes right now. See <a href="https://github.com/justcoding121/titanium-web-proxy/releases">GitHub Releases</a> or <a href="/download">Download</a>.</p>
</div>

<div v-for="r in releases" :key="r.id" class="release-item">
  <h2>
    <a :href="r.html_url" target="_blank" rel="noreferrer">{{ r.tag_name }}</a>
    <span v-if="r.prerelease" class="badge-pre">prerelease</span>
  </h2>
  <p v-if="r.published_at"><em>{{ new Date(r.published_at).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) }}</em></p>
  <div v-if="r.body_html" class="release-body" v-html="r.body_html"></div>
  <p v-else-if="r.body"><pre style="white-space: pre-wrap;">{{ r.body }}</pre></p>
  <p v-if="r.assets?.length">
    <strong>Assets:</strong>
    <template v-for="(a, i) in r.assets" :key="a.id">
      <a :href="a.browser_download_url">{{ a.name }}</a><span v-if="i < r.assets.length - 1"> · </span>
    </template>
  </p>
</div>

<p v-if="releases?.length" class="download-row">
  Older release notes are on
  <a href="https://github.com/justcoding121/titanium-web-proxy/releases" target="_blank" rel="noreferrer">GitHub Releases</a>.
</p>
