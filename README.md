# Titanium Web Proxy

A lightweight, high-performance HTTP(S) proxy — reverse / edge CLI, desktop Inspector, and optional Plus ops on Windows, Linux, and macOS. Embed the same engine in .NET via NuGet when you need a library.

**[Website](https://titaniumproxy.com)** · [Download](https://titaniumproxy.com/download) · [Docs](https://titaniumproxy.com/docs/getting-started) · [Releases](https://titaniumproxy.com/releases)

[![Build](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/dotnetcore.yml/badge.svg?branch=develop)](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/dotnetcore.yml)
[![NuGet](https://img.shields.io/nuget/v/Titanium.Web.Proxy.svg)](https://www.nuget.org/packages/Titanium.Web.Proxy)
[![NuGet downloads](https://img.shields.io/nuget/dt/Titanium.Web.Proxy.svg)](https://www.nuget.org/packages/Titanium.Web.Proxy)

## Code Quality

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

## Features

- Intercept, inspect, modify, redirect, or block HTTP and HTTPS traffic
- Explicit, transparent, and SOCKS4/5 proxy endpoints
- Request and response body streaming across HTTP/1.x (plain and TLS), HTTP/2, and HTTP/3 (see the [protocol support matrix](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support))
- HTTP/2 support, on by default, opt-out via `ProxyServer.EnableHttp2` (see the [protocol support matrix](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support) for exact coverage)
- HTTP/3 (QUIC) support, opt-in via `ProxyServer.EnableHttp3 = true` (requires MsQuic; CLI/Inspector Release zips bundle natives per RID — see the [HTTP/3 wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/HTTP-3) for packaging and the [protocol bridge matrix](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support#protocol-bridges) for every client→origin direction)
- Upstream HTTP, HTTPS, and SOCKS proxies with automatic system proxy detection
- Proxy authentication, mutual TLS, Kerberos, and NTLM support
- Connection, certificate, and buffer pooling
- Built-in, zero-overhead-when-disabled logging (every caught exception, optionally to console/file or your own `ILoggerFactory`) and opt-in structured request/connection timing. See [Logging and diagnostics](https://github.com/justcoding121/titanium-web-proxy/wiki/Home#logging-and-diagnostics) in the wiki.

## Editions

| Product | What it is | How you get it |
|---------|------------|----------------|
| **Titanium.Cli** (`titanium` / `twp`) | Standalone reverse / edge proxy for any stack: `run`, `test`, `version`, `update` | [Download (Windows, Linux & Mac)](https://titaniumproxy.com/download#cli) |
| **Titanium Inspector** | Desktop MITM debugger (session grid, inspectors, AutoResponder, breakpoints, HAR) | [Download (Windows, Linux & Mac)](https://titaniumproxy.com/download#inspector) |
| **Titanium.Plus** | Optional advanced features: control plane, ops, observability, and dashboard | After installing CLI, run `titanium update --plus` |
| **Titanium.Web.Proxy** | Core library. Embed a MITM and/or reverse proxy in a .NET app | [NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy/7.0.4) (`dotnet add package Titanium.Web.Proxy`) |

CLI and Plus target reverse-proxy / edge workloads (routing, load balancing, health, discovery) on Windows, Linux, and macOS. Inspector is the MITM debugging product. The Core library is the embed path for .NET. Requires .NET 10 or later.

## Installation

Install the stable package from [NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy):

```shell
dotnet add package Titanium.Web.Proxy
```

To use the latest prerelease:

```shell
dotnet add package Titanium.Web.Proxy --prerelease
```

### CLI (`titanium` / `twp`)

On Windows, **winget is stable-only**:

```shell
winget install justcoding121.TitaniumCli
```

For **beta**, download self-contained zips from [Download](https://titaniumproxy.com/download) / [GitHub Releases](https://github.com/justcoding121/titanium-web-proxy/releases) when a product release includes `Titanium.Cli-*.zip` assets (e.g. `v7.0.4-beta`; stable is `v7.0.4`). Extract and run:

```shell
titanium run -c twp.yaml
titanium test -c twp.yaml
titanium version --check
titanium update
```

Each CLI zip also includes a `twp` alias binary.

Optional Plus: run `titanium update --plus` (add `--channel beta` for prereleases), then enable Plus in config (`plus.enabled: true` with `plus.controlPlane.sharedSecret`). Check with `titanium version --check --plus`.

### Titanium Inspector

Prefer [Download](https://titaniumproxy.com/download). On Windows, winget id `justcoding121.TitaniumInspector` is **stable-only** (currently `v7.0.4`); MSI / portable zip for beta come from the product `v*` release (e.g. `v7.0.4-beta`). Start interception from the Capture menu, install the root CA, then toggle system proxy.

## Quick start

The following example starts an explicit HTTP(S) proxy on `127.0.0.1:8000` and logs each requested URL:

```csharp
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();

// Built-in console sink is a bounded channel + background writer, so LogInformation
// never blocks a session thread on Console I/O.
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

Configure your client to use `127.0.0.1:8000` as its HTTP and HTTPS proxy. Trusting a generated root certificate changes the current user's certificate store; only do this on a machine you control.

## Performance

Typically at or above **YARP**; ahead of **nginx** on H2/H3→H1 reverse, near parity for the rest (nginx still edges tiny keep-alive). Details: [Performance](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance).

## Examples and documentation

- **[Website](https://titaniumproxy.com)**: product docs, [download](https://titaniumproxy.com/download), and [release notes](https://titaniumproxy.com/releases)
- [Wiki](https://github.com/justcoding121/titanium-web-proxy/wiki): additional feature guides (also mirrored on the website), including [performance measurements](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance), [streaming request/response bodies](https://github.com/justcoding121/titanium-web-proxy/wiki/Streaming-Bodies), the [HTTP/3 setup guide](https://github.com/justcoding121/titanium-web-proxy/wiki/HTTP-3), and a [protocol feature support matrix](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support) (what's supported for HTTP/1.x, HTTP/2, and HTTP/3, including [protocol bridges](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support#protocol-bridges))
- [Basic console proxy](examples/Titanium.Web.Proxy.Examples.Basic)
- [WPF proxy application](examples/Titanium.Web.Proxy.Examples.Wpf)
- [Windows service](examples/Titanium.Web.Proxy.Examples.WindowsService)
- [Benchmarks](benchmarks/Titanium.Web.Proxy.Benchmarks): loopback throughput and allocation (BenchmarkDotNet)
- [RPS saturation probe](tools/RpsLoadProbe): concurrent breaking-point RPS
- [API documentation](https://titaniumproxy.com/api/Titanium.Web.Proxy.ProxyServer.html)

### Screenshots

**Titanium Inspector:** session grid with request/response details:

<img src="wiki/images/inspector-screenshot.jpg" alt="Titanium Inspector screenshot" width="900" />

**Basic console example:** compact per-request traffic tape:

<img src="wiki/images/basic-screenshot.jpg" alt="Basic console proxy screenshot" width="900" />

## Support and contributing

- Report reproducible bugs and feature requests through [GitHub Issues](https://github.com/justcoding121/Titanium-Web-Proxy/issues).
- Ask programming questions on [Stack Overflow](https://stackoverflow.com/questions/tagged/titanium-web-proxy) using the `titanium-web-proxy` tag.
- Pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Maintainers

This project is actively maintained by:

- [justcoding121](https://github.com/justcoding121)

Past contributors:

- [honfika](https://github.com/honfika)

## License

Titanium.Web.Proxy and Titanium.Cli are available under the [MIT License](LICENSE). Titanium Inspector and Titanium.Plus are licensed under [PolyForm Noncommercial 1.0.0](licenses/PolyForm-Noncommercial-1.0.0.txt).
