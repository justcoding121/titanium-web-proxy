# Protocol Feature Support

A feature-by-feature snapshot of what Titanium Web Proxy actually implements for HTTP/1.0, HTTP/1.1, and
HTTP/2, so you can tell at a glance whether something you depend on is fully supported, relayed
best-effort, or not implemented yet. "Yes" means the proxy actively parses/enforces the feature (and you
can observe/modify it via the public API where relevant); "Partial" means it works for the common case but
has a known gap; "No" means it isn't implemented.

This table reflects the `develop` branch as of the HTTP/1.x gap-closure work (chunked trailers, interim 1xx
responses, and TLS body-write-hook parity). If you find something inaccurate, please open an issue.

## Connections and framing

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | Notes |
|---|---|---|---|---|
| Persistent connections / keep-alive | Yes | Yes | Yes (inherent) | `Connection: keep-alive` (1.0) / default (1.1); HTTP/2 multiplexes over one connection. |
| Chunked transfer-encoding | N/A (no chunked in 1.0) | Yes | N/A (HTTP/2 uses DATA frames, not chunking) | Read and write, both request and response, via `HttpStream`. |
| Chunked trailers (trailing headers) | N/A | Yes | No | See `RequestResponseBase.TrailingHeaders`; forwarded/emitted for HTTP/1.x. HTTP/2 trailer HEADERS frames are not yet distinguished from the main header block. |
| `Expect: 100-continue` | Yes | Yes | N/A (no equivalent frame flow) | `ProxyServer.Enable100ContinueBehaviour`. |
| Other 1xx interim responses (e.g. 103 Early Hints) | N/A | Yes | No | Relayed to the client and looped past on the server connection; not yet exposed as a dedicated event. HTTP/2 has no interim-response handling. |
| `Upgrade` / WebSocket (101 Switching Protocols) | N/A | Yes | N/A | Raw duplex relay once upgraded; see `RequestHandler.HandleWebSocketUpgrade`. |
| `CONNECT` tunneling | Yes | Yes | Yes (via `ExplicitProxyEndPoint`) | Supports both decrypt-and-inspect and pass-through-as-opaque-tunnel. |
| Stream multiplexing | N/A | N/A | Yes | Concurrent streams tracked per connection in `Http2Helper`. |
| HPACK header compression | N/A | N/A | Partial | Decode reuses a persistent dynamic table; encode currently creates a fresh table per call, so repeated response headers aren't re-indexed on the encode side. |
| Flow control (`WINDOW_UPDATE`) | N/A | N/A | Partial | Frames are relayed but the proxy does no window accounting of its own. |
| Server push (`PUSH_PROMISE`) | N/A | N/A | Partial | Frames are byte-relayed only; not parsed/exposed (push is deprecated in browsers, so low priority). |
| `PING` / keepalive frames | N/A | N/A | Partial | Relayed passively; the proxy does not originate or respond to pings itself. |
| `SETTINGS` negotiation | N/A | N/A | Partial | `HEADER_TABLE_SIZE` and `MAX_FRAME_SIZE` are read; other settings are relayed without being enforced. |
| `RST_STREAM` / per-stream cancellation | N/A | N/A | Yes | Cleans up the corresponding session and relays the frame. |

## Body handling and streaming

| Feature | HTTP/1.0 | HTTP/1.1 | HTTP/2 | Notes |
|---|---|---|---|---|
| Buffered body read/modify (`GetRequestBody`/`SetResponseBodyString`, etc.) | Yes | Yes | Yes | |
| Per-chunk streaming hooks (`OnRequestBodyWrite`/`OnResponseBodyWrite`) - plain HTTP | Yes | Yes | Yes | |
| Per-chunk streaming hooks - TLS-decrypted connections | Yes | Yes | Yes | Fixed for HTTP/1.x: the hook now fires with parity for `SslStream`-backed connections, not just plain `NetworkStream`. |
| Synthetic streamed responses (`RespondStreaming`) | Yes | Yes | Yes | Chunked or fixed-length framing chosen automatically from the response headers you set. |
| Automatic decompression for body inspection (gzip/deflate/brotli) | Yes | Yes | Yes | |
| Multipart/form-data boundary-aware streaming | Yes | Yes | N/A (not multipart-aware over h2 yet) | |

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
