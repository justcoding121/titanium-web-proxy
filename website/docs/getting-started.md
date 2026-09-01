# Getting started

Titanium Web Proxy is a lightweight, high-performance HTTP(S) proxy for general use: reverse / edge, MITM debugging, and ops. The CLI, Plus, and Inspector run on **Windows, Linux, and macOS**. A **.NET library** is available when you want to embed the engine in an app.

## Choose a path

| Goal | Start here |
|------|------------|
| Run a reverse / edge proxy from YAML | [Download CLI](/download) → [CLI](/docs/cli) → [Configuration](/docs/configuration) |
| Desktop traffic debugging | [Download Inspector](/download) → [Inspector](/docs/inspector) |
| Ops / control plane | [Plus](/docs/plus) (`titanium update --plus`; use `--channel beta` for prereleases) |
| Embed in a .NET app | [Library](/docs/library) + NuGet (`--prerelease` for beta) |

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

## Library in 30 seconds (.NET)

```shell
dotnet add package Titanium.Web.Proxy
# beta / prerelease:
dotnet add package Titanium.Web.Proxy --prerelease
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

## Platforms

- **CLI, Plus, Core:** Windows, Linux, and macOS (self-contained CLI zips; no .NET SDK required to *run* the CLI).
- **Inspector:** Windows-first (MSI / portable zip).
- **Library:** .NET 10 (NuGet).

## Next

- [Install](/docs/install)
- [Configuration (`twp.yaml`)](/docs/configuration)
- [Editions & licenses](/docs/editions)
- [API reference](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}
