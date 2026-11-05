---
layout: home
title: Titanium Web Proxy
hero:
  name: Titanium Web Proxy
  text: High-performance HTTP(S) proxy
  tagline: Inspect HTTPS traffic, put a reverse proxy in front of your apps, or embed the same engine in .NET — on Windows, Linux, and macOS.
  image:
    src: /logo.svg
    alt: Titanium Web Proxy
  actions:
    - theme: brand
      text: Get started
      link: /docs/getting-started
    - theme: alt
      text: Download
      link: /download
    - theme: alt
      text: GitHub
      link: https://github.com/justcoding121/titanium-web-proxy
features:
  - title: Debug HTTPS traffic
    details: Native desktop Inspector on Windows, macOS, and Linux — decrypt HTTPS, turn on system proxy, and inspect sessions, headers, and bodies. Only on machines you control.
    link: /docs/inspector
    linkText: Inspector guide
  - title: Rewrite and replay
    details: AutoResponder, Map Local, Map Remote, breakpoints, and Composer. Export or import HAR; copy sessions as curl or fetch.
    link: /docs/inspector#right-pane-inspect-and-tools
    linkText: Rewrite tools
  - title: Automatic tunnel
    details: When a server blocks the proxy (TLS fingerprinting or other reasons), we detect it and automatically tunnel that traffic.
    link: /docs/features#inspector
    linkText: Inspector features
  - title: HTTP/1 · HTTP/2 · HTTP/3
    details: HTTP/2 is on by default. HTTP/3 is on when QUIC is available. Bridge when client and backend differ. Inspect WebSocket, gRPC, SSE, and GraphQL in the same grid.
    link: /docs/protocol-support
    linkText: Protocol support
  - title: Reverse proxy from the CLI
    details: Download the CLI, write a short YAML file, and run `titanium`. Routes, TLS, and optional automatic certificates (ACME).
    link: /docs/cli
    linkText: CLI guide
  - title: Zero-downtime reload
    details: Change routes and backends with `titanium reload` while current connections stay up. SIGHUP does the same on Linux and macOS, and `--watch` follows Kubernetes ConfigMap updates.
    link: /docs/cli#reload-without-restart
    linkText: Reload without restart
  - title: Balance and fail over
    details: Round-robin, random, least requests, and least time. Health checks eject a failing backend, retries move a request to another one within a budget, and a cookie or header can pin a client to one.
    link: /docs/configuration#routes-clusters
    linkText: Routes and clusters
  - title: Secure at the edge
    details: Set a TLS floor, get ACME certificates, rate-limit by client or header, ask an external service to authorize each request, and add OpenID Connect login and WAF screening with Plus.
    link: /docs/capabilities#auth-methods
    linkText: Auth methods
  - title: Kubernetes native
    details: A hardened container image, a Helm chart with probes and a graceful drain, and a Gateway API and Ingress controller.
    link: /docs/kubernetes
    linkText: Kubernetes guide
  - title: Observe everything
    details: Prometheus metrics, OTLP traces and metrics with W3C trace context, a JSON access log, and a Plus dashboard.
    link: /docs/configuration#observability
    linkText: Observability
  - title: Chain and authenticate
    details: Forward through an upstream HTTP, HTTPS, or SOCKS proxy. Proxy auth, mTLS, Kerberos, and NTLM are included.
    link: /docs/library
    linkText: Library guide
  - title: Cache, compress, protect
    details: A shared response cache that skips `Set-Cookie` and private responses (Plus), gzip and brotli response compression, and caps on request bodies, headers, and decompression ratio.
    link: /docs/capabilities#compression
    linkText: Compression and caching
---

## What do you want to do?

<div class="edition-grid">
  <div class="edition-card">
    <h3>Inspect traffic</h3>
    <p class="license"><a href="/download#stable-inspector">Inspector</a> · Windows / macOS / Linux</p>
    <p>Desktop debugger for HTTP and HTTPS. Free for personal and education use (<a href="/docs/editions">PolyForm Noncommercial</a>); commercial use needs a separate license. <a href="/download#stable-inspector">Download</a> → <a href="/docs/inspector">Inspector guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Run a reverse proxy</h3>
    <p class="license"><a href="/download#stable-cli">CLI</a> · MIT</p>
    <p>Standalone proxy for any backend stack. <a href="/download#stable-cli">Download CLI</a> → <a href="/docs/cli">CLI guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Ops add-on</h3>
    <p class="license"><a href="/docs/plus">Plus</a> · optional</p>
    <p>Dashboard, metrics, auth helpers, and WAF request screening with a detect mode. After the CLI: <code>titanium update --plus</code>. <a href="/docs/plus">Plus</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Embed in .NET</h3>
    <p class="license"><a href="https://www.nuget.org/packages/Titanium.Web.Proxy">Library</a> · NuGet · MIT</p>
    <p>Same engine inside your app. <a href="/docs/library">Library guide</a> · <a href="https://www.nuget.org/packages/Titanium.Web.Proxy">NuGet</a>.</p>
  </div>
</div>

## Performance

Throughput (requests per second) vs **YARP**, **nginx**, **HAProxy**, and **Envoy** on GitHub Actions runners. The Linux tiny-GET chart is below. Charts for the other operating systems, and all other results, are on the [performance](/docs/performance) page.

<div class="rps-preview">

![Practical reverse proxy throughput on Linux (tiny requests)](../wiki/images/rps-practical-linux.png)

</div>

## Quick start

::: code-group

```shell [CLI]
# Download a CLI zip from /download, then:
titanium test -c twp.yaml
titanium run -c twp.yaml
```

```yaml [twp.yaml]
schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 8000
    # false = plain reverse proxy (no HTTPS decrypt)
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

```csharp [Library (.NET)]
using System.Net;
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
proxyServer.Start();
```

:::

## Next steps

- [Download CLI & Inspector](/download)
- [Getting started](/docs/getting-started)
- [Features](/docs/features)
- [Performance](/docs/performance)
- [Configuration](/docs/configuration)
- [Release notes](/releases)
- [API reference](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}
