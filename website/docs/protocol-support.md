# Protocol support

Titanium speaks HTTP/1.0, HTTP/1.1, HTTP/2, and HTTP/3, and can bridge between them when client and origin disagree.

> **For implementers / .NET library users** — the tables below use API names (`EnableHttp2`, `EnableHttp3`, `TWP001`). CLI and Inspector enable HTTP/3 when QUIC is available; see [HTTP/3](/docs/http3).

HTTP/2 is **on by default**. HTTP/3 is **on by default** in the CLI and Inspector when QUIC is available. The library property `EnableHttp3` stays off until the host turns it on. CLI/Inspector release zips bundle MsQuic natives per platform; Library hosts install system MsQuic — see [HTTP/3](/docs/http3).

## Protocol bridges

| Client | Origin | Kind |
|--------|--------|------|
| HTTP/1.x | HTTP/1.x | Native |
| HTTP/2 | HTTP/2 | Native frame relay when the route table is forward-host equivalent (one shared origin connection per client connection). Per-stream dispatch via `Http2OriginConnectionPool` when `RequiresPerRequestDispatch` applies (explicit listeners, non-equivalent routes, or policies). |
| HTTP/3 | HTTP/3 | Native when `EnableHttp3` |
| HTTP/1.1 ↔ HTTP/2 | Bridge | When `UpstreamHttpProtocol` + `AllowHttpProtocolTranslation` |
| HTTP/1.1 / HTTP/2 ↔ HTTP/3 | Bridge | When H3 selected |
| HTTP/3 → HTTP/2 / HTTP/1.1 | Bridge | Via origin connection helpers |

**Not supported:** `Upgrade: h2c` (prior-knowledge only); mid-connection H2→H3 on an open H2↔H2 MITM session; explicit QUIC proxying (inbound H3 is transparent QUIC / dual-listen only).

**WebSocket over HTTP/3 (RFC 9220):** when `EnableRfc8441 = true`, the proxy advertises `SETTINGS_ENABLE_CONNECT_PROTOCOL` (0x08) on HTTP/3 and bridges extended CONNECT (`:protocol = websocket`) to HTTP/1.1 WebSocket origins. HTTP/2 and HTTP/3 origins are not covered yet.

**Explicit HTTP proxy and origin `Alt-Svc`.** A browser that uses the proxy as an explicit HTTP proxy sends traffic as TCP (`CONNECT`) and cannot send QUIC through it. The origin's `Alt-Svc` header is still forwarded. Checked locally with Chrome, Edge, and Firefox (QUIC left enabled, loopback not bypassed): later requests stayed on the proxy, and no UDP arrived at the advertised HTTP/3 port. Safari was not part of that run. The proxy's own HTTP/3 to the server is separate; it still learns `Alt-Svc` for its origin cache.

## Connections and framing (high level)

| Feature | H1.0 | H1.1 | H2 | H3 |
|---------|------|------|----|----|
| Keep-alive / multiplexing | Yes | Yes | Yes | Yes |
| Chunked / DATA frames | N/A / Yes | Yes | DATA | DATA |
| Trailers | N/A | Yes | Yes | Yes |
| WebSocket | Yes | Yes | RFC 8441 | RFC 9220 (H1 origin) |

For the full matrix (headers, flow control, SETTINGS/PING/GOAWAY, streaming, auth), see the [Protocol-Support wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support).

## Response compression

Opt-in `server.responseCompression` compresses origin responses (brotli or gzip), either buffered (up to 1 MiB with a known length) or, with `mode: streaming`, while the body is relayed. Streaming has no size limit, flushes per upstream chunk and uses chunked framing (HTTP/1.1) or DATA frames (the HTTP/2 bridge); HTTP/1.0 clients get identity. See [Configuration](/docs/configuration#response-compression-server-responsecompression).

| Path | Compressed |
|------|------------|
| HTTP/1.1 client, HTTP/1.1 origin (session path) | Yes |
| HTTP/2 client, HTTP/1.1 origin (bridge) | Yes |
| HTTP/2 client, HTTP/2 origin | No |
| HTTP/3 | No |
| HTTP/1.1 client, HTTP/2 origin | No |

Opt-in `server.requestDecompression` decompresses gzip and brotli request bodies before they reach the origin, with a decompressed-size cap and a ratio limit; a breach answers `413`, a corrupt body `400`. It applies to HTTP/1.x requests on the session path only. See [Configuration](/docs/configuration#request-decompression-server-requestdecompression).

## gRPC-Web

A route with `grpcWeb: true` translates a binary gRPC-Web call (`application/grpc-web` or `application/grpc-web+proto`) to native gRPC for an HTTP/2 origin, and folds the origin trailers into a final `0x80` frame. It runs on the HTTP/2-to-HTTP/2 dispatch bridge and on the HTTP/1.1-to-HTTP/2 bridge. Unary and server-streaming only; `application/grpc-web-text` is not translated. See [Configuration](/docs/configuration#grpc-web-route-grpcweb) for the flag and CORS guidance.

## Routing and clusters

Per-request route dispatch runs on the HTTP/1 session path, the HTTP/1-to-HTTP/2 bridge, the HTTP/2-to-HTTP/1 bridge, the HTTP/2-to-HTTP/2 dispatch bridge (`SendHttp2ToHttp2DispatchBridge`), the HTTP/2-to-HTTP/3 bridge, and HTTP/3 streams. A session-less HTTP/3 fast path stays on the listener forward target only when the route table is forward-host equivalent; any other route table takes the session path so the route is honored. HTTP/2-to-HTTP/2 native frame relay is unchanged when the listener is forward-host equivalent (no per-request dispatch).

## Related

- [HTTP/3](/docs/http3)
- [Streaming bodies](/docs/streaming-bodies)
- [Library](/docs/library)
