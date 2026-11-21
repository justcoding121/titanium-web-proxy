---
layout: home
title: Titanium Web Proxy
hero:
  name: Titanium Web Proxy
  text: Intercept and inspect HTTP(S) from .NET in a few lines
  tagline: Decrypt HTTPS and log each request from a .NET 10 app on Windows, Linux, and macOS.
  image:
    src: /logo.svg
    alt: Titanium Web Proxy
  actions:
    - theme: brand
      text: Get started
      link: /docs/getting-started#embed-in-net-library
    - theme: alt
      text: Download Inspector or CLI
      link: /download/
    - theme: alt
      text: GitHub
      link: https://github.com/justcoding121/titanium-web-proxy
---

## Choose a path

<div class="edition-grid">
  <div class="edition-card">
    <h3>Embed in .NET</h3>
    <p class="license"><a href="https://www.nuget.org/packages/Titanium.Web.Proxy">Titanium.Web.Proxy</a> · NuGet</p>
    <p>Same engine inside your app. <a href="/docs/library">Library guide</a> · <a href="https://www.nuget.org/packages/Titanium.Web.Proxy">NuGet</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Run a reverse proxy</h3>
    <p class="license"><a href="/download#stable-cli">Titanium.Cli</a> · <code>titanium</code> / <code>twp</code></p>
    <p>Standalone proxy for any backend stack. <a href="/download#stable-cli">Download CLI</a> → <a href="/docs/cli">CLI guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Inspect traffic</h3>
    <p class="license"><a href="/download#stable-inspector">Titanium Inspector</a> · Windows / macOS / Linux</p>
    <p>Desktop debugger for HTTP and HTTPS. <a href="/download#stable-inspector">Download</a> → <a href="/docs/inspector">Inspector guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Ops add-on</h3>
    <p class="license"><a href="/docs/plus">Titanium.Plus</a> · optional</p>
    <p>Dashboard, metrics, auth helpers, and WAF request screening with a detect mode. After the CLI: <code>titanium update --plus</code>. <a href="/docs/plus">Plus</a>.</p>
  </div>
</div>

## Major features

<div class="edition-grid">
  <div class="edition-card">
    <h3>Embeddable .NET proxy</h3>
    <p>NuGet package Titanium.Web.Proxy for HTTP(S) and MITM inside your .NET 10 app.</p>
    <p class="license"><a href="/docs/library">Learn more →</a></p>
  </div>
  <div class="edition-card">
    <h3>CLI and Kubernetes edge proxy</h3>
    <p>Standalone titanium / twp reverse proxy from YAML, Docker, or Helm.</p>
    <p class="license"><a href="/docs/cli">Learn more →</a></p>
  </div>
  <div class="edition-card">
    <h3>Automatic tunnel</h3>
    <p>When a server blocks the proxy, we detect it and automatically tunnel that traffic.</p>
    <p class="license"><a href="/docs/features#inspector">Learn more →</a></p>
  </div>
  <div class="edition-card">
    <h3>Visual MITM debugger</h3>
    <p>Native Titanium Inspector with session grid, rewrite tools, and HAR export.</p>
    <p class="license"><a href="/docs/inspector">Learn more →</a></p>
  </div>
  <div class="edition-card">
    <h3>Benchmarked speed at scale</h3>
    <p>Practical reverse throughput charts against peers on the same harness.</p>
    <p class="license"><a href="/docs/performance">Learn more →</a></p>
  </div>
  <div class="edition-card">
    <h3>Control-plane add-ons</h3>
    <p>Optional Plus for xDS, GeoIP, caching, rate limiting, health probes, and a dashboard.</p>
    <p class="license"><a href="/docs/plus">Learn more →</a></p>
  </div>
</div>

## Run it in 30 seconds

<HomeQuickStart />

## Performance

**45,499** requests per second: a tiny keep-alive GET (~56 B), HTTP/1 with TLS in and plain HTTP/1 out, on Linux (GitHub-hosted runner, 4 vCPU / 16 GiB, median of 3, sustain at concurrency 64). Same harness versus peer proxies. The Linux tiny-GET chart is below. Charts for the other operating systems, and the named comparison, are on the [performance](/docs/performance) page and the [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance).

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
    <p>Titanium Inspector, Titanium.Plus, and the xDS / GeoIP satellites are for non-commercial use only (PolyForm Noncommercial 1.0.0). Full terms: <a href="/docs/editions">Editions &amp; licenses</a>.</p>
  </div>
</div>

## Next steps

- [Download Inspector or CLI](/download)
- [Getting started](/docs/getting-started)
- [Features](/docs/features)
- [Performance](/docs/performance)
- [Configuration](/docs/configuration)
- [Release notes](/releases)
- [API reference](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}
