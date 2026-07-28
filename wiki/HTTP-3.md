# HTTP/3 (QUIC) Support

Titanium Web Proxy supports HTTP/3 as an **opt-in experimental** feature built on top of `System.Net.Quic`
(which in turn uses the MsQuic native library).

> **Experimental API:** The HTTP/3 public surface (`EnableHttp3`, `TransparentQuicProxyEndPoint`,
> `BeforeQuicAuthenticateEventArgs`) is marked `[Experimental("TWP001")]`.  Consuming projects must suppress
> the diagnostic to opt in:
> ```csharp
> #pragma warning disable TWP001
> proxyServer.EnableHttp3 = true;
> #pragma warning restore TWP001
> ```
> The attribute is removed in a future semver-minor release after the interop/soak/fuzz gates listed in
> [Rollout / rollback runbook](#rollout--rollback-runbook) have passed.

## Prerequisites

- .NET 10 or later.
- MsQuic native library: bundled with the .NET runtime on Windows and Linux.
  - **Windows**: Windows 11 or Windows Server 2022 or later.
  - **Linux**: install `libmsquic` (e.g. `apt install libmsquic`).
  - **macOS**: not bundled by the .NET runtime. See [macOS](#macos) below for the bundling workaround.
- At runtime: `System.Net.Quic.QuicListener.IsSupported == true` — check this before enabling HTTP/3.
- A `TransparentQuicProxyEndPoint` added to `ProxyServer.ProxyEndPoints`.

## Quick start

```csharp
#pragma warning disable TWP001 // Experimental HTTP/3 API
var proxy = new ProxyServer();

// Guard with IsSupported so the app degrades gracefully on unsupported platforms.
if (QuicListener.IsSupported)
{
    // Opt in to HTTP/3.
    proxy.EnableHttp3 = true;

    // Add a transparent QUIC endpoint on UDP 443.
    var quicEndPoint = new TransparentQuicProxyEndPoint(IPAddress.Any, 443)
    {
        // Optional: plug in your own NAT-original-destination resolver.
        // OriginalDestinationResolver = new MyNatResolver(),

        // Optional: tune QUIC connection limits.
        MaxInboundBidirectionalStreams = 100,
        IdleTimeout = TimeSpan.FromSeconds(30),
    };
    proxy.AddEndPoint(quicEndPoint);

    // Hook into QUIC connections (analogous to BeforeSslAuthenticate for TCP).
    quicEndPoint.BeforeQuicAuthenticate += (sender, e) =>
    {
        // Optionally pin the origin protocol for all streams on this connection.
        // e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;

        // Optionally override certificate validation.
        // e.CertificateValidationCallback = MyValidator;
        return Task.CompletedTask;
    };
}
#pragma warning restore TWP001

// All existing event hooks work unchanged.
proxy.BeforeRequest += async (sender, e) =>
{
    // e.HttpClient.Request.Url — the full request URL.
    // e.UpstreamHttpProtocol — override origin protocol for this stream only.
};

proxy.Start();
```

## Protocol auto-selection (default behaviour)

When `UpstreamHttpProtocol.Auto` (the default) is in effect, the proxy selects the outbound protocol
for each request as follows:

1. **HTTP/3** — if `EnableHttp3 == true` **and** the origin's H3 capability has been cached
   (via `Alt-Svc` response header from a previous request to the same host:port).
2. **HTTP/2** — if the origin has been probed and supports HTTP/2 (via ALPN).
3. **HTTP/1.1** — fallback.

To force a specific protocol for a single request, set `SessionEventArgs.UpstreamHttpProtocol` inside
`BeforeRequest`.  To force it for every request on a QUIC connection, set it on the `BeforeQuicAuthenticateEventArgs`.

## Alt-Svc automatic discovery

When a response is received from an origin that advertises HTTP/3 via the `Alt-Svc` header, the proxy
automatically caches the capability:

```
Alt-Svc: h3=":443"; ma=86400
```

Subsequent requests to the same host:port will use HTTP/3 transparently (when `EnableHttp3 == true`).
The cache entry expires after the advertised `ma` (max-age) duration.  To evict early, call
`ProxyServer.Http3OriginCapabilityCache.Evict("host:port")`.

## Protocol bridges

All five bridge directions are implemented:

| Inbound | Outbound | How |
|---------|----------|-----|
| HTTP/3 (QUIC) | HTTP/3 (QUIC) | `QuicConnectionPool` + QPACK framing |
| HTTP/3 (QUIC) | HTTP/2 (TCP) | `TcpConnectionFactory` with h2 ALPN |
| HTTP/3 (QUIC) | HTTP/1.1 (TCP) | `TcpConnectionFactory` with default ALPN |
| HTTP/1.1 (TCP) | HTTP/3 (QUIC) | `Http3OriginBridge.ForwardAsync` when capability cached |
| HTTP/2 (TCP) | HTTP/3 (QUIC) | `Http3OriginBridge.ForwardAsync` when capability cached |

## Per-request overrides

The following `SessionEventArgs` properties override the global defaults for a single request stream:

| Property | Overrides | Notes |
|----------|-----------|-------|
| `UpstreamHttpProtocol` | `BeforeQuicAuthenticateEventArgs.UpstreamHttpProtocol` | Set in `BeforeRequest`; changing it afterward has no effect. |
| `ConnectTimeout` | `ProxyServer.ConnectTimeOutSeconds` | Controls the QUIC handshake timeout for new origin connections. |
| `MaxBufferedBodyBytes` | `ProxyServer.MaxBufferedBodyBytes` | Limits response body buffering for this stream. |
| `NetworkFailureRetryAttempts` | `ProxyServer.NetworkFailureRetryAttempts` | Set to `0` for non-idempotent methods. |
| `OriginHttpVersionPolicy` | `ProxyServer.OriginHttpVersionPolicy` | Controls request-line HTTP version for TCP-based origin connections. |

## QPACK

Titanium Web Proxy uses **static-table-only** QPACK (RFC 9204): the Required Insert Count is always
zero and the dynamic table is never synchronised.  This is fully interoperable with all RFC 9204-compliant
peers; it sacrifices some header-compression efficiency in exchange for simpler, allocation-free encoding.

## Transparent QUIC endpoint configuration

`TransparentQuicProxyEndPoint` exposes additional settings beyond the base `ProxyEndPoint`:

| Property | Default | Description |
|----------|---------|-------------|
| `OriginalDestinationResolver` | `null` (uses `RemoteEndPoint`) | Plug-in for resolving pre-NAT destination. |
| `MaxInboundBidirectionalStreams` | `100` | Maximum concurrent HTTP/3 request streams per connection. |
| `MaxInboundUnidirectionalStreams` | `10` | Minimum 3 (control + QPACK encoder/decoder); increase for server push (not implemented). |
| `HandshakeTimeout` | `10 s` | QUIC TLS handshake deadline. |
| `IdleTimeout` | `60 s` | Connection idle timeout. |

## Limitations

- **Transparent only**: HTTP/3 cannot be configured as an explicit (system-proxy) endpoint because the
  Windows system-proxy mechanism does not support QUIC/UDP.
- **Static QPACK only**: dynamic table synchronisation is not implemented.
- **No 0-RTT**: early data is not supported by `System.Net.Quic` in .NET 10.
- **No connection migration**: `System.Net.Quic` does not expose migration APIs.
- **No server push**: removed from RFC 9114.
- **macOS**: see [macOS](#macos) below.

## macOS

MsQuic is **not bundled** with the .NET runtime on macOS. To use HTTP/3 on macOS, bundle `libmsquic`,
`libssl`, and `libcrypto` alongside your application and configure `@loader_path` RPATH so the libraries
can locate each other locally. When this is done correctly, `QuicListener.IsSupported` returns `true` and
TWP's HTTP/3 support works without any code changes — no OS detection or special configuration in TWP is
required.

See the [MsQuic GitHub](https://github.com/microsoft/msquic) for library build and bundling instructions.

## ECH (Encrypted Client Hello) constraints

HTTP/3 transparent interception is **incompatible with ECH** when ECH is enabled for a domain's DNS record.
ECH encrypts the TLS `ClientHello` (including the SNI) using a public key published in a HTTPS/SVCB DNS
record. Because TWP performs TLS termination it must read the SNI to select the correct MITM certificate —
ECH prevents this.

**Remediation options:**
- Configure managed DNS to omit HTTPS/SVCB `ech=` parameters for intercepted domains.
- Deploy a managed DNS resolver that strips or ignores ECH hints for intercepted networks.
- Managed clients (e.g., corporate devices) can disable ECH via group policy or browser flags.
- For development/testing proxies: most browsers only enable ECH when a valid DNS HTTPS record with `ech=`
  is present; a split-horizon DNS that returns a plain A/AAAA record disables it automatically.

## UDP/NAT/firewall setup

Unlike TCP transparent proxying (which uses standard `SO_ORIGINAL_DST`/`IP_TRANSPARENT`), HTTP/3 requires
**UDP interception**:

**Linux (iptables/nftables):**
```
# Redirect inbound UDP 443 to the proxy's QUIC listener (e.g. UDP 44300)
iptables -t nat -A PREROUTING -p udp --dport 443 -j REDIRECT --to-ports 44300
```
Implement `IOriginalDestinationResolver` using `getsockopt(SO_ORIGINAL_DST)` (for IPv4) or the
`IP6T_SO_ORIGINAL_DST` socket option (for IPv6) — the same technique used for TCP transparent proxying.

**Windows:** Windows does not expose `SO_ORIGINAL_DST` for UDP. Use WFP (Windows Filtering Platform) callout
drivers or a fixed-forward configuration (`ForwardHost`/`ForwardPort` on `TransparentQuicProxyEndPoint`)
instead of `IOriginalDestinationResolver`.

**macOS:** Use `pf` (Packet Filter) with a `rdr` rule to redirect UDP traffic, then read the original
destination via `getsockopt(SO_ORIGINAL_DST)`.

> **Note:** UDP 443 and TCP 443 can bind the same port number simultaneously — UDP and TCP are independent
> L4 protocols. A QUIC listener on UDP 443 and a TCP listener on TCP 443 do not conflict.

## Rollout / rollback runbook

**Current status:** HTTP/3 support is marked `[Experimental("TWP001")]`. Enable it with:
```csharp
#pragma warning disable TWP001
proxyServer.EnableHttp3 = true;
proxyServer.AddEndPoint(new TransparentQuicProxyEndPoint(...));
#pragma warning restore TWP001
```

**Rollback:** Set `EnableHttp3 = false` and remove the `TransparentQuicProxyEndPoint`. For origins that
cached `h3` via Alt-Svc, they will fall back to TCP on the next request cycle. To accelerate rollback, send
`Alt-Svc: clear` in a response to the client for affected origins (strips all cached alternatives).

**Graduation to stable** requires all of the following gates to pass:
- Full interop against Chrome, Edge, Firefox, and Safari (HTTPS/SVCB discovery, QPACK, stream reuse).
- Soak test: 72 hours under representative production traffic with no unexpected stream resets.
- Malformed-peer / fuzzing gate: no panics or unhandled exceptions under crafted QUIC frames.
- Resource-exhaustion gate: stream/connection limits hold under attack (no OOM or goroutine leak equivalent).

Once those pass, the `[Experimental]` attribute is removed in a semver-minor release.

## See also

- [Protocol Support](Protocol-Support) — feature matrix for all protocols.
- [Home](Home) — general usage and the rest of the public API surface.
- RFC 9114 — HTTP/3
- RFC 9204 — QPACK
- RFC 9000 — QUIC
- RFC 7838 — Alt-Svc
