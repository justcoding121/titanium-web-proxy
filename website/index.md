---
layout: home
title: Titanium Web Proxy
hero:
  name: Titanium Web Proxy
  text: Library, edge proxy, and MITM debugger
  tagline: Embed the same high-performance HTTP(S) engine in .NET, run it as a standalone reverse proxy, or inspect traffic in a desktop debugger — on Windows, Linux, and macOS.
  image:
    src: /logo.svg
    alt: Titanium Web Proxy
  actions:
    - theme: brand
      text: Get started
      link: /docs/getting-started
    - theme: alt
      text: Download Inspector & CLI
      link: /download/
    - theme: alt
      text: GitHub
      link: https://github.com/justcoding121/titanium-web-proxy
features:
  - title: Embeddable .NET proxy
    details: NuGet package Titanium.Web.Proxy for HTTP(S) and MITM inside your .NET 10 app.
    link: /docs/library
    linkText: Learn more
  - title: CLI and Kubernetes edge proxy
    details: Standalone titanium / twp reverse proxy from YAML, Docker, or Helm.
    link: /docs/cli
    linkText: Learn more
  - title: Automatic tunnel
    details: When a server blocks the proxy, we detect it and automatically tunnel that traffic.
    link: /docs/features#inspector
    linkText: Learn more
  - title: Visual MITM debugger
    details: Native Titanium Inspector with session grid, rewrite tools, and HAR export.
    link: /docs/inspector
    linkText: Learn more
  - title: Benchmarked speed at scale
    details: Practical reverse throughput charts against peers on the same harness.
    link: /docs/performance
    linkText: Learn more
  - title: Control-plane add-ons
    details: Optional Plus for xDS, GeoIP, caching, rate limiting, health probes, and a dashboard.
    link: /docs/plus
    linkText: Learn more
---

## Performance

Throughput (requests per second) vs **YARP**, **nginx**, **HAProxy**, and **Envoy** on GitHub Actions runners. The Linux tiny-GET chart is below. Charts for the other operating systems, and all other results, are on the [performance](/docs/performance) page.

<div class="rps-preview">

![Practical reverse proxy throughput on Linux (tiny requests)](../wiki/images/rps-practical-linux.png)

</div>

## More capabilities

<div class="edition-grid">
  <div class="edition-card">
    <h3>Debug HTTPS traffic</h3>
    <p>Native desktop Inspector on Windows, macOS, and Linux — decrypt HTTPS, turn on system proxy, and inspect sessions, headers, and bodies. Only on machines you control.</p>
    <p class="license"><a href="/docs/inspector">Inspector guide →</a></p>
  </div>
  <div class="edition-card">
    <h3>Rewrite and replay</h3>
    <p>AutoResponder, Map Local, Map Remote, breakpoints, and Composer. Export or import HAR; copy sessions as curl or fetch.</p>
    <p class="license"><a href="/docs/inspector#right-pane-inspect-and-tools">Rewrite tools →</a></p>
  </div>
  <div class="edition-card">
    <h3>Automatic tunnel</h3>
    <p>When a server blocks the proxy (TLS fingerprinting or other reasons), we detect it and automatically tunnel that traffic.</p>
    <p class="license"><a href="/docs/features#inspector">Inspector features →</a></p>
  </div>
  <div class="edition-card">
    <h3>HTTP/1 · HTTP/2 · HTTP/3</h3>
    <p>HTTP/2 is on by default. HTTP/3 is on when QUIC is available. Bridge when client and backend differ. Inspect WebSocket, gRPC, SSE, and GraphQL in the same grid.</p>
    <p class="license"><a href="/docs/protocol-support">Protocol support →</a></p>
  </div>
  <div class="edition-card">
    <h3>Reverse proxy from the CLI</h3>
    <p>Download the CLI, write a short YAML file, and run `titanium`. Routes, TLS, and optional automatic certificates (ACME).</p>
    <p class="license"><a href="/docs/cli">CLI guide →</a></p>
  </div>
  <div class="edition-card">
    <h3>Zero-downtime reload</h3>
    <p>Change routes and backends with `titanium reload` while current connections stay up. SIGHUP does the same on Linux and macOS, and `--watch` follows Kubernetes ConfigMap updates.</p>
    <p class="license"><a href="/docs/cli#reload-without-restart">Reload without restart →</a></p>
  </div>
  <div class="edition-card">
    <h3>Balance and fail over</h3>
    <p>Round-robin, random, least requests, and least time. Health checks eject a failing backend, retries move a request to another one within a budget, and a cookie or header can pin a client to one.</p>
    <p class="license"><a href="/docs/configuration#routes-clusters">Routes and clusters →</a></p>
  </div>
  <div class="edition-card">
    <h3>Secure at the edge</h3>
    <p>Set a TLS floor, get ACME certificates, rate-limit by client or header, ask an external service to authorize each request, and add OpenID Connect login and WAF screening with Plus.</p>
    <p class="license"><a href="/docs/capabilities#auth-methods">Auth methods →</a></p>
  </div>
  <div class="edition-card">
    <h3>Kubernetes native</h3>
    <p>A hardened container image, a Helm chart with probes and a graceful drain, and a Gateway API and Ingress controller.</p>
    <p class="license"><a href="/docs/kubernetes">Kubernetes guide →</a></p>
  </div>
  <div class="edition-card">
    <h3>Observe everything</h3>
    <p>Prometheus metrics, OTLP traces and metrics with W3C trace context, a JSON access log, and a Plus dashboard.</p>
    <p class="license"><a href="/docs/configuration#observability">Observability →</a></p>
  </div>
  <div class="edition-card">
    <h3>Chain and authenticate</h3>
    <p>Forward through an upstream HTTP, HTTPS, or SOCKS proxy. Proxy auth, mTLS, Kerberos, and NTLM are included.</p>
    <p class="license"><a href="/docs/library">Library guide →</a></p>
  </div>
  <div class="edition-card">
    <h3>Cache, compress, protect</h3>
    <p>A shared response cache that skips `Set-Cookie` and private responses (Plus), gzip and brotli response compression, and caps on request bodies, headers, and decompression ratio.</p>
    <p class="license"><a href="/docs/capabilities#compression">Compression and caching →</a></p>
  </div>
</div>

## Choose a path

<div class="edition-grid">
  <div class="edition-card">
    <h3>Inspect traffic</h3>
    <p class="license"><a href="/download#stable-inspector">Inspector</a> · Windows / macOS / Linux</p>
    <p>Desktop debugger for HTTP and HTTPS. Non-commercial use only (<a href="/docs/editions">PolyForm Noncommercial</a>). <a href="/download#stable-inspector">Download</a> → <a href="/docs/inspector">Inspector guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Run a reverse proxy</h3>
    <p class="license"><a href="/download#stable-cli">CLI</a> · MIT</p>
    <p>Standalone proxy for any backend stack. <a href="/download#stable-cli">Download CLI</a> → <a href="/docs/cli">CLI guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Ops add-on</h3>
    <p class="license"><a href="/docs/plus">Plus</a> · optional</p>
    <p>Dashboard, metrics, auth helpers, and WAF request screening with a detect mode. Non-commercial use only. After the CLI: <code>titanium update --plus</code>. <a href="/docs/plus">Plus</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Embed in .NET</h3>
    <p class="license"><a href="https://www.nuget.org/packages/Titanium.Web.Proxy">Library</a> · NuGet · MIT</p>
    <p>Same engine inside your app. <a href="/docs/library">Library guide</a> · <a href="https://www.nuget.org/packages/Titanium.Web.Proxy">NuGet</a>.</p>
  </div>
</div>

## Licensing

<div class="edition-grid">
  <div class="edition-card">
    <h3>Open source (MIT)</h3>
    <p class="license">Library · CLI · Gateway · this site</p>
    <p><code>Titanium.Web.Proxy</code>, <code>Titanium.Cli</code>, Manager, Gateway, docs, and the website are MIT-licensed for commercial and non-commercial use.</p>
  </div>
  <div class="edition-card">
    <h3>Inspector &amp; Plus (noncommercial)</h3>
    <p class="license">PolyForm Noncommercial</p>
    <p>Titanium Inspector, Titanium.Plus, and the xDS / GeoIP satellites are free for personal and education use. Commercial use needs a separate arrangement. Full terms: <a href="/docs/editions">Editions &amp; licenses</a>.</p>
  </div>
</div>

## Next steps

- [Download CLI & Inspector](/download)
- [Getting started](/docs/getting-started)
- [Features](/docs/features)
- [Performance](/docs/performance)
- [Configuration](/docs/configuration)
- [Release notes](/releases)
- [API reference](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}
