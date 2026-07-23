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
| Stream multiplexing | N/A | N/A | Yes | Concurrent streams tracked per connection in `Http2Helper`; a slow synthetic response on one stream no longer blocks frames for other streams. |
| HPACK header compression | N/A | N/A | Yes | Both decode and encode reuse a persistent, connection-direction-scoped dynamic table, so repeated headers are indexed/re-indexed correctly on both sides. |
| Flow control (`WINDOW_UPDATE`) | N/A | N/A | Yes | Independent send-side window accounting per connection and per stream on each of the two TLS legs (client↔proxy, proxy↔server); validates length/increment/overflow and translates rather than blindly relays. |
| Server push (`PUSH_PROMISE`) | N/A | N/A | No | Not decoded/transcoded; no public API to originate a push (push is deprecated in browsers, so low priority). |
| `PING` / keepalive frames | N/A | N/A | Yes | ACKed locally by the proxy on each leg; not blindly relayed. |
| `SETTINGS` negotiation | N/A | N/A | Yes | `HEADER_TABLE_SIZE`, `MAX_FRAME_SIZE`, and `INITIAL_WINDOW_SIZE` are validated (range-checked, malformed values rejected) and applied per leg; the proxy sends its own SETTINGS on each leg rather than relaying the peer's verbatim. |
| `RST_STREAM` / per-stream cancellation | N/A | N/A | Yes | Cleans up the corresponding session/flow-control state and relays the error code. |
| `GOAWAY` / connection shutdown | N/A | N/A | Yes | Parses `lastStreamId`/error code, stops admitting new streams above it, cancels in-flight work that can't complete, and relays to the other leg. |

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

## Proxying, auth, and misc

| Feature | Support | Notes |
|---|---|---|
| Explicit, transparent, and SOCKS4/5 endpoints | Yes | `ExplicitProxyEndPoint`, `TransparentProxyEndPoint`, `SocksProxyEndPoint`. |
| Upstream proxy chaining (HTTP/HTTPS/SOCKS) | Yes | Static, per-request (`GetCustomUpStreamProxyFunc`), or system-gateway detection. |
| Proxy Basic authentication | Yes | `ProxyBasicAuthenticateFunc`. |
| Windows authentication (Kerberos/NTLM) to upstream servers | Yes | `EnableWinAuth`. |
| Mutual TLS to upstream servers | Yes | `ClientCertificateSelectionCallback` / `ServerCertificateValidationCallback`. |
| Upstream connection pooling | Yes | `EnableConnectionPool` (default on). |

## Where to look for more detail

- [Streaming Bodies](Streaming-Bodies) - the `OnRequestBodyWrite`/`OnResponseBodyWrite`/`RespondStreaming` APIs in depth.
- [Home](Home) - general usage and the rest of the public API surface.
