# Protocol Feature Support

A feature-by-feature snapshot of what Titanium Web Proxy actually implements for HTTP/1.0, HTTP/1.1, and
HTTP/2, so you can tell at a glance whether something you depend on is fully supported, relayed
best-effort, or not implemented yet. "Yes" means the proxy actively parses/enforces the feature (and you
can observe/modify it via the public API where relevant); "Partial" means it works for the common case but
has a known gap; "No" means it isn't implemented.

This table reflects the `develop` branch as of the HTTP/1.x and HTTP/2 gap-closure work (chunked trailers,
interim 1xx responses, and TLS body-write-hook parity for HTTP/1.x; HPACK dynamic-table correctness/reuse,
HEADERS/CONTINUATION reassembly and re-splitting, trailers, interim 1xx responses, two-hop flow control,
SETTINGS/PING/GOAWAY handling, and synthetic-response API parity for HTTP/2). HTTP/2 has gone through a full
regression pass and is now **on by default** (`ProxyServer.EnableHttp2 = true`); set it to `false` to force
HTTP/1.1 only. If you find something inaccurate, please open an issue.

## Connections and framing

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | Notes |
|---|---|---|---|---|
| Persistent connections / keep-alive | Yes | Yes | Yes (inherent) | `Connection: keep-alive` (1.0) / default (1.1); HTTP/2 multiplexes over one connection. |
| Chunked transfer-encoding | N/A (no chunked in 1.0) | Yes | N/A (HTTP/2 uses DATA frames, not chunking) | Read and write, both request and response, via `HttpStream`. |
| Chunked trailers (trailing headers) | N/A | Yes | Yes | See `RequestResponseBase.TrailingHeaders`; forwarded/emitted for HTTP/1.x. For HTTP/2, a second HEADERS block without request/status pseudo-headers is decoded as trailers and re-encoded/relayed without re-firing `BeforeRequest`/`BeforeResponse`. |
| `Expect: 100-continue` | Yes | Yes | N/A (no equivalent frame flow) | `ProxyServer.Enable100ContinueBehaviour`. |
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
| Buffered body read/modify (`GetRequestBody`/`SetResponseBodyString`, etc.) | Yes | Yes | Yes | |
| Per-chunk streaming hooks (`OnRequestBodyWrite`/`OnResponseBodyWrite`) - plain HTTP | Yes | Yes | Yes | |
| Per-chunk streaming hooks - TLS-decrypted connections | Yes | Yes | Yes | Fixed for HTTP/1.x: the hook now fires with parity for `SslStream`-backed connections, not just plain `NetworkStream`. |
| Synthetic streamed responses (`RespondStreaming`) | Yes | Yes | Yes | Chunked or fixed-length framing chosen automatically from the response headers you set. |
| Automatic decompression for body inspection (gzip/deflate/brotli) | Yes | Yes | Yes | |
| Multipart/form-data boundary-aware streaming | Yes | Yes | N/A (not multipart-aware over h2 yet) | |

## Interception APIs

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | Notes |
|---|---|---|---|---|
| Header/body modification in `BeforeRequest`/`BeforeResponse` | Yes | Yes | Yes | Every HEADERS block is fully decoded/transcoded, so mutations made in the event handler are re-encoded and relayed rather than passed through opaquely. |
| Synthetic responses (`Ok`, `Respond`, `Redirect`, `GenericResponse`) from `BeforeRequest` | Yes | Yes | Yes | The request is never forwarded upstream; any unfinished client request body already in flight is drained with flow-control credit returned. |
| `Respond` replacing an already-received response from `BeforeResponse` | Yes | Yes | Yes | The origin's own response body, if still arriving, is discarded (with flow-control credit still returned) in favor of the replacement. |
| `RespondStreaming` (synthetic streamed body) | Yes | Yes | Yes | See "Synthetic streamed responses" above for framing details. |
| `AfterResponse` / per-request disposal | Yes | Yes | Yes | Every h2 stream - whether it completes normally, is reset (`RST_STREAM`), or is still open when the connection itself tears down - gets exactly one `AfterResponse` invocation and one `SessionEventArgs.Dispose()`, matching the HTTP/1.x `finally` guarantee. Request-header preparation (`Accept-Encoding` filtering, hop-by-hop header stripping) also now runs for h2 requests before they are forwarded upstream, matching HTTP/1.x. |
| `SessionEventArgs.TimeLine` milestones | Yes | Yes | Yes | `Http2Helper` now stamps `"Request Sent"`, `"Response Received"`, and `"Response Sent"` at the same logical points in the h2 stream lifecycle (terminating HEADERS or DATA frame observed on each direction) as `RequestHandler`/`ResponseHandler` do for HTTP/1.x, so latency diagnostics built on `TimeLine` behave the same regardless of which protocol a session actually used. |

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

## Where to look for more detail

- [Streaming Bodies](Streaming-Bodies) - the `OnRequestBodyWrite`/`OnResponseBodyWrite`/`RespondStreaming` APIs in depth.
- [Home](Home) - general usage and the rest of the public API surface.
