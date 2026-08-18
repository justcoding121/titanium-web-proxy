# HTTP/3 (QUIC) Support

Titanium Web Proxy supports HTTP/3 as an **opt-in experimental** feature built on top of `System.Net.Quic`
(which in turn uses the MsQuic native library).

> **Experimental API:** The HTTP/3 public surface (`EnableHttp3`, `TransparentQuicProxyEndPoint`,
> `TransparentProxyEndPoint.EnableHttp3`, `BeforeQuicAuthenticateEventArgs`) is marked
> `[Experimental("TWP001")]`.  Consuming projects must suppress the diagnostic to opt in:
> ```csharp
> #pragma warning disable TWP001
> proxyServer.EnableHttp3 = true;
> #pragma warning restore TWP001
> ```

## Prerequisites

- .NET 10 or later.
- MsQuic native library:
  - **Windows**: shipped with the .NET runtime (Windows 11 / Server 2022 or later).
  - **Linux**: install `libmsquic` from [packages.microsoft.com](https://packages.microsoft.com) (not bundled with the runtime). Example on Ubuntu:

```bash
curl -fsSL --proto '=https' --tlsv1.2 \
  "https://packages.microsoft.com/config/ubuntu/$(. /etc/os-release; echo $VERSION_ID)/packages-microsoft-prod.deb" \
  -o packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y libmsquic
```

  - **macOS**: not bundled by the .NET runtime. See [macOS](#macos) below for the bundling workaround.
- At runtime: `System.Net.Quic.QuicListener.IsSupported == true` — check this before enabling HTTP/3.
- An inbound HTTP/3 endpoint: either `TransparentQuicProxyEndPoint` (UDP-only transparent/NAT) or
  `TransparentProxyEndPoint` with `EnableHttp3 = true` (reference .NET server stack-style TCP+UDP reverse listen).

The Linux [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml) workflow installs `libmsquic` so HTTP/3 probe arms run on `ubuntu-latest`.

## Quick start — reverse dual-listen (HttpClient / browsers)

Same IP:port speaks TLS H1/H2 over TCP and H3 over UDP, and injects `Alt-Svc: h3=":PORT"` on H1/H2
responses so `HttpClient` (`HttpVersion.Version30` + `RequestVersionExact`) and Alt-Svc discovery work:

```csharp
#pragma warning disable TWP001
var proxy = new ProxyServer { EnableHttp3 = true, EnableHttp2 = true };

if (QuicListener.IsSupported)
{
    var reverse = new TransparentProxyEndPoint(IPAddress.Any, 443, decryptSsl: true)
    {
        EnableHttp3 = true,
        ForwardHost = "127.0.0.1",
        ForwardPort = 8080,
        ForwardCleartext = true,
        GenericCertificateName = "localhost",
        MaxInboundBidirectionalStreams = 256,
    };
    reverse.BeforeQuicAuthenticate += (_, e) =>
    {
        e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
        return Task.CompletedTask;
    };
    reverse.BeforeSslAuthenticate += (_, e) =>
    {
        e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
        e.AllowHttpProtocolTranslation = true;
        return Task.CompletedTask;
    };
    proxy.AddEndPoint(reverse);
}
#pragma warning restore TWP001

proxy.Start();
```

## Quick start — transparent UDP-only

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
as follows (evaluated at CONNECT / new TCP request setup — not by flipping streams on an already-open
H2↔H2 MITM session):

1. **HTTP/3** — if `EnableHttp3 == true`, the origin is already in `Http3OriginCapabilityCache` (from a
   prior `Alt-Svc` response and/or a completed background HTTPS/SVCB lookup), **and** that origin has
   completed QUIC warm-up (`Http3WarmOrigins`). A cache hit alone only arms background warm-up; the
   current connection stays on TCP until the origin is warm. SVCB discovery never blocks the first
   connection.
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

Once the origin is warm, **new** Auto-mode connections to the same host:port use HTTP/3 transparently
(when `EnableHttp3 == true`) — for example a later CONNECT that selects the cold H2→H3 bridge, or an
HTTP/1.1 request that routes through `Http3OriginBridge`. An already-open H2↔H2 MITM session does
**not** upgrade individual multiplexed streams to H3 mid-connection (that mix has been observed to
trigger client `ERR_HTTP2_PROTOCOL_ERROR`); those streams stay on the attached H2 origin until the
tunnel ends. The cache entry expires after the advertised `ma` (max-age) duration and is trimmed
periodically. There is no public eviction API; to force a protocol for a given request or
connection, set `SessionEventArgs.UpstreamHttpProtocol` or `BeforeQuicAuthenticateEventArgs.UpstreamHttpProtocol`.

## HTTPS/SVCB DNS discovery (opt-in)

In addition to reactive `Alt-Svc` caching, you can enable **proactive** HTTP/3 capability discovery via
RFC 9460 HTTPS resource record queries:

```csharp
#pragma warning disable TWP001
proxyServer.EnableHttp3 = true;
// EnableHttpsSvcbDnsDiscovery inherits EnableHttp3; set false to rely on Alt-Svc only.
proxyServer.EnableHttpsSvcbDnsDiscovery = true;

// Optional: override the OS-configured plain-UDP DNS server (best-effort default).
// proxyServer.DnsServerEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 53);
#pragma warning restore TWP001
```

When enabled, Auto-mode CONNECT/request paths never await DNS. A cache miss queues a single coalesced
background HTTPS RR lookup; if it finds ALPN `h3`, later connections to that origin use HTTP/3.
First-connection adoption also still comes from `Alt-Svc` on the first response.

**Behavior:**

- Queries are sent over UDP to `DnsServerEndPoint` with a 500 ms timeout.
- Positive results are cached with the record's TTL, capped at 1 hour.
- NXDOMAIN and NOERROR-without-`h3` are definitive negatives (1-minute TTL).
- SERVFAIL/REFUSED/timeouts/truncation use short transient suppression plus resolver backoff.
- Query coalescing: concurrent misses for the same origin share one in-flight DNS query.
- The resolver is pluggable: set `ProxyServer.HttpsSvcbResolver` to a custom `IHttpsSvcbResolver`
  implementation (e.g. for testing with pre-built responses).

> **Note:** Background SVCB discovery no longer adds DNS latency to page loads. Enable it when you
> want earlier H3 adoption than `Alt-Svc` alone. `DnsServerEndPoint` already defaults to the first
> usable OS-configured plain-UDP DNS server; set it explicitly only when you need a different resolver.
> Disable discovery if your DNS path cannot answer HTTPS RRs usefully.

## Protocol bridges

HTTP/3 participates in four of the seven translation pairs (plus native H3↔H3). The full client→origin
matrix, including TCP HTTP/1.1 ↔ HTTP/2 translation, is on
[Protocol Support — Protocol bridges](Protocol-Support#protocol-bridges).

HTTP/3-specific limits:

- **H2 → H3** is cold CONNECT only (`SendHttp2ToHttp3Bridge`). Mid-connection Alt-Svc upgrades on an
  existing H2↔H2 MITM relay are not taken.
- A configured upstream proxy cannot speak QUIC CONNECT or SOCKS UDP ASSOCIATE; those requests fall
  back to `ForwardOverTcpAsync`.
- Inbound HTTP/3: **UDP-only transparent** (`TransparentQuicProxyEndPoint`) or **dual-listen reverse**
  (`TransparentProxyEndPoint.EnableHttp3`). There is no explicit (system-proxy) QUIC endpoint.

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
- It advertises `SETTINGS_QPACK_MAX_TABLE_CAPACITY = 4096` and `SETTINGS_QPACK_BLOCKED_STREAMS = 0`
  to the client - the proxy never blocks a stream waiting for dynamic-table insertions.
- A `QpackDynamicTable` (thread-safe via `ReaderWriterLockSlim`) tracks inbound and outbound table entries
  as absolute indices, per RFC 9204 §3.
- Because `SETTINGS_QPACK_BLOCKED_STREAMS = 0` is advertised, decoding an incoming HEADERS block whose
  Required Insert Count has not yet been satisfied is a protocol violation by the peer rather than
  something the proxy waits out: the decoder immediately raises `QPACK_DECOMPRESSION_FAILED` and the
  connection is aborted, per RFC 9204 §2.1.2/§4.5.1.1. It never suspends decoding to wait for
  acknowledgment.
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
| `MaxInboundUnidirectionalStreams` | `3` | Minimum 3 (control + QPACK encoder/decoder); values below 3 are clamped to 3. |
| `HandshakeTimeout` | `30 s` | QUIC TLS handshake deadline. |
| `IdleTimeout` | `60 s` | Connection idle timeout. |
| `AdvertiseToHttpClients` | `false` | Documented for origin-upgrade scenarios; unused on UDP-only endpoints (no H1/H2 listen). Prefer dual-listen reverse below for client-facing Alt-Svc. |

## Dual-listen reverse HTTP/3 (`TransparentProxyEndPoint.EnableHttp3`)

When `EnableHttp3` is set on a TLS-terminating `TransparentProxyEndPoint` (and `ProxyServer.EnableHttp3`
is true), `Start()` binds TCP first, then a QUIC listener on the **same** port. H1/H2 responses from
that endpoint receive `Alt-Svc: h3=":PORT"; ma=86400` when the origin did not already send Alt-Svc.

| Property | Default | Description |
|----------|---------|-------------|
| `EnableHttp3` | `false` | Opt-in dual-listen reverse H3. Requires `DecryptSsl = true`. |
| `BeforeQuicAuthenticate` | — | Per-connection QUIC policy (same as transparent QUIC). |
| `MaxInboundBidirectionalStreams` | `100` | Per QUIC connection stream limit. |
| `HandshakeTimeout` / `IdleTimeout` | `30 s` / `60 s` | Same semantics as `TransparentQuicProxyEndPoint`. |

## Limitations

- **No explicit (system-proxy) inbound HTTP/3**: see
  [Why no explicit HTTP/3 endpoint yet](#why-no-explicit-http3-endpoint-yet) below.
  Reverse dual-listen and transparent UDP-only cover the supported inbound shapes.
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

- [Protocol Support](Protocol-Support) — feature matrix for all protocols, including [protocol bridges](Protocol-Support#protocol-bridges).
- [Home](Home) — general usage and the rest of the public API surface.
- RFC 9114 — HTTP/3
- RFC 9204 — QPACK
- RFC 9000 — QUIC
- RFC 7838 — Alt-Svc
