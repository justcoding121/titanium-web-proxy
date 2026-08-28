# Protocol support

Feature snapshot for HTTP/1.0, HTTP/1.1, HTTP/2, and HTTP/3. HTTP/2 is **on by default** (`ProxyServer.EnableHttp2 = true`). HTTP/3 is experimental (`TWP001`) and opt-in (`EnableHttp3 = true`).

## Protocol bridges

| Client | Origin | Kind |
|--------|--------|------|
| HTTP/1.x | HTTP/1.x | Native |
| HTTP/2 | HTTP/2 | Native (ALPN MITM; prior-knowledge h2c on transparent reverse) |
| HTTP/3 | HTTP/3 | Native when `EnableHttp3` |
| HTTP/1.1 ↔ HTTP/2 | Bridge | When `UpstreamHttpProtocol` + `AllowHttpProtocolTranslation` |
| HTTP/1.1 / HTTP/2 ↔ HTTP/3 | Bridge | When H3 selected |
| HTTP/3 → HTTP/2 / HTTP/1.1 | Bridge | Via origin connection helpers |

**Not supported:** `Upgrade: h2c` (prior-knowledge only); mid-connection H2→H3 on an open H2↔H2 MITM session; WebSocket over HTTP/3; explicit QUIC proxying (inbound H3 is transparent QUIC / dual-listen only).

## Connections and framing (high level)

| Feature | H1.0 | H1.1 | H2 | H3 |
|---------|------|------|----|----|
| Keep-alive / multiplexing | Yes | Yes | Yes | Yes |
| Chunked / DATA frames | N/A / Yes | Yes | DATA | DATA |
| Trailers | N/A | Yes | Yes | Yes |
| WebSocket | Yes | Yes | RFC 8441 | No |

For the full matrix (headers, flow control, SETTINGS/PING/GOAWAY, streaming, auth), see the [Protocol-Support wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Protocol-Support).

## Related

- [HTTP/3](/docs/http3)
- [Streaming bodies](/docs/streaming-bodies)
- [Library](/docs/library)
