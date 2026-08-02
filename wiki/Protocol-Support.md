A feature-by-feature snapshot of what Titanium Web Proxy actually implements for HTTP/1.0, HTTP/1.1,
HTTP/2, and HTTP/3, so you can tell at a glance whether something you depend on is fully supported,
relayed best-effort, or not implemented yet. "Yes" means the proxy actively parses/enforces the feature
(and you can observe/modify it via the public API where relevant); "Partial" means it works for the common
case but has a known gap; "No" means it isn't implemented; "N/A" means the concept does not apply to that
protocol version.

This table reflects the `develop` branch as of the HTTP/1.x and HTTP/2 gap-closure work (chunked trailers,
interim 1xx responses, and TLS body-write-hook parity for HTTP/1.x; HPACK dynamic-table correctness/reuse,
HEADERS/CONTINUATION reassembly and re-splitting, trailers, interim 1xx responses, two-hop flow control,
SETTINGS/PING/GOAWAY handling, and synthetic-response API parity for HTTP/2), plus the subsequent protocol
policy and safety hardening work (HTTP/2 frame/header-list bounds, bounded body streaming, RFC 8441 WebSocket
over HTTP/2 including both the h2-client-to-h1-origin bridge and the native h2↔h2 tunnel, WebSocket frame
validation, Via header injection, multipart streaming, stacked Content-Encoding parsing, and authentication retry
bounds), and the HTTP/3 (QUIC) opt-in feature — including the six HTTP/3 gap-closure features: request-lifecycle
timing, 1xx interim response relay, per-chunk streaming body hooks (`OnRequestBodyWrite`/`OnResponseBodyWrite`),
upstream proxy chaining with TCP fallback, HTTPS/SVCB DNS discovery, and QPACK dynamic table (opt-in via
`ProxyServer.EnableQpackDynamicTable`). HTTP/2 has gone through a full regression pass and is now **on by
default** (`ProxyServer.EnableHttp2 = true`); set it to `false` to force HTTP/1.1 only. HTTP/3 is
`[Experimental("TWP001")]` and opt-in (`ProxyServer.EnableHttp3 = true`). If you find something inaccurate,
please open an issue.

## Connections and framing

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | HTTP/3 | Notes |
|---|---|---|---|---|---|
| Persistent connections / keep-alive | Yes | Yes | Yes (inherent) | Yes (inherent) | `Connection: keep-alive` (1.0) / default (1.1); HTTP/2 multiplexes over one connection; HTTP/3 QUIC connections persist across streams. |
| Chunked transfer-encoding | N/A (no chunked in 1.0) | Yes | N/A (HTTP/2 uses DATA frames, not chunking) | N/A (HTTP/3 uses DATA frames) | Read and write, both request and response, via `HttpStream`. |
| Chunked trailers (trailing headers) | N/A | Yes | Yes | Yes | See `RequestResponseBase.TrailingHeaders`; forwarded/emitted for HTTP/1.x. For HTTP/2, a second HEADERS block without request/status pseudo-headers is decoded as trailers. For HTTP/3, a trailing HEADERS frame after the final DATA frame is decoded as trailers per RFC 9114 §4.1. |
| `Expect: 100-continue` | Yes | Yes | N/A (no equivalent frame flow) | N/A | `ProxyServer.Enable100ContinueBehaviour`. Set `CompatibilityMode100Continue = true` to emit a synthetic `100 Continue` to clients that block on it when `Enable100ContinueBehaviour = false`. |
| Other 1xx interim responses (e.g. 103 Early Hints) | N/A | Yes | Yes | Yes | Relayed for all protocol versions. HTTP/3: `Http3OriginBridge` loops on 1xx responses from the origin (up to 20 per request) and forwards each interim HEADERS frame to the client before the final response. |
| HEADERS/CONTINUATION reassembly and re-splitting | N/A | N/A | Yes | N/A | HTTP/3 uses QPACK (no CONTINUATION frames); multi-frame header blocks do not exist in HTTP/3. |
| `Upgrade` / WebSocket (101 Switching Protocols) | N/A | Yes | N/A | N/A | HTTP/3 uses extended CONNECT (RFC 9220) for WebSocket — not yet implemented. |
| `CONNECT` tunneling | Yes | Yes | Yes (via `ExplicitProxyEndPoint`) | No | HTTP/3 transparent endpoint only; no explicit QUIC proxy yet (see [HTTP-3](HTTP-3) §Why no explicit endpoint). |
| ALPN-based protocol routing | N/A | N/A | Yes | Yes | HTTP/3 negotiates `h3` via QUIC TLS 1.3; the QUIC listener only accepts connections with `h3` ALPN. |
| Origin HTTP/3 discovery / Alt-Svc caching | N/A | N/A | N/A | Yes | `Http3OriginCapabilityCache` stores per-origin H3 capability with TTL from `Alt-Svc: h3=...; ma=...` headers; all protocols use the cache for origin selection when `EnableHttp3 = true`. |
| Stream multiplexing | N/A | N/A | Yes | Yes | HTTP/2: concurrent streams tracked per connection in `Http2Helper`. HTTP/3: QUIC bidirectional streams, one per request/response pair. |
| HPACK header compression | N/A | N/A | Yes | N/A | HTTP/3 uses QPACK (RFC 9204); see QPACK row in the HTTP/3 section below. |
| Flow control (`WINDOW_UPDATE`) | N/A | N/A | Yes | N/A | HTTP/3 relies on QUIC transport-layer flow control; there is no HTTP/3-layer `WINDOW_UPDATE` frame. |
| Server push (`PUSH_PROMISE`) | N/A | N/A | No | No | Disabled for HTTP/2 (`SETTINGS_ENABLE_PUSH=0`). Removed from HTTP/3 by RFC 9114. |
| `PING` / keepalive frames | N/A | N/A | Yes | N/A | HTTP/3 uses QUIC PING at the transport layer; there is no HTTP/3-layer PING frame. |
| `SETTINGS` negotiation | N/A | N/A | Yes | Yes | HTTP/3 SETTINGS are sent on the outbound unidirectional control stream (RFC 9114 §7.2.4). |
| `RST_STREAM` / per-stream cancellation | N/A | N/A | Yes | Yes | HTTP/3 uses QUIC `RESET_STREAM`/`STOP_SENDING`; stream-level cancellation cleans up session state and guarantees exactly one `AfterResponse`. |
| `GOAWAY` / connection shutdown | N/A | N/A | Yes | Yes | HTTP/3 GOAWAY carries the last stream ID; `Http3Connection` sends and handles GOAWAY for graceful shutdown. |
| `MAX_CONCURRENT_STREAMS` admission | N/A | N/A | Yes | Yes | HTTP/3: `TransparentQuicProxyEndPoint.MaxInboundBidirectionalStreams` (default 100) limits concurrent request streams per QUIC connection. |
| Flow-control reservation timeout | N/A | N/A | Yes | N/A | HTTP/3 relies on QUIC's own idle-timeout and flow-control mechanisms. |

## Body handling and streaming

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | HTTP/3 | Notes |
|---|---|---|---|---|---|
| Buffered body read/modify (`GetRequestBody`/`SetResponseBodyString`, etc.) | Yes | Yes | Yes | Yes | Body bytes buffered up to `MaxBufferedBodyBytes` (default 4 MiB); larger bodies are rejected. |
| Per-chunk streaming hooks (`OnRequestBodyWrite`/`OnResponseBodyWrite`) - plain HTTP | Yes | Yes | Yes | N/A | HTTP/3 is always TLS-encrypted (QUIC mandates TLS 1.3). |
| Per-chunk streaming hooks - TLS-decrypted connections | Yes | Yes | Yes | Yes | `Http3RequestStream` fires `OnRequestBodyWrite` and `Http3OriginBridge` fires `OnResponseBodyWrite` for each DATA frame, using a one-frame lookahead so `IsLastChunk` is accurate. The fast path (no subscribers) skips all hook allocations. |
| Synthetic streamed responses (`RespondStreaming`) | Yes | Yes | Yes | Yes | Chunked or fixed-length framing chosen automatically from the response headers you set. |
| Automatic decompression for body inspection (gzip/deflate/brotli) | Yes | Yes | Yes | Yes | Stacked encodings (e.g. `gzip, deflate`) are unwrapped layer-by-layer. |
| Multipart/form-data boundary-aware streaming | Yes | Yes | Yes | Yes | Multipart request bodies are observed incrementally without buffering the full body. |
| Bounded body streaming | Yes | Yes | Yes | Yes | `BoundedBodyPipe` wraps the underlying pipe; reads beyond `MaxBufferedBodyBytes` fail fast rather than OOM-ing the process. |

## Interception APIs

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | HTTP/3 | Notes |
|---|---|---|---|---|---|
| Header/body modification in `BeforeRequest`/`BeforeResponse` | Yes | Yes | Yes | Yes | Every HEADERS block is fully decoded/transcoded (HPACK for h2, QPACK for h3), so mutations made in the event handler are re-encoded and relayed rather than passed through opaquely. |
| Synthetic responses (`Ok`, `Respond`, `Redirect`, `GenericResponse`) from `BeforeRequest` | Yes | Yes | Yes | Yes | The request is never forwarded upstream; any unfinished client request body already in flight is drained. |
| `Respond` replacing an already-received response from `BeforeResponse` | Yes | Yes | Yes | Yes | The origin's own response body, if still arriving, is discarded in favor of the replacement. |
| `RespondStreaming` (synthetic streamed body) | Yes | Yes | Yes | Yes | See "Synthetic streamed responses" above for framing details. |
| `AfterResponse` / per-request disposal | Yes | Yes | Yes | Yes | Every HTTP/3 stream — whether it completes normally, is reset, or is still open when the QUIC connection tears down — gets exactly one `AfterResponse` invocation and one `SessionEventArgs.Dispose()`, guaranteed by an atomic `FinalizedFlag`. |
| `SessionEventArgsBase.Timing` (`HttpRequestTiming`) milestones | Yes | Yes | Yes | Yes | Allocated only when `EnableRequestTimingCapture` is true. All five milestones are stamped for HTTP/3: `RequestHeadersReceivedAt` (after QPACK decode), `ConnectionReadyAt` (pool lease, with `UpstreamConnectionReused` flag), `RequestSentAt` (after `CompleteWrites`), `ResponseHeadersReceivedAt` (final non-1xx response), and `CompletedAt` (always, even on error). |

## HTTP/2 safety and frame validation

| Feature | HTTP/2 | Notes |
|---|---|---|
| Frame size bounds | Yes | Frames exceeding the negotiated `SETTINGS_MAX_FRAME_SIZE` (or the hard cap) are rejected with a `FRAME_SIZE_ERROR` connection error. |
| SETTINGS parameter validation | Yes | Unknown parameters are silently ignored per RFC 7540 §6.5; out-of-range values for known parameters (`INITIAL_WINDOW_SIZE`, `MAX_FRAME_SIZE`, `HEADER_TABLE_SIZE`) trigger a `PROTOCOL_ERROR`. |
| CONTINUATION frame safety | Yes | A HEADERS frame with `END_HEADERS` clear must be followed by CONTINUATION frames on the same stream; any intervening frame triggers a `PROTOCOL_ERROR` connection error, closing the connection. |
| Decoded header list size limit | Yes | HPACK-decoded header list bytes (name + value + 32-byte overhead per entry, per RFC 7541 §4.1) that exceed `MaxDecodedHeaderListBytes` (default 64 KiB) cause the stream to be reset with `RST_STREAM(ENHANCE_YOUR_CALM)` rather than forwarded. |
| RFC 8441 WebSocket over HTTP/2 (extended CONNECT) | Yes | `EnableRfc8441 = true` enables extended-CONNECT negotiation. Both tunnel paths are fully implemented: **h2-client→h1-origin bridge** – the proxy validates required pseudo-headers, opens an HTTP/1.1 origin connection, performs the WebSocket upgrade handshake, preserves negotiated subprotocol/extensions, and relays DATA frames bidirectionally through bounded per-stream buffers; **native h2↔h2 tunnel** – when the origin advertises `SETTINGS_ENABLE_CONNECT_PROTOCOL=1`, the proxy forwards the extended CONNECT HEADERS decoded and re-encoded (preserving `:protocol`, `:authority`, `:scheme`, `:path`, and all application headers), marks the stream established on a final 2xx response, then raw-relays DATA frames for both directions without HTTP body buffering; `DataSent`/`DataReceived` events still fire with the unpadded tunnel payload. Per-leg SETTINGS negotiation is independent: the client's and origin's `ENABLE_CONNECT_PROTOCOL` preferences are never cross-forwarded; the proxy intercepts and independently decides what to advertise to each leg. Invalid `SETTINGS_ENABLE_CONNECT_PROTOCOL` values and the forbidden 1→0 downgrade are each connection-level `GOAWAY(PROTOCOL_ERROR)` errors. An origin that does not advertise the setting causes the affected stream to be reset with `REFUSED_STREAM` rather than forwarding malformed HEADERS. `Request.ExtendedConnectProtocol` exposes the `:protocol` token to `BeforeRequest` handlers; `Request.UpgradeToWebSocket` returns `true` for both RFC 8441 and HTTP/1.1 WebSocket upgrades. Calling `GetRequestBody`/`GetResponseBody` on an established extended CONNECT stream throws `InvalidOperationException` (these are unbounded duplex streams, not finite HTTP bodies). Post-establishment HEADERS/trailers on the tunnel stream are rejected with `RST_STREAM(PROTOCOL_ERROR)`. DATA combined with `END_STREAM`, independent per-direction half-closes, resets, and connection shutdown are handled without dropping payloads or leaking tunnel work. Only the `websocket` protocol token is implemented; unsupported `:protocol` values return `501 Not Implemented` after running `BeforeRequest` (allowing handlers to synthesize their own response). Extended CONNECT inherits the existing explicit-proxy `Via` header policy. |

## WebSocket safety

| Feature | Support | Notes |
|---|---|---|
| Reserved opcode rejection | Yes | WebSocket frames with opcodes not defined by RFC 6455 (or not enabled by a negotiated extension) are rejected by closing the WebSocket with status `1002 Protocol Error`. |
| Control frame size / fragmentation | Yes | Control frames (Close, Ping, Pong) exceeding 125 bytes or marked as fragmented are rejected per RFC 6455 §5.5. |
| Extension bit stripping | Yes | RSV1/RSV2/RSV3 bits set without a corresponding negotiated extension trigger frame rejection; extension bits are stripped from relayed frames. |
| Maximum frame payload size | Yes | The declared payload length is validated against `MaxWebSocketFramePayloadBytes` (default 16 MiB) *before* any payload is buffered — frames exceeding it are rejected with a `1009 Message Too Big` close, without ever allocating the oversized buffer. Reserved bits in a 64-bit declared length, and lengths exceeding `int.MaxValue`, are rejected the same way. |

## Proxying, auth, and misc

| Feature | Support (H1.0/H1.1/H2) | HTTP/3 | Notes |
|---|---|---|---|
| Explicit, transparent, and SOCKS4/5 endpoints | Yes | Yes (`TransparentQuicProxyEndPoint`) | H1/H2: `ExplicitProxyEndPoint`, `TransparentProxyEndPoint`, `SocksProxyEndPoint`. HTTP/3 adds `TransparentQuicProxyEndPoint` (UDP/QUIC only; no explicit QUIC proxy yet). |
| Upstream proxy chaining (HTTP/HTTPS/SOCKS) | Yes | Yes | H1/H2: static, per-request (`GetCustomUpStreamProxyFunc`), or system-gateway detection. HTTP/3: `Http3OriginBridge` mirrors the full upstream-proxy resolution logic (static → per-request callback → system gateway). Because `System.Net.Quic` does not support HTTP CONNECT tunnelling or SOCKS5 UDP ASSOCIATE, a configured upstream proxy causes `QuicProxyNotSupportedException` to be thrown and the request automatically falls back to `ForwardOverTcpAsync` where standard proxy rules apply. |
| Proxy Basic authentication | Yes | No | Not implemented for QUIC endpoints. |
| Windows authentication (Kerberos/NTLM) to upstream servers | Yes (h1.1); not applicable to h2 | N/A | NTLM/Negotiate is connection-oriented; RFC 7540 §9.2.3 excludes it from HTTP/2. HTTP/3 shares the same exclusion and additionally runs over QUIC where connection-oriented auth is impractical. |
| Mutual TLS to upstream servers | Yes (all TCP protocols) | Yes | `ServerCertificateValidationCallback` fires for QUIC origin connections via `QuicConnectionFactory`; QUIC always uses TLS 1.3 so certificate validation is always active. |
| Upstream connection pooling | Yes | Yes | H1/H2: `TcpConnectionPool`. HTTP/3: `QuicConnectionPool` leases live `QuicConnection` objects per origin; pool is drained on `ProxyServer.Stop()`. |
| Per-connection upstream HTTP version policy | Yes | Yes | H1/H2: `UpstreamHttpProtocol`/`AllowHttpProtocolTranslation` on `BeforeSslAuthenticateEventArgs`. HTTP/3: `BeforeQuicAuthenticateEventArgs.UpstreamHttpProtocol` (per-connection) and `SessionEventArgs.UpstreamHttpProtocol` (per-stream override). |
| `BeforeQuicAuthenticate` connection event | N/A | Yes | Fired once per accepted QUIC connection (analogous to `BeforeSslAuthenticate` for TCP); allows per-connection protocol pinning, custom cert validation, and connection rejection. |
| Origin HTTP/1.0 request-version normalization | Yes | N/A | `ProxyServer.OriginHttpVersionPolicy`; not applicable when the origin connection is HTTP/3. |
| `Via` header injection | Yes | Yes | `ViaHeaderPseudonym` defaults to `"titanium-web-proxy"`, appending `Via: {version} {pseudonym}` for all protocol versions including HTTP/3 (version token `"3.0"`). Loop detection checks across all `Via` fields and refuses with `508 Loop Detected`. |
| Authentication retry bounds | Yes | N/A | NTLM/Negotiate 407/401 loops are capped at 3 round-trips for TCP protocols; not applicable to HTTP/3. |

## HTTP/3 (QUIC) — opt-in, experimental

HTTP/3 support is gated behind `ProxyServer.EnableHttp3 = true` (marked `[Experimental("TWP001")]`).
It requires the MsQuic native library and `System.Net.Quic.QuicListener.IsSupported == true` at runtime
(available on Windows 11/Server 2022+ and Linux with a recent `libmsquic` package; on macOS, bundle
`libmsquic`, `libssl`, and `libcrypto` with `@loader_path` RPATH — see [HTTP-3](HTTP-3) for details).

| Feature | Support | Notes |
|---------|---------|-------|
| Inbound HTTP/3 (client → proxy over QUIC) | Yes | `TransparentQuicProxyEndPoint` — QUIC only; explicit QUIC proxying is not yet standardised. |
| QPACK header compression | Yes | **Static table** (always active): RFC 9204 static-table indexed encoding and literal encoding. **Dynamic table** (opt-in): set `ProxyServer.EnableQpackDynamicTable = true` to enable RFC 9204 §3 dynamic table synchronisation. When enabled, the proxy advertises `SETTINGS_QPACK_MAX_TABLE_CAPACITY = 4096` and `SETTINGS_QPACK_BLOCKED_STREAMS = 0` to the client (it never blocks a stream waiting on dynamic-table insertions); opens the QPACK encoder/decoder unidirectional control streams; tracks per-connection inbound and outbound tables in `QpackDynamicTable` (thread-safe via `ReaderWriterLockSlim`); immediately raises `QPACK_DECOMPRESSION_FAILED` per RFC 9204 §4.5.1.1 if a field section's Required Insert Count is not yet satisfied, rather than waiting for it; and sends Section Acknowledgments on a bounded `Channel` (capacity 1000, `DropNewest` on full). In-flight eviction protection prevents removing a table entry while any open stream holds a reference to it. |
| HTTP/3 frame codec | Yes | HEADERS, DATA, SETTINGS, GOAWAY, and unknown/reserved frame types per RFC 9114. |
| Per-stream request/response lifecycle | Yes | `BeforeRequest`, `BeforeResponse`, `AfterResponse` (exactly once per stream), `Via` header injection, per-stream `UpstreamHttpProtocol` override, `ConnectTimeout` override. |
| Outbound H3→H3 (proxy → origin over QUIC) | Yes | `QuicConnectionPool` leases a live `QuicConnection` per origin; streams are opened per-request. |
| Outbound H3→H2 bridge | Yes | Falls through to `TcpConnectionFactory` with h2 ALPN when `UpstreamHttpProtocol.Http2` is set. |
| Outbound H3→H1.1 bridge | Yes | Falls through to `TcpConnectionFactory` with default ALPN negotiation. |
| Inbound H1.1/H2 → H3 origin bridge | Yes | `RequestHandler` checks `Http3OriginCapabilityCache` before opening a TCP connection; if H3 is cached, `Http3OriginBridge.ForwardAsync` is used instead. |
| Alt-Svc discovery | Yes | Response `Alt-Svc: h3=":443"; ma=86400` headers are parsed and cached in `Http3OriginCapabilityCache` with the advertised max-age TTL, enabling proactive H3 reuse on subsequent requests. |
| HTTPS/SVCB DNS discovery | Yes | Enable with `ProxyServer.EnableHttpsSvcbDnsDiscovery = true` (experimental; defaults to on when `EnableHttp3` is on). Auto-mode discovery is **background-only**: CONNECT/request paths consult `Http3OriginCapabilityCache` and never await DNS. `UdpSvcbDnsResolver` performs RFC 9460 HTTPS RR queries over UDP to `DnsServerEndPoint` (default: first usable OS-configured plain-UDP DNS server; never a public third-party fallback). NXDOMAIN / NOERROR-without-h3 are definitive negatives; SERVFAIL/REFUSED/timeouts use short transient backoff. |
| `BeforeQuicAuthenticate` event | Yes | Fired once per accepted QUIC connection (analogous to `BeforeSslAuthenticate`); allows setting `UpstreamHttpProtocol`, custom cert validation, and `AllowHttpProtocolTranslation` per connection. |
| `IOriginalDestinationResolver` | Yes | Plug-in interface for resolving the pre-NAT (real) destination of a transparently intercepted QUIC connection. |
| QUIC connection pool drain on stop | Yes | `QuicConnectionPool.DrainAsync` is called during `ProxyServer.Stop`/`DrainAsync`/`Dispose`. |
| QUIC control stream (server→client) | Yes | `Http3Connection` sends its own outbound control stream with a SETTINGS frame on startup. |
| Server push | No | Not defined in RFC 9114 (server push was removed from HTTP/3). |
| 0-RTT / early data | No | Not supported by `System.Net.Quic`; all connections start with a 1-RTT handshake. |
| QUIC connection migration | No | `System.Net.Quic` does not expose migration APIs in .NET 10. |

| Property | Default | Description |
|----------|---------|-------------|
| `MaxDecodedHeaderListBytes` | 65,536 (64 KiB) | Maximum HTTP/2 decoded header list size (RFC 7541 §4.1 accounting: name + value + 32 bytes per entry). Streams exceeding this are reset with `RST_STREAM(ENHANCE_YOUR_CALM)`. |
| `MaxBufferedBodyBytes` | 4,194,304 (4 MiB) | Maximum body bytes buffered for body-read hooks, authentication retry, and related features. Reads beyond this limit fail fast. |
| `MaxWebSocketFramePayloadBytes` | 16,777,216 (16 MiB) | Maximum WebSocket frame payload size in intercepted sessions. Frames exceeding this are rejected. |
| `ViaHeaderPseudonym` | `"titanium-web-proxy"` | Token appended to `Via` headers on forwarded requests and responses. Set to an empty string to disable. Loop detection rejects incoming requests whose `Via` already contains this token with `508 Loop Detected`. |
| `CompatibilityMode100Continue` | `false` | Sends a synthetic `100 Continue` to the client before reading the request body when `Enable100ContinueBehaviour = false`, preventing deadlock with strict `Expect: 100-continue` clients. |
| `EnableRfc8441` | `false` | Enables WebSocket over HTTP/2 extended CONNECT negotiation (RFC 8441). When enabled, the proxy advertises `ENABLE_CONNECT_PROTOCOL=1` to h2 clients. If the origin is HTTP/2 and advertises `ENABLE_CONNECT_PROTOCOL=1`, the proxy uses the native h2↔h2 DATA relay; if the origin is HTTP/2 and does not, the stream is reset with `REFUSED_STREAM`. If the origin is HTTP/1.1, the h2→h1 WebSocket upgrade bridge is used. |
| `EnableHttp3` | `false` | Enables HTTP/3 (QUIC) support (opt-in, experimental — suppress `TWP001`). See [HTTP-3](HTTP-3) wiki page for full details. |
| `EnableQpackDynamicTable` | `false` | Enables QPACK dynamic table synchronisation per RFC 9204. Requires `EnableHttp3 = true`. See [HTTP-3](HTTP-3) for details. |
| `EnableHttpsSvcbDnsDiscovery` | inherits `EnableHttp3` | Enables proactive HTTP/3 capability discovery via HTTPS/SVCB DNS queries (RFC 9460). Background-only in Auto mode; first-connection H3 adoption otherwise comes from `Alt-Svc`. |
| `DnsServerEndPoint` | OS-configured UDP DNS (best-effort) | UDP endpoint for HTTPS/SVCB queries. Does not honor NRPT/DoH/VPN split-DNS; assign explicitly to override. When none is discoverable, proactive discovery is skipped. |

## Where to look for more detail

- [Streaming Bodies](Streaming-Bodies) - the `OnRequestBodyWrite`/`OnResponseBodyWrite`/`RespondStreaming` APIs in depth.
- [HTTP/3](HTTP-3) - HTTP/3 and QUIC support, `TransparentQuicProxyEndPoint`, protocol bridges, and `EnableHttp3`.
- [Home](Home) - general usage and the rest of the public API surface.
