# HTTP/3 (QUIC) Support

Titanium Web Proxy supports HTTP/3 on .NET 6+ as an **opt-in** feature built on top of `System.Net.Quic`
(which in turn uses the MsQuic native library).

## Prerequisites

- .NET 6 or later (tested on .NET 10).
- MsQuic native library installed: bundled with .NET 6+ runtimes on Windows and Linux; macOS is **not**
  supported by MsQuic.
- At runtime: `System.Net.Quic.QuicListener.IsSupported == true` — check this before enabling HTTP/3.
- A `TransparentQuicProxyEndPoint` added to `ProxyServer.ProxyEndPoints`.

## Quick start

```csharp
var proxy = new ProxyServer();

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
};

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
- **macOS**: not supported by MsQuic.

## See also

- [Protocol Support](Protocol-Support) — feature matrix for all protocols.
- [Home](Home) — general usage and the rest of the public API surface.
- RFC 9114 — HTTP/3
- RFC 9204 — QPACK
- RFC 9000 — QUIC
- RFC 7838 — Alt-Svc
