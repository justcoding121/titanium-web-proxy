# HTTP/3 (QUIC)

HTTP/3 is an **opt-in experimental** feature on `System.Net.Quic` / MsQuic.

```csharp
#pragma warning disable TWP001
proxyServer.EnableHttp3 = true;
#pragma warning restore TWP001
```

## Prerequisites

- .NET 10+
- MsQuic:
  - **Windows**: in-box with the .NET runtime (Windows 11 / Server 2022+)
  - **Linux**: install `libmsquic` from packages.microsoft.com
  - **macOS**: not bundled; see the wiki for bundling notes
- `System.Net.Quic.QuicListener.IsSupported == true` before enabling
- Inbound endpoint: `TransparentQuicProxyEndPoint` or `TransparentProxyEndPoint` with `EnableHttp3 = true`

## Dual-listen reverse (TCP + UDP)

Same IP:port speaks TLS H1/H2 over TCP and H3 over UDP, and can inject `Alt-Svc`:

```csharp
#pragma warning disable TWP001
var proxy = new ProxyServer { EnableHttp3 = true, EnableHttp2 = true };

if (QuicListener.IsSupported)
{
    var reverse = new TransparentProxyEndPoint(IPAddress.Any, 443, decryptSsl: true)
    {
        EnableHttp3 = true,
    };
    proxy.AddEndPoint(reverse);
}
#pragma warning restore TWP001
```

## CLI

```yaml
listeners:
  - host: "0.0.0.0"
    port: 443
    decryptSsl: true
    enableHttp3: true
```

## Full guide

Setup, macOS notes, Alt-Svc, bridges, and gap list: [HTTP/3 wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/HTTP-3).
