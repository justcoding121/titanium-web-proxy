# Protocol support

Titanium speaks HTTP/1.0, HTTP/1.1, HTTP/2, and experimental HTTP/3, and can bridge between them when client and origin disagree.

> **For implementers / .NET library users** — the tables below use API names (`EnableHttp2`, `EnableHttp3`, `TWP001`). Operators enabling HTTP/3 from the CLI can start with [HTTP/3](/docs/http3).

HTTP/2 is **on by default**. HTTP/3 is experimental and opt-in. CLI/Inspector release zips bundle MsQuic natives per platform; Library hosts install system MsQuic — see [HTTP/3](/docs/http3).

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

**Explicit HTTP proxy and origin `Alt-Svc`.** A browser that uses the proxy as an explicit HTTP proxy sends traffic as TCP (`CONNECT`) and cannot send QUIC through it. The origin's `Alt-Svc` header is still forwarded. Checked locally with Chrome, Edge, and Firefox (QUIC left enabled, loopback not bypassed): later requests stayed on the proxy, and no UDP arrived at the advertised HTTP/3 port. Safari was not part of that run. The proxy's own HTTP/3 to the server is separate; it still learns `Alt-Svc` for its origin cache.

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
