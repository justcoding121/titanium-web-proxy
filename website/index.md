---
layout: home
title: Titanium Web Proxy
hero:
  name: Titanium Web Proxy
  text: High-performance HTTP(S) proxy
  tagline: Reverse / edge CLI, desktop Inspector, and optional Plus ops — on Windows, Linux, and macOS. Embed in .NET when you need a library.
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
    details: Run `titanium` / `twp` with twp.yaml — routes, clusters, load balancing, TLS terminate, and ACME. Self-contained zips for every major OS.
  - title: HTTP/1 · HTTP/2 · HTTP/3
    details: HTTP/2 on by default. HTTP/3 (QUIC) opt-in. Protocol bridges between client and origin versions.
  - title: Measured performance
    details: Typically at or above YARP; ahead of nginx on H2/H3→H1 reverse, near parity for the rest. See the performance guide for publishable tables.
---

## Editions

<div class="edition-grid">
  <div class="edition-card">
    <h3>Titanium.Cli</h3>
    <p class="license">MIT · zip / winget</p>
    <p>Standalone reverse / edge daemon for any stack: <code>run</code>, <code>test</code>, <code>version</code>, <code>update</code>.</p>
  </div>
  <div class="edition-card">
    <h3>Titanium Inspector</h3>
    <p class="license">PolyForm NC · MSI / zip</p>
    <p>Desktop MITM debugger — session grid, inspectors, AutoResponder, breakpoints, HAR.</p>
  </div>
  <div class="edition-card">
    <h3>Titanium.Plus</h3>
    <p class="license">PolyForm NC</p>
    <p>Control plane, dashboard, observability, discovery, WAF. Install with <code>titanium update --plus</code>.</p>
  </div>
  <div class="edition-card">
    <h3>Titanium.Web.Proxy</h3>
    <p class="license">MIT · NuGet</p>
    <p>Optional .NET library — embed a MITM and/or reverse proxy in your app.</p>
  </div>
</div>

## Quick start

::: code-group

```shell [CLI]
# Download a CLI zip from /download, then:
titanium run -c twp.yaml
titanium test -c twp.yaml
```

```yaml [twp.yaml]
schemaVersion: "7.0"
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
- [Configuration reference](/docs/configuration)
- [Release notes](/releases)
- [API reference](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}
