# Getting started

Titanium Web Proxy helps you do three things:

1. **Embed** the same engine in a .NET application (Library)
2. **Inspect** HTTP/HTTPS traffic in a desktop app (Inspector)
3. **Run a reverse proxy** in front of your apps (CLI)

Optional **Plus** adds a dashboard and ops features on top of the CLI. Everything runs on **Windows, Linux, and macOS**.

## Highlights

Embed via NuGet and log each request in a few lines; native HTTPS Inspector with AutoResponder, Map Local/Remote, breakpoints, Composer, and HAR; when a server blocks the proxy we detect it and tunnel automatically; HTTP/2 / HTTP/3 with WebSocket, gRPC, and GraphQL in the grid; the same engine as a reverse/edge CLI. Full catalog: [Features](/docs/features).

## Choose a path

- **Use Titanium inside a .NET app** — [Library sample](#embed-in-net-library)
- **Debug browser or app traffic** — [Download Inspector](/download) → [Inspector guide](/docs/inspector)
- **Put a proxy in front of my site or API** — [Download CLI](/download) → [reverse proxy below](#reverse-proxy-in-a-minute-cli) → [CLI](/docs/cli)
- **Add dashboard / metrics / auth helpers** — [Plus](/docs/plus) after installing the CLI

## Embed in .NET (Library)

```shell
dotnet add package Titanium.Web.Proxy
# Newer than stable:
dotnet add package Titanium.Web.Proxy --prerelease
```

Explicit HTTP(S) proxy on `127.0.0.1:8000` that logs each requested URL:

```csharp
using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();
proxyServer.BeforeRequest += async (s, e) =>
{
    Console.WriteLine(e.HttpClient.Request.Url);
};

var endPoint = new ExplicitProxyEndPoint(
    IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);
// The first run installs a root certificate in the current user's trust store.
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);
proxyServer.Start();
```

Point the client at `127.0.0.1:8000` as its HTTP and HTTPS proxy. The first run installs a root certificate for the current user — only on a machine you control. More endpoints, reverse proxy, and trust behavior: [Library](/docs/library). Full API: [ProxyServer](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}.

## Inspect traffic (Inspector)

1. [Download](/download) and install Inspector.
2. Launch it — capturing and system proxy are on by default on the local machine.
3. Turn on **Decrypt HTTPS** when you need to see inside HTTPS (installs a local root certificate — only on machines you control).

Details: [Inspector](/docs/inspector).

## Reverse proxy in a minute (CLI)

1. [Download](/download) the CLI for your OS and extract it (or Homebrew on macOS — see [Install](/docs/install)).
2. Create `twp.yaml` that forwards to your backend (example: local port 8080):

```yaml twp
schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 8000
    # false = do not decrypt HTTPS (plain reverse proxy)
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

3. Validate and run:

```shell
titanium test -c twp.yaml
titanium run -c twp.yaml
```

`titanium run` stays in the foreground (Ctrl+C to stop). To start at boot: `titanium service install -c twp.yaml` — see [CLI — service](/docs/cli#service). More YAML: [Configuration](/docs/configuration). When a request fails, the Error log names the limit and the key: [Limits](/docs/limits) and [troubleshooting](/docs/troubleshooting).

## Platforms at a glance

- **CLI / Plus** — Windows, Linux, macOS (self-contained — no .NET SDK to *run*)
- **Inspector** — Windows, macOS, Linux
- **Library** — .NET 10 (NuGet)

## Next

- [Features](/docs/features)
- [Install](/docs/install)
- [Editions & licenses](/docs/editions)
- [Performance](/docs/performance)
