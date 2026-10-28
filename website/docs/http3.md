# HTTP/3 (QUIC)

CLI and Inspector turn HTTP/3 on when this computer supports QUIC. Release zips already ship the Quic natives for common platforms. Set `enableHttp3: false` on a listener, or `server.enableHttp3: false`, to turn it off.

**Next:** `titanium http3-deps status` if QUIC is unsupported.

## CLI

A decrypting listener uses HTTP/3 when QUIC is supported. Omit `enableHttp3` to keep that default:

```yaml
listeners:
  - host: "0.0.0.0"
    port: 443
    decryptSsl: true
```

```bash
titanium http3-deps status
titanium http3-deps install   # only if status shows unsupported
```

## Pick the right download

Self-contained GitHub Release zips **ship MsQuic natives** for common platforms:

| Environment | Zip |
| --- | --- |
| Windows 11 / Server 2022+ | `win-x64` (OS MsQuic; no extra DLL) |
| Ubuntu/Debian/RHEL-like (glibc) x64 | `linux-x64` |
| glibc arm64 (Graviton, Arm nodes) | `linux-arm64` |
| Alpine / musl containers (K8s sidecars) x64 | `linux-musl-x64` |
| Alpine / musl arm64 | `linux-musl-arm64` |
| macOS Intel | `osx-x64` |
| macOS Apple Silicon | `osx-arm64` |

Those zips ship **MsQuic + OpenSSL only** (MIT / Apache-2.0). They do **not** redistribute LGPL/GPL host libs (`libnuma`, `lttng-ust`). On empty/distroless images install those with `http3-deps` (or your package manager) so the loader can resolve them:

| Host | Host packages |
| --- | --- |
| Ubuntu/Debian | `libnuma1` (pulled with `libmsquic` via `http3-deps install`) |
| Alpine/musl | `numactl`, `lttng-ust` |

Edge OS / old glibc / missing host deps: `titanium http3-deps install` (apt / dnf / apk / brew; needs network + sudo).

### Alpine / Kubernetes

Use the **`linux-musl-*`** zip inside Alpine images. A `linux-x64` (glibc) zip will not load MsQuic on musl. Also install `numactl` + `lttng-ust` (or run `titanium http3-deps install`).

## Library (for .NET library users)

Embedding the Library in your own app does **not** bundle MsQuic:

- **Windows**: Windows 11 / Server 2022+
- **Linux**: install `libmsquic` (e.g. packages.microsoft.com / `apk add libmsquic`)
- **macOS**: bundle `libmsquic` + OpenSSL with `@loader_path`, or `brew install libmsquic`

Confirm `System.Net.Quic.QuicListener.IsSupported == true` before enabling. Library code that sets `EnableHttp3` suppresses `TWP001`:

```csharp
#pragma warning disable TWP001
proxyServer.EnableHttp3 = true;
#pragma warning restore TWP001
```

Inbound endpoint: `TransparentQuicProxyEndPoint` or `TransparentProxyEndPoint` with `EnableHttp3 = true`.

### Dual-listen reverse (TCP + UDP)

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

## When an origin's QUIC path fails

In Auto mode a cold QUIC handshake that is slow races a TCP/HTTP/2 connection, and a safe bodiless request (`GET`, `HEAD`, `OPTIONS`) whose QUIC origin stays silent for 5 s is replayed once over TCP. The origin then stays on TCP for 5 minutes, doubling after each repeat failure up to 1 hour. Forced HTTP/3 (`UpstreamHttpProtocol.Http3` in the library) never falls back: a dead QUIC origin returns an error.

## Full guide

Packaging, Alt-Svc, bridges, and gap list: [HTTP/3 wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/HTTP-3).
