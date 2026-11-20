<script setup>
import { ref } from 'vue'
import inspectorScreenshot from '../../../../wiki/images/inspector-screenshot.jpg'

const tabs = [
  { id: 'library', label: 'C# Library' },
  { id: 'cli', label: 'CLI / Edge proxy' },
  { id: 'inspector', label: 'Titanium Inspector' },
]

const active = ref('library')
</script>

<template>
  <section class="home-quick-start">
    <h2 class="home-quick-start__title">Run it in 30 seconds</h2>
    <div class="home-quick-start__tabs" role="tablist" aria-label="Quick start">
      <button
        v-for="tab in tabs"
        :key="tab.id"
        type="button"
        role="tab"
        class="home-quick-start__tab"
        :class="{ 'is-active': active === tab.id }"
        :aria-selected="active === tab.id"
        @click="active = tab.id"
      >
        {{ tab.label }}
      </button>
    </div>

    <div v-show="active === 'library'" class="home-quick-start__panel" role="tabpanel">
      <pre class="home-quick-start__pre"><code>dotnet add package Titanium.Web.Proxy</code></pre>
      <pre class="home-quick-start__pre"><code>using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();
proxyServer.BeforeRequest += async (s, e) =>
{
    Console.WriteLine(e.HttpClient.Request.Url);
};

var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);
proxyServer.Start();</code></pre>
      <p class="home-quick-start__links">
        <a href="/docs/library">Library guide →</a>
      </p>
    </div>

    <div v-show="active === 'cli'" class="home-quick-start__panel" role="tabpanel">
      <pre class="home-quick-start__pre"><code># Download a CLI zip from /download, then:
titanium test -c twp.yaml
titanium run -c twp.yaml</code></pre>
      <pre class="home-quick-start__pre"><code>schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 8000
    # false = plain reverse proxy (no HTTPS decrypt)
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080</code></pre>
      <pre class="home-quick-start__pre"><code>docker run --rm -p 8080:8080 --read-only --tmpfs /tmp -v "$PWD/conf:/etc/titanium:ro" ghcr.io/justcoding121/titanium-web-proxy:latest
helm install titanium deploy/helm/titanium -n proxy --create-namespace -f my-values.yaml</code></pre>
      <p class="home-quick-start__links">
        <a href="/docs/cli">CLI guide →</a>
        ·
        <a href="/docs/kubernetes">Kubernetes →</a>
      </p>
    </div>

    <div v-show="active === 'inspector'" class="home-quick-start__panel" role="tabpanel">
      <figure class="inspector-preview">
        <img
          :src="inspectorScreenshot"
          alt="Titanium Inspector session grid with HTTPS decrypt and headers pane"
          width="1400"
          height="888"
        />
        <figcaption>Session grid, HTTPS decrypt, and inspectors.</figcaption>
      </figure>
      <p class="home-quick-start__links">
        <a class="home-quick-start__cta" href="/download/#stable-inspector">Download Inspector →</a>
      </p>
    </div>
  </section>
</template>
