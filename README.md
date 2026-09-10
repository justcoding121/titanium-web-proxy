# Titanium Web Proxy

A lightweight, high-performance HTTP(S) proxy for Windows, Linux, and macOS.

**[Website](https://titaniumproxy.com)** · [Download](https://titaniumproxy.com/download) · [Docs](https://titaniumproxy.com/docs/getting-started) · [Releases](https://titaniumproxy.com/releases)

| Product | Best for |
|---------|----------|
| **Titanium.Cli** (`titanium` / `twp`)<br>[Download](https://titaniumproxy.com/download#cli) | Standalone reverse / edge proxy from the command line |
| **Titanium Inspector**<br>[Download](https://titaniumproxy.com/download#inspector) | Desktop MITM debugger — session grid, inspectors, AutoResponder, breakpoints, HAR export |
| **Titanium.Plus**<br>`titanium update --plus` (after installing CLI) | Optional ops: control plane, dashboard, observability |
| **Titanium.Web.Proxy**<br>[NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy) | Embed a proxy (MITM and/or reverse) in a .NET app |

Requires .NET 10 or later for the library. CLI and Inspector downloads are self-contained (no SDK needed to run them).

[![Build](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/dotnetcore.yml/badge.svg?branch=develop)](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/dotnetcore.yml)
[![NuGet](https://img.shields.io/nuget/v/Titanium.Web.Proxy.svg)](https://www.nuget.org/packages/Titanium.Web.Proxy)
[![NuGet downloads](https://img.shields.io/nuget/dt/Titanium.Web.Proxy.svg)](https://www.nuget.org/packages/Titanium.Web.Proxy)

## What you can do

- Run a reverse / edge proxy in front of any backend, or inspect and modify HTTP(S) traffic in the desktop Inspector
- Explicit, transparent, and SOCKS4/5 endpoints; decrypt HTTPS when you trust a local root certificate
- Stream bodies across HTTP/1.x, HTTP/2, and HTTP/3; upstream proxies, auth, and mutual TLS

Protocol coverage: [protocol support matrix](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support). HTTP/3 packaging: [HTTP/3 wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/HTTP-3).

## Performance

![Practical reverse proxy throughput on Linux](wiki/images/rps-practical-linux.png)

**RPS** is requests per second (gRPC bars are **RPC/s**). Compared on the same harness vs **YARP**, **nginx**, **HAProxy**, and **Envoy**. See [Performance](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance).

## Installation

**Inspector and CLI** (Windows, Linux, macOS): use [Download](https://titaniumproxy.com/download) — MSI / DMG / AppImage / `.deb` / `.rpm` / zip. Short walkthrough: [Install](https://titaniumproxy.com/docs/install).

After the CLI is on your PATH (or in the current folder):

```shell
titanium run -c twp.yaml
titanium test -c twp.yaml
titanium version --check
titanium update
```

`titanium run` is foreground (Ctrl+C to stop). Boot persistence: `titanium service install -c twp.yaml` — [CLI — service](https://titaniumproxy.com/docs/cli#service). Each CLI zip also includes a `twp` alias.

**Optional Plus:** `titanium update --plus`, then `plus.enabled: true` — [Plus](https://titaniumproxy.com/docs/plus).

<img src="wiki/images/inspector-screenshot.jpg" alt="Titanium Inspector screenshot" width="900" />

### Library (.NET)

```shell
dotnet add package Titanium.Web.Proxy
# Prerelease / beta:
dotnet add package Titanium.Web.Proxy --prerelease
```

## Quick start (library)

Explicit HTTP(S) proxy on `127.0.0.1:8000` that logs each requested URL:

```csharp
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();

proxyServer.Logging.MinimumLevel = LogLevel.Information;
proxyServer.BeforeRequest += OnRequest;

var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);

// Create and trust the root certificate used to decrypt HTTPS traffic.
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);

proxyServer.Start();
proxyServer.Logger.LogInformation("Proxy listening on 127.0.0.1:8000. Press Enter to stop.");
await Console.In.ReadLineAsync();
proxyServer.Stop();

Task OnRequest(object sender, SessionEventArgs e)
{
    proxyServer.Logger.LogInformation("{Url}", e.HttpClient.Request.Url);
    return Task.CompletedTask;
}
```

<img src="wiki/images/basic-screenshot.jpg" alt="Basic console proxy screenshot" width="900" />

Point your client at `127.0.0.1:8000` as its HTTP and HTTPS proxy. Trusting a generated root certificate changes the current user's certificate store — only do this on a machine you control.

## Examples and documentation

- **[Website](https://titaniumproxy.com)** — product docs, [download](https://titaniumproxy.com/download), [getting started](https://titaniumproxy.com/docs/getting-started), [release notes](https://titaniumproxy.com/releases)
- **[Wiki](https://github.com/justcoding121/titanium-web-proxy/wiki)** — deeper guides (performance, streaming bodies, HTTP/3, protocol support)
- [Basic console proxy](examples/Titanium.Web.Proxy.Examples.Basic)
- [WPF desktop example](examples/Titanium.Web.Proxy.Examples.Wpf)
- [Windows service example](examples/Titanium.Web.Proxy.Examples.WindowsService)
- [API reference](https://titaniumproxy.com/api/Titanium.Web.Proxy.ProxyServer.html)

## Support and contributing

- Bugs and feature requests: [GitHub Issues](https://github.com/justcoding121/Titanium-Web-Proxy/issues)
- Programming questions: [Stack Overflow](https://stackoverflow.com/questions/tagged/titanium-web-proxy) (`titanium-web-proxy` tag)
- Pull requests welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) (includes local QA tooling for maintainers)

## Code quality

[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=alert_status)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=coverage)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Lines of Code](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=ncloc)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Bugs](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=bugs)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Vulnerabilities](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=vulnerabilities)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Code Smells](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=code_smells)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=security_rating)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Reliability Rating](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=reliability_rating)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Maintainability Rating](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=sqale_rating)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Duplicated Lines](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=duplicated_lines_density)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)
[![Technical Debt](https://sonarcloud.io/api/project_badges/measure?project=justcoding121_titanium-web-proxy&metric=sqale_index)](https://sonarcloud.io/summary/overall?id=justcoding121_titanium-web-proxy&branch=develop)

## Maintainers

Actively maintained by [justcoding121](https://github.com/justcoding121). Past contributor: [honfika](https://github.com/honfika).

## License

Titanium.Web.Proxy and Titanium.Cli are [MIT](LICENSE). Titanium Inspector and Titanium.Plus are [PolyForm Noncommercial 1.0.0](licenses/PolyForm-Noncommercial-1.0.0.txt).
