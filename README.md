# Titanium Web Proxy

Embeddable .NET HTTP(S) and man-in-the-middle (MITM) proxy library, standalone CLI and Kubernetes reverse proxy, and desktop MITM inspector — on Windows, Linux, and macOS.

[![Build](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/dotnetcore.yml/badge.svg?branch=develop&event=push)](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/dotnetcore.yml?query=branch%3Adevelop+event%3Apush)
[![NuGet](https://img.shields.io/nuget/v/Titanium.Web.Proxy.svg)](https://www.nuget.org/packages/Titanium.Web.Proxy)
[![NuGet downloads](https://img.shields.io/nuget/dt/Titanium.Web.Proxy.svg)](https://www.nuget.org/packages/Titanium.Web.Proxy)

**[Website](https://titaniumproxy.com)** · [Download](https://titaniumproxy.com/download) · [Library](#run-the-library) · [CLI](#run-the-cli) · [Inspector](#run-inspector) · [Benchmarks](#performance) · [Docs](https://titaniumproxy.com/docs/getting-started) · [License](#license)

## Run the library

```shell
dotnet add package Titanium.Web.Proxy
# Prerelease / beta:
dotnet add package Titanium.Web.Proxy --prerelease
```

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

var endPoint = new ExplicitProxyEndPoint(
    IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);

// The first run installs a root certificate in the current user's trust store.
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);

proxyServer.Start();
proxyServer.Logger.LogInformation(
    "Listening on 127.0.0.1:8000. Press Enter to stop.");
await Console.In.ReadLineAsync();
proxyServer.Stop();

Task OnRequest(object sender, SessionEventArgs e)
{
    proxyServer.Logger.LogInformation("{Url}", e.HttpClient.Request.Url);
    return Task.CompletedTask;
}
```

<img src="wiki/images/basic-screenshot.jpg" alt="Console log of each requested URL" width="900" />

Point your client at `127.0.0.1:8000` as its HTTP and HTTPS proxy. The first run installs a root certificate for the current user — only on a machine you control. [Trust](https://titaniumproxy.com/docs/library#system-proxy-and-root-ca-windows-macos-linux).

- **[Titanium.Web.Proxy](https://www.nuget.org/packages/Titanium.Web.Proxy)** — Embed a proxy (MITM and/or reverse) in a .NET app
- **[Titanium.Cli](https://titaniumproxy.com/download#stable-cli)** (`titanium` / `twp`) — Standalone reverse / edge proxy from the command line
- **[Titanium Inspector](https://titaniumproxy.com/download#stable-inspector)** — Desktop MITM debugger: session grid, inspectors, AutoResponder, breakpoints, HAR export

Requires .NET 10 or later for the library. CLI and Inspector downloads are self-contained (no SDK needed to run them).

## Major features

- **Embeddable .NET HTTP(S) and MITM** (`Titanium.Web.Proxy`) — Same engine inside your app. [NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy) · [Library](https://titaniumproxy.com/docs/library)
- **Standalone CLI and Kubernetes edge** (`titanium` / `twp`) — Reverse / edge proxy from YAML, Docker, or Helm. [Download CLI](https://titaniumproxy.com/download#stable-cli) · [CLI](https://titaniumproxy.com/docs/cli) · [Kubernetes](https://titaniumproxy.com/docs/kubernetes)
- **Automatic block and TLS-fingerprint tunneling** — When a server blocks the proxy, we detect it and tunnel that traffic. [Features](https://titaniumproxy.com/docs/features#inspector)
- **Visual MITM traffic debugger** (Titanium Inspector) — Session grid, inspectors, AutoResponder, breakpoints, HAR export. [Download Inspector](https://titaniumproxy.com/download#stable-inspector) · [Inspector](https://titaniumproxy.com/docs/inspector)
- **Benchmarked speed at scale** — Practical reverse RPS versus peers on the same harness. [Performance](#performance) · [Site charts](https://titaniumproxy.com/docs/performance)
- **Optional control-plane add-ons** (`Titanium.Plus`) — xDS, GeoIP, caching, rate limiting, health probes, dashboard. `titanium update --plus` · [Plus](https://titaniumproxy.com/docs/plus)

## Run the CLI

### CLI (reverse / edge proxy)

Create `twp.yaml`, then validate and run:

```yaml
schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 8000
    # false = plain reverse proxy (no HTTPS decrypt)
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

```shell
titanium test -c twp.yaml
titanium run -c twp.yaml
```

**Plus** is optional and is not a separate download. After the CLI is installed, run `titanium update --plus`, then set `plus.enabled: true`. It adds a control plane, dashboard, and observability. [Plus](https://titaniumproxy.com/docs/plus).

## Run Inspector

Desktop debugger for HTTP and HTTPS. [Download](https://titaniumproxy.com/download#stable-inspector) for Windows, macOS, or Linux, then launch Titanium Inspector. Capturing and the system proxy are on by default on the local machine. Turn on **Decrypt HTTPS** when you need to see inside HTTPS. The first run installs a root certificate for the current user — only on a machine you control. [Inspector guide](https://titaniumproxy.com/docs/inspector).

<img src="wiki/images/inspector-screenshot.jpg" alt="Titanium Inspector session grid" width="900" />

## Go deeper

Full catalog: [Features](https://titaniumproxy.com/docs/features). What is supported, partial, and not supported: [capability coverage](https://titaniumproxy.com/docs/capabilities). Protocol coverage: [protocol support matrix](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support). HTTP/3 packaging: [HTTP/3 wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/HTTP-3).

## Performance

Tiny keep-alive GET (~56 B) on common reverse wires, plus WebSocket and unary gRPC:

![Practical reverse proxy throughput on Linux (tiny requests)](wiki/images/rps-practical-linux.png)

Heavier reverse load (64 KB GET/POST on typical wires):

![Practical reverse proxy throughput on Linux (64 KB)](wiki/images/rps-practical-heavier-linux.png)

**RPS** is requests per second (gRPC bars are **RPC/s**). **45,499** requests per second on Linux for a tiny keep-alive GET (~56 B), HTTP/1 with TLS in and plain HTTP/1 out (4 vCPU / 16 GiB, median of 3, sustain at concurrency 64), on the same harness as peer proxies. Named comparison: [Performance](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance).

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

**Container and Helm:** the CLI ships as a hardened image (`ghcr.io/justcoding121/titanium-web-proxy`, Linux (64-bit and ARM)) and charts under `deploy/helm/` (also Release `.tgz` and `oci://ghcr.io/justcoding121/charts`) — [Kubernetes](https://titaniumproxy.com/docs/kubernetes). Gateway API / Ingress: [Gateway API](https://titaniumproxy.com/docs/gateway-api).

The image entrypoint is `titanium run --config /etc/titanium/twp.yaml`. Save this as `conf/twp.yaml`. The listener is `0.0.0.0:8080` so the published port reaches it.

```yaml
schemaVersion: "7.1"
listeners:
  - host: "0.0.0.0"
    port: 8080
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

```shell
mkdir conf
docker run --rm -p 8080:8080 --read-only --tmpfs /tmp \
  -v "$PWD/conf:/etc/titanium:ro" \
  ghcr.io/justcoding121/titanium-web-proxy:latest
```

From a clone of this repo:

```shell
helm install titanium deploy/helm/titanium \
  -n proxy --create-namespace \
  -f my-values.yaml
```

## What you can do

- Decrypt and inspect HTTPS in a native desktop Inspector on Windows, macOS, and Linux — AutoResponder, Map Local/Remote, breakpoints, Composer, HAR, curl/fetch
- Speak modern HTTP — HTTP/2 by default, optional HTTP/3, WebSocket, gRPC, SSE, GraphQL rules in the same grid
- Run the same engine as a reverse / edge proxy from the CLI — YAML routes, load balancing and failover, retries, rate limits, response compression, gRPC-Web, TCP and UDP relay, ACME, live reload, Lua 5.4 and proxy-wasm scripting (`titanium scripting-deps`)
- Deploy on Kubernetes with a hardened container image, Helm charts (git, Release `.tgz`, or OCI), probes, a Gateway API / Ingress controller, and an optional Manager control plane; hot restart for TCP without a gap
- Observe it with Prometheus text, OTLP traces and metrics, and an access log; Plus adds a dashboard, OpenID Connect login, a response cache, fleet rate limits, xDS, and WAF screening
- Embed in .NET via NuGet; when a server blocks the proxy (TLS fingerprinting or other reasons), we detect it and automatically tunnel that traffic

## Examples and documentation

- **[Website](https://titaniumproxy.com)** — product docs, [download](https://titaniumproxy.com/download), [getting started](https://titaniumproxy.com/docs/getting-started), [features](https://titaniumproxy.com/docs/features), [release notes](https://titaniumproxy.com/releases)
- **[Wiki](https://github.com/justcoding121/titanium-web-proxy/wiki)** — deeper guides (performance, streaming bodies, HTTP/3, protocol support) and a short [Features](https://github.com/justcoding121/titanium-web-proxy/wiki/Features) pointer
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

Actively maintained by [justcoding121](https://github.com/justcoding121). Past collaborator: [honfika](https://github.com/honfika).

## License

`Titanium.Web.Proxy`, `Titanium.Cli`, Titanium.Manager, Titanium.Gateway, docs, and this website are [MIT](LICENSE). Titanium Inspector, Titanium.Plus, Titanium.Xds, and Titanium.Cli.GeoIp are [PolyForm Noncommercial 1.0.0](licenses/PolyForm-Noncommercial-1.0.0.txt). Product overview: [titaniumproxy.com](https://titaniumproxy.com) · [Editions & licenses](https://titaniumproxy.com/docs/editions).

Vendored xDS protos keep their Apache-2.0 text in `src/Titanium.Xds/LICENSE.Apache-2.0.txt` and `src/Titanium.Xds/NOTICE`. Third-party notices for the MIT library are in `src/Titanium.Web.Proxy/NOTICE`.

- **MIT** — `src/Titanium.Web.Proxy`, `src/Titanium.Web.Proxy.Abstractions`, `src/Titanium.Web.Proxy.Configuration`, `src/Titanium.Web.Proxy.Scripting`, `src/Titanium.Cli`, `src/Titanium.Manager`, `src/Titanium.Gateway`, examples, website, docs, wiki, tools, deploy, and tests other than the four PolyForm test projects
- **PolyForm Noncommercial 1.0.0** — `src/Titanium.Plus`, `src/Titanium.Inspector`, `src/Titanium.Xds`, `src/Titanium.Cli.GeoIp`, `tests/Titanium.Plus.Tests`, `tests/Titanium.Inspector.Tests`, `tests/Titanium.Xds.Tests`, `tests/Titanium.Cli.GeoIp.Tests`
