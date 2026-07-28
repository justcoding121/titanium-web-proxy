A feature-by-feature snapshot of what Titanium Web Proxy actually implements for HTTP/1.0, HTTP/1.1, and
HTTP/2, so you can tell at a glance whether something you depend on is fully supported, relayed
best-effort, or not implemented yet. "Yes" means the proxy actively parses/enforces the feature (and you
can observe/modify it via the public API where relevant); "Partial" means it works for the common case but
has a known gap; "No" means it isn't implemented.

This table reflects the `develop` branch as of the HTTP/1.x and HTTP/2 gap-closure work (chunked trailers,
interim 1xx responses, and TLS body-write-hook parity for HTTP/1.x; HPACK dynamic-table correctness/reuse,
HEADERS/CONTINUATION reassembly and re-splitting, trailers, interim 1xx responses, two-hop flow control,
SETTINGS/PING/GOAWAY handling, and synthetic-response API parity for HTTP/2), plus the subsequent protocol
policy and safety hardening work (HTTP/2 frame/header-list bounds, bounded body streaming, RFC 8441 WebSocket
over HTTP/2 including both the h2-client-to-h1-origin bridge and the native h2↔h2 tunnel, WebSocket frame
validation, Via header injection, multipart streaming, stacked Content-Encoding parsing, and authentication retry
bounds). HTTP/2 has gone through a full regression pass and is now **on by default** (`ProxyServer.EnableHttp2 =
true`); set it to `false` to force HTTP/1.1 only. If you find something inaccurate, please open an issue.

## Connections and framing

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | Notes |
|---|---|---|---|---|
| Persistent connections / keep-alive | Yes | Yes | Yes (inherent) | `Connection: keep-alive` (1.0) / default (1.1); HTTP/2 multiplexes over one connection. |
| Chunked transfer-encoding | N/A (no chunked in 1.0) | Yes | N/A (HTTP/2 uses DATA frames, not chunking) | Read and write, both request and response, via `HttpStream`. |
| Chunked trailers (trailing headers) | N/A | Yes | Yes | See `RequestResponseBase.TrailingHeaders`; forwarded/emitted for HTTP/1.x. For HTTP/2, a second HEADERS block without request/status pseudo-headers is decoded as trailers and re-encoded/relayed without re-firing `BeforeRequest`/`BeforeResponse`. |
| `Expect: 100-continue` | Yes | Yes | N/A (no equivalent frame flow) | `ProxyServer.Enable100ContinueBehaviour`. Set `CompatibilityMode100Continue = true` to emit a synthetic `100 Continue` to clients that block on it when `Enable100ContinueBehaviour = false`. |
| Other 1xx interim responses (e.g. 103 Early Hints) | N/A | Yes | Yes | Relayed to the client (looping past on the HTTP/1.x server connection, or on their own HEADERS frame for HTTP/2) without invoking `BeforeResponse`/locking the final `Response`; not yet exposed as a dedicated event. |
| HEADERS/CONTINUATION reassembly and re-splitting | N/A | N/A | Yes | Multi-frame inbound header blocks are reassembled before HPACK decoding; outbound blocks larger than the peer's `SETTINGS_MAX_FRAME_SIZE` are split back across HEADERS + CONTINUATION. |
| `Upgrade` / WebSocket (101 Switching Protocols) | N/A | Yes | N/A | Raw duplex relay once upgraded; see `RequestHandler.HandleWebSocketUpgrade`. |
| `CONNECT` tunneling | Yes | Yes | Yes (via `ExplicitProxyEndPoint`) | Supports both decrypt-and-inspect and pass-through-as-opaque-tunnel. |
| ALPN-based protocol routing | N/A | N/A | Yes | A decrypted tunnel (`ExplicitProxyEndPoint` CONNECT) or a decrypted transparent TLS connection (`TransparentProxyEndPoint`) is only routed to the HTTP/2 relay when the TLS handshake actually negotiated `h2` via ALPN; an HTTP/2 connection-preface (`PRI * HTTP/2.0...`) received on a connection that negotiated `http/1.1` (or no ALPN at all - including cleartext h2c, not implemented) is rejected as a protocol violation rather than opportunistically switching protocols after the fact. |
| Origin HTTP/2 discovery/prefetch connection ownership | N/A | N/A | Yes | Deciding whether an origin supports HTTP/2 (needed before the client TLS handshake, since ALPN cannot change afterward) and starting a connection ahead of time while the client handshake is still in progress are coordinated through one shared negotiation step - shared by explicit CONNECT tunnels and transparent TLS connections alike, so transparent mode carries none of the duplicated logic or extra origin connections a separate implementation would add: a cold capability-cache discovery connection is retained and adopted directly as the session connection once it is confirmed healthy and correctly keyed, and a cache-hit prefetch connection is adopted the same way, rather than either being opened, validated, and then wastefully discarded in favor of a brand-new connection. A stale, mismatched, or broken retained connection is still released and replaced with a fresh one. For transparent connections, the capability/pool cache key is derived from the identity (SNI/certificate hostname) plus the effective forward target (`BeforeSslAuthenticateEventArgs.ForwardHttpsHostName`/`ForwardHttpsPort`, defaulting from `TransparentProxyEndPoint.ForwardHost`/`ForwardPort`), so different forward targets behind the same SNI never share a cached capability result. |
| Stream multiplexing | N/A | N/A | Yes | Concurrent streams tracked per connection in `Http2Helper`; a slow synthetic response on one stream no longer blocks frames for other streams. |
| HPACK header compression | N/A | N/A | Yes | Both decode and encode reuse a persistent, connection-direction-scoped dynamic table, so repeated headers are indexed/re-indexed correctly on both sides. |
| Flow control (`WINDOW_UPDATE`) | N/A | N/A | Yes | Independent send-side window accounting per connection and per stream on each of the two TLS legs (client↔proxy, proxy↔server); validates length/increment/overflow and translates rather than blindly relays. |
| Server push (`PUSH_PROMISE`) | N/A | N/A | No | `SETTINGS_ENABLE_PUSH=0` is always forced toward the origin (overriding/appending it in the relayed client SETTINGS frame), and any PUSH_PROMISE received anyway (from either peer) is treated as a connection-level `PROTOCOL_ERROR` (`GOAWAY`) rather than decoded, relayed, or silently forwarded. Push is deprecated in browsers, so no public API originates one. |
| `PING` / keepalive frames | N/A | N/A | Yes | ACKed locally by the proxy on each leg; not blindly relayed. |
| `SETTINGS` negotiation | N/A | N/A | Yes | `HEADER_TABLE_SIZE`, `MAX_FRAME_SIZE`, `INITIAL_WINDOW_SIZE`, and `MAX_CONCURRENT_STREAMS` are validated/tracked and applied per leg. The proxy relays each peer's own SETTINGS frame to the other leg (rather than generating a fully independent proxy-authored SETTINGS frame per leg) except for the forced `ENABLE_PUSH=0` override above; the proxy also enforces that the first frame after the connection preface is SETTINGS on both legs, and that client-initiated stream ids are odd and strictly increasing (RFC 7540 §5.1.1/§3.5), rejecting violations with `GOAWAY`. |
| `RST_STREAM` / per-stream cancellation | N/A | N/A | Yes | Cleans up the corresponding session/flow-control state, relays the error code, and still runs exactly one `AfterResponse`/dispose for the affected stream (see below). |
| `GOAWAY` / connection shutdown | N/A | N/A | Yes | Parses `lastStreamId`/error code, stops admitting new streams above it, cancels in-flight work that can't complete, relays to the other leg, and the proxy itself sends a best-effort graceful `GOAWAY` to the other leg when either side of the relay disconnects. |
| `MAX_CONCURRENT_STREAMS` admission | N/A | N/A | Yes | A new client-initiated stream that would exceed the origin's advertised limit is refused locally (`RST_STREAM`/`REFUSED_STREAM`) rather than forwarded. |
| Flow-control reservation timeout | N/A | N/A | Yes | An outbound `DATA` write that cannot obtain flow-control credit within 60 seconds (peer stopped sending `WINDOW_UPDATE`) fails the stream instead of waiting indefinitely. |

## Body handling and streaming

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | Notes |
|---|---|---|---|---|
| Buffered body read/modify (`GetRequestBody`/`SetResponseBodyString`, etc.) | Yes | Yes | Yes | Body bytes buffered up to `MaxBufferedBodyBytes` (default 4 MiB); larger bodies are rejected. |
| Per-chunk streaming hooks (`OnRequestBodyWrite`/`OnResponseBodyWrite`) - plain HTTP | Yes | Yes | Yes | |
| Per-chunk streaming hooks - TLS-decrypted connections | Yes | Yes | Yes | Fixed for HTTP/1.x: the hook now fires with parity for `SslStream`-backed connections, not just plain `NetworkStream`. |
| Synthetic streamed responses (`RespondStreaming`) | Yes | Yes | Yes | Chunked or fixed-length framing chosen automatically from the response headers you set. |
| Automatic decompression for body inspection (gzip/deflate/brotli) | Yes | Yes | Yes | Stacked encodings (e.g. `gzip, deflate`) are unwrapped layer-by-layer. |
| Multipart/form-data boundary-aware streaming | Yes | Yes | Yes | Multipart request bodies are observed incrementally without buffering the full body. `MultipartRequestPartSent` receives each part's MIME headers even when boundaries or headers span HTTP/2 DATA frames; standard initial delimiters and subsequent CRLF-prefixed delimiters are distinguished from boundary-like bytes inside part content. |
| Bounded body streaming | Yes | Yes | Yes | `BoundedBodyPipe` wraps the underlying pipe; reads beyond `MaxBufferedBodyBytes` fail fast rather than OOM-ing the process. |

## Interception APIs

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | Notes |
|---|---|---|---|---|
| Header/body modification in `BeforeRequest`/`BeforeResponse` | Yes | Yes | Yes | Every HEADERS block is fully decoded/transcoded, so mutations made in the event handler are re-encoded and relayed rather than passed through opaquely. |
| Synthetic responses (`Ok`, `Respond`, `Redirect`, `GenericResponse`) from `BeforeRequest` | Yes | Yes | Yes | The request is never forwarded upstream; any unfinished client request body already in flight is drained with flow-control credit returned. |
| `Respond` replacing an already-received response from `BeforeResponse` | Yes | Yes | Yes | The origin's own response body, if still arriving, is discarded (with flow-control credit still returned) in favor of the replacement. |
| `RespondStreaming` (synthetic streamed body) | Yes | Yes | Yes | See "Synthetic streamed responses" above for framing details. |
| `AfterResponse` / per-request disposal | Yes | Yes | Yes | Every h2 stream - whether it completes normally, is reset (`RST_STREAM`), or is still open when the connection itself tears down - gets exactly one `AfterResponse` invocation and one `SessionEventArgs.Dispose()`, matching the HTTP/1.x `finally` guarantee. Request-header preparation (`Accept-Encoding` filtering, hop-by-hop header stripping) also now runs for h2 requests before they are forwarded upstream, matching HTTP/1.x. |
| `SessionEventArgs.TimeLine` milestones | Yes | Yes | Yes | `Http2Helper` now stamps `"Request Sent"`, `"Response Received"`, and `"Response Sent"` at the same logical points in the h2 stream lifecycle (terminating HEADERS or DATA frame observed on each direction) as `RequestHandler`/`ResponseHandler` do for HTTP/1.x, so latency diagnostics built on `TimeLine` behave the same regardless of which protocol a session actually used. |

## HTTP/2 safety and frame validation

| Feature | HTTP/2 | Notes |
|---|---|---|
| Frame size bounds | Yes | Frames exceeding the negotiated `SETTINGS_MAX_FRAME_SIZE` (or the hard cap) are rejected with a `FRAME_SIZE_ERROR` connection error. |
| SETTINGS parameter validation | Yes | Unknown parameters are silently ignored per RFC 7540 §6.5; out-of-range values for known parameters (`INITIAL_WINDOW_SIZE`, `MAX_FRAME_SIZE`, `HEADER_TABLE_SIZE`) trigger a `PROTOCOL_ERROR`. |
| CONTINUATION frame safety | Yes | A HEADERS frame with `END_HEADERS` clear must be followed by CONTINUATION frames on the same stream; any intervening frame triggers a `PROTOCOL_ERROR` connection error, closing the connection. |
| Decoded header list size limit | Yes | HPACK-decoded header list bytes (name + value + 32-byte overhead per entry, per RFC 7541 §4.1) that exceed `MaxDecodedHeaderListBytes` (default 64 KiB) cause the stream to be reset with `RST_STREAM(ENHANCE_YOUR_CALM)` rather than forwarded. |
| RFC 8441 WebSocket over HTTP/2 (extended CONNECT) | Yes | `EnableRfc8441 = true` enables extended-CONNECT negotiation. Both tunnel paths are fully implemented: **h2-client→h1-origin bridge** – the proxy validates required pseudo-headers, opens an HTTP/1.1 origin connection, performs the WebSocket upgrade handshake, preserves negotiated subprotocol/extensions, and relays DATA frames bidirectionally through bounded per-stream buffers; **native h2↔h2 tunnel** – when the origin advertises `SETTINGS_ENABLE_CONNECT_PROTOCOL=1`, the proxy forwards the extended CONNECT HEADERS decoded and re-encoded (preserving `:protocol`, `:authority`, `:scheme`, `:path`, and all application headers), marks the stream established on a final 2xx response, then raw-relays DATA frames for both directions without HTTP body buffering; `OnDataSent`/`OnDataReceived` events still fire with the unpadded tunnel payload. Per-leg SETTINGS negotiation is independent: the client's and origin's `ENABLE_CONNECT_PROTOCOL` preferences are never cross-forwarded; the proxy intercepts and independently decides what to advertise to each leg. Invalid `SETTINGS_ENABLE_CONNECT_PROTOCOL` values and the forbidden 1→0 downgrade are each connection-level `GOAWAY(PROTOCOL_ERROR)` errors. An origin that does not advertise the setting causes the affected stream to be reset with `REFUSED_STREAM` rather than forwarding malformed HEADERS. `Request.ExtendedConnectProtocol` exposes the `:protocol` token to `BeforeRequest` handlers; `Request.UpgradeToWebSocket` returns `true` for both RFC 8441 and HTTP/1.1 WebSocket upgrades. Calling `GetRequestBody`/`GetResponseBody` on an established extended CONNECT stream throws `InvalidOperationException` (these are unbounded duplex streams, not finite HTTP bodies). Post-establishment HEADERS/trailers on the tunnel stream are rejected with `RST_STREAM(PROTOCOL_ERROR)`. DATA combined with `END_STREAM`, independent per-direction half-closes, resets, and connection shutdown are handled without dropping payloads or leaking tunnel work. Only the `websocket` protocol token is implemented; unsupported `:protocol` values return `501 Not Implemented` after running `BeforeRequest` (allowing handlers to synthesize their own response). Extended CONNECT inherits the existing explicit-proxy `Via` header policy. |

## WebSocket safety

| Feature | Support | Notes |
|---|---|---|
| Reserved opcode rejection | Yes | WebSocket frames with opcodes not defined by RFC 6455 (or not enabled by a negotiated extension) are rejected by closing the WebSocket with status `1002 Protocol Error`. |
| Control frame size / fragmentation | Yes | Control frames (Close, Ping, Pong) exceeding 125 bytes or marked as fragmented are rejected per RFC 6455 §5.5. |
| Extension bit stripping | Yes | RSV1/RSV2/RSV3 bits set without a corresponding negotiated extension trigger frame rejection; extension bits are stripped from relayed frames. |
| Maximum frame payload size | Yes | Frames exceeding `MaxWebSocketFramePayloadBytes` (default 16 MiB) are rejected to prevent unbounded memory allocation. |

## Proxying, auth, and misc

| Feature | Support | Notes |
|---|---|---|
| Explicit, transparent, and SOCKS4/5 endpoints | Yes | `ExplicitProxyEndPoint`, `TransparentProxyEndPoint`, `SocksProxyEndPoint`. |
| Upstream proxy chaining (HTTP/HTTPS/SOCKS) | Yes | Static, per-request (`GetCustomUpStreamProxyFunc`), or system-gateway detection. |
| Proxy Basic authentication | Yes | `ProxyBasicAuthenticateFunc`. |
| Windows authentication (Kerberos/NTLM) to upstream servers | Yes (h1.1); not applicable to h2 | `EnableWinAuth`. NTLM/Negotiate is connection-oriented, per-TCP-connection state; RFC 7540 §9.2.3 (HTTP/2) states such schemes "cannot be used with the connection reuse in HTTP/2" and are expected not to be offered by compliant h2 origins, so `Http2Helper` does not implement a WinAuth handshake loop. Origin connections are still never returned to the pool once `IsWinAuthenticated` is set (see `TcpConnectionFactory.Release`), which already applies uniformly regardless of which protocol ends up negotiated on that connection. |
| Mutual TLS to upstream servers | Yes (all protocols) | `ClientCertificateSelectionCallback` / `ServerCertificateValidationCallback`. Client-certificate selection happens during `SslStream.AuthenticateAsClientAsync`, before ALPN is resolved, so it is shared unchanged by the h1.1 and h2 code paths - there is no protocol-specific origin-connection setup to duplicate. |
| Upstream connection pooling | Yes | `EnableConnectionPool` (default on). |
| Per-connection upstream HTTP version policy | Yes | `UpstreamHttpProtocol`/`AllowHttpProtocolTranslation` on `TunnelConnectSessionEventArgs`/`BeforeSslAuthenticateEventArgs` decouple which HTTP version the proxy uses toward the origin from which version the client negotiates with the proxy. `Auto` (default) preserves existing coupled behavior. `Http11`/`Http2` pin the origin-facing protocol; without `AllowHttpProtocolTranslation`, `Http11` simply never offers "h2" to the client (so no mismatch is possible) and `Http2` fails the connection outright if the client or origin cannot also do HTTP/2. Setting `AllowHttpProtocolTranslation` lets the proxy bridge a client/origin protocol mismatch instead of failing: an h2 client talking to an HTTP/1.1-only origin is served by one independently managed HTTP/1.1 transaction per h2 stream, and an HTTP/1.1 client talking to an h2-only origin is served by leasing one h2 stream per HTTP/1.1 request from a persistent origin connection (bound to that one client connection). Both bridge directions are wired identically for explicit CONNECT tunnels and transparent TLS endpoints, and both are covered by acceptance tests for large streamed bodies, `BeforeRequest`-time synthetic short-circuiting (the origin is never contacted), `BeforeResponse` header mutation, and client-initiated `RST_STREAM`/cancellation mid-response. Both directions run the same `BeforeRequest`/`BeforeResponse`/`AfterResponse` interception pipeline as the non-translated paths; response bodies crossing a bridge are fully buffered rather than streamed frame-by-frame, and only one origin `TcpServerConnection`/h2 connection is ever open per bridged client connection (no cross-client h2 stream multiplexing yet). WinAuth (NTLM/Negotiate), client-certificate-bound, and custom-per-session-upstream-proxy connections are never shared or translated across bridge boundaries. |
| Origin-facing HTTP/1.0 request-version normalization | Yes | `ProxyServer.OriginHttpVersionPolicy` (default `PreserveClientVersion`) controls whether the HTTP version the proxy declares to an HTTP/1.1-wire origin matches the client's own declared version verbatim (default, matching historical pass-through behavior) or is always normalized to `HTTP/1.1` (`NormalizeToHttp11`) so a compliant origin can be pooled/reused as a persistent connection regardless of whether individual clients are still HTTP/1.0. This never changes the client-facing `Request.HttpVersion` that event handlers observe, nor the version/persistence rules used to write the response back to the client. |
| `Via` header injection | Yes (default) | `ViaHeaderPseudonym` defaults to `"titanium-web-proxy"`, appending `Via: {version} {pseudonym}` to forwarded HTTP/1.x and native/bridged HTTP/2 requests and responses (RFC 9110 §7.6.3). HTTP/2 uses the `2` received-protocol token, while responses record the origin response version. Set to an empty string to disable. Loop detection checks exact received-by tokens across every `Via` field and refuses matches with `508 Loop Detected`. |
| Authentication retry bounds | Yes | NTLM/Negotiate and 407/401 retry loops are capped at 3 round-trips per session to prevent indefinite handshake cycles against a misbehaving peer. |

## HTTP/3 (QUIC) — opt-in, experimental

HTTP/3 support is gated behind `ProxyServer.EnableHttp3 = true` (marked `[Experimental("TWP001")]`).
It requires the MsQuic native library and `System.Net.Quic.QuicListener.IsSupported == true` at runtime
(available on Windows 11/Server 2022+ and Linux with a recent `libmsquic` package; on macOS, bundle
`libmsquic`, `libssl`, and `libcrypto` with `@loader_path` RPATH — see [HTTP-3](HTTP-3) for details).

| Feature | Support | Notes |
|---------|---------|-------|
| Inbound HTTP/3 (client → proxy over QUIC) | Yes | `TransparentQuicProxyEndPoint` — QUIC only; explicit QUIC proxying is not yet standardised. |
| QPACK header compression | Yes (static table only) | Only RFC 9204 static-table lookups and literal encoding are implemented. Dynamic-table synchronisation is not used (encoder sends Required Insert Count = 0 on every field section). |
| HTTP/3 frame codec | Yes | HEADERS, DATA, SETTINGS, GOAWAY, and unknown/reserved frame types per RFC 9114. |
| Per-stream request/response lifecycle | Yes | `BeforeRequest`, `BeforeResponse`, `AfterResponse` (exactly once per stream), `Via` header injection, per-stream `UpstreamHttpProtocol` override, `ConnectTimeout` override. |
| Outbound H3→H3 (proxy → origin over QUIC) | Yes | `QuicConnectionPool` leases a live `QuicConnection` per origin; streams are opened per-request. |
| Outbound H3→H2 bridge | Yes | Falls through to `TcpConnectionFactory` with h2 ALPN when `UpstreamHttpProtocol.Http2` is set. |
| Outbound H3→H1.1 bridge | Yes | Falls through to `TcpConnectionFactory` with default ALPN negotiation. |
| Inbound H1.1/H2 → H3 origin bridge | Yes | `RequestHandler` checks `Http3OriginCapabilityCache` before opening a TCP connection; if H3 is cached, `Http3OriginBridge.ForwardAsync` is used instead. |
| Alt-Svc discovery | Yes | Response `Alt-Svc: h3=":443"; ma=86400` headers are parsed and cached in `Http3OriginCapabilityCache` with the advertised max-age TTL, enabling proactive H3 reuse on subsequent requests. |
| HTTPS/SVCB DNS discovery | Future | Extension point in `Http3OriginCapabilityCache.Set` is available; DNS resolution is not yet wired. |
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
| `EnableRfc8441` | `false` | Enables WebSocket over HTTP/2 extended CONNECT negotiation (RFC 8441). When enabled, the proxy advertises `ENABLE_CONNECT_PROTOCOL=1` to h2 clients and selects the appropriate tunnel path: if the origin also advertises `ENABLE_CONNECT_PROTOCOL=1`, the proxy uses the native h2↔h2 DATA relay path; otherwise it falls back to opening an HTTP/1.1 WebSocket upgrade to the origin. |
| `EnableHttp3` | `false` | Enables HTTP/3 (QUIC) support (opt-in, experimental — suppress `TWP001`). See [HTTP-3](HTTP-3) wiki page for full details. |

## Where to look for more detail

- [Streaming Bodies](Streaming-Bodies) - the `OnRequestBodyWrite`/`OnResponseBodyWrite`/`RespondStreaming` APIs in depth.
- [HTTP/3](HTTP-3) - HTTP/3 and QUIC support, `TransparentQuicProxyEndPoint`, protocol bridges, and `EnableHttp3`.
- [Home](Home) - general usage and the rest of the public API surface.
