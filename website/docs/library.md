# Library (embed)

NuGet package **Titanium.Web.Proxy** (MIT). Target framework: **.NET 10**.

```shell
dotnet add package Titanium.Web.Proxy
# Prerelease (e.g. 7.0.4-beta when newer than stable):
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

`CertificateManager.TrustRootCertificate` installs into the current-user store on all platforms and additionally trusts for SSL on macOS (login keychain) and Linux (user NSS db). Check `LastOsTrustResult` for structured outcomes (e.g. missing `certutil`, Keychain Always Trust needed). `InstallNssCertutilAndRetryUserTrust` installs NSS tools via package manager / Homebrew after user consent. `FirefoxCertificateTrust` enables Windows `ImportEnterpriseRoots` or profile `user.js` (`security.enterprise_roots.enabled`) so Firefox trusts OS roots, and can import into the default Firefox NSS profile. `TrustRootCertificateAsAdmin` shows an OS admin prompt (UAC / macOS authentication / polkit) for machine-wide trust.

## MITM hostname exclusions

Two layers (both work with system proxy on **Windows**, **macOS**, and **Linux**):

1. **OS bypass** — `SystemProxySettings.BypassRules` + `SetAsSystemProxy(..., settings)`. Traffic never hits the proxy when System proxy is on. Factory seed: `MitmExclusionDefaults.SystemProxyBypassRules` (Microsoft identity / SSO / RDP).
2. **Tunnel only** — `BeforeTunnelConnectRequest` → `e.DecryptSsl = false`, or `MitmExclusionDefaults.ApplyDecryptExclusions(...)`. CONNECT stays visible; no decrypt. Factory seed: `MitmExclusionDefaults.TunnelOnlyPinningDomains` (`dropbox.com`, `webex.com`).

Use **`MitmExclusionMode.Merge`** (default, back-compat) to always re-inject factory hosts, or **`Replace`** so the caller lists are authoritative (you can remove identity hosts — risk breaking SSO while System proxy is on).

```csharp
// Merge: factory identity hosts + extras
var settings = MitmExclusionDefaults.CreateSystemProxySettings(
    proxyLoopback: true,
    additionalBypassRules: ["*.corp.example.com"]);
proxyServer.SetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp, settings);

// Replace: full list you provide (factory not re-added)
var custom = MitmExclusionDefaults.CreateSystemProxySettings(
    proxyLoopback: true,
    MitmExclusionDefaults.SystemProxyBypassRules.Append("*.corp.example.com"),
    MitmExclusionMode.Replace);

MitmExclusionDefaults.ApplyDecryptExclusions(endPoint, () => true,
    decryptSkipHosts: ["*.bank.example.com"],
    decryptOnlyHosts: null,
    MitmExclusionMode.Replace);
```

CLI: when `server.decryptSkipHosts` and/or `server.decryptOnlyHosts` are present in `twp.yaml`, exclusions apply with **Replace**. Omit both to leave Merge defaults. Optional `server.systemProxyBypassHosts` / `server.proxyLoopback` build OS-bypass settings the same way (present ⇒ Replace; omit ⇒ Merge). Removing identity hosts from OS bypass can break Microsoft SSO while System proxy is enabled.
