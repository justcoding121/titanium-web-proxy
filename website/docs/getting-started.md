# Getting started

Titanium Web Proxy is a lightweight, high-performance HTTP(S) proxy for .NET. Use it as:

- an **embeddable library** (MITM and/or reverse) in your app, or
- a **standalone CLI** reverse / edge proxy (`titanium` / `twp`).

## Choose a path

| Goal | Start here |
|------|------------|
| Embed in a .NET app | [Library](/docs/library) + NuGet |
| Run a reverse proxy from YAML | [Download CLI](/download) → [CLI](/docs/cli) → [Configuration](/docs/configuration) |
| Desktop traffic debugging | [Download Inspector](/download) → [Inspector](/docs/inspector) |
| Ops / control plane | [Plus](/docs/plus) (`titanium update --plus`) |

## Library in 30 seconds

```shell
dotnet add package Titanium.Web.Proxy
```

```csharp
using System.Net;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();
proxyServer.Logging.MinimumLevel = LogLevel.Information;
proxyServer.BeforeRequest += OnRequest;

var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);
proxyServer.Start();

Task OnRequest(object sender, SessionEventArgs e)
{
    proxyServer.Logger.LogInformation("{Url}", e.HttpClient.Request.Url);
    return Task.CompletedTask;
}
```

Point your client at `127.0.0.1:8000`. Only trust a generated root CA on a machine you control.

## CLI reverse in 30 seconds

1. [Download](/download) the CLI zip for your OS and extract it.
2. Create `twp.yaml`:

```yaml
schemaVersion: "7.0"
listeners:
  - host: "127.0.0.1"
    port: 8000
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

3. Run:

```shell
titanium test -c twp.yaml
titanium run -c twp.yaml
```

## Runtime

- **.NET 10** only (as of 4.0+).
- Windows, Linux, and macOS for Core and CLI. Inspector is Windows-first.

## Next

- [Install](/docs/install)
- [Configuration (`twp.yaml`)](/docs/configuration)
- [Editions & licenses](/docs/editions)
- [API reference](/api/Titanium.Web.Proxy.ProxyServer.html)
