# Library (embed)

NuGet package **Titanium.Web.Proxy** (MIT). Target framework: **.NET 10**.

```shell
dotnet add package Titanium.Web.Proxy
# Latest prerelease (e.g. 7.0.4-beta):
dotnet add package Titanium.Web.Proxy --prerelease
```

## Explicit MITM proxy

```csharp
using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();
proxyServer.BeforeRequest += async (sender, e) =>
{
    // Inspect or rewrite e.HttpClient.Request
};

var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);
proxyServer.Start();
```

## Capabilities

- Intercept, modify, redirect, or block HTTP and HTTPS
- Explicit, transparent, and SOCKS4/5 endpoints
- HTTP/2 on by default (`ProxyServer.EnableHttp2`); HTTP/3 opt-in (`EnableHttp3`, experimental `TWP001`)
- Upstream HTTP/HTTPS/SOCKS + system proxy detection
- Proxy auth, mTLS, Kerberos, NTLM
- Connection, certificate, and buffer pooling
- Built-in async logging and opt-in request timing

## Reverse proxy

Use `ForwardHost` on a transparent endpoint for a zero-cost terminate-lite path, or declarative routes/clusters (also used by the CLI). See [Configuration](/docs/configuration) and [Protocol support](/docs/protocol-support).

## API reference

- [ProxyServer](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}
- Full API tree under [/api/](/api/){target="_blank" rel="noreferrer"}

## Examples in the repo

- `examples/Titanium.Web.Proxy.Examples.Basic`
- `examples/Titanium.Web.Proxy.Examples.Wpf`
- `examples/Titanium.Web.Proxy.Examples.WindowsService`


## System proxy and root CA (Windows / macOS / Linux)

`ProxyServer.SetAsSystemProxy` / `RestoreOriginalProxySettings` work on Windows (WinINET), macOS (`networksetup`), and Linux (GNOME + KDE + process environment). Unsupported platforms throw `NotSupportedException`.

`CertificateManager.TrustRootCertificate` installs into the current-user store on all platforms and additionally trusts for SSL on macOS (login keychain) and Linux (user NSS db). `TrustRootCertificateAsAdmin` shows an OS admin prompt (UAC / macOS authentication / polkit) for machine-wide trust.
