> For operators and library users. HTTP/3 WebTransport needs a datagram API this runtime does not expose. HTTP/1.1 and HTTP/2 capsule sessions are opt-in.

WebTransport here is an HTTP resource session (`:path` is the application path, or `Upgrade: webtransport` on HTTP/1.1). It is not a UDP relay. CONNECT-UDP stays on a `type: masque` listener.

## What is shipped

Off unless `server.enableWebTransport` is true (explicit listeners only) or `listeners[].acceptWebTransport` is true. Those connections advertise `SETTINGS_ENABLE_CONNECT_PROTOCOL` and `SETTINGS_WT_MAX_SESSIONS=1` (draft-ietf-webtrans-http2). A client's WebTransport setting is not forwarded to the origin. If the origin omits `SETTINGS_WT_MAX_SESSIONS`, the stream is `501`.

| Path | Behavior |
|---|---|
| HTTP/2 to HTTP/2 | `:protocol=webtransport` is forwarded. After 2xx, DATA frames are copied as capsule bytes. |
| HTTP/2 to HTTP/1.1 | The origin receives `GET` with `Upgrade: webtransport` and `Capsule-Protocol: ?1`. A `101` becomes `200` toward the HTTP/2 client, then capsule bytes are copied. |
| HTTP/1.1 to HTTP/2 | `Upgrade: webtransport` becomes extended CONNECT when the origin advertises WebTransport. The client receives `101`. |
| HTTP/3 | `501`. Datagrams are not available. |

`capsule-protocol` must be absent or `?1`. A later `HEADERS` frame on an established HTTP/2 tunnel is `PROTOCOL_ERROR`.

## What is not shipped

No `WT_STREAM` state machine and no HTTP/3 WebTransport.

## Terminate listener

`type: webtransport` is a TLS endpoint that accepts only `:protocol=webtransport` (or `Upgrade: webtransport` when `allowHttp11Upgrade` is true). It opens `GET` with `Upgrade: webtransport` on the pinned `originHost`. The client path is forwarded. The client does not choose the origin host, and the listener does not open a UDP socket.

```yaml twp-fragment
listeners:
  - type: webtransport
    host: 127.0.0.1
    port: 443
    webtransport:
      requireAuth: false
      originHost: 127.0.0.1
      originPort: 8443
      originHttps: false
```

`connect-udp` and `websocket` on this listener are `501`. Authentication fails closed unless `requireAuth` is false or credentials are set. The listener passes extended CONNECT into its own HTTP/2 session. It does not set `server.enableRfc8441`.
