---
layout: home
title: Titanium Web Proxy
hero:
  name: Titanium Web Proxy
  text: High-performance HTTP(S) proxy
  tagline: Reverse / edge CLI, desktop traffic debugger, and optional ops add-on — on Windows, Linux, and macOS. Embed the same engine in .NET when you need a library.
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
  - title: Intercept & modify
    details: Explicit, transparent, and SOCKS4/5 endpoints. Decrypt HTTPS, stream bodies, and shape traffic — from the Inspector, CLI, or your own app.
  - title: Reverse / edge CLI
    details: Run `titanium` / `twp` with a YAML config — routes, clusters, load balancing, TLS terminate, and automatic certificates via Automatic Certificate Management Environment (ACME). Self-contained zips for every major OS.
  - title: HTTP/1 · HTTP/2 · HTTP/3
    details: HTTP/2 on by default. HTTP/3 (QUIC) opt-in. Bridges when the client and origin speak different protocol versions.
  - title: Measured performance
    details: Same-harness RPS vs YARP (gated), nginx, HAProxy, and Envoy on matched CI runners. Linux chart below; Windows and macOS in the performance guide.
---

## Editions

<div class="edition-grid">
  <div class="edition-card">
    <h3>Titanium.Cli</h3>
    <p class="license">MIT · zip / winget</p>
    <p>Standalone reverse / edge proxy for any backend stack, managed by the command line interface (CLI): <code>run</code>, <code>test</code>, <code>version</code>, <code>update</code>, <code>http3-deps</code>, <code>service</code>.</p>
  </div>
  <div class="edition-card">
    <h3>Titanium Inspector</h3>
    <p class="license">Windows / macOS / Linux</p>
    <p>Desktop man-in-the-middle (MITM) debugger — session grid, inspectors, AutoResponder, breakpoints, HTTP Archive (HAR) export.</p>
  </div>
  <div class="edition-card">
    <h3>Titanium.Plus</h3>
    <p class="license">optional CLI add-on</p>
    <p>Control plane, dashboard, observability, discovery, and a thin web application firewall (WAF). After installing CLI: <code>titanium update --plus</code>.</p>
  </div>
  <div class="edition-card">
    <h3>Titanium.Web.Proxy</h3>
    <p class="license">MIT · NuGet</p>
    <p>Optional .NET library — embed a MITM and/or reverse proxy in your app.</p>
  </div>
</div>

## Performance

Same-harness reverse RPS vs **YARP** (gated), **nginx**, **HAProxy**, and **Envoy** on matched GitHub Actions runners. One Linux chart here; Windows, macOS, and heavier workloads live on the [performance](/docs/performance) page.

<div class="rps-preview">

![Practical reverse proxy throughput on Linux](../wiki/images/rps-practical-linux.png)

</div>

## Quick start

::: code-group

```shell [CLI]
# Download a CLI zip from /download, then:
titanium run -c twp.yaml
titanium test -c twp.yaml
```

```yaml [twp.yaml]
schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 8000
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
- [Performance](/docs/performance)
- [Configuration reference](/docs/configuration)
- [gRPC-JSON transcoding](/docs/grpc-json-transcoding)
- [Release notes](/releases)
- [API reference](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}
