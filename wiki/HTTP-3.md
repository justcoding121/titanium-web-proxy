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

1. **HTTP/3** — if `EnableHttp3 == true` **and** the origin's H3 capability is known. Capability is
   populated from either a previous `Alt-Svc` response header **or** (when `EnableHttpsSvcbDnsDiscovery`
   is true) a proactive HTTPS/SVCB DNS query before the first connection attempt.
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

## HTTPS/SVCB DNS discovery (opt-in)

In addition to reactive `Alt-Svc` caching, you can enable **proactive** HTTP/3 capability discovery via
RFC 9460 HTTPS resource record queries:

```csharp
#pragma warning disable TWP001
proxyServer.EnableHttp3 = true;
proxyServer.EnableHttpsSvcbDnsDiscovery = true; // opt-in

// Optional: use a specific DNS server (default: 127.0.0.1:53)
// proxyServer.DnsServerEndPoint = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
#pragma warning restore TWP001
```

When enabled, the proxy issues a DNS HTTPS RR query for an origin before opening the first connection.
If the DNS response contains an HTTPS record with ALPN `h3`, HTTP/3 is used immediately rather than
waiting for an `Alt-Svc` response header.

**Behavior:**

- Queries are sent over UDP to `DnsServerEndPoint` with a 1-second timeout.
- Results are cached with the record's TTL, capped at 1 hour.
- NXDOMAIN, alias-mode entries (SvcPriority = 0), and records without `h3` in the ALPN list are
  cached as negative entries (suppress further queries for the TTL duration).
- Query coalescing: concurrent requests for the same origin share one in-flight DNS query.
- The resolver is pluggable: set `ProxyServer.HttpsSvcbResolver` to a custom `IHttpsSvcbResolver`
  implementation (e.g. for testing with pre-built responses).

> **Note:** HTTPS/SVCB discovery adds a DNS round-trip before the first QUIC connection to any origin.
> On a LAN or loopback resolver this is typically < 1 ms. Enable it selectively if you control the
> DNS infrastructure; leave it disabled if you rely on the OS resolver (`/etc/resolv.conf` / WinDNS)
> and cannot guarantee HTTPS RR support.

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

Titanium Web Proxy implements two QPACK modes:

### Static-table mode (default)

By default, the encoder sends Required Insert Count = 0 on every field section and never synchronises the
dynamic table. This is fully interoperable with all RFC 9204-compliant peers and involves no per-connection
state, but sacrifices some header-compression efficiency on repeated headers.

### Dynamic-table mode (opt-in)

Set `ProxyServer.EnableQpackDynamicTable = true` to enable full RFC 9204 dynamic table support:

```csharp
#pragma warning disable TWP001
proxyServer.EnableHttp3 = true;
proxyServer.EnableQpackDynamicTable = true; // opt-in
#pragma warning restore TWP001
```

When enabled, per accepted QUIC connection:

- The proxy opens the QPACK encoder and decoder unidirectional control streams.
- It advertises `SETTINGS_QPACK_MAX_TABLE_CAPACITY = 4096` and `SETTINGS_QPACK_BLOCKED_STREAMS = 100`
  to the client.
- A `QpackDynamicTable` (thread-safe via `ReaderWriterLockSlim`) tracks inbound and outbound table entries
  as absolute indices, per RFC 9204 §3.
- When decoding an incoming HEADERS block whose Required Insert Count is > 0, the proxy suspends decoding
  until the required number of insertions have been acknowledged via the encoder stream, per RFC 9204 §4.5.1.
- Section Acknowledgments are queued on a bounded `Channel` (capacity 1000, `DropNewest` on overflow) and
  written to the client's decoder stream by a background `QpackDecoderStreamWriter` task.
- In-flight eviction protection: a table entry cannot be evicted while any open request stream holds a
  reference to it (tracked per stream by absolute index). This satisfies RFC 9204 §2.1.1.

> **Note:** Dynamic table support adds per-connection state (inbound + outbound tables, two extra QUIC
> streams, and a background drain task). Leave `EnableQpackDynamicTable = false` if your traffic has a
> high connection churn rate or if header repetition is low.

### QPACK configuration

| Property | Default | Description |
|----------|---------|-------------|
| `EnableQpackDynamicTable` | `false` | Enables RFC 9204 dynamic table synchronisation for HTTP/3 connections. |

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

- **Transparent only**: HTTP/3 cannot be configured as an explicit (system-proxy) endpoint. See
  [Why no explicit HTTP/3 endpoint yet](#why-no-explicit-http3-endpoint-yet) below.
- **QPACK dynamic table is opt-in**: static-table-only mode is the default; see [QPACK](#qpack) above.
- **Upstream proxy with QUIC falls back to TCP**: `System.Net.Quic` does not support HTTP CONNECT
  tunnelling or SOCKS5 UDP ASSOCIATE. When a per-request or global upstream proxy is configured, the
  QUIC leg gracefully falls back to `ForwardOverTcpAsync` where the proxy rules are honoured on the
  TCP connection.
- **No 0-RTT**: early data is not supported by `System.Net.Quic` in .NET 10.
- **No connection migration**: `System.Net.Quic` does not expose migration APIs.
- **No server push**: removed from RFC 9114.
- **macOS**: see [macOS](#macos) below.

### Why no explicit HTTP/3 endpoint yet

**An RFC exists but it is a relay, not an interceptor.**
RFC 9298 (MASQUE/CONNECT-UDP) is the standardized mechanism for proxying HTTP/3 through an explicit proxy.
A browser sends an extended-CONNECT request to the proxy with `:protocol = connect-udp`, the proxy opens a
UDP socket to the origin, and QUIC datagrams are shuttled between browser and origin wrapped in HTTP
Datagrams (RFC 9297):

```
Browser ──CONNECT-UDP──► TWP ──UDP datagrams (opaque)──► Origin
         (HTTP/3)                                         ▲
         └──────── TLS 1.3 end-to-end (QUIC) ────────────┘
```

The QUIC TLS 1.3 session is **end-to-end between browser and origin**. TWP receives only encrypted QUIC
datagrams and relays them without seeing their content. There is no opportunity to terminate TLS, read
HTTP/3 frames, or fire `BeforeRequest`/`BeforeResponse` events. This is the opposite of how TCP CONNECT
works, where TWP sits inside the TLS session by issuing a MITM certificate.

**OS proxy APIs do not support HTTP/3 targets.**
Windows WinHTTP/WPAD, macOS system proxy settings, and Linux `$http_proxy`/`$https_proxy` all reference a
TCP host:port. There is no standardized mechanism for a user or application to specify an HTTP/3
(UDP-based) proxy endpoint through OS-level proxy configuration.

**No browser exposes HTTP/3 proxy configuration.**
Chrome, Edge, Firefox, and Safari all fall back to HTTP/2 or HTTP/1.1 for the proxy leg when a system
proxy is configured — they do not negotiate QUIC with the proxy. Chrome has private MASQUE support
(used by iCloud Private Relay and Google One VPN), but this path is not user-configurable and bypasses
system proxy settings entirely.

**SOCKS5 UDP ASSOCIATE is not used by browsers.**
SOCKS5 (RFC 1928) has supported UDP proxying since 1996, but all major browsers ignore the UDP ASSOCIATE
command for QUIC traffic even when the SOCKS5 server advertises it.

**What would be required for TWP to support an explicit HTTP/3 endpoint:**

1. OS proxy APIs gain an HTTP/3 target field (new registry key / proxy.pac extension / etc.).
2. Browsers honor that configuration and connect to the proxy over QUIC.
3. Browsers use standard `CONNECT host:443` (not `CONNECT-UDP`) over that HTTP/3 connection, creating a
   bidirectional stream that TWP can terminate TLS on — same MITM model as today's TCP CONNECT.

None of these exist yet. If they land, TWP can add an `ExplicitQuicProxyEndPoint` following the same
pattern as `ExplicitProxyEndPoint` without touching the transparent implementation.

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

## See also

- [Protocol Support](Protocol-Support) — feature matrix for all protocols.
- [Home](Home) — general usage and the rest of the public API surface.
- RFC 9114 — HTTP/3
- RFC 9204 — QPACK
- RFC 9000 — QUIC
- RFC 7838 — Alt-Svc
