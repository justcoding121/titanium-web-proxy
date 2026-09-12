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
  - title: Rewrite and replay
    details: AutoResponder, Map Local, Map Remote, breakpoints, and Composer. Export or import HAR; copy sessions as curl or fetch.
  - title: Login-safe decrypt
    details: SSO hosts stay on OS bypass by default. When a site rejects MITM, Inspector auto-tunnels so sign-in and hostile pages keep working.
  - title: HTTP/1 · HTTP/2 · HTTP/3
    details: HTTP/2 is on by default; HTTP/3 (QUIC) is optional. Bridge when client and backend differ. Inspect WebSocket, gRPC, SSE, and GraphQL in the same grid.
  - title: Reverse proxy from the CLI
    details: Download the CLI, write a short YAML file, and run `titanium`. Routes, load balancing, TLS, and optional automatic certificates (ACME).
  - title: Measured performance
    details: Compared on the same test harness against YARP, nginx, HAProxy, and Envoy. See the charts below and the performance guide.
---

## What do you want to do?

<div class="edition-grid">
  <div class="edition-card">
    <h3>Inspect traffic</h3>
    <p class="license">Inspector · Windows / macOS / Linux</p>
    <p>Desktop debugger for HTTP and HTTPS. Free for personal and education use (<a href="/docs/editions">PolyForm Noncommercial</a>); commercial use needs a separate license. <a href="/download#inspector">Download</a> → <a href="/docs/inspector">Inspector guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Run a reverse proxy</h3>
    <p class="license">CLI · MIT</p>
    <p>Standalone proxy for any backend stack. <a href="/download#cli">Download CLI</a> → <a href="/docs/cli">CLI guide</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Ops add-on</h3>
    <p class="license">Plus · optional</p>
    <p>Dashboard, metrics, auth helpers, and a thin WAF. After the CLI: <code>titanium update --plus</code>. <a href="/docs/plus">Plus</a>.</p>
  </div>
  <div class="edition-card">
    <h3>Embed in .NET</h3>
    <p class="license">Library · NuGet · MIT</p>
    <p>Same engine inside your app. <a href="/docs/library">Library guide</a> · <a href="https://www.nuget.org/packages/Titanium.Web.Proxy">NuGet</a>.</p>
  </div>
</div>

## Performance

Throughput (requests per second) vs **YARP**, **nginx**, **HAProxy**, and **Envoy** on matched GitHub Actions runners. Linux tiny-GET chart here; Windows, macOS, 64 KB practical charts, and heavier workloads on the [performance](/docs/performance) page.

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
